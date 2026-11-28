#!/usr/bin/env python3
"""Diagnose SortedSet data loss under the log memory limit with repeated checkpoints (seen on unmodified Garnet main).

Starts Garnet (GARNET_DLL, default the local build) with -m MEM (default 8g) on port 6400, loads and runs the throughput
benchmark (20 sets x MEMBERS, 16 clients, 50% ZINCRBY) with BGSAVE at SAVEAT, and every 100 ms records each set's ZCARD and the
store's log addresses / heap size from INFO STORE. STORAGE_TIER=1 adds --storage-tier (a real main log device);
without it the main log is a NullDevice and evicted records are dropped (cache mode). Prints when each set first shrinks and the log state around it.

Usage: diag_loss.py [out_csv]
"""
import os, shutil, signal, socket, subprocess, sys, threading, time

HERE = os.path.dirname(os.path.abspath(__file__))
DOTNET = "/usr/local/share/dotnet/dotnet"
GARNET = os.environ.get("GARNET_DLL", os.path.join(HERE, "../../../main/GarnetServer/bin/Release/net10.0/GarnetServer.dll"))
BENCH = os.path.join(HERE, "../throughput/bin/Release/net10.0/ThroughputBench.dll")
PORT = 6400
MEM = os.environ.get("MEM", "8g")
MEMBERS = int(os.environ.get("MEMBERS", 1000000))
SAVEAT = os.environ.get("SAVEAT", "1,4,7")
WORK = os.path.join(HERE, "local", "work_diag")
OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "local", "diag_loss.csv")
FIELDS = ["CurrentVersion", "SystemState", "Log.HeadAddress", "Log.SafeReadOnlyAddress", "Log.FlushedUntilAddress", "Log.TailAddress",
          "Log.CurrentHeapSizeBytes", "Log.CurrentMemorySizeBytes"]


def resp(*args):
    return b"".join([b"*%d\r\n" % len(args)] + [b"$%d\r\n%s\r\n" % (len(str(a).encode()), str(a).encode()) for a in args])


class Conn:
    def __init__(self):
        self.s = socket.create_connection(("127.0.0.1", PORT))
        self.f = self.s.makefile("rb")

    def cmd(self, *a):
        self.s.sendall(resp(*a))
        line = self.f.readline()
        if line[:1] == b"$":
            n = int(line[1:])
            return self.f.read(n + 2)[:-2]
        return line[1:].strip()


def main():
    shutil.rmtree(WORK, ignore_errors=True)
    os.makedirs(WORK)
    log = open(os.path.join(WORK, "server.log"), "w")
    p = subprocess.Popen([DOTNET, GARNET, "--port", str(PORT), "--checkpointdir", os.path.join(WORK, "ckpt"), "--device-type", "RandomAccess",
                          "--logger-level", "Information", "-m", MEM, "-i", "64m"]
                         + (["--storage-tier", "--logdir", os.path.join(WORK, "log")] if os.environ.get("STORAGE_TIER") == "1" else []), stdout=log, stderr=subprocess.STDOUT,
                         env={**os.environ, "DOTNET_ROOT": "/usr/local/share/dotnet"})
    for _ in range(300):
        try:
            c = Conn()
            break
        except OSError:
            time.sleep(0.1)
    rows, stop = [], threading.Event()

    def poll():
        t0 = time.time()
        while not stop.is_set():
            try:
                sizes = [int(c.cmd("ZCARD", f"z{s}")) for s in range(20)]
                info = dict(l.split(":", 1) for l in c.cmd("INFO", "store").decode().splitlines() if ":" in l)
                rows.append((round(time.time() - t0, 2), sizes, [info.get(k, "") for k in FIELDS]))
            except Exception as e:  # noqa: BLE001
                rows.append((round(time.time() - t0, 2), None, [str(e)]))
            time.sleep(0.1)

    th = threading.Thread(target=poll)
    try:
        bench = subprocess.Popen([DOTNET, BENCH, f"port={PORT}", "sets=20", f"members={MEMBERS}", "clients=16", "pipeline=1", "readpct=50",
                                  "run=12", f"saveat={SAVEAT}"], stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, text=True)
        # Start polling after the load finishes.
        for line in bench.stderr:
            if line.startswith("loaded"):
                th.start()
                break
        bench.wait()
        time.sleep(1)
    finally:
        stop.set()
        if th.is_alive():
            th.join()
        p.send_signal(signal.SIGKILL)
        p.wait()

    with open(OUT, "w") as f:
        f.write("t," + ",".join(f"z{s}" for s in range(20)) + "," + ",".join(FIELDS) + "\n")
        for t, sizes, info in rows:
            f.write(f"{t}," + (",".join(map(str, sizes)) if sizes else ",".join([""] * 20)) + "," + ",".join(x.replace(",", ";") for x in info) + "\n")

    # When did each set first shrink below MEMBERS, and what did the log look like just before and at that moment?
    first = {}
    for i, (t, sizes, info) in enumerate(rows):
        if sizes:
            for s, n in enumerate(sizes):
                if n < MEMBERS and s not in first:
                    first[s] = i
    print(f"MEM={MEM} SAVEAT={SAVEAT} STORAGE_TIER={os.environ.get('STORAGE_TIER', '0')}: {len(first)} of 20 sets shrank")
    for i in sorted(set(first.values())):
        for j in (i - 1, i):
            t, sizes, info = rows[j]
            print(f"  t={t:5.2f}s shrunk={[s for s in first if first[s] <= j]} " + " ".join(f"{k}={v}" for k, v in zip(FIELDS, info)))
    shutil.rmtree(WORK, ignore_errors=True)


if __name__ == "__main__":
    main()
