#!/usr/bin/env python3
"""Paper figures for throughput across snapshot checkpoints (cygnus results).

Fig. throughput_timeline: requests/s per 100 ms over a 45 s run with 6 checkpoints (shaded), one panel per configuration;
  16 clients, 50% ZINCRBY / 50% ZSCORE, 20 sorted sets x 1M members (results_cygnus_churn, readpct 50).
Fig. throughput_during_ckpt: throughput during each checkpoint (from its start, over max(its duration, 1 s)) as a share of
  the throughput before the first checkpoint, by write share, one panel per client count (16 / 64 / 128); bar = mean of the
  6 checkpoints, whisker = min..max (results_cygnus_churn, results_cygnus_churn_hi).

Writes PDF (fonts embedded as TrueType) and PNG next to this script.
"""
import csv, json, os, re

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.patches import Patch
from matplotlib.lines import Line2D

HERE = os.path.dirname(os.path.abspath(__file__))
DELTA = os.path.join(HERE, "..", "delta")
CHURN = os.path.join(DELTA, "results_cygnus_churn")
CHURN_HI = os.path.join(DELTA, "results_cygnus_churn_hi")

# Baseline is the muted reference (grey, dashed / hatched); the two methods take the first two categorical hues.
STYLE = {
    "baseline":   {"label": "Garnet (unmodified)",        "color": "#8a8a8a", "ls": "--", "hatch": "////"},
    "viewpooled": {"label": "Member views",               "color": "#eb6834", "ls": "-",  "hatch": ""},
    "delta":      {"label": "Member views + deltas",      "color": "#2a78d6", "ls": "-",  "hatch": ""},
}
CONFIGS = ["baseline", "viewpooled", "delta"]
INK, QUIET, GRID = "#1f1f1f", "#5f5f5f", "#e6e6e6"

plt.rcParams.update({
    "font.family": "DejaVu Sans", "font.size": 8, "axes.labelsize": 8, "axes.titlesize": 8.5,
    "xtick.labelsize": 7.5, "ytick.labelsize": 7.5, "legend.fontsize": 7.5,
    "axes.edgecolor": QUIET, "axes.labelcolor": INK, "xtick.color": QUIET, "ytick.color": QUIET,
    "axes.spines.top": False, "axes.spines.right": False, "axes.linewidth": 0.6,
    "pdf.fonttype": 42, "ps.fonttype": 42, "savefig.dpi": 300,
})


def checkpoint_windows(trace_csv, start_ms):
    """(start s, end s) of each checkpoint relative to the benchmark start."""
    marks = [(int(r["unix_ms"]), r["kind"]) for r in csv.DictReader(open(trace_csv)) if r["kind"] in ("ckpt_prepare", "ckpt_flushed")]
    out, s = [], None
    for t, k in marks:
        if k == "ckpt_prepare":
            s = t
        elif s is not None:
            out.append(((s - start_ms) / 1000, (t - start_ms) / 1000))
            s = None
    return out


def fig_timeline():
    fig, axes = plt.subplots(3, 1, figsize=(7.0, 3.6), sharex=True, sharey=True)
    for ax, cfg in zip(axes, CONFIGS):
        prefix = os.path.join(CHURN, f"churn_{cfg}_read50")
        d = json.load(open(prefix + ".json"))
        tl, step = d["timeline"], 10  # completions per 10 ms
        per100 = [sum(tl[i:i + step]) * 10 / 1e6 for i in range(0, len(tl) - step + 1, step)]  # M ops/s per 100 ms
        xs = [i * 0.1 + 0.05 for i in range(len(per100))]
        for a, b in checkpoint_windows(prefix + "_trace.csv", d["startUnixMs"]):
            ax.axvspan(a, b, color="#d9d9d9", alpha=0.55, lw=0, zorder=0)
        st = STYLE[cfg]
        ax.plot(xs, per100, color=st["color"], lw=1.0, ls="-", zorder=2)
        ax.set_ylim(0, 0.68)
        ax.set_yticks([0, 0.2, 0.4])
        ax.grid(axis="y", color=GRID, lw=0.5, zorder=0)
        ax.text(0.005, 0.98, st["label"], transform=ax.transAxes, ha="left", va="top", color=INK, fontsize=8)
    axes[1].set_ylabel("Throughput (M ops/s)")
    axes[-1].set_xlabel("Time (s)")
    axes[-1].set_xlim(0, d["runS"])
    axes[0].legend(handles=[Patch(facecolor="#d9d9d9", alpha=0.55, label="checkpoint in progress")],
                   loc="upper right", frameon=False, borderaxespad=0.1)
    fig.tight_layout(h_pad=0.4)
    for ext in ("pdf", "png"):
        fig.savefig(os.path.join(HERE, f"throughput_timeline.{ext}"), bbox_inches="tight")
    plt.close(fig)


MIN_WINDOW_S = 1.0


def ckpt_ratios(results_dir, clients):
    """{(config, write%): [ratio of each checkpoint]}: throughput from a checkpoint's start over max(its duration, 1 s),
    as a share of the throughput from 0.5 s to the first BGSAVE. The 1 s floor keeps checkpoints of a few tens of ms
    (deltas with few changes) from being measured over windows too short to mean anything."""
    tag = "" if clients == 16 else f"_c{clients}"
    out = {}
    for cfg in CONFIGS:
        for read in (99, 95, 80, 50, 0):
            prefix = os.path.join(results_dir, f"churn_{cfg}{tag}_read{read}")
            if not os.path.exists(prefix + ".json"):
                continue
            d = json.load(open(prefix + ".json"))
            tl = d["timeline"]
            rate = lambda a, b: sum(tl[int(a * 100):int(b * 100)]) / (int(b * 100) - int(a * 100)) * 100  # ops/s
            steady = rate(0.5, d["saveIssuedMs"][0] / 1000)
            ratios = []
            for a, b in checkpoint_windows(prefix + "_trace.csv", d["startUnixMs"]):
                ratios.append(rate(a, max(b, a + MIN_WINDOW_S)) / steady * 100)
            out[(cfg, 100 - read)] = ratios
    return out


def fig_during_ckpt():
    panels = [(16, CHURN), (64, CHURN_HI), (128, CHURN_HI)]
    writes = [1, 5, 20, 50, 100]
    fig, axes = plt.subplots(1, 3, figsize=(7.0, 2.3), sharey=True)
    width = 0.26
    for ax, (clients, d) in zip(axes, panels):
        r = ckpt_ratios(d, clients)
        for j, cfg in enumerate(CONFIGS):
            st = STYLE[cfg]
            for i, w in enumerate(writes):
                vals = r.get((cfg, w))
                if not vals:
                    continue
                mean = sum(vals) / len(vals)
                x = i + (j - 1) * (width + 0.02)
                ax.bar(x, mean, width, color=st["color"] if cfg != "baseline" else "white", edgecolor=st["color"],
                       hatch=st["hatch"], lw=0.8, zorder=2)
                ax.vlines(x, min(vals), max(vals), color=INK, lw=0.7, zorder=3)
        ax.axhline(100, color=QUIET, lw=0.6, ls=":", zorder=1)
        ax.set_xticks(range(len(writes)))
        ax.set_xticklabels([f"{w}%" for w in writes])
        ax.set_title(f"{clients} clients", color=INK)
        ax.set_xlabel("Write share")
        ax.set_ylim(0, 120)
        ax.set_yticks([0, 25, 50, 75, 100])
        ax.grid(axis="y", color=GRID, lw=0.5, zorder=0)
    axes[0].set_ylabel("Throughput during checkpoint\n(% of normal)")
    handles = [Patch(facecolor="white" if c == "baseline" else STYLE[c]["color"], edgecolor=STYLE[c]["color"],
                     hatch=STYLE[c]["hatch"], label=STYLE[c]["label"]) for c in CONFIGS]
    handles.append(Line2D([0], [0], color=INK, lw=0.7, label="min–max of 6 checkpoints"))
    handles.append(Line2D([0], [0], color=QUIET, lw=0.6, ls=":", label="normal throughput"))
    fig.legend(handles=handles, loc="upper center", ncol=5, frameon=False, bbox_to_anchor=(0.5, 1.07), columnspacing=1.2, handlelength=1.6)
    fig.tight_layout()
    for ext in ("pdf", "png"):
        fig.savefig(os.path.join(HERE, f"throughput_during_ckpt.{ext}"), bbox_inches="tight")
    plt.close(fig)


if __name__ == "__main__":
    fig_timeline()
    fig_during_ckpt()
    print("wrote", [f for f in sorted(os.listdir(HERE)) if f.endswith((".pdf", ".png"))])
