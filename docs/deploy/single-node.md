# Single-node deployment (P17-T15)

One machine, no Kubernetes. `latticelog-server` receives data from forwarders and serves queries.

## 1. Prepare

1. **Data** on a disk with plenty of space, ideally not the system disk. It holds `ns/<namespace>/` (one store per
   namespace) and `server.key` (the cursor signing key; include it in backups).
2. **TLS certificate** as a PFX. Off loopback the server refuses to start on plain HTTP unless you pass
   `--allow-insecure-http`, which is not recommended.
3. **Tokens**. For each secret:
   ```bash
   latticelog-server hash-token "<long random secret>"
   ```
   and a `tokens.json` with one token per application (`ingest`, limited to its namespace) and one per person or team
   (`read`). See `specs/server-api.md` §2.

## 2. Start

Binary:

```bash
export LATTICELOG_CERT_PASSWORD='…'
latticelog-server --data /srv/latticelog --tokens /etc/latticelog/tokens.json --listen https://0.0.0.0:7400 --cert /etc/latticelog/server.pfx
```

Container (`deploy/Dockerfile`, a *chiseled* image with no shell and a non-root user):

```bash
docker build -f deploy/Dockerfile -t latticelog-server:dev .
docker run -d --name latticelog -p 7400:7400 -v /srv/latticelog:/data \
  -v /etc/latticelog/tokens.json:/config/tokens.json:ro -v /etc/latticelog/server.pfx:/run/secrets/server.pfx:ro \
  -e LATTICELOG_CERT_PASSWORD='…' latticelog-server:dev
```

Check: `curl https://host:7400/v1/health` returns `{"status":"ok",…}`. With an `admin` token it also shows the details
of each namespace.

## 3. Applications

Applications write to their local store, and a `Forwarder` (package `LatticeLog.Ingest`) ships to the server (see the
README). If the server is unavailable, data waits on the application's disk, which has its own quota.

## 4. Operations

- **Rotating tokens**: add the new token to the file, switch the clients, then remove the old one. No restart needed.
- **Upgrading**: stop, replace the binary or image, and start. On startup every namespace is recovered and
  deduplication is rebuilt from the data, so forwarders can retry without creating duplicates.
- **Backups**: hot, with `POST /v1/admin/backup?ns=…` (start with `--backup-dir`) or `latticelog backup`; every copy is
  verified. Also keep `server.key` and the tokens file. Details and restore in `docs/operations/runbook.md`.
- **Retention, compaction, verification and privacy erasure**: online, without stopping, with `POST /v1/admin/…`
  (`specs/server-api.md` §5). Offline, `latticelog retention|compact|verify` still exist.
- **Metrics**: `GET /metrics` (Prometheus, admin token). **Console**: `https://host:7400/ui`. **Audit log**:
  `data/audit/`.
- **Capacity**: one writer thread per namespace. On the reference machine, ~300–600k records/s of ingestion with a
  few producers (`reports/ingest-v1.md`). Heavy queries compete for CPU with ingestion; if ingestion takes priority,
  lower `MaxConcurrentQueries`.

## 5. Known limits

- Optional asynchronous replica with manual failover (`docs/operations/replication.md`); no cluster and no automatic
  failover. Without a replica, losing the disk loses whatever is not in a backup or in the producers' local stores.
- The Docker image is built, scanned and smoke-tested over HTTPS on every CI run (`deploy/smoke-test.sh`, which you
  can also run locally). It is not published to a registry yet: build it from the repository.
