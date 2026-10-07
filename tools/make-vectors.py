#!/usr/bin/env python3
"""Turn evidence bundles into conformance vectors by recording what the published `cai` CLI (black-box oracle) says.
Usage: make-vectors.py --oracle <cai.dll> --rubrics <archive> --out <dir> <bundle.json>...
Each vector: { "evidence": {...}, "expected": { "cai", "band", "lenses": { lens: { score, band, gated, items, weight } } } }
or { "evidence": {...}, "expectedError": true }. Scores at 1 dp, weights at 3 dp — the oracle's display precision."""
import json, os, re, subprocess, sys
sys.path.insert(0, os.path.dirname(__file__))
from importlib import util
spec = util.spec_from_file_location('oc', os.path.join(os.path.dirname(__file__), 'oracle-compare.py'))
args = sys.argv[1:]
def opt(n):
    i = args.index(n); v = args[i+1]; del args[i:i+2]; return v
oracle, rubrics, out = opt('--oracle'), opt('--rubrics'), opt('--out')
def parse(o):
    m = re.search(r'CAI\s+([\d.]+)\s+\((\w+)\)', o)
    if not m: return None
    lenses = {}
    for lm in re.finditer(r'^\s{2}(\w+)\s+([\d.]+)\s+(\w+)(\*?)\s+(\d+)\s+([\d.]+)\s+([\d.]+)\s*$', o, re.M):
        lenses[lm.group(1)] = dict(score=float(lm.group(2)), band=lm.group(3), gated=lm.group(4) == '*', items=int(lm.group(5)), weight=float(lm.group(6)))
    return dict(cai=float(m.group(1)), band=m.group(2), lenses=lenses)
for f in args:
    p = subprocess.run(['dotnet', oracle, 'score', f, '--rubrics', rubrics], capture_output=True, text=True)
    ev = json.load(open(f))
    name = os.path.splitext(os.path.basename(f))[0]
    vec = {'source': 'oracle: cai score (published CLI), 2026-10-07', 'evidence': ev}
    if p.returncode != 0: vec['expectedError'] = True; vec['oracleMessage'] = (p.stdout + p.stderr).strip()[:300]
    else: vec['expected'] = parse(p.stdout)
    json.dump(vec, open(os.path.join(out, name + '.json'), 'w'), indent=1)
    print(name, 'error' if 'expectedError' in vec else vec['expected']['cai'])
