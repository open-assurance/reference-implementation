#!/usr/bin/env python3
"""Compare this implementation's fold with the published `cai` CLI (black-box oracle) over a set of evidence bundles.
Usage: oracle-compare.py --oracle <cai.dll> --rubrics <archive> --mine <cai-ref dll> <bundle.json>...
Prints one line per bundle: MATCH / DIFF / BOTH-ERROR / ERROR-MISMATCH; exit 1 on any DIFF or mismatch."""
import json, re, subprocess, sys

def run(cmd):
    p = subprocess.run(cmd, capture_output=True, text=True)
    return p.returncode, p.stdout + p.stderr

def parse_oracle(out):
    m = re.search(r'CAI\s+([\d.]+)\s+\((\w+)\)', out)
    if not m: return None
    lenses = {}
    for lm in re.finditer(r'^\s{2}(\w+)\s+([\d.]+)\s+(\w+)(\*?)\s+(\d+)\s+([\d.]+)\s+([\d.]+)\s*$', out, re.M):
        lenses[lm.group(1)] = dict(score=float(lm.group(2)), band=lm.group(3), gated=lm.group(4) == '*', dims=int(lm.group(5)), weight=float(lm.group(6)), contrib=float(lm.group(7)))
    return dict(cai=float(m.group(1)), band=m.group(2), lenses=lenses)

def main():
    args = sys.argv[1:]
    def opt(n):
        i = args.index(n); v = args[i+1]; del args[i:i+2]; return v
    oracle, rubrics, mine = opt('--oracle'), opt('--rubrics'), opt('--mine')
    bad = 0
    for f in args:
        oc, oo = run(['dotnet', oracle, 'score', f, '--rubrics', rubrics])
        mc, mo = run(['dotnet', mine, 'score', f, '--rubrics', rubrics, '--json'])
        if oc != 0 and mc != 0:
            print(f'BOTH-ERROR  {f}'); continue
        if (oc != 0) != (mc != 0):
            print(f'ERROR-MISMATCH {f}\n  oracle: {oo.strip()[:200]}\n  mine:   {mo.strip()[:200]}'); bad += 1; continue
        o = parse_oracle(oo); m = json.loads(mo)
        diffs = []
        if abs(o['cai'] - m['caiExact']) > 0.051: diffs.append(f"cai {o['cai']} vs {m['cai']}")
        if o['band'] != m['band']: diffs.append(f"band {o['band']} vs {m['band']}")
        ml = {l['lens']: l for l in m['lenses']}
        if set(o['lenses']) != set(ml): diffs.append(f"lenses {sorted(o['lenses'])} vs {sorted(ml)}")
        for k, ol in o['lenses'].items():
            if k not in ml: continue
            l = ml[k]
            if abs(ol['score'] - round(l['score'], 1)) > 0.051: diffs.append(f"{k} score {ol['score']} vs {l['score']}")
            band = l['band']
            if ol['band'] != band: diffs.append(f"{k} band {ol['band']} vs {band}")
            if ol['gated'] != l['criticalGated']: diffs.append(f"{k} gated {ol['gated']} vs {l['criticalGated']}")
            if ol['dims'] != l['itemCount']: diffs.append(f"{k} items {ol['dims']} vs {l['itemCount']}")
            if abs(ol['weight'] - round(l['weight'], 3)) > 0.0011: diffs.append(f"{k} weight {ol['weight']} vs {l['weight']}")
            if abs(ol['contrib'] - round(l['contribution'], 2)) > 0.011: diffs.append(f"{k} contrib {ol['contrib']} vs {l['contribution']}")
        if diffs: bad += 1; print(f'DIFF        {f}\n  ' + '\n  '.join(diffs))
        else: print(f'MATCH       {f}')
    sys.exit(1 if bad else 0)

main()
