# Context model (P01-T07, P01-T08, P08)

- A **ContextSnapshot** is immutable: `{kind: resource|scope|operation|request|custom, name?, attributes[], parent?}`.
  Creating a child never mutates the parent, so concurrent producers can share snapshots without races.
- The logger's root is the **resource** snapshot (service.name, service.version, deployment.environment, host…).
  `BeginScope`/`BeginOperation` push a child snapshot through `AsyncLocal`; disposing restores the previous one.
- A record stores only a reference to its innermost snapshot; its own attributes are kept separately. Readers can
  therefore distinguish *own* from *inherited* values (provenance). Lookup walks child → root, innermost wins.
- What propagates: values the application puts in scopes. What does not: secrets (redaction applies to context values
  in outputs), `Activity` baggage (not copied unless explicitly added), ephemeral per-call data (put it on the record).
- Process boundaries: contexts are not transmitted implicitly. Cross-process relations use `trace_id`,
  `operation_id`/`parent_operation_id` and `correlation_id` explicitly.
- Storage: snapshots are interned per segment (context table); Lattice-C additionally encodes "same context tuple
  as the previous record" with one bit. A reader never assumes a context that is not referenced explicitly.
