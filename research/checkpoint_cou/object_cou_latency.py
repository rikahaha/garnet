#!/usr/bin/env python3
"""Pilot: latency of object updates while a checkpoint is in progress.

During a checkpoint (IN_PROGRESS..WAIT_FLUSH), the first update to an in-memory
object of version v goes through CopyUpdate, which serializes the whole v object
synchronously (HeapObjectBase.CacheSerializedObjectData). This measures per-op
latency of ZADD on sorted sets of a given size, with a BGSAVE issued mid-run.

usage: [MODE=snapshot|snapshot_tier|foldover_tier] object_cou_latency.py <members_per_set> [num_sets]
"""
import csv, os, random, shlex, shutil, signal, socket, subprocess, sys, threading, time

HERE = os.path.dirname(os.path.abspath(__file__))
GARNET = os.path.join(HERE, "../../main/GarnetServer/bin/Release/net10.0/GarnetServer.dll")
DOTNET = "/usr/local/share/dotnet/dotnet"
# GARNET_BIN: a self-contained GarnetServer executable (e.g. published for linux-x64); otherwise run the local build with dotnet.
# GARNET_NUMA: command prefix for the server, e.g. "numactl --cpunodebind=1 --membind=1".
SERVER_CMD = shlex.split(os.environ.get("GARNET_NUMA", "")) + ([os.environ["GARNET_BIN"]] if os.environ.get("GARNET_BIN") else [DOTNET, GARNET])
PORT = 6398
MEMBERS = int(sys.argv[1]) if len(sys.argv) > 1 else 100000
SETS = int(sys.argv[2]) if len(sys.argv) > 2 else 20
# CHECKPOINT_TIMES: seconds into the measured run at which BGSAVE is issued, e.g. "2,5" for two consecutive checkpoints.
BGSAVE_AT_S = [float(t) for t in os.environ.get("CHECKPOINT_TIMES", "2").split(",")]
RUN_S = max(8.0, BGSAVE_AT_S[-1] + 6.0)
WORK = os.path.join(HERE, "work")
MODE = os.environ.get("MODE", "snapshot")
MODE_ARGS = {
    "snapshot": [],
    "snapshot_tier": ["--storage-tier", "--logdir", f"{WORK}/log"],
    "foldover_tier": ["--storage-tier", "--logdir", f"{WORK}/log", "--use-fold-over-checkpoints"],
}[MODE]

def enc(*args):
    out = [b"*%d\r\n" % len(args)]
    for a in args:
        a = a if isinstance(a, bytes) else str(a).encode()
        out.append(b"$%d\r\n%s\r\n" % (len(a), a))
    return b"".join(out)

class Conn:
    def __init__(self):
        self.s = socket.create_connection(("127.0.0.1", PORT))
        self.s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self.f = self.s.makefile("rb")
    def send(self, data): self.s.sendall(data)
    def read(self):
        line = self.f.readline()
        t, rest = line[:1], line[1:-2]
        if t in b"+-:":
            if t == b"-": raise RuntimeError(rest.decode())
            return rest
        if t == b"$":
            n = int(rest)
            return None if n < 0 else self.f.read(n + 2)[:-2]
        if t == b"*":
            return [self.read() for _ in range(max(int(rest), 0))]
        raise RuntimeError(f"bad reply {line!r}")
    def cmd(self, *a): self.send(enc(*a)); return self.read()

def sample_rss(pid, out, stop):
    """Record the server's resident memory (MB) every 50 ms into out as (t, mb), until stop is set."""
    t0 = time.perf_counter()
    while not stop.is_set():
        try:
            kb = int(subprocess.run(["ps", "-o", "rss=", "-p", str(pid)], capture_output=True, text=True).stdout.strip() or 0)
            out.append((time.perf_counter() - t0, kb / 1024))
        except ValueError:
            pass
        stop.wait(0.05)

def main():
    shutil.rmtree(WORK, ignore_errors=True); os.makedirs(WORK)
    log = open(f"{WORK}/server.log", "w")
    p = subprocess.Popen([*SERVER_CMD, "--port", str(PORT), "--checkpointdir", f"{WORK}/ckpt", "--device-type", "RandomAccess",
                          "--logger-level", "Information", "-m", "8g", "-i", "64m", *MODE_ARGS],
                         stdout=log, stderr=subprocess.STDOUT, env={**os.environ, "DOTNET_ROOT": "/usr/local/share/dotnet", "TSAVORITE_COU_TRACE": f"{WORK}/trace.csv"})
    try:
        for _ in range(600):
            try: c = Conn(); c.cmd("PING"); break
            except OSError: time.sleep(0.1)
        # load: SETS sorted sets of MEMBERS members each, 1000 members per ZADD
        for s in range(SETS):
            for b in range(0, MEMBERS, 1000):
                args = []
                for i in range(b, min(b + 1000, MEMBERS)): args += [i, f"m{i}"]
                c.send(enc("ZADD", f"z{s}", *args))
            for b in range(0, MEMBERS, 1000): c.read()

        ctl = Conn()
        rss, stop = [], threading.Event()
        sampler = threading.Thread(target=sample_rss, args=(p.pid, rss, stop), daemon=True)
        rows, save_ts, t0 = [], [], time.perf_counter()
        sampler.start()
        while (now := time.perf_counter() - t0) < RUN_S:
            if len(save_ts) < len(BGSAVE_AT_S) and now >= BGSAVE_AT_S[len(save_ts)]:
                ctl.cmd("BGSAVE"); save_ts.append(now)
            s = random.randrange(SETS)
            a = time.perf_counter()
            c.cmd("ZADD", f"z{s}", random.random(), f"x{random.randrange(10**9)}")
            rows.append((round(a - t0, 6), s, round((time.perf_counter() - a) * 1e6, 1)))
        stop.set(); sampler.join()
    finally:
        p.send_signal(signal.SIGKILL); p.wait()
        shutil.rmtree(f"{WORK}/ckpt", ignore_errors=True)   # keep only the logs; checkpoints are large

    out = os.path.join(HERE, f"lat_{MODE}_m{MEMBERS}_s{SETS}.csv")
    with open(out, "w", newline="") as f:
        w = csv.writer(f); w.writerow(["t_s", "set", "lat_us"]); w.writerows(rows)

    def stats(xs):
        xs = sorted(xs)
        q = lambda p: xs[min(len(xs) - 1, int(p * len(xs)))]
        return f"n={len(xs):6d} p50={q(.5):8.1f} p99={q(.99):9.1f} max={xs[-1]:10.1f} us"
    print(f"mode={MODE} members/set={MEMBERS} sets={SETS}")
    print("  before BGSAVE     ", stats([r[2] for r in rows if r[0] < save_ts[0]]))
    for i, t in enumerate(save_ts):
        print(f"  ckpt {i + 1}: first 1s ", stats([r[2] for r in rows if t <= r[0] < t + 1.0]))
    print("  later             ", stats([r[2] for r in rows if r[0] >= save_ts[-1] + 1.0]))
    print("  top 5 latencies (t_s, set, us):", sorted(rows, key=lambda r: -r[2])[:5])
    base_rss = max((mb for t, mb in rss if t < save_ts[0]), default=0)
    print(f"  server RSS MB: before checkpoint {base_rss:.0f}, peak {max((mb for _, mb in rss), default=0):.0f}")

if __name__ == "__main__":
    main()
