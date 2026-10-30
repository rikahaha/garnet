#!/usr/bin/env python3
"""End-to-end correctness check for checkpoint pre-serialization.

A: pre-serialized objects that no CopyUpdate touched must return to REST: after the checkpoint they are
   modified in place, a second checkpoint is taken, and recovery (no AOF) must return the modified data.
B: writes continue during the checkpoint; with AOF, recovery (checkpoint + AOF replay) must reproduce the
   exact pre-crash contents.

Run with and without TSAVORITE_PRESERIALIZE_THREADS set; both must print PASS.
"""
import hashlib, os, random, shlex, shutil, signal, socket, subprocess, sys, time

HERE = os.path.dirname(os.path.abspath(__file__))
GARNET = os.path.join(HERE, "../../main/GarnetServer/bin/Release/net10.0/GarnetServer.dll")
DOTNET = "/usr/local/share/dotnet/dotnet"
# GARNET_BIN: a self-contained GarnetServer executable (e.g. published for linux-x64); otherwise run the local build with dotnet.
# GARNET_NUMA: command prefix for the server, e.g. "numactl --cpunodebind=1 --membind=1".
SERVER_CMD = shlex.split(os.environ.get("GARNET_NUMA", "")) + ([os.environ["GARNET_BIN"]] if os.environ.get("GARNET_BIN") else [DOTNET, GARNET])
PORT = 6397
SETS, MEMBERS = 20, 200000
WORK = os.path.join(HERE, "work_correctness")

def enc(*args):
    out = [b"*%d\r\n" % len(args)]
    for a in args:
        a = a if isinstance(a, bytes) else str(a).encode()
        out.append(b"$%d\r\n%s\r\n" % (len(a), a))
    return b"".join(out)

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
            return [self.read() for _ in range(max(int(rest), 0))]
        raise RuntimeError(f"bad reply {line!r}")
    def cmd(self, *a): self.send(enc(*a)); return self.read()

def start(extra):
    log = open(f"{WORK}/server.log", "a")
    p = subprocess.Popen([*SERVER_CMD, "--port", str(PORT), "--checkpointdir", f"{WORK}/ckpt", "--device-type", "RandomAccess",
                          "--logger-level", "Information", "-m", "4g", "-i", "64m", *extra],
                         stdout=log, stderr=subprocess.STDOUT, env={**os.environ, "DOTNET_ROOT": "/usr/local/share/dotnet"})
    for _ in range(600):
        try: c = Conn(); c.cmd("PING"); return p, c
        except OSError: time.sleep(0.1)
    raise RuntimeError("server did not start")

def kill(p): p.send_signal(signal.SIGKILL); p.wait()

def load(c):
    for s in range(SETS):
        for b in range(0, MEMBERS, 1000):
            args = []
            for i in range(b, min(b + 1000, MEMBERS)): args += [i, f"m{i}"]
            c.send(enc("ZADD", f"z{s}", *args))
        for b in range(0, MEMBERS, 1000): c.read()

def digest(c):
    out = {}
    for s in range(SETS):
        items = c.cmd("ZRANGE", f"z{s}", 0, -1, "WITHSCORES")
        out[s] = (len(items) // 2, hashlib.sha1(b"\0".join(items)).hexdigest())
    return out

def bgsave_and_wait(c):
    last = c.cmd("LASTSAVE")
    c.cmd("BGSAVE")
    while c.cmd("LASTSAVE") == last: time.sleep(0.05)

def compare(name, expected, actual):
    bad = [s for s in range(SETS) if expected[s] != actual.get(s)]
    for s in bad[:5]: print(f"  {name} z{s}: expected {expected[s]}, got {actual.get(s)}")
    print(f"{name}: {'PASS' if not bad else f'FAIL ({len(bad)} sets differ)'}")
    return not bad

def scenario_a():
    shutil.rmtree(WORK, ignore_errors=True); os.makedirs(WORK)
    p, c = start([])
    load(c)
    time.sleep(2.5)                        # let LASTSAVE (second resolution) differ from the next save
    bgsave_and_wait(c)                     # checkpoint 1: objects pre-serialized, none modified
    for s in range(SETS):                  # in-place updates after the checkpoint
        c.cmd("ZADD", f"z{s}", -1, f"after{s}")
        c.cmd("ZREM", f"z{s}", "m0")
    expected = digest(c)
    time.sleep(1.1)
    bgsave_and_wait(c)                     # checkpoint 2 must capture the modified objects
    kill(p)
    p, c = start(["--recover"])
    ok = compare("A (untouched pre-serialized objects, then in-place updates)", expected, digest(c))
    kill(p)
    return ok

def scenario_b():
    shutil.rmtree(WORK, ignore_errors=True); os.makedirs(WORK)
    p, c = start(["--aof", "--aof-commit-freq", "10"])
    load(c)
    ctl = Conn()
    ctl.cmd("BGSAVE")
    rng = random.Random(1)
    t_end = time.time() + 3.0              # keep writing through the checkpoint and after it
    n = 0
    while time.time() < t_end:
        s = rng.randrange(SETS)
        if rng.random() < 0.8: c.cmd("ZADD", f"z{s}", rng.random(), f"x{n}")
        else: c.cmd("ZREM", f"z{s}", f"m{rng.randrange(MEMBERS)}")
        n += 1
    c.cmd("COMMITAOF")
    expected = digest(c)
    time.sleep(0.5)
    kill(p)
    p, c = start(["--aof", "--recover"])
    ok = compare(f"B (writes during checkpoint, {n} ops, checkpoint + AOF replay)", expected, digest(c))
    kill(p)
    return ok

if __name__ == "__main__":
    print(f"TSAVORITE_PRESERIALIZE_THREADS={os.environ.get('TSAVORITE_PRESERIALIZE_THREADS', '0')}")
    ok = scenario_a() & scenario_b()
    shutil.rmtree(WORK, ignore_errors=True)
    sys.exit(0 if ok else 1)
