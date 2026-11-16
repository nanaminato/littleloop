"""Summarize observed counters without converting an unfinished run into a pass."""
import json
from pathlib import Path
import statistics
import sys

root = Path(sys.argv[1])
state = json.loads((root / 'status.json').read_text(encoding='utf-8'))
rows = []
for line in (root / 'metrics.jsonl').read_text(encoding='utf-8').splitlines():
    try: rows.append(json.loads(line))
    except json.JSONDecodeError: pass  # The collector may currently be appending its final line.
groups = []
for row in rows:
    if not groups or row['pid'] != groups[-1][-1]['pid'] or row['processAgeSeconds'] < groups[-1][-1]['processAgeSeconds']:
        groups.append([])
    groups[-1].append(row)

def trend(samples, field):
    warmed = [s for s in samples if s['processAgeSeconds'] >= 300]
    if len(warmed) < 10: return None
    x = [s['processAgeSeconds'] / 3600 for s in warmed]
    y = [s[field] for s in warmed]
    mx, my = statistics.mean(x), statistics.mean(y)
    denominator = sum((v-mx)**2 for v in x)
    return dict(firstMedian=statistics.median(y[:10]), lastMedian=statistics.median(y[-10:]),
                slopePerHour=sum((a-mx)*(b-my) for a,b in zip(x,y))/denominator if denominator else None)

summary = dict(status=state, samples=len(rows), cohorts=[dict(pid=g[0]['pid'],
    observedSeconds=g[-1]['processAgeSeconds'], samples=len(g),
    workingSetPeak=max(s['workingSetBytes'] for s in g), privatePeak=max(s['privateBytes'] for s in g),
    handlesPeak=max(s['handles'] for s in g), privateTrend=trend(g, 'privateBytes'),
    handleTrend=trend(g, 'handles')) for g in groups],
    maximumObservationGapSeconds=max((b['monotonic']-a['monotonic'] for a,b in zip(rows,rows[1:])), default=0),
    finalEvidenceBytes=rows[-1]['evidenceBytes'] if rows else 0,
    finalLogBytes=rows[-1]['logBytes'] if rows else 0,
    childPeaks={name: dict(workingSetBytes=max(c['workingSetBytes'] for r in rows for c in r.get('children',[]) if c['name']==name),
        privateBytes=max(c['privateBytes'] for r in rows for c in r.get('children',[]) if c['name']==name),
        handles=max(c['handles'] for r in rows for c in r.get('children',[]) if c['name']==name))
        for name in {c['name'] for r in rows for c in r.get('children',[])}})
(root / 'resource-summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(summary,ensure_ascii=False,indent=2))
