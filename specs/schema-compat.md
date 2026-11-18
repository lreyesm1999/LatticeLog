# Event contract compatibility (P01-T06)

An event contract is `EventDescriptor{id, name, version, severity, template?, fields[name, kind, sensitivity]}`.

| change | compatible within the same version? |
|---|---|
| Add description / fix template wording that keeps placeholders | yes |
| Add a field at the end | **no** — bump `version` (readers keyed by `(id, version)` must not see two shapes) |
| Remove, rename or reorder a field | no — bump `version` |
| Change a field kind | no — bump `version` |
| Change sensitivity to a stricter level | yes (safer); to a weaker level: no, needs review |
| Change event name for an id | never (LL0006); create a new id |

Enforcement:
- Compile time: the source generator rejects duplicate `(id, version)` (LL0001), name conflicts for one id (LL0006)
  and, when `latticelog.events.lock.json` is provided as `AdditionalFiles`, any difference between the declared
  signature and the locked one (LL0007). The generator emits the current snapshot (`LatticeEvents.SchemaSnapshot.g.cs`)
  to refresh the lock file deliberately.
- Storage: every segment embeds the descriptors it uses, so historical files remain decodable after contracts change.
- Values whose kind differs from the declared field kind are still stored exactly (tagged), never coerced.
