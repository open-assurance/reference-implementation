## What and why

Closes #

## How it was tested

- [ ] `dotnet test Cai.Reference.slnx` is green
- [ ] new or changed detectors have a must-fire and a near-miss test
- [ ] benchmark numbers for affected units (recall / trap resistance / noise), if detectors changed:

## Checklist

- [ ] every commit is signed off (`git commit -s`, DCO)
- [ ] implemented from the published CAI text only; no other engine's source was read (clean-room rule)
- [ ] ambiguities recorded in SPEC-GAPS.md; COVERAGE.md regenerated if coverage changed (`python3 tools/coverage-md.py`)
- [ ] scan output stays deterministic (no timestamps, network, culture-dependent formatting)
