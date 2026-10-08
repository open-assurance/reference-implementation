# Security policy

## Reporting a vulnerability

Report vulnerabilities **privately** through GitHub Security Advisories:
[Report a vulnerability](https://github.com/open-assurance/reference-implementation/security/advisories/new). Please do
not open a public issue or discussion for a vulnerability.

Include what is affected (version or commit), how to reproduce it, and the impact you expect. The maintainers will
acknowledge the report within 7 days, keep you informed in the advisory, and agree a disclosure date with you once a fix
is ready (normally within 90 days). Credit is given in the advisory unless you prefer otherwise.

## Scope

The engine reads untrusted repositories. In scope: anything that lets a scanned repository execute code, write outside
the output directory, read files outside the scanned tree, exhaust resources disproportionately, or make the engine
use the network. Wrong findings or scores are bugs, not vulnerabilities — open an issue for those.

Only the latest commit on `main` is supported.
