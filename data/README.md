# Offline data snapshots

The scan never touches the network. Everything it needs that is not in the repository under measurement is here,
dated and hashed, so the same repository at the same commit with the same engine build and the same snapshots folds
to byte-identical output. Rebuild with `python3 -I tools/build-data-snapshots.py` (needs network); a rebuild is the
"newly published security advisory" reason a score is allowed to move (codeassuranceindex.info/rubric).

| File | Source | Used by |
|---|---|---|
| `osv-nuget.json` | https://osv-vulnerabilities.storage.googleapis.com/NuGet/all.zip (every OSV advisory for NuGet, trimmed to id, aliases, summary, severity, affected ranges/versions) | D30 Dependency Vulnerabilities, D43 Malicious Dependencies, D12 |
| `dotnet-eol.json` | https://endoflife.date/api/dotnet.json | D44 Platform End-of-Life |
| `licence-policy.json` | hand-written: SPDX ids treated as copyleft under the default policy | D14 License Compliance |

Each file records `source`, `fetchedAt` and the `sha256` of the fetched bytes; the evidence bundle's `engine.snapshots`
repeats them.
