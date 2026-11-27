#!/usr/bin/env python3
"""ckpt-delta step 1: how much smaller would a member-level delta be than the full snapshot write?

Runs a local Garnet with snapshot views (GARNET_SORTEDSET_MEMBER_CHECKPOINT=1) and GARNET_DELTA_TRACE, loads SETS sorted
sets of MEMBERS members, takes a base checkpoint, then for each workload changes some members and takes another
checkpoint. Nothing is written differently: the trace only estimates, per set, the bytes of a full write and of a delta
against the previous checkpoint.

Workloads between two checkpoints (per set):
  uniform:<p>  ZINCRBY a uniform sample (without replacement) of p * MEMBERS existing members, plus p/10 * MEMBERS
               deletes of other members and as many inserts of new members.
  zipf:<p>     p * MEMBERS ZINCRBY operations on members drawn from Zipf(1.0) (hot members repeat).
In both, the script knows exactly which members changed, and checks the server's estimate (changed / deleted counts)
against it: with no writes during a checkpoint the counts must match exactly.

Usage: delta_estimate.py [out_dir]   (env SETS, MEMBERS override sizes)
"""
import csv, os, random, shutil, sys, time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
import correctness_check as cc  # noqa: E402  (server start/stop and RESP client)

SETS = int(os.environ.get("SETS", 10))
MEMBERS = int(os.environ.get("MEMBERS", 100000))
WORKLOADS = ["uniform:0.001", "uniform:0.01", "uniform:0.1", "uniform:0.5", "zipf:0.01", "zipf:0.1", "zipf:1.0"]
OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "local")


def pipeline(c, cmds, batch=1000):
    for i in range(0, len(cmds), batch):
        chunk = cmds[i:i + batch]
        c.send(b"".join(cc.enc(*a) for a in chunk))
        for _ in chunk:
            c.read()


def bgsave_and_wait(c):
    # LASTSAVE has one-second resolution: start the checkpoint in a later second than the previous save, so it changes.
    last = int(c.cmd("LASTSAVE"))
    while int(time.time()) <= last:
        time.sleep(0.05)
    c.cmd("BGSAVE")
    while int(c.cmd("LASTSAVE")) == last:
        time.sleep(0.05)


def zipf_indices(n, k, rng, s=1.0):
    # Inverse-CDF sampling over ranks 1..n; rank r maps to a fixed random member so hot members are spread over ids.
    import bisect
    weights, acc = [], 0.0
    for r in range(1, n + 1):
        acc += 1.0 / r ** s
        weights.append(acc)
    perm = list(range(n))
    rng.shuffle(perm)
    return [perm[bisect.bisect_left(weights, rng.random() * acc)] for _ in range(k)]


def main():
    os.makedirs(OUT, exist_ok=True)
    cc.WORK = os.path.join(OUT, "work")
    shutil.rmtree(cc.WORK, ignore_errors=True)
    os.makedirs(cc.WORK)
    trace = os.path.join(cc.WORK, "delta_trace.csv")
    os.environ["GARNET_SORTEDSET_MEMBER_CHECKPOINT"] = "1"
    os.environ["GARNET_DELTA_TRACE"] = trace
    rng = random.Random(42)

    p, c = cc.start([])
    try:
        live = {s: list(range(MEMBERS)) for s in range(SETS)}  # member indices per set
        next_id = MEMBERS
        for s in range(SETS):
            pipeline(c, [("ZADD", f"z{s}", *[x for i in range(b, min(b + 1000, MEMBERS)) for x in (i, f"m{i}")])
                         for b in range(0, MEMBERS, 1000)], batch=50)
        bgsave_and_wait(c)
        time.sleep(0.2)
        seen = sum(1 for _ in open(trace)) - 1
        results = []
        for wl in WORKLOADS:
            kind, frac = wl.split(":")
            frac = float(frac)
            expect = {}
            for s in range(SETS):
                cur = live[s]
                if kind == "uniform":
                    n_upd, n_del = int(frac * MEMBERS), int(frac * MEMBERS / 10)
                    picked = rng.sample(range(len(cur)), n_upd + n_del)
                    upd = [cur[i] for i in picked[:n_upd]]
                    dels = set(cur[i] for i in picked[n_upd:])
                    ins = list(range(next_id, next_id + n_del))
                    next_id += n_del
                    cmds = [("ZINCRBY", f"z{s}", 1, f"m{m}") for m in upd]
                    cmds += [("ZREM", f"z{s}", f"m{m}") for m in dels]
                    cmds += [("ZADD", f"z{s}", m, f"m{m}") for m in ins]
                    live[s] = [m for m in cur if m not in dels] + ins
                    expect[s] = (n_upd + len(ins), len(dels))
                else:
                    idx = zipf_indices(len(cur), int(frac * MEMBERS), rng)
                    cmds = [("ZINCRBY", f"z{s}", 1, f"m{cur[i]}") for i in idx]
                    expect[s] = (len(set(idx)), 0)
                pipeline(c, cmds)
            bgsave_and_wait(c)
            time.sleep(0.2)
            rows = list(csv.DictReader(open(trace)))[seen:]
            seen += len(rows)
            rows = [r for r in rows if r.get("written", "inline") == "inline"]  # snapshot writes only, not main-log flushes
            assert len(rows) == SETS, f"{wl}: expected {SETS} trace rows, got {len(rows)}"
            got = sorted((int(r["changed"]), int(r["deleted"])) for r in rows)
            want = sorted(expect.values())
            ok = got == want
            full = sum(int(r["full_bytes"]) for r in rows)
            delta = sum(int(r["delta_bytes"]) for r in rows)
            results.append((wl, full, delta, ok))
            print(f"{wl:14s} full {full / 1e6:8.2f} MB  delta {delta / 1e6:8.3f} MB  ratio {delta / full * 100:6.2f}%  "
                  f"counts {'OK' if ok else 'MISMATCH'}", flush=True)
            if not ok:
                print("   got ", got[:5], "\n   want", want[:5])
        with open(os.path.join(OUT, f"delta_estimate_s{SETS}_m{MEMBERS}.csv"), "w") as f:
            f.write("workload,full_bytes,delta_bytes,ratio,counts_ok\n")
            for wl, full, delta, ok in results:
                f.write(f"{wl},{full},{delta},{delta / full:.5f},{ok}\n")
        shutil.copy(trace, os.path.join(OUT, f"delta_trace_s{SETS}_m{MEMBERS}.csv"))
        print("PASS" if all(r[3] for r in results) else "FAIL")
    finally:
        cc.kill(p)
        shutil.rmtree(cc.WORK, ignore_errors=True)


if __name__ == "__main__":
    main()
