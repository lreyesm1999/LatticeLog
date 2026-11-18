# Remote ingestion protocol v1 (P16, ADR-0009)

Status: draft implemented in `src/LatticeLog.Ingest` (client) and `src/LatticeLog.Server` (collector).
All integers little-endian. "Local sequence" = the record's sequence number in the producer's local store.

## 1. Request

```
POST /v1/ingest HTTP/1.1 or HTTP/2
Authorization: Bearer <token>
Content-Type: application/vnd.latticelog.batch
LatticeLog-Ack: fsync | os            (optional, default fsync)
<body = envelope>
```

### 1.1 Envelope (48-byte header + body)

| offset | size | field | rule |
|---:|---:|---|---|
| 0 | 4 | magic | `LLIB` |
| 4 | 1 | version | 1 |
| 5 | 1 | body_encoding | 1 = `latticelog.jsonl` v1, 2 = binary mini segment (§1.3); others reserved, unknown → 415 |
| 6 | 1 | compression | 0 = none, 1 = gzip |
| 7 | 1 | flags | bit 0 = `gap_acknowledged` (the producer knows records before `first_local_seq` are missing) |
| 8 | 16 | producer_id | 128-bit id = local `store_id`; never all-zero |
| 24 | 8 | first_local_seq | ≥ 1 |
| 32 | 8 | last_local_seq | ≥ first; `last - first + 1 == record_count` (dense batch) |
| 40 | 4 | record_count | 1 … `MaxRecordsPerBatch` |
| 44 | 4 | body_crc32 | CRC-32 (IEEE) of the body bytes as sent (after compression) |
| 48 | … | body | `body_length = request length - 48` |

The decoded body must contain exactly `record_count` records whose `seq` fields are `first_local_seq …
last_local_seq` in order. Anything else → 400 `invalid_batch`.

### 1.2 Limits (defaults; server answers 413/400 with `error`)

| limit | default |
|---|---:|
| request size | 8 MiB |
| decoded body | 64 MiB |
| decoded / compressed ratio | 100 |
| records per batch | 50,000 |
| line length in the body | 1 MiB |

### 1.3 Binary body (body_encoding = 2)

After decompression: `varint catalog_length`, the catalog copy (schemas then contexts, exactly as the footer catalog
section of `specs/llog-format-v0.md` §7.3), `u8 codec` (0 Raw, 1 Lattice-S, 2 Lattice-C), then the records payload of
that codec for `record_count` records (same encoding as a Records block). Every count and length is bounded by the
decoder, and malformed input gives 400 `decode_failed`. Clients send JSONL instead when a batch has attribute keys or
names with lone surrogates (the binary form would not carry them exactly). Default for the .NET forwarder since
2026-11-18. It falls back to JSONL permanently after a 415.

## 2. Server processing

1. **Authenticate.** The token's SHA-256 must be in the token file, and the token must be allowed for `producer_id`.
2. **Validate.** Check the envelope and limits, the CRC, then decompress with limits and decode.
3. **Admit.** Check the token quota (token bucket, records/s), then queue the batch in a bounded writer queue.
4. **Single writer (group commit).** Compare the batch with `committed_through[producer]` (= C):
   - `last ≤ C` → `duplicate`: nothing stored;
   - `first ≤ C < last` → store only `C+1 … last` (`partial_duplicate`);
   - `first == C+1` → store everything (`stored`);
   - `first > C+1` → 409 `sequence_gap` unless `gap_acknowledged`, then store and report `gap_before`.

   Each record is stored with its own context wrapped in a context `ingest` with attributes `ingest.producer` (hex)
   and `ingest.first_local_seq` of the stored part; server sequence numbers are assigned by the server store.
5. **Acknowledge.** After the writer has handled every queued request, it performs a single `Flush(level)`. Each
   request is then answered with the level actually reached.

### 2.1 Rebuilding `committed_through` after a restart

`committed_through[p] = max over stored ingest contexts of p of (first_local_seq + records stored with that context
- 1)`. Records lost in a torn tail are not counted, so a client that resends them gets them stored, not deduplicated.
A checkpoint (`INGEST-CHECKPOINT.json`: server sequence S + cursors) limits the scan to records with server sequence > S.

## 3. Response (`application/json`)

```json
{"status":"stored","producer":"…","committed_through":"1200","stored":1000,"skipped_duplicates":0,
 "ack":"FsyncConfirmed","server_seq_first":"5001","server_seq_last":"6000","gap_before":"0"}
```

| HTTP | `status` / `error` | client action |
|---|---|---|
| 200 | `stored`, `partial_duplicate`, `duplicate` | advance cursor to `committed_through` |
| 400 | `invalid_envelope`, `invalid_batch`, `checksum_mismatch`, `decode_failed` | stop, report (bug or corruption) |
| 401 / 403 | `unauthorized` / `forbidden_producer` | stop, report |
| 409 | `sequence_gap` (+ `committed_through`) | resend from `committed_through + 1`; if the records no longer exist locally, set `gap_acknowledged` |
| 413 | `too_large` | split the batch (client halves it) |
| 415 | `unsupported_encoding` (+ `accepted`) | fall back to an accepted encoding |
| 429 / 503 | `quota_exceeded` / `overloaded` (+ `Retry-After`) | retry later |
| 5xx, timeout, connection error | — | retry with backoff + jitter (same batch: idempotent) |

`ack` is the persistence level actually reached on the server: `FsyncConfirmed`, `WrittenToOs`, or `None` together
with HTTP 503 `persist_failed` when the server could not persist.

## 4. Guarantees and limits (P16-T15)

- Per producer, **every local record is stored at most once** and, while the local store survives, **at least
  once**: in effect exactly once in the server store.
- **Lost if the producer dies before its local write.** Records still in the in-memory queue are lost if the
  application dies before the local store has them (`specs/durability.md`).
- **Lost if the local store is lost before shipping.** No other copy exists yet.
- An acknowledgement at level `fsync` means the server's own fsync returned. There is no replication yet (P19).
- **Ordering:** per producer it follows local sequence order. Between producers there is no global order beyond
  the server sequence assigned on arrival.
