#!/bin/bash
# cygnus run for ckpt-delta, from the uploaded directory (e.g. ~/member-delta):
#   1. throughput across checkpoints: baseline / viewpooled / delta x 100K / 1M members x 3 reps (16 clients, BGSAVE at 5 s and 12 s)
#   2. recovery time vs delta chain length (baseline, delta with 0-4 deltas)
# -m 32g (a limit, not an allocation): without --storage-tier Garnet evicts whole keys past -m, which -m 8g hit before.
# Server on NUMA node 1, client on node 0. Writes done.txt at the end.
set -e
cd "$(dirname "$0")"
export GARNET_BIN=$PWD/garnet-bin/GarnetServer BENCH_BIN=$PWD/bench-bin/ThroughputBench
export GARNET_NUMA="numactl --cpunodebind=1 --membind=1" CLIENT_NUMA="numactl --cpunodebind=0 --membind=0"
export MEM=32g
(cd throughput && SKIP_SWEEP=1 REPS=3 CLIENTS=16 SIZES="100000 1000000" CONFIGS="baseline viewpooled delta" RESULTS=results \
  ./run_throughput.sh) > run_throughput.log 2>&1
(cd delta && python3 -I recovery_time.py results/recovery.csv) > run_recovery.log 2>&1
touch done.txt
