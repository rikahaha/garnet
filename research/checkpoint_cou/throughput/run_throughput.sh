#!/bin/bash
# Multi-client throughput across snapshot checkpoints: baseline vs snapshot views vs snapshot views + pooled bytes
# (vs delta: views + pooled + side store chains). Environment SIZES and CONFIGS select set sizes and configurations.
# Step 1 (sweep): baseline only, no checkpoint, 100K members, CLIENTS_SWEEP client counts, to see where the server saturates.
# Step 2 (main): each config x set size x REPS, CLIENTS clients, BGSAVE at 5 s and 12 s of a 20 s run.
# Environment as for throughput_run.py (GARNET_BIN, GARNET_NUMA, BENCH_BIN, CLIENT_NUMA). Results go to $RESULTS (default results/).
set -e
cd "$(dirname "$0")"
RESULTS=${RESULTS:-results}
mkdir -p "$RESULTS"
REPS=${REPS:-3}
CLIENTS=${CLIENTS:-16}
CLIENTS_SWEEP=${CLIENTS_SWEEP:-"4 8 16 32"}

if [ "${SKIP_SWEEP:-0}" != 1 ]; then
  for c in $CLIENTS_SWEEP; do
    echo "### sweep clients=$c"
    python3 -I throughput_run.py "$RESULTS/sweep_c$c" members=100000 sets=20 clients=$c pipeline=1 readpct=50 run=10 saveat=
    python3 -c "import json; d=json.load(open('$RESULTS/sweep_c$c.json')); print(f\"  {d['totalOps']/d['runS']/1000:.1f} kops/s\")"
  done
fi

SIZES=${SIZES:-"100000 1000000"}
CONFIGS=${CONFIGS:-"baseline view viewpooled"}
for rep in $(seq 1 "$REPS"); do
  for m in $SIZES; do
    for config in $CONFIGS; do
      delta=0
      case $config in
        baseline)   member=0; pooled=0 ;;
        view)       member=1; pooled=0 ;;
        viewpooled) member=1; pooled=1 ;;
        delta)      member=1; pooled=1; delta=1 ;;
      esac
      out="$RESULTS/${config}_m${m}_r${rep}"
      echo "### config=$config members=$m rep=$rep"
      GARNET_SORTEDSET_MEMBER_CHECKPOINT=$member TSAVORITE_SERIALIZE_POOLED=$pooled GARNET_SORTEDSET_DELTA=$delta \
        python3 -I throughput_run.py "$out" members=$m sets=20 clients=$CLIENTS pipeline=1 readpct=50 run=20 saveat=5,12
      python3 -I throughput_analyze.py "$out"
    done
  done
done
