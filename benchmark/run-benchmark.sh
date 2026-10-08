#!/usr/bin/env bash
# Runs the reference engine over every materialised benchmark unit and scores it with the scanner-benchmark harness.
#
#   benchmark/run-benchmark.sh <units-dir> <harness-dir> <out-dir> [unit ...]
#
#   <units-dir>   directory holding one freestanding clone per unit (training-set-2026/tools/materialize.sh output)
#   <harness-dir> clone of code-assurance-initiative/scanner-benchmark (python3 -m cai_bench must work from there)
#   <out-dir>     where evidence.json / findings.sarif / scores.json / report.json land, one sub-directory per unit
#
# Set NUGET_PACKAGES to a NuGet cache directory to let the licence detector read package nuspecs.
# Prints one summary line per unit (recall / trap resistance / noise) and writes <out-dir>/summary.tsv.
set -euo pipefail
HERE="$(cd "$(dirname "$0")/.." && pwd)"
UNITS="$(cd "$1" && pwd)"; HARNESS="$(cd "$2" && pwd)"; OUT="$(mkdir -p "$3" && cd "$3" && pwd)"; shift 3
DLL="$HERE/src/Cai.Reference/bin/Release/net10.0/Cai.Reference.dll"
[ -f "$DLL" ] || dotnet build "$HERE/src/Cai.Reference/Cai.Reference.csproj" -c Release -nologo -v q
MAPPING="$HERE/benchmark/cai-reference.json"
dotnet "$DLL" mapping --taxonomy "$HARNESS/taxonomy.json" --out "$MAPPING" >/dev/null
# every unit that holds a .NET project (the engine measures C#; TypeScript units are skipped)
if [ $# -eq 0 ]; then set -- $(cd "$UNITS" && for d in */; do d="${d%/}"; find "$d" -name '*.csproj' -not -path '*/node_modules/*' | grep -q . && echo "$d"; done); fi
printf 'unit\trecall\ttrap_resistance\tnoise\tfindings\tbands_in\n' > "$OUT/summary.tsv"
for u in "$@"; do
  [ -f "$UNITS/$u/benchmark/answer-key.json" ] || { echo "skip $u: no answer key"; continue; }
  dotnet "$DLL" scan "$UNITS/$u" --out "$OUT/$u" --quiet ${NUGET_PACKAGES:+--nuget-packages "$NUGET_PACKAGES"} > "$OUT/$u.scan.log" 2>&1 || { echo "FAIL $u: see $OUT/$u.scan.log"; continue; }
  (cd "$HARNESS" && python3 -m cai_bench score --key "$UNITS/$u/benchmark/answer-key.json" --sarif "$OUT/$u/findings.sarif" \
      --mapping "$MAPPING" --scores "$OUT/$u/scores.json" --json "$OUT/$u/report.json" > "$OUT/$u.report.txt" 2>&1) || true
  python3 - "$OUT/$u/report.json" "$u" "$OUT/summary.tsv" <<'INNER'
import json, sys
r = json.load(open(sys.argv[1])); s = r["summary"]
def pct(n, d): return "n/a" if d == 0 else f"{100*n/d:.1f}"
mf = s["tp"] + s["fn"]; traps = s["trapFp"] + s["trapTn"]
noise = "n/a" if s.get("noiseRate") is None else f"{100*s['noiseRate']:.1f}"
bands = r.get("scoreBands", []); inband = sum(1 for b in bands if b["outcome"] == "in"); scored = sum(1 for b in bands if b["outcome"] in ("in", "out"))
line = f"{sys.argv[2]}\t{pct(s['tp'], mf)}\t{pct(s['trapTn'], traps)}\t{noise}\t{s['results']}\t{inband}/{scored} ({len(bands)} bands)"
print(line); open(sys.argv[3], "a").write(line + "\n")
INNER
done
