#!/usr/bin/env python3
"""Render the benchmark run in <out-dir> (from run-benchmark.sh) as the generated tables of RESULTS.md.

    benchmark/results-md.py <out-dir> [RESULTS.md]

Replaces everything between the GENERATED markers in RESULTS.md (or prints the fragment when no file is given).
Rates follow docs/CONTRACT.md: recall = TP / must-fire, trap resistance = traps left alone / traps, noise = the harness's
noiseRate (findings on traps, on certified-clean code, on not-applicable concepts, or matching nothing the key expects,
over the findings of concepts the key covers)."""
import json, os, sys

def pct(n, d): return "–" if not d else f"{100 * n / d:.0f} %"
def rate(v): return "–" if v is None else f"{100 * v:.0f} %"

def unit_rows(out):
    rows = []
    for u in sorted(os.listdir(out)):
        rp = os.path.join(out, u, "report.json")
        if not os.path.isfile(rp): continue
        r = json.load(open(rp)); s = r["summary"]
        bands = r.get("scoreBands", []); inband = sum(1 for b in bands if b["outcome"] == "in"); scored = sum(1 for b in bands if b["outcome"] in ("in", "out"))
        rows.append((u, s, r, bands, inband, scored))
    return rows

def dim_table(rows):
    agg = {}
    for u, s, r, *_ in rows:
        for dim, d in r["dimensions"].items():
            a = agg.setdefault(dim, dict(tp=0, fn=0, fp=0, trapFp=0, trapTn=0, noise=0, results=0, units=set()))
            for k in ("tp", "fn", "fp", "trapFp", "trapTn", "noise", "results"): a[k] += d.get(k, 0)
            if d.get("tp") or d.get("fn") or d.get("results"): a["units"].add(u.replace("bench-csharp-", "").replace("estate-quellbrook-", "estate/"))
    lines = ["| Dimension | Planted | Found | Recall | Traps | Resisted | Findings | Noise | Units |", "|---|---|---|---|---|---|---|---|---|"]
    for dim, a in sorted(agg.items(), key=lambda kv: (kv[0].rstrip("0123456789"), int("".join(c for c in kv[0] if c.isdigit()) or 0))):
        if not (a["tp"] or a["fn"] or a["results"] or a["trapFp"]): continue
        planted = a["tp"] + a["fn"]; traps = a["trapFp"] + a["trapTn"]
        lines.append(f"| {dim} | {planted} | {a['tp']} | {pct(a['tp'], planted)} | {traps} | {pct(a['trapTn'], traps)} | {a['results']} | {pct(a['noise'], a['results'])} | {', '.join(sorted(a['units']))} |")
    return "\n".join(lines)

def fragment(out):
    rows = unit_rows(out)
    lines = ["| Unit | Planted | Found | Recall | Traps | Trap resistance | Findings | Noise | Score bands in range |", "|---|---|---|---|---|---|---|---|---|"]
    T = dict(tp=0, fn=0, trapFp=0, trapTn=0, noise=0, results=0)
    for u, s, r, bands, inband, scored in rows:
        planted = s["tp"] + s["fn"]; traps = s["trapFp"] + s["trapTn"]
        for k in T: T[k] += s.get(k, 0)
        lines.append(f"| `{u}` | {planted} | {s['tp']} | {pct(s['tp'], planted)} | {traps} | {pct(s['trapTn'], traps)} | {s['results']} | {rate(s.get('noiseRate'))} | {inband}/{scored} ({len(bands)} labelled) |")
    planted = T["tp"] + T["fn"]; traps = T["trapFp"] + T["trapTn"]
    lines.append(f"| **all C# units** | {planted} | {T['tp']} | {pct(T['tp'], planted)} | {traps} | {pct(T['trapTn'], traps)} | {T['results']} | {pct(T['noise'], T['results'])} | |")
    misses = []
    for u, s, r, *_ in rows:
        for e in r["entries"]:
            if e["outcome"] == "FN": misses.append(f"| `{u}` | {e['id']} | `{e.get('concept')}` | {e.get('file') or '(repository)'} |")
            if e["outcome"] == "FP": misses.append(f"| `{u}` | {e['id']} ({e['label']}) | `{(e.get('concept') if isinstance(e.get('concept'), str) else 'clean file')}` | {e.get('file') or '(repository)'}: {', '.join(sorted({x['ruleId'] for x in e['results']}))} |")
        for x in r["results"]:
            if x["outcome"] == "unmatched-fp": misses.append(f"| `{u}` | unmatched | `{x['ruleId']}` | {x.get('file') or '(repository)'}:{x.get('line') or ''} |")
    return "\n".join(["### Per unit", "", *lines, "", "### Per dimension (all C# units)", "", dim_table(rows), "", "### Every miss and every noise finding", "", "| Unit | Entry | Concept | Where |", "|---|---|---|---|", *misses])

if __name__ == "__main__":
    frag = fragment(sys.argv[1])
    if len(sys.argv) < 3: print(frag); sys.exit(0)
    path = sys.argv[2]; text = open(path).read()
    a, b = "<!-- GENERATED:BEGIN -->", "<!-- GENERATED:END -->"
    i, j = text.index(a) + len(a), text.index(b)
    open(path, "w").write(text[:i] + "\n" + frag + "\n" + text[j:])
    print(f"updated {path}")
