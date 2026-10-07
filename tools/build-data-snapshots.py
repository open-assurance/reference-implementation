#!/usr/bin/env python3 -I
"""Rebuild the offline data snapshots under data/ (run with network; the scan itself never needs it).
  data/osv-nuget.json   — every OSV advisory for the NuGet ecosystem, trimmed to what matching needs
  data/dotnet-eol.json  — endoflife.date's .NET table
Each snapshot records its source URL, fetch time and the sha256 of the bytes fetched, so a scan can name the data it ran on."""
import hashlib, io, json, os, sys, urllib.request, zipfile, datetime

OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'data')
OSV = 'https://osv-vulnerabilities.storage.googleapis.com/NuGet/all.zip'
EOL = 'https://endoflife.date/api/dotnet.json'
now = datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')

def fetch(url):
    with urllib.request.urlopen(url, timeout=120) as r: return r.read()

raw = fetch(OSV)
advisories = []
with zipfile.ZipFile(io.BytesIO(raw)) as z:
    for name in sorted(z.namelist()):
        if not name.endswith('.json'): continue
        d = json.loads(z.read(name))
        sev = None
        for s in d.get('severity', []) or []:
            if s.get('type') == 'CVSS_V3' or s.get('type') == 'CVSS_V4': sev = s.get('score'); break
        dbsev = (d.get('database_specific') or {}).get('severity')
        affected = []
        for a in d.get('affected', []) or []:
            pkg = (a.get('package') or {})
            if pkg.get('ecosystem') != 'NuGet': continue
            affected.append({
                'package': pkg.get('name'),
                'ranges': [{'type': r.get('type'), 'events': r.get('events', [])} for r in a.get('ranges', []) or []],
                'versions': a.get('versions', []) or [],
            })
        if not affected: continue
        advisories.append({'id': d['id'], 'aliases': d.get('aliases', []) or [], 'summary': d.get('summary', ''),
                           'severity': dbsev, 'cvss': sev, 'withdrawn': d.get('withdrawn'), 'affected': affected})
json.dump({'source': OSV, 'fetchedAt': now, 'sha256': hashlib.sha256(raw).hexdigest(), 'count': len(advisories), 'advisories': advisories},
          open(os.path.join(OUT, 'osv-nuget.json'), 'w'), indent=0, sort_keys=True)
raw2 = fetch(EOL)
eol = json.loads(raw2)
json.dump({'source': EOL, 'fetchedAt': now, 'sha256': hashlib.sha256(raw2).hexdigest(), 'cycles': eol},
          open(os.path.join(OUT, 'dotnet-eol.json'), 'w'), indent=1, sort_keys=True)
print('osv advisories:', len(advisories), 'eol cycles:', len(eol), 'fetched', now)
