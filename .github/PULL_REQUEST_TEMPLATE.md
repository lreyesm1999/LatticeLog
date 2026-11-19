## What and why

<!-- One concern per PR. Link the issue: Fixes #123 -->

## How it was tested

<!-- New or updated tests, commands you ran, benchmark numbers if the hot path or storage changed. -->

## Checklist

- [ ] `dotnet test LatticeLog.sln -c Release` passes locally
- [ ] `dotnet format whitespace LatticeLog.sln --verify-no-changes` is clean
- [ ] New tests are deterministic (no wall-clock budgets)
- [ ] Specs updated if anything on the wire or on disk changed (`specs/`), with new golden files when the format changed
- [ ] `CHANGELOG.md` updated for user-visible changes
- [ ] No silent data loss, no durability ACK before persistence, no cross-tenant access
