#!/bin/bash
# cygnus run of paper_eval.py (experiments modelled on the Garnet paper's Section 8), from the uploaded directory
# (e.g. ~/member-delta). Server on NUMA node 1, clients on node 0, -m 32g. Writes done.txt at the end.
set -e
cd "$(dirname "$0")"
export GARNET_BIN=$PWD/garnet-bin/GarnetServer BENCH_BIN=$PWD/bench-bin/ThroughputBench RB_BIN=$PWD/rb-bin/Resp.benchmark
export GARNET_NUMA="numactl --cpunodebind=1 --membind=1" CLIENT_NUMA="numactl --cpunodebind=0 --membind=0"
export MEM=32g REPS=2
export CHURN_CLIENTS=${CHURN_CLIENTS:-16}
export LISTCMP_CONFIGS=${LISTCMP_CONFIGS:-baseline viewpooled delta_scan delta}
(cd delta && python3 -I paper_eval.py results ${EXPERIMENTS:-zaddrem savetime rounds scaling}) > run_paper.log 2>&1
touch done.txt
