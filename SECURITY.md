# Security Policy

## Supported versions

LatticeLog is in alpha (`0.1.0-alpha.1`). Fixes land on `main`. No released version is supported for production use yet.

## Reporting a vulnerability

Please **do not open a public issue**. Report privately through GitHub instead: open the **Security** tab of [lreyesm1999/LatticeLog](https://github.com/lreyesm1999/LatticeLog/security) and choose **Report a vulnerability**.

Include the affected component (SDK, `.llog` reader, ingest protocol, server API, console, MCP), the version or commit, and steps to reproduce.

What to expect:
- acknowledgement within **3 business days**;
- a fix or mitigation for high-severity issues within **30 days**;
- credit in the advisory, unless you prefer to stay anonymous.

## Scope and design principles

- **Secrets**: they are not logged by default. Redaction happens before data is persisted.
- **Untrusted log text**: log text is treated as untrusted in every output. The web console renders it as text only under a strict CSP, and evidence documents mark which fields were copied from logs.
- **External tools**: they get read-only access (MCP, `read` scope).
- **Cryptography**: no home-made cryptography (HMAC-SHA256 cursors, SHA-256 token hashes, TLS required off loopback).

The current threat model and the known gaps are in [`docs/security/threat-model.md`](docs/security/threat-model.md). Known gaps include encryption at rest and an external penetration test, which has not been done yet.
