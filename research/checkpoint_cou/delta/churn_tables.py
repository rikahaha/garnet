#!/usr/bin/env python3
"""Tables for the churn sweep (paper_eval.py churn): per write share, steady throughput, throughput during each checkpoint, its
share of steady, max latency, checkpoint durations, and for delta the members changed between checkpoints and how each set was
written. Reads <dir>/run_paper.log and <dir>/churn_delta_read*_delta.csv; prints Markdown.

Usage: churn_tables.py <results_dir> [clients, default 16]
"""
import csv, os, re, sys

d = sys.argv[1]
log = open(os.path.join(d, "run_paper.log")).read()
runs = {}
CLIENTS = sys.argv[2] if len(sys.argv) > 2 else "16"
tag = "" if CLIENTS == "16" else f"_c{CLIENTS}"
for m in re.finditer(r"churn_(baseline|viewpooled|delta)(_c\d+)?_read(\d+): .*?durations ms=\[([^\]]*)\]\n((?:\s+(?:steady|ckpt\d).*\n)+)", log):
    cfg, ctag, read, durs, body = m.groups()
    if (ctag or "") != tag:
        continue
    lines = {}
    for l in body.strip().split("\n"):
        mm = re.match(r"\s*(\w+)\s+([\d.]+) kops.*?p99\.9=\s*(\d+)\s+max=\s*(\d+)", l)
        if mm:
            lines[mm.group(1)] = (float(mm.group(2)), int(mm.group(3)), int(mm.group(4)))
    runs[(cfg, int(read))] = ([int(x) for x in durs.split(",")], lines)

reads = sorted({r for _, r in runs}, reverse=True)
cfgs = [c for c in ("baseline", "viewpooled", "delta") if any((c, r) in runs for r in reads)]
names = {"baseline": "原版", "viewpooled": "视图 + 池化", "delta": "增量（改动列表）"}


def changed(read):
    path = os.path.join(d, f"churn_delta{tag}_read{read}_delta.csv")
    if not os.path.exists(path):
        return ""
    rows = [r for r in csv.DictReader(open(path)) if r["written"].startswith("side_delta")]
    return f"{sum(int(r['changed']) for r in rows) / len(rows) / 1e4:.2f}%" if rows else "—（全部走快照）"


print("### 平时吞吐量（第一次 checkpoint 前，K ops/s）\n")
print("| 写的比例 | 两次 checkpoint 之间改动的成员 | " + " | ".join(names[c] for c in cfgs) + " |")
print("| --- | --- | " + " | ".join("---" for _ in cfgs) + " |")
for r in reads:
    print(f"| {100 - r}% | {changed(r)} | " + " | ".join(f"{runs[(c, r)][1]['steady'][0]:.0f}" for c in cfgs if (c, r) in runs) + " |")

print("\n### checkpoint 期间的吞吐量（K ops/s，第 1–6 次）及占平时的比例\n")
print("| 写的比例 | " + " | ".join(names[c] for c in cfgs) + " |")
print("| --- | " + " | ".join("---" for _ in cfgs) + " |")
for r in reads:
    cells = []
    for c in cfgs:
        if (c, r) not in runs:
            cells.append("")
            continue
        _, l = runs[(c, r)]
        st = l["steady"][0]
        ck = [l[f"ckpt{i}"][0] for i in range(1, 7) if f"ckpt{i}" in l]
        cells.append(" / ".join(f"{x:.0f}" for x in ck) + f"<br>（{min(ck) / st * 100:.0f}–{max(ck) / st * 100:.0f}%）")
    print(f"| {100 - r}% | " + " | ".join(cells) + " |")

print("\n### checkpoint 时长（ms，第 1–6 次）\n")
print("| 写的比例 | " + " | ".join(names[c] for c in cfgs) + " |")
print("| --- | " + " | ".join("---" for _ in cfgs) + " |")
for r in reads:
    print(f"| {100 - r}% | " + " | ".join(" / ".join(map(str, runs[(c, r)][0])) for c in cfgs if (c, r) in runs) + " |")

print("\n### checkpoint 期间的最大延迟（ms，第 1–6 次）\n")
print("| 写的比例 | " + " | ".join(names[c] for c in cfgs) + " |")
print("| --- | " + " | ".join("---" for _ in cfgs) + " |")
for r in reads:
    cells = []
    for c in cfgs:
        if (c, r) in runs:
            l = runs[(c, r)][1]
            cells.append(" / ".join(f"{l[f'ckpt{i}'][2] // 1000}" for i in range(1, 7) if f"ckpt{i}" in l))
    print(f"| {100 - r}% | " + " | ".join(cells) + " |")
