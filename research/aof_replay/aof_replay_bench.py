#!/usr/bin/env python3
"""Pilot: AOF replay cost per command type in Garnet.

For each workload: start Garnet with AOF, load N ops, COMMITAOF, kill -9,
restart with --recover, and parse Garnet's own "AOF Recovery" log lines.
"""
import os, re, shutil, signal, socket, subprocess, sys, time, csv

GARNET = os.path.join(os.path.dirname(os.path.abspath(__file__)), "../../main/GarnetServer/bin/Release/net10.0/GarnetServer.dll")
DOTNET = "/usr/local/share/dotnet/dotnet"
PORT = 6399
N = int(os.environ.get("N", "500000"))
BATCH = 1000
WORK = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "work")

def enc(*args):
    out = [b"*%d\r\n" % len(args)]
    for a in args:
        a = a if isinstance(a, bytes) else str(a).encode()
        out.append(b"$%d\r\n%s\r\n" % (len(a), a))
    return b"".join(out)

V32, V1K = b"v" * 32, b"v" * 1024
LUA = "redis.call('SET', KEYS[1], ARGV[1]) return 1"

# workload name -> function(i) returning list of encoded commands, extra server args
WORKLOADS = {
    "set_32B":    (lambda i: [enc("SET", f"k{i}", V32)], []),
    "set_1KB":    (lambda i: [enc("SET", f"k{i}", V1K)], []),
    "set_hot":    (lambda i: [enc("SET", f"k{i % 1000}", V32)], []),
    "incr":       (lambda i: [enc("INCR", f"c{i % 100000}")], []),
    "hset":       (lambda i: [enc("HSET", f"h{i % 1000}", f"f{i}", V32)], []),
    "sadd":       (lambda i: [enc("SADD", f"s{i % 1000}", f"m{i}")], []),
    "zadd":       (lambda i: [enc("ZADD", f"z{i % 1000}", i, f"m{i}")], []),
    "zadd_big":   (lambda i: [enc("ZADD", f"z{i % 10}", i, f"m{i}")], []),
    "rpush":      (lambda i: [enc("RPUSH", f"l{i % 1000}", V32)], []),
    "multi_set":  (lambda i: [enc("MULTI"), enc("SET", f"k{i}", V32), enc("EXEC")], []),
    "eval_set":   (lambda i: [enc("EVAL", LUA, 1, f"k{i}", V32)], ["--lua"]),
}

class Conn:
    def __init__(self):
        self.s = socket.create_connection(("127.0.0.1", PORT))
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
            n = int(rest)
            return [self.read() for _ in range(max(n, 0))]
        raise RuntimeError(f"bad reply {line!r}")

def start(extra, logpath):
    log = open(logpath, "w")
    p = subprocess.Popen([DOTNET, GARNET, "--port", str(PORT), "--aof", "--aof-commit-freq", "100",
                          "--checkpointdir", f"{WORK}/ckpt",
                          "--logger-level", "Information", "-m", "4g", "-i", "128m", *extra],
                         stdout=log, stderr=subprocess.STDOUT, env={**os.environ, "DOTNET_ROOT": "/usr/local/share/dotnet"})
    for _ in range(600):
        try:
            c = Conn(); c.send(enc("PING")); c.read(); return p, c
        except OSError: time.sleep(0.1)
    raise RuntimeError("server did not start")

def run(name):
    gen, extra = WORKLOADS[name]
    shutil.rmtree(WORK, ignore_errors=True); os.makedirs(WORK)
    p, c = start(extra, f"{WORK}/load.log")
    t0 = time.time()
    for b in range(0, N, BATCH):
        cmds = [x for i in range(b, min(b + BATCH, N)) for x in gen(i)]
        c.send(b"".join(cmds))
        for _ in cmds: c.read()
    load_s = time.time() - t0
    c.send(enc("COMMITAOF")); c.read()
    time.sleep(0.5)
    p.send_signal(signal.SIGKILL); p.wait()

    t0 = time.time()
    p, c = start(extra + ["--recover"], f"{WORK}/recover.log")
    startup_s = time.time() - t0
    p.send_signal(signal.SIGKILL); p.wait()
    log = open(f"{WORK}/recover.log").read()
    g = lambda pat: (m.group(1).replace(",", "") if (m := re.search(pat, log)) else "")
    row = dict(workload=name, ops=N, load_s=round(load_s, 2), startup_s=round(startup_s, 2),
               replay_s=g(r"AOF Recovery in ([\d.]+) secs"),
               records=g(r"replayed records ([\d,]+)"),
               aof_bytes=g(r"AOF Recovery size ([\d,]+)"))
    if row["replay_s"] and row["records"]:
        s, r, b = float(row["replay_s"]), int(row["records"]), int(row["aof_bytes"] or 0)
        row["us_per_rec"] = round(s / r * 1e6, 3) if r else ""
        row["ns_per_byte"] = round(s / b * 1e9, 3) if b else ""
        row["bytes_per_rec"] = round(b / r, 1) if r else ""
    print(row, flush=True)
    return row

if __name__ == "__main__":
    names = sys.argv[2:] or list(WORKLOADS)
    rows = [run(n) for n in names]
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "aof_replay_results.csv")
    keys = ["workload", "ops", "records", "aof_bytes", "bytes_per_rec", "replay_s", "us_per_rec", "ns_per_byte", "load_s", "startup_s"]
    with open(out, "w", newline="") as f:
        w = csv.DictWriter(f, keys, extrasaction="ignore"); w.writeheader(); w.writerows(rows)
    print("wrote", out)
