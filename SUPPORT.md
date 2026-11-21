# Getting help

LatticeLog is an alpha project with a single maintainer. There is no commercial support and no promised response
time; questions and reports are still very welcome.

| You want to… | Go to |
|---|---|
| Get it running | [Getting started](docs/guides/getting-started.md) and [Troubleshooting](docs/guides/troubleshooting.md) |
| Ask a usage question | Open an issue with the **Question** template |
| Report a bug | Open an issue with the **Bug report** template. Mark data-integrity problems (lost, duplicated or corrupted records) clearly |
| Propose a feature | Open an issue with the **Feature request** template; for larger changes, before writing code |
| Report a vulnerability | **Privately**, as described in [`SECURITY.md`](SECURITY.md). Never in a public issue |
| Contribute | [`CONTRIBUTING.md`](CONTRIBUTING.md) |

A good report includes the version (`latticelog --version` or the commit), the OS, the exact command or code, and,
for storage problems, the output of `latticelog verify <store> --format json`.
