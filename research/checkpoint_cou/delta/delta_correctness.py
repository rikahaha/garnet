#!/usr/bin/env python3
"""ckpt-delta: end-to-end correctness of sorted set checkpoints through the side store (stubs + chain files).

Runs the local Garnet build with GARNET_SORTEDSET_MEMBER_CHECKPOINT=1 GARNET_SORTEDSET_DELTA=1 and GARNET_DELTA_TRACE.

  chain     Scenario C of correctness_check.py with 7 checkpoints (one writer through all of them, kill -9, recover without AOF):
            the recovered state must be a consistent prefix. Also checks that deltas were written, that a chain was cut by a
            full file after 4 deltas, and that afterwards only the chain referenced by the last checkpoint is left.
  restart   Checkpoints with deltas, DEL and re-create a set, checkpoint, kill, recover; then write, checkpoint three more times
            (inline, full chain files, deltas), kill, recover again. No writes after each final checkpoint, so the recovered
            contents must equal the contents at that checkpoint exactly. Files of the deleted set must be gone.
  crash     Kill -9 at random moments while a checkpoint with deltas is in flight (no writes after BGSAVE): recovery must give
            exactly the state of the previous or of the in-flight checkpoint.
  aof       Scenario B of correctness_check.py (writes during a checkpoint, then checkpoint + AOF replay must match exactly).

Usage: delta_correctness.py [chain restart crash aof]
"""
import csv, glob, os, random, shutil, subprocess, sys, time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
import correctness_check as cc  # noqa: E402

cc.WORK = os.path.join(HERE, "local", "work_correctness")
TRACE = os.path.join(HERE, "local", "delta_correctness_trace.csv")
os.environ.update(GARNET_SORTEDSET_MEMBER_CHECKPOINT="1", GARNET_SORTEDSET_DELTA="1", GARNET_DELTA_TRACE=TRACE)


def fresh():
    shutil.rmtree(cc.WORK, ignore_errors=True)
    os.makedirs(cc.WORK)
    if os.path.exists(TRACE):
        os.remove(TRACE)


def trace_rows():
    return list(csv.DictReader(open(TRACE))) if os.path.exists(TRACE) else []


def side_files():
    """(uid dir, file name) of every chain file under the work directory."""
    out = []
    for d in glob.glob(os.path.join(cc.WORK, "**", "sortedset-members", "*"), recursive=True):
        out += [(os.path.basename(d), f) for f in sorted(os.listdir(d))]
    return sorted(out)


def bgsave(c):
    last = int(c.cmd("LASTSAVE"))
    while int(time.time()) <= last:
        time.sleep(0.05)
    c.cmd("BGSAVE")
    while int(c.cmd("LASTSAVE")) == last:
        time.sleep(0.02)


def contents(c, sets):
    out = {}
    for s in sets:
        items = c.cmd("ZRANGE", s, 0, -1, "WITHSCORES")
        out[s] = sorted(zip(items[0::2], items[1::2]))
    return out


def load(c, key, n, prefix="m"):
    for b in range(0, n, 1000):
        c.cmd("ZADD", key, *[x for i in range(b, min(b + 1000, n)) for x in (i, f"{prefix}{i}")])


def mutate(c, rng, sets, members, n_ops):
    cmds = []
    for _ in range(n_ops):
        s = rng.choice(sets)
        r = rng.random()
        m = f"m{rng.randrange(members)}"
        if r < 0.6:
            cmds.append(("ZINCRBY", s, 1, m))
        elif r < 0.8:
            cmds.append(("ZREM", s, m))
        else:
            cmds.append(("ZADD", s, rng.random(), f"x{rng.randrange(10**9)}"))
    for i in range(0, len(cmds), 500):
        chunk = cmds[i:i + 500]
        c.send(b"".join(cc.enc(*a) for a in chunk))
        for _ in chunk:
            c.read()


def check(name, cond, detail=""):
    print(f"  {'ok ' if cond else 'FAIL'} {name}{(': ' + detail) if detail else ''}")
    return cond


def scenario_chain():
    fresh()
    os.environ["GARNET_SORTEDSET_DELTA_MIN"] = "10000"
    ok = cc.scenario_c(rounds=7)
    rows = [r for r in trace_rows() if r["written"].startswith("side_")]
    kinds = [r["written"] for r in rows]
    by_set = {}
    for r in rows:
        by_set.setdefault(r["object"], []).append(r["written"])
    kinds = [k.replace("side_delta_list", "side_delta") for k in kinds]  # change-list reads count as deltas
    by_set = {o: [k.replace("side_delta_list", "side_delta") for k in v] for o, v in by_set.items()}
    ok &= check("deltas written", kinds.count("side_delta") > 0, f"{kinds.count('side_full')} full, {kinds.count('side_delta')} delta files")
    cut = any(seq[:6] == ["side_full"] + ["side_delta"] * 4 + ["side_full"] for seq in by_set.values())
    ok &= check("chain cut by a full file after 4 deltas", cut, str(next(iter(by_set.values()), [])))
    files = side_files()
    per_uid = {}
    for uid, f in files:
        per_uid.setdefault(uid, []).append(f)
    # After the last recovery, each set keeps exactly its chain (full + deltas) and no other store directory is left.
    ok &= check("only referenced chains left", len(per_uid) == cc.SETS and all(len(v) <= 5 for v in per_uid.values()),
                f"{len(per_uid)} stores, files per store {sorted(set(len(v) for v in per_uid.values()))}")
    return ok


def scenario_restart():
    fresh()
    os.environ["GARNET_SORTEDSET_DELTA_MIN"] = "1000"
    sets, members = [f"r{i}" for i in range(4)], 20000
    rng = random.Random(3)
    p, c = cc.start([])
    for s in sets:
        load(c, s, members)
    bgsave(c)
    mutate(c, rng, sets, members, 400)
    bgsave(c)
    uids_before = {u for u, _ in side_files()}
    c.cmd("DEL", "r0")
    load(c, "r0", members, prefix="q")
    mutate(c, rng, sets, members, 400)
    bgsave(c)
    expected = contents(c, sets)
    cc.kill(p)
    p, c = cc.start(["--recover"])
    ok = check("recover after DEL + re-create", contents(c, sets) == expected)
    uids_after = {u for u, _ in side_files()}
    # The re-created set is all new members, so it was written inline (no chain); the deleted set's chain must be gone.
    ok &= check("files of the deleted set removed", len(uids_after) <= len(sets) and len(uids_before - uids_after) == 1,
                f"{len(uids_before)} stores before, {len(uids_after)} after, {len(uids_before - uids_after)} gone")

    # After recovery every member counts as new (high change), so the first checkpoint writes the sets inline into the snapshot;
    # the next one starts a chain with full files, and the one after writes deltas.
    kinds = []
    for _ in range(3):
        n_rows = len(trace_rows())
        mutate(c, rng, sets, members, 400)
        bgsave(c)
        kinds.append(sorted({r["written"] for r in trace_rows()[n_rows:] if r["written"] not in ("inline_mainlog", "written")}))
    ok &= check("after recovery: inline, then full chain files, then deltas",
                kinds[0] == ["inline"] and kinds[1] == ["side_full"] and len(kinds[2]) == 1 and kinds[2][0].startswith("side_delta"), str(kinds))
    expected = contents(c, sets)
    cc.kill(p)
    p, c = cc.start(["--recover"])
    ok &= check("recover again", contents(c, sets) == expected)
    cc.kill(p)
    print(f"restart (DEL/re-create, recovery, checkpoints after recovery): {'PASS' if ok else 'FAIL'}")
    return ok


def scenario_crash(trials=8):
    os.environ["GARNET_SORTEDSET_DELTA_MIN"] = "1000"
    sets, members = [f"k{i}" for i in range(20)], 100000
    rng = random.Random(5)
    ok = True
    outcomes = []
    for t in range(trials):
        fresh()
        p, c = cc.start([])
        for s in sets:
            load(c, s, members)
        bgsave(c)
        mutate(c, rng, sets, members, 3000)
        bgsave(c)
        before = contents(c, sets)
        mutate(c, rng, sets, members, 3000)
        after = contents(c, sets)
        last = int(c.cmd("LASTSAVE"))
        while int(time.time()) <= last:
            time.sleep(0.05)
        c.cmd("BGSAVE")
        time.sleep(rng.uniform(0.0, 0.15))
        cc.kill(p)
        p, c = cc.start(["--recover"])
        got = contents(c, sets)
        cc.kill(p)
        which = "previous" if got == before else "in-flight" if got == after else "NEITHER"
        outcomes.append(which)
        ok &= which != "NEITHER"
    print(f"  outcomes: {outcomes}")
    print(f"crash (kill -9 during a checkpoint with deltas, {trials} trials): {'PASS' if ok else 'FAIL'}")
    return ok


def scenario_aof():
    fresh()
    os.environ["GARNET_SORTEDSET_DELTA_MIN"] = "10000"
    return cc.scenario_b()


if __name__ == "__main__":
    which = sys.argv[1:] or ["chain", "restart", "crash", "aof"]
    ok = True
    try:
        for name in which:
            print(f"== {name}", flush=True)
            ok &= {"chain": scenario_chain, "restart": scenario_restart, "crash": scenario_crash, "aof": scenario_aof}[name]()
    finally:
        # Never leave a server behind on the port (a later run would talk to it).
        subprocess.run(["pkill", "-9", "-f", f"GarnetServer.*port {cc.PORT}"])
    shutil.rmtree(cc.WORK, ignore_errors=True)
    print("ALL PASS" if ok else "SOME FAILED")
    sys.exit(0 if ok else 1)
