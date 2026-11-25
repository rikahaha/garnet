#!/bin/bash
# Multi-client throughput across snapshot checkpoints: baseline vs snapshot views vs snapshot views + pooled bytes.
# Step 1 (sweep): baseline only, no checkpoint, 100K members, CLIENTS_SWEEP client counts, to see where the server saturates.
# Step 2 (main): each config x set size x REPS, CLIENTS clients, BGSAVE at 5 s and 12 s of a 20 s run.
# Environment as for throughput_run.py (GARNET_BIN, GARNET_NUMA, BENCH_BIN, CLIENT_NUMA). Results go to results/.
set -e
cd "$(dirname "$0")"
mkdir -p results
REPS=${REPS:-3}
CLIENTS=${CLIENTS:-16}
CLIENTS_SWEEP=${CLIENTS_SWEEP:-"4 8 16 32"}

if [ "${SKIP_SWEEP:-0}" != 1 ]; then
  for c in $CLIENTS_SWEEP; do
    echo "### sweep clients=$c"
    python3 -I throughput_run.py "results/sweep_c$c" members=100000 sets=20 clients=$c pipeline=1 readpct=50 run=10 saveat=
    python3 -c "import json; d=json.load(open('results/sweep_c$c.json')); print(f\"  {d['totalOps']/d['runS']/1000:.1f} kops/s\")"
  done
fi

for rep in $(seq 1 "$REPS"); do
  for m in 100000 1000000; do
    for config in baseline view viewpooled; do
      case $config in
        baseline)   member=0; pooled=0 ;;
        view)       member=1; pooled=0 ;;
        viewpooled) member=1; pooled=1 ;;
      esac
      out="results/${config}_m${m}_r${rep}"
      echo "### config=$config members=$m rep=$rep"
      GARNET_SORTEDSET_MEMBER_CHECKPOINT=$member TSAVORITE_SERIALIZE_POOLED=$pooled \
        python3 -I throughput_run.py "$out" members=$m sets=20 clients=$CLIENTS pipeline=1 readpct=50 run=20 saveat=5,12
      python3 -I throughput_analyze.py "$out"
    done
  done
done
