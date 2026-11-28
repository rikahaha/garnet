#!/usr/bin/env python3
"""ckpt-delta: recovery time vs delta chain length.

For each configuration, start Garnet, load SETS sorted sets of MEMBERS members and run the throughput benchmark (16 clients,
50% ZINCRBY on uniform random members) with BGSAVE at 3 s intervals, so that the last checkpoint's chains have 0..4 deltas
(the first checkpoint writes full files). Then kill -9 and time a restart with --recover until the server answers, and check
every set has MEMBERS members. An empty restart is timed as the fixed startup cost.

  baseline  plain Garnet (members inline in the snapshot)
  delta     snapshot views + pooled bytes + side store chains

Usage: recovery_time.py [out_csv]   (env SETS, MEMBERS, REPS, MEM = log memory limit, default 32g)

MEM defaults to 32g: without --storage-tier Garnet is a cache (NullDevice main log), and with -m 8g this load evicts whole
sorted sets after ~3 checkpoints (see ../upstream_bug/SORTEDSET_LOSS_UNDER_MEMORY_LIMIT.md).
"""
import os, shutil, signal, socket, subprocess, sys, time

HERE = os.path.dirname(os.path.abspath(__file__))
DOTNET = "/usr/local/share/dotnet/dotnet"
GARNET = os.path.join(HERE, "../../../main/GarnetServer/bin/Release/net10.0/GarnetServer.dll")
BENCH = os.path.join(HERE, "../throughput/bin/Release/net10.0/ThroughputBench.dll")
PORT = 6399
SETS = int(os.environ.get("SETS", 20))
MEMBERS = int(os.environ.get("MEMBERS", 1000000))
REPS = int(os.environ.get("REPS", 1))
WORK = os.path.join(HERE, "local", "work_recovery")
OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "local", f"recovery_s{SETS}_m{MEMBERS}.csv")

CONFIGS = {
    "baseline": {"GARNET_SORTEDSET_MEMBER_CHECKPOINT": "0", "TSAVORITE_SERIALIZE_POOLED": "0", "GARNET_SORTEDSET_DELTA": "0"},
    "delta": {"GARNET_SORTEDSET_MEMBER_CHECKPOINT": "1", "TSAVORITE_SERIALIZE_POOLED": "1", "GARNET_SORTEDSET_DELTA": "1"},
}


def resp(*args):
    out = [b"*%d\r\n" % len(args)]
    for a in args:
        a = str(a).encode()
        out.append(b"$%d\r\n%s\r\n" % (len(a), a))
    return b"".join(out)


def command(*args):
    with socket.create_connection(("127.0.0.1", PORT)) as s:
        s.sendall(resp(*args))
        return s.makefile("rb").readline().strip()


def start(env, recover):
    log = open(os.path.join(WORK, "server.log"), "a")
    args = [DOTNET, GARNET, "--port", str(PORT), "--checkpointdir", os.path.join(WORK, "ckpt"), "--device-type", "RandomAccess",
            "--logger-level", "Information", "-m", os.environ.get("MEM", "32g"), "-i", "64m"] + (["--recover"] if recover else [])
    t0 = time.time()
    p = subprocess.Popen(args, stdout=log, stderr=subprocess.STDOUT, env={**os.environ, **env, "DOTNET_ROOT": "/usr/local/share/dotnet"})
    while True:
        try:
            if command("PING") == b"+PONG":
                return p, time.time() - t0
        except OSError:
            if p.poll() is not None:
                raise RuntimeError("server exited; see " + os.path.join(WORK, "server.log"))
            time.sleep(0.01)


def kill(p):
    p.send_signal(signal.SIGKILL)
    p.wait()


def side_files():
    """Total bytes, and the set of chain file counts per store."""
    total, per_store = 0, set()
    for root, _, files in os.walk(os.path.join(WORK, "ckpt")):
        if "sortedset-members" in root and files:
            total += sum(os.path.getsize(os.path.join(root, f)) for f in files)
            per_store.add(len(files))
    return total, per_store


def run(config, deltas):
    shutil.rmtree(WORK, ignore_errors=True)
    os.makedirs(WORK)
    env = CONFIGS[config]
    p, _ = start(env, recover=False)
    try:
        saves = [1 + 3 * i for i in range(deltas + 1)]
        subprocess.run([DOTNET, BENCH, f"port={PORT}", f"sets={SETS}", f"members={MEMBERS}", "clients=16", "pipeline=1", "readpct=50",
                        f"run={saves[-1] + 3}", "saveat=" + ",".join(map(str, saves))],
                       stdout=subprocess.DEVNULL, check=True, env={**os.environ, "DOTNET_ROOT": "/usr/local/share/dotnet"})
        # The last checkpoint must have completed: LASTSAVE moves at each one; wait a little for the last.
        time.sleep(1.0)
    finally:
        kill(p)
    p, secs = start(env, recover=True)
    try:
        counts = {int(command("ZCARD", f"z{s}").lstrip(b":")) for s in range(SETS)}
    finally:
        kill(p)
    if counts != {MEMBERS}:
        raise RuntimeError(f"{config} deltas={deltas}: recovered set sizes {counts}")
    return (secs, *side_files())


def main():
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    shutil.rmtree(WORK, ignore_errors=True)
    os.makedirs(WORK)
    p, empty = start(CONFIGS["baseline"], recover=False)
    kill(p)
    print(f"empty startup {empty:.2f} s", flush=True)
    with open(OUT, "w") as f:
        f.write("config,deltas,rep,recover_s,empty_startup_s,side_bytes\n")
        for rep in range(1, REPS + 1):
            for config, chain in (("baseline", [0]), ("delta", [0, 1, 2, 3, 4])):
                for d in chain:
                    secs, side, files = run(config, d)
                    print(f"{config:8s} deltas={d} rep={rep}: recover {secs:.2f} s (side store {side / 1e6:.0f} MB, "
                          f"files per set {sorted(files)})", flush=True)
                    f.write(f"{config},{d},{rep},{secs:.3f},{empty:.3f},{side}\n")
    shutil.rmtree(WORK, ignore_errors=True)


if __name__ == "__main__":
    main()
