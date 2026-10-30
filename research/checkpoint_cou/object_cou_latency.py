#!/usr/bin/env python3
"""Pilot: latency of object updates while a checkpoint is in progress.

During a checkpoint (IN_PROGRESS..WAIT_FLUSH), the first update to an in-memory
object of version v goes through CopyUpdate, which serializes the whole v object
synchronously (HeapObjectBase.CacheSerializedObjectData). This measures per-op
latency of ZADD on sorted sets of a given size, with a BGSAVE issued mid-run.

usage: [MODE=snapshot|snapshot_tier|foldover_tier] object_cou_latency.py <members_per_set> [num_sets]
"""
import os, shutil, signal, socket, subprocess, sys, time, random, csv

HERE = os.path.dirname(os.path.abspath(__file__))
GARNET = os.path.join(HERE, "../../main/GarnetServer/bin/Release/net10.0/GarnetServer.dll")
DOTNET = "/usr/local/share/dotnet/dotnet"
PORT = 6398
MEMBERS = int(sys.argv[1]) if len(sys.argv) > 1 else 100000
SETS = int(sys.argv[2]) if len(sys.argv) > 2 else 20
RUN_S, BGSAVE_AT_S = 8.0, 2.0
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

def main():
    shutil.rmtree(WORK, ignore_errors=True); os.makedirs(WORK)
    log = open(f"{WORK}/server.log", "w")
    p = subprocess.Popen([DOTNET, GARNET, "--port", str(PORT), "--checkpointdir", f"{WORK}/ckpt",
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
        rows, t0, saved = [], time.perf_counter(), False
        while (now := time.perf_counter() - t0) < RUN_S:
            if not saved and now >= BGSAVE_AT_S:
                ctl.cmd("BGSAVE"); saved = True; save_t = now
            s = random.randrange(SETS)
            a = time.perf_counter()
            c.cmd("ZADD", f"z{s}", random.random(), f"x{random.randrange(10**9)}")
            rows.append((round(a - t0, 6), s, round((time.perf_counter() - a) * 1e6, 1)))
    finally:
        p.send_signal(signal.SIGKILL); p.wait()

    out = os.path.join(HERE, f"lat_{MODE}_m{MEMBERS}_s{SETS}.csv")
    with open(out, "w", newline="") as f:
        w = csv.writer(f); w.writerow(["t_s", "set", "lat_us"]); w.writerows(rows)

    def stats(xs):
        xs = sorted(xs)
        q = lambda p: xs[min(len(xs) - 1, int(p * len(xs)))]
        return f"n={len(xs):6d} p50={q(.5):8.1f} p99={q(.99):9.1f} max={xs[-1]:10.1f} us"
    before = [r[2] for r in rows if r[0] < save_t]
    during = [r[2] for r in rows if save_t <= r[0] < save_t + 1.0]
    after = [r[2] for r in rows if r[0] >= save_t + 1.0]
    print(f"mode={MODE} members/set={MEMBERS} sets={SETS}")
    print("  before BGSAVE     ", stats(before))
    print("  first 1s after    ", stats(during))
    print("  later             ", stats(after))
    print("  top 5 latencies (t_s, set, us):", sorted(rows, key=lambda r: -r[2])[:5])

if __name__ == "__main__":
    main()
