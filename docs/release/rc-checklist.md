# Release candidate checklist (P14, P27)

Status on 2026-11-18. An RC is published only when **every** blocking box is checked.

## Blocking (owner decision or action)

- [x] **License** chosen and applied: MIT (`LICENSE`, `PackageLicenseExpression`).
- [x] **Name** checked: no other logging project called LatticeLog on GitHub (2026-11-18). No trademark registration,
      which a free project does not need.
- [x] **Public repository** created, with CI on Linux, macOS and Windows, including the Native AOT job.
- [ ] **Stable hardware** for the measurements (the current machine corrupts memory under all-core load:
      `reports/ingest-v1.md` §3). Performance repeated on that machine or in CI.

## Technical

- [x] Full suite green, plus the Python reader over the frozen test files.
- [x] Real crash, retention, deduplication-after-restart and lossy-network tests.
- [x] Trimming/AOT analysis active and warning-free under `src/`.
- [x] SBOM generated and vulnerable packages checked (CI `security` job).
- [x] TLS tested end to end with a real certificate (`TlsTests` on three OSes; the container smoke test over HTTPS).
- [ ] A real network between two machines (CI only covers one host).
- [x] Docker image built, scanned (Trivy, no fixable HIGH/CRITICAL) and started in CI (`deploy/smoke-test.sh`).
- [ ] Signed packages and SourceLink pointing at the public repository.
- [ ] External security review (pentest) of the server: see `docs/security/pentest-scope.md`.
- [ ] User documentation reviewed by someone who did not write it.

## Procedure

1. Branch `release/0.x`. Version `0.x.0-rc.N` in `Directory.Build.props`.
2. Full CI green. Artifacts: packages, image, SBOM and AOT binaries.
3. Run the RC for at least a week in a real environment, following `docs/deploy/single-node.md`.
4. No blocking issues → tag `v0.x.0` and write release notes from `CHANGELOG.md`.
