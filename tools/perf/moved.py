"""The rows of one runtime's numbers that moved between two versions of docs/src/data/bench.json: a row 15 % slower
or faster, or allocating differently, is checked before it is published (bench.sh --sync, sync-ci.sh).

    python3 -I tools/perf/moved.py <before.json> <after.json> <tfm>
"""
import json
import sys

before, after, runtime = sys.argv[1:4]
old, new = (json.load(open(f))["runtimes"].get(runtime, {}).get("benchmarks", {}) for f in (before, after))
key = lambda r: (r["method"], tuple(sorted(r["params"].items())))
for cls, entry in new.items():
    rows = {key(r): r for r in old.get(cls, {}).get("rows", [])}
    for r in entry["rows"]:
        o = rows.get(key(r))
        if o is None or not o["meanNs"]:
            continue
        d = r["meanNs"] / o["meanNs"] - 1
        alloc = o["allocatedBytes"] != r["allocatedBytes"]
        if abs(d) > 0.15 or alloc:
            params = ",".join(f"{k}={v}" for k, v in key(r)[1])
            print(f"  {runtime} {cls}.{r['method']}({params}) {o['meanNs']:.0f} -> {r['meanNs']:.0f} ns {d:+.0%}"
                  + (f", {o['allocatedBytes']} -> {r['allocatedBytes']} B" if alloc else ""))
for cls in old:
    if cls not in new:
        print(f"  {runtime} {cls}: gone")
