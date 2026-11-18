# Ordering and time semantics (P01-T09, P01-T10, spec §3.4)

1. **Sequence** — total order of persistence inside one store (assigned by the single writer). It reflects arrival
   at the writer, not causality.
2. **Producer wall clock** (`timestamp`) — may go backwards (NTP), may differ across hosts. Query results and
   timelines sort by (timestamp, sequence) and say so (`ordering_note`).
3. **Monotonic clock** (`monotonic`) — valid for durations inside one process run only.
4. **Arrival time** at a collector/server is `observed_timestamp`; it never replaces `timestamp`.
5. **Causality** is only stated through explicit relations: same `trace_id`; span parentage (from the tracing system);
   `parent_operation_id → operation_id`; `correlation_id`. Temporal proximity is never reported as a relation.
6. With unsynchronized clocks, a child event can appear before its parent in a timeline; readers keep the explicit
   relation and must not "fix" timestamps.
