#!/usr/bin/env python3
"""Summarize throughput_run.py results: throughput and latency outside and during checkpoints.

usage: throughput_analyze.py <out_prefix> [...]

Checkpoint windows come from the server trace (ckpt_prepare .. ckpt_flushed). "steady" is the run before the first BGSAVE
and from 1 s after the last checkpoint ended. Latency percentiles use the 100 ms histogram buckets that overlap the window
(5% bins, so values are accurate to about 5%); max is exact.
"""
import csv, json, math, sys

def load(prefix):
    d = json.load(open(f"{prefix}.json"))
    marks = {"ckpt_prepare": [], "ckpt_flushed": []}
    for row in csv.DictReader(open(f"{prefix}_trace.csv")):
        if row["kind"] in marks: marks[row["kind"]].append(int(row["unix_ms"]) - d["startUnixMs"])
    d["ckpts"] = list(zip(marks["ckpt_prepare"], marks["ckpt_flushed"]))
    return d

def summarize(d, windows):
    tl, step = d["timeline"], d["timelineMs"]
    ops = sum(tl[i] for a, b in windows for i in range(int(a // step), min(len(tl), int(math.ceil(b / step)))))
    secs = sum(b - a for a, b in windows) / 1000
    bins, mx = {}, 0.0
    for i, h in enumerate(d["hist"]):
        t0, t1 = i * d["histMs"], (i + 1) * d["histMs"]
        if any(t0 < b and t1 > a for a, b in windows):
            mx = max(mx, h["maxUs"])
            for k, v in h["bins"].items(): bins[int(k)] = bins.get(int(k), 0) + v
    total = sum(bins.values())
    def q(p):
        acc = 0
        for k in sorted(bins):
            acc += bins[k]
            if acc >= p * total: return d["binBase"] ** (k + 1)
        return 0
    # Lowest throughput over any 100 ms inside the windows.
    per = d["histMs"] // step
    lows = [sum(tl[j:j + per]) * 1000 / d["histMs"] for a, b in windows for j in range(int(a // step), int(b // step) - per + 1, per)]
    return dict(kops=ops / secs / 1000 if secs else 0, p50=q(.5), p99=q(.99), p999=q(.999), max=mx,
                min100ms_kops=min(lows) / 1000 if lows else None)

def main():
    for prefix in sys.argv[1:]:
        d = load(prefix)
        run_ms = d["runS"] * 1000
        steady = [(500, d["saveIssuedMs"][0])] + ([(d["ckpts"][-1][1] + 1000, run_ms)] if d["ckpts"] else [])
        print(f"{prefix}: clients={d['clients']} pipeline={d['pipeline']} readpct={d['readPct']} "
              f"ckpt durations ms={[b - a for a, b in d['ckpts']]}")
        for name, w in [("steady", steady)] + [(f"ckpt{i + 1}", [c]) for i, c in enumerate(d["ckpts"])]:
            s = summarize(d, w)
            low = f" min100ms={s['min100ms_kops']:.0f}k" if s["min100ms_kops"] is not None else ""
            print(f"  {name:7} {s['kops']:8.1f} kops/s{low}  p50={s['p50']:7.0f} p99={s['p99']:8.0f} "
                  f"p99.9={s['p999']:8.0f} max={s['max']:9.0f} us")

if __name__ == "__main__":
    main()
