#!/usr/bin/env python3
"""Experiments modelled on the Garnet paper's evaluation (VLDB 2026, Section 8), for baseline / snapshot views / delta.

  zaddrem   Paper Fig. 7a: Resp.bench --op ZADDREM over 1024 sorted sets, offline (batched, -b 1024) throughput while varying
            client threads, with and without a BGSAVE every 2 s. Each batch adds members and removes the same members (same
            seed), so the sets stay nearly empty: a small-object control that the changes cost nothing there.
  rounds    Our large-set workload with 6 checkpoints 7 s apart (BGSAVE at 5, 12, ..., 40 s of 45 s), uniform and Zipf(0.9):
            whether later checkpoints keep the low latency and small writes. Per checkpoint: duration, throughput, p99.9, max
            latency (throughput_analyze.py), and from the delta trace: distinct members changed, full or delta, bytes.
  scaling   Our large-set workload (20 sets x 1M members, ZSCORE/ZINCRBY 50/50, BGSAVE at 5 s and 12 s of 20 s) with more
            clients (16 / 64 / 128) and uniform vs Zipf(0.9) members, as the paper varies clients and uses Zipf(0.9).
  savetime  Paper Sec. 8.6: checkpoint time with no concurrent load: load 20 x 1M, SAVE, change 1% of the members of every
            set, SAVE again; checkpoint durations from the server trace, bytes written.

Environment: GARNET_BIN / BENCH_BIN / RB_BIN (Resp.bench) self-contained executables (default: local builds via dotnet),
GARNET_NUMA / CLIENT_NUMA command prefixes, MEM (log memory limit, default 32g), REPS (default 2).
Usage: paper_eval.py <out_dir> [zaddrem] [savetime] [rounds] [scaling]
"""
import csv, json, os, re, shlex, shutil, signal, socket, subprocess, sys, threading, time

HERE = os.path.dirname(os.path.abspath(__file__))
DOTNET = "/usr/local/share/dotnet/dotnet"
ROOT = os.path.join(HERE, "../../..")
LOCAL = {
    "GARNET_BIN": os.path.join(ROOT, "main/GarnetServer/bin/Release/net10.0/GarnetServer.dll"),
    "BENCH_BIN": os.path.join(HERE, "../throughput/bin/Release/net10.0/ThroughputBench.dll"),
    "RB_BIN": os.path.join(ROOT, "benchmark/Resp.benchmark/bin/Release/net10.0/Resp.benchmark.dll"),
}
PORT = 6402
MEM = os.environ.get("MEM", "32g")
REPS = int(os.environ.get("REPS", 2))
CONFIGS = {
    "baseline": {"GARNET_SORTEDSET_MEMBER_CHECKPOINT": "0", "TSAVORITE_SERIALIZE_POOLED": "0", "GARNET_SORTEDSET_DELTA": "0"},
    "viewpooled": {"GARNET_SORTEDSET_MEMBER_CHECKPOINT": "1", "TSAVORITE_SERIALIZE_POOLED": "1", "GARNET_SORTEDSET_DELTA": "0"},
    "delta": {"GARNET_SORTEDSET_MEMBER_CHECKPOINT": "1", "TSAVORITE_SERIALIZE_POOLED": "1", "GARNET_SORTEDSET_DELTA": "1"},
}


def exe(name):
    path = os.environ.get(name)
    return [path] if path else [DOTNET, LOCAL[name]]


def numa(name):
    return shlex.split(os.environ.get(name, ""))


def resp(*args):
    return b"".join([b"*%d\r\n" % len(args)] + [b"$%d\r\n%s\r\n" % (len(str(a).encode()), str(a).encode()) for a in args])


class Conn:
    def __init__(self):
        self.s = socket.create_connection(("127.0.0.1", PORT))
        self.f = self.s.makefile("rb")

    def read(self):
        line = self.f.readline()
        if line[:1] == b"$":
            n = int(line[1:])
            return None if n < 0 else self.f.read(n + 2)[:-2]
        if line[:1] == b"-":
            raise RuntimeError(line.decode().strip())
        return line[1:].strip()

    def cmd(self, *a):
        self.s.sendall(resp(*a))
        return self.read()


class Server:
    def __init__(self, work, config, trace=True):
        shutil.rmtree(work, ignore_errors=True)
        os.makedirs(work)
        self.work = work
        self.trace = os.path.join(work, "trace.csv")
        self.delta = os.path.join(work, "delta.csv")
        env = {**os.environ, **CONFIGS[config], "DOTNET_ROOT": "/usr/local/share/dotnet"}
        if trace:
            env.update(TSAVORITE_COU_TRACE=self.trace, GARNET_DELTA_TRACE=self.delta)
        self.log = open(os.path.join(work, "server.log"), "w")
        self.p = subprocess.Popen([*numa("GARNET_NUMA"), *exe("GARNET_BIN"), "--port", str(PORT), "--checkpointdir", os.path.join(work, "ckpt"),
                                   "--device-type", "RandomAccess", "--logger-level", "Information", "-m", MEM, "-i", "64m"],
                                  stdout=self.log, stderr=subprocess.STDOUT, env=env)
        for _ in range(600):
            try:
                Conn().cmd("PING")
                return
            except OSError:
                time.sleep(0.1)
        raise RuntimeError("server did not start")

    def stop(self, keep_to=None):
        self.p.send_signal(signal.SIGKILL)
        self.p.wait()
        self.log.close()
        if keep_to:
            for name in ("trace.csv", "delta.csv", "server.log"):
                src = os.path.join(self.work, name)
                if os.path.exists(src):
                    shutil.copy(src, f"{keep_to}_{name}")
        shutil.rmtree(self.work, ignore_errors=True)

    def checkpoints(self):
        """(prepare unix ms, flushed unix ms) for each checkpoint in the trace (no trace file: no checkpoint was traced)."""
        if not os.path.exists(self.trace):
            return []
        marks = [(int(r["unix_ms"]), r["kind"]) for r in csv.DictReader(open(self.trace)) if r["kind"] in ("ckpt_prepare", "ckpt_flushed")]
        out, start = [], None
        for t, k in marks:
            if k == "ckpt_prepare":
                start = t
            elif start is not None:
                out.append((start, t))
                start = None
        return out


def save_and_wait(c):
    last = int(c.cmd("LASTSAVE"))
    while int(time.time()) <= last:
        time.sleep(0.05)
    c.cmd("BGSAVE")
    while int(c.cmd("LASTSAVE")) == last:
        time.sleep(0.02)


def zaddrem(out):
    threads = os.environ.get("RB_THREADS", "1,4,16,64")
    rows = []
    for rep in range(1, REPS + 1):
        for config in ("baseline", "delta"):
            for saving in (False, True):
                name = f"{out}/zaddrem_{config}_{'save' if saving else 'nosave'}_r{rep}"
                srv = Server(os.path.join(out, "work"), config)
                stop = threading.Event()

                def saver():
                    c = Conn()
                    while not stop.wait(2.0):
                        c.cmd("BGSAVE")
                th = threading.Thread(target=saver)
                if saving:
                    th.start()
                try:
                    res = subprocess.run([*numa("CLIENT_NUMA"), *exe("RB_BIN"), "--port", str(PORT), "--op", "ZADDREM", "--dbsize", "1024",
                                          "-t", threads, "-b", "1024", "--runtime", "10"], capture_output=True, text=True, check=True)
                finally:
                    stop.set()
                    if th.is_alive():
                        th.join()
                    try:
                        n_ckpt = len(srv.checkpoints())
                    finally:
                        srv.stop(keep_to=name)
                open(f"{name}.txt", "w").write(res.stdout)
                for t, ops in re.findall(r"Num threads: (\d+)\n.*?\[Throughput\]: ([\d,.]+) ops/sec", res.stdout, re.S):
                    rows.append((config, saving, rep, int(t), float(ops.replace(",", "")) / 1e6, n_ckpt))
                    print(f"zaddrem {config:8s} save={saving!s:5s} r{rep} threads={t:>3}: {rows[-1][4]:.2f} Mops/s ({n_ckpt} checkpoints)", flush=True)
    with open(f"{out}/zaddrem.csv", "w") as f:
        f.write("config,saving,rep,threads,mops,checkpoints\n")
        f.writelines(",".join(map(str, r)) + "\n" for r in rows)


def scaling(out):
    run = os.path.join(HERE, "../throughput/throughput_run.py")
    analyze = os.path.join(HERE, "../throughput/throughput_analyze.py")
    clients_list = os.environ.get("SCALING_CLIENTS", "16 64 128").split()
    for rep in range(1, REPS + 1):
        for dist in ("uniform", "zipf"):
            for clients in clients_list:
                for config in CONFIGS:
                    name = f"{out}/scaling_{config}_{dist}_c{clients}_r{rep}"
                    env = {**os.environ, **CONFIGS[config], "MEM": MEM}
                    args = ["members=1000000", "sets=20", f"clients={clients}", "pipeline=1", "readpct=50", "run=20", "saveat=5,12"]
                    if dist == "zipf":
                        args.append("zipf=0.9")
                    subprocess.run([sys.executable, "-I", run, name, *args], env=env, check=True, stderr=subprocess.DEVNULL)
                    subprocess.run([sys.executable, "-I", analyze, name], check=True)
                    sizes = {int(l.split()[1]) for l in open(f"{name}_sizes.txt")}
                    if sizes != {1000000}:
                        print(f"  WARNING {name}: set sizes at the end {sorted(sizes)}", flush=True)


def rounds(out):
    run = os.path.join(HERE, "../throughput/throughput_run.py")
    analyze = os.path.join(HERE, "../throughput/throughput_analyze.py")
    saves = [5 + 7 * i for i in range(6)]
    summary = []
    for rep in range(1, REPS + 1):
        for dist in ("uniform", "zipf"):
            for config in CONFIGS:
                name = f"{out}/rounds_{config}_{dist}_r{rep}"
                env = {**os.environ, **CONFIGS[config], "MEM": MEM}
                args = ["members=1000000", "sets=20", "clients=16", "pipeline=1", "readpct=50", f"run={saves[-1] + 5}",
                        "saveat=" + ",".join(map(str, saves))]
                if dist == "zipf":
                    args.append("zipf=0.9")
                subprocess.run([sys.executable, "-I", run, name, *args], env=env, check=True, stderr=subprocess.DEVNULL)
                subprocess.run([sys.executable, "-I", analyze, name], check=True)
                sizes = {int(l.split()[1]) for l in open(f"{name}_sizes.txt")}
                if sizes != {1000000}:
                    print(f"  WARNING {name}: set sizes at the end {sorted(sizes)}", flush=True)
                # Per checkpoint, from the delta trace (member mode only): rows written in each checkpoint window.
                if config == "baseline" or not os.path.exists(f"{name}_delta.csv"):
                    continue
                marks = [(int(r["unix_ms"]), r["kind"]) for r in csv.DictReader(open(f"{name}_trace.csv")) if r["kind"] in ("ckpt_prepare", "ckpt_flushed")]
                windows, start = [], None
                for t, k in marks:
                    if k == "ckpt_prepare":
                        start = t
                    elif start is not None:
                        windows.append((start, t))
                        start = None
                rows = [r for r in csv.DictReader(open(f"{name}_delta.csv")) if r["written"] != "inline_mainlog"]
                for i, (a, b) in enumerate(windows):
                    w = [r for r in rows if a <= int(r["unix_ms"]) <= b + 50]
                    changed = sum(int(r["changed"]) for r in w)
                    kinds = sorted({r["written"] for r in w})
                    written = sum(int(r["file_bytes"]) if r["written"].startswith("side_") else int(r["full_bytes"]) for r in w)
                    summary.append((config, dist, rep, i + 1, b - a, len(w), changed, "+".join(kinds), written))
                    print(f"  ckpt{i + 1}: {b - a} ms, {len(w)} sets, changed members {changed} ({changed / 2e5:.1f}%), "
                          f"{'+'.join(kinds)}, written {written / 1e6:.1f} MB", flush=True)
    with open(f"{out}/rounds.csv", "w") as f:
        f.write("config,dist,rep,ckpt,duration_ms,sets,changed_members,written_kind,written_bytes\n")
        f.writelines(",".join(map(str, r)) + "\n" for r in summary)


def savetime(out):
    rows = []
    for rep in range(1, REPS + 1):
        for config in CONFIGS:
            name = f"{out}/savetime_{config}_r{rep}"
            srv = Server(os.path.join(out, "work"), config)
            try:
                subprocess.run([*numa("CLIENT_NUMA"), *exe("BENCH_BIN"), f"port={PORT}", "sets=20", "members=1000000", "clients=1", "run=0",
                                "saveat="], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                c = Conn()
                save_and_wait(c)
                # Change 1% of the members of every set (distinct members, ZINCRBY), then save again.
                import random
                rng = random.Random(rep)
                for s in range(20):
                    members = rng.sample(range(1000000), 10000)
                    for b in range(0, len(members), 1000):
                        chunk = members[b:b + 1000]
                        c.s.sendall(b"".join(resp("ZINCRBY", f"z{s}", 1, f"m{m}") for m in chunk))
                        for _ in chunk:
                            c.read()
                save_and_wait(c)
                time.sleep(0.5)
                ck = srv.checkpoints()
                side = [r for r in csv.DictReader(open(srv.delta))] if os.path.exists(srv.delta) else []
                side_bytes = [sum(int(r["file_bytes"]) for r in side if r["written"] == k) for k in ("side_full", "side_delta")]
                sizes = {int(c.cmd("ZCARD", f"z{s}")) for s in range(20)}
            finally:
                srv.stop(keep_to=name)
            if len(ck) != 2 or sizes != {1000000}:
                raise RuntimeError(f"{name}: checkpoints {ck}, set sizes {sizes}")
            d = [b - a for a, b in ck]
            rows.append((config, rep, d[0], d[1], side_bytes[0], side_bytes[1]))
            print(f"savetime {config:10s} r{rep}: first {d[0]} ms, second (1% changed) {d[1]} ms; side store full {side_bytes[0] / 1e6:.0f} MB, "
                  f"delta {side_bytes[1] / 1e6:.1f} MB", flush=True)
    with open(f"{out}/savetime.csv", "w") as f:
        f.write("config,rep,first_ms,second_ms,side_full_bytes,side_delta_bytes\n")
        f.writelines(",".join(map(str, r)) + "\n" for r in rows)


if __name__ == "__main__":
    # A server left on the port by an earlier failed run would silently take this run's traffic.
    try:
        socket.create_connection(("127.0.0.1", PORT), timeout=1).close()
        sys.exit(f"port {PORT} is already in use")
    except OSError:
        pass
    out = os.path.abspath(sys.argv[1])
    os.makedirs(out, exist_ok=True)
    for what in sys.argv[2:] or ["zaddrem", "savetime", "rounds", "scaling"]:
        {"zaddrem": zaddrem, "scaling": scaling, "savetime": savetime, "rounds": rounds}[what](out)
    shutil.rmtree(os.path.join(out, "work"), ignore_errors=True)
