#!/usr/bin/env python3
"""Run one throughput benchmark: start Garnet, run ThroughputBench against it, stop Garnet, keep the results.

usage: throughput_run.py <out_prefix> [bench args, e.g. members=1000000 clients=16 pipeline=1 readpct=50 run=20 saveat=5,12]

Environment:
  GARNET_BIN    self-contained GarnetServer executable (default: local build via dotnet)
  GARNET_NUMA   command prefix for the server, e.g. "numactl --cpunodebind=1 --membind=1"
  BENCH_BIN     ThroughputBench executable (default: local build via dotnet)
  CLIENT_NUMA   command prefix for the benchmark client
  GARNET_SORTEDSET_MEMBER_CHECKPOINT, TSAVORITE_SERIALIZE_POOLED, GARNET_SORTEDSET_DELTA  passed through to the server
Writes <out_prefix>.json (benchmark output), <out_prefix>_trace.csv (server checkpoint trace), <out_prefix>_server.log,
<out_prefix>_delta.csv (sorted set side store writes) and <out_prefix>_ckptbytes.txt (bytes of the last checkpoint's files).
"""
import os, shlex, shutil, signal, socket, subprocess, sys, time

HERE = os.path.dirname(os.path.abspath(__file__))
DOTNET = "/usr/local/share/dotnet/dotnet"
LOCAL_GARNET = os.path.join(HERE, "../../../main/GarnetServer/bin/Release/net10.0/GarnetServer.dll")
LOCAL_BENCH = os.path.join(HERE, "bin/Release/net10.0/ThroughputBench.dll")
PORT = 6398
WORK = os.path.join(HERE, "work")

def cmd(numa_env, bin_env, local):
    """Command prefix from numa_env, then the executable from bin_env or the local build run with dotnet."""
    return shlex.split(os.environ.get(numa_env, "")) + ([os.environ[bin_env]] if os.environ.get(bin_env) else [DOTNET, local])

def main():
    out = sys.argv[1]
    bench_args = [f"port={PORT}", *sys.argv[2:]]
    shutil.rmtree(WORK, ignore_errors=True); os.makedirs(WORK)
    trace = f"{WORK}/trace.csv"
    delta = f"{WORK}/delta.csv"
    log = open(f"{WORK}/server.log", "w")
    server = subprocess.Popen([*cmd("GARNET_NUMA", "GARNET_BIN", LOCAL_GARNET), "--port", str(PORT), "--checkpointdir", f"{WORK}/ckpt",
                               "--device-type", "RandomAccess", "--logger-level", "Information", "-m", "8g", "-i", "64m"],
                              stdout=log, stderr=subprocess.STDOUT,
                              env={**os.environ, "DOTNET_ROOT": "/usr/local/share/dotnet", "TSAVORITE_COU_TRACE": trace, "GARNET_DELTA_TRACE": delta})
    try:
        for _ in range(600):
            try: socket.create_connection(("127.0.0.1", PORT)).close(); break
            except OSError: time.sleep(0.1)
        with open(f"{out}.json", "w") as f:
            subprocess.run([*cmd("CLIENT_NUMA", "BENCH_BIN", LOCAL_BENCH), *bench_args], stdout=f, check=True)
    finally:
        server.send_signal(signal.SIGKILL); server.wait()
        log.close()
        if os.path.exists(trace): shutil.copy(trace, f"{out}_trace.csv")
        if os.path.exists(delta): shutil.copy(delta, f"{out}_delta.csv")
        with open(f"{out}_ckptbytes.txt", "w") as f:
            # Files of the checkpoint that is kept (snapshot, metadata, index) and the side store; not the main log.
            for name in ("cpr-checkpoints", "index-checkpoints", "sortedset-members"):
                total = 0
                for root, _, files in os.walk(f"{WORK}/ckpt"):
                    if f"/{name}" in root:
                        total += sum(os.path.getsize(os.path.join(root, x)) for x in files)
                f.write(f"{name} {total}\n")
        shutil.copy(f"{WORK}/server.log", f"{out}_server.log")
        shutil.rmtree(WORK, ignore_errors=True)   # checkpoints are large

if __name__ == "__main__":
    main()
