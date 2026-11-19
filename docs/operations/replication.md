# Replication between servers (P19, current scope)

Status on 2026-11-18. What exists is **asynchronous primary → replica replication with manual producer failover**.
It is not a cluster: there is no sharding, leader election, consensus or automatic failover.

## How it works

- The primary starts with `--replicate-to https://replica:7400` and `LATTICELOG_REPLICA_TOKEN` (an `ingest` token
  valid on the replica for the replicated namespaces).
- For each namespace on the primary, a `Forwarder` (the same component applications use) reads the namespace's store
  and sends it to the same namespace on the replica with ingest protocol v1. New namespaces are discovered every 2 s.
- Each namespace's cursor is stored next to the primary's store. After a primary restart it resumes where it left off;
  the replica deduplicates whatever is resent.
- **Provenance**: the replica stores each record with two `ingest` context layers (the primary's and the original
  producer's). From that the replica learns up to which sequence it has received from each original producer, and keeps
  it across restarts (`INGEST-CHECKPOINT.json`, field `upstream`).

## Guarantees

| situation | result |
|---|---|
| The primary acknowledges a batch to a producer | It does **not** wait for the replica (asynchronous). What is acknowledged but not yet replicated lives only on the primary and in the producer's spool. |
| The primary fails and producers switch to the replica | The producer resends from its cursor (or from the start if it lost it); the replica drops what it already had from the primary and stores the rest. **Nothing duplicated, nothing lost** as long as the producer's spool still holds the data. Tested in `ReplicationTests`. |
| The replica fails | The primary keeps accepting; the lag grows and recovers when the replica returns. |
| Privacy erasure (`/v1/admin/erase`) | **Not replicated**. It has to be run on the replica too. |
| Retention / compaction | Each server applies its own. |

## Monitoring

- `GET /v1/health` (admin token) includes the status per namespace: replicated sequence, forwarder state and last
  error.
- `latticelog_replication_lag_records{namespace=…}` in `/metrics`. Suggested alert: lag > 0 for 5 min, or growing for
  15 min.

## Manual failover (runbook)

1. Confirm that the primary is not coming back soon (if it does, there is nothing to do: it resumes on its own).
2. Point the producers at the replica (DNS or configuration). Their `Forwarder`s resend whatever is still
   unacknowledged; the replica deduplicates.
3. What the primary acknowledged but never replicated is **only recoverable** if it is still in the producers' spools.
   That is why the producers' local retention must cover at least the detection + switch time.
4. When the old primary comes back, do **not** start it as a primary at the same time: there is no fencing today. Start
   it as an empty replica (or after copying the new primary's store) and point replication the other way.

## What is missing before this can be called a cluster (P19/P20)

Fencing/epochs (T05), a consistency model with replica ACKs (T06–T07), shards and a router (T02–T03), shard movement
(T10), partition and split-brain tests (T14), measured scaling to N nodes (T15), tiered object storage (P20). None of
it is done; the plan first asks to measure single-node limits (T01), which needs stable hardware.
