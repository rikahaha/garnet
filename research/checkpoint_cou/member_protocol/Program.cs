using System.Diagnostics;
using System.Text;
using MemberProtocol;

static class Program
{
    static byte[] Key(int i) => Encoding.UTF8.GetBytes($"member-{i:D8}");
    static string Name(ReadOnlyMemory<byte> key) => Encoding.UTF8.GetString(key.Span);
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    static void Reject(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Expected rejection");
    }

    static void Tests(OwnerOptions options)
    {
        ISnapshotOwner New(int maxSlots = 2_000_000, long maxKeyBytes = 128L << 20) => OwnerFactory.Create(options, maxSlots, maxKeyBytes);
        var owner = New();
        var model = new Dictionary<string, double>();
        void Set(int i, double score) { owner.Set(Key(i), score); model[Name(Key(i))] = score; }
        void Remove(int i) { owner.Remove(Key(i)); model.Remove(Name(Key(i))); }
        for (int i = 0; i < 500; i++) Set(i, i);
        // The consumer and mutator are distinct tasks, but their ordering is explicit:
        // each completed batch is followed by a completed mutation phase.
        for (int round = 0; round < 4; round++)
        {
            var expected = new Dictionary<string, double>(model);
            var view = owner.Begin();
            Reject(() => owner.Begin());
            var result = new Dictionary<string, double>();
            int batch = 0;
            while (!owner.Done(view))
            {
                var rows = Task.Run(() => owner.Read(view, 7)).GetAwaiter().GetResult();
                Task.Run(() =>
                {
                    Set(0, -round - batch); // already scanned
                    Set(499, -1); Set(499, -2); // not yet scanned, repeated update
                    Remove(250); Set(250, 123); // new identity on every reinsertion
                    Remove(251); // deleted boundary node must still be discovered
                    Set(2000 + batch, batch);
                    if (batch == 0)
                        for (int i = 3000; i < 13000; i++) Set(i, i); // dictionary growth
                }).GetAwaiter().GetResult();
                foreach (var row in rows)
                    Check(result.TryAdd(Name(row.Key), row.Score), "Duplicate member");
                batch++;
            }
            Check(view.Count == expected.Count && result.Count == expected.Count, "Wrong count");
            Check(expected.All(kv => result.TryGetValue(kv.Key, out var score) && score == kv.Value), "Wrong boundary state");
            owner.Close(view);
            Check(owner.Slots == owner.Count, "Retained deleted nodes after close");
            Reject(() => owner.Read(view));
        }
        // Failure after a partial batch; abort drains without publishing any snapshot.
        var aborted = owner.Begin();
        _ = owner.Read(aborted, 3);
        for (int i = 0; i < 500; i++) Remove(i);
        owner.Close(aborted);
        Check(owner.Slots == owner.Count, "Abort leaked nodes");
        var retry = owner.Begin();
        int recovered = 0;
        while (!owner.Done(retry)) recovered += owner.Read(retry).Count;
        Check(recovered == model.Count, "Retry after abort");
        owner.Close(retry);

        var bounded = New(maxSlots: 2, maxKeyBytes: 32);
        var input = Key(1);
        bounded.Set(input, 1);
        input[0] = 0; // owner owns a copy
        var snap = bounded.Begin();
        bounded.Remove(Key(1)); bounded.Set(Key(1), 2);
        Reject(() => bounded.Set(Key(2), 3));
        Check(bounded.Count == 1 && snap.Count == 1, "Budget rejection mutated state");
        var saved = bounded.Read(snap);
        Check(saved.Count == 1 && saved[0].Score == 1 && Name(saved[0].Key) == Name(Key(1)), "Byte ownership/reinsert");
        bounded.Close(snap);
        Check(bounded.Slots == 1 && saved[0].Score == 1, "Batch lifetime");
        bounded.Set(Key(2), 3);
        var empty = New();
        var emptyView = empty.Begin(); empty.Set(Key(1), 1);
        Check(empty.Done(emptyView) && emptyView.Count == 0, "Empty boundary"); empty.Close(emptyView);
        var payloadBound = New(maxKeyBytes: 1);
        Reject(() => payloadBound.Set(Key(0), 0));
        Check(payloadBound.Count == 0, "Payload budget");
        for (int seed = 0; seed < 100; seed++)
        {
            var random = new Random(seed);
            var subject = New();
            var reference = new Dictionary<string, double>();
            var reused = new List<Owner.Row>();  // alternate Read and ReadInto: a reused batch list must not leak rows
            for (int generation = 0; generation < 3; generation++)
            {
                for (int i = 0; i < 50; i++)
                {
                    subject.Set(Key(i), i);
                    reference[Name(Key(i))] = i;
                }
                var boundary = new Dictionary<string, double>(reference);
                var v = subject.Begin();
                var output = new Dictionary<string, double>();
                for (int step = 0; step < 200; step++)
                {
                    int id = random.Next(100);
                    if (random.Next(3) == 0)
                    {
                        subject.Remove(Key(id)); reference.Remove(Name(Key(id)));
                    }
                    else
                    {
                        subject.Set(Key(id), step); reference[Name(Key(id))] = step;
                    }
                    if (random.Next(5) == 0)
                    {
                        List<Owner.Row> rows;
                        if (random.Next(2) == 0) rows = subject.Read(v, random.Next(1, 5));
                        else { subject.ReadInto(v, reused, random.Next(1, 5)); rows = reused; }
                        foreach (var row in rows)
                            Check(output.TryAdd(Name(row.Key), row.Score), "Seeded duplicate");
                    }
                }
                while (!subject.Done(v))
                    foreach (var row in subject.Read(v, 3))
                        Check(output.TryAdd(Name(row.Key), row.Score), "Seeded duplicate at drain");
                Check(output.Count == v.Count && boundary.Count == output.Count &&
                    boundary.All(kv => output.TryGetValue(kv.Key, out var score) && score == kv.Value), $"Seed {seed}, generation {generation}");
                subject.Close(v);
                Check(subject.Slots == subject.Count, "Seeded reclamation");
            }
        }
        Console.WriteLine($"PASS [{options}]: 4 snapshots, ordered two-task interleavings, update/delete/reinsert/growth, abort/retry, ownership, budgets, reclamation");
        Console.WriteLine($"PASS [{options}]: 100 fixed seeds x 3 generations x 200 mutation/scan steps against full reference model");
    }

    static double Ns(long ticks, int n) => ticks * (1e9 / Stopwatch.Frequency) / n;
    static long Heap()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetTotalMemory(false);
    }
    static void Bench(int count, int operations)
    {
        var keys = Enumerable.Range(0, count).Select(Key).ToArray();
        long heapBefore = Heap();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var baseline = new Dictionary<byte[], double>(new Owner.Bytes());
        foreach (var key in keys) baseline.Add(key.ToArray(), 1);
        long baselineBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        long baselineHeap = Heap() - heapBefore;
        heapBefore = Heap();
        before = GC.GetAllocatedBytesForCurrentThread();
        var owner = new Owner();
        foreach (var key in keys) owner.Set(key, 1);
        long ownerBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        long ownerHeap = Heap() - heapBefore;
        // Same byte comparer and private key copies; simple monitor baseline has no
        // instrumentation. This is still not Garnet or a SortedSet benchmark.
        var baselineGate = new object();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < operations; i++)
            lock (baselineGate) baseline[keys[i % count]] = i;
        long baselineTicks = Stopwatch.GetTimestamp() - start;
        start = Stopwatch.GetTimestamp();
        for (int i = 0; i < operations; i++) owner.Set(keys[i % count], i);
        long normalTicks = Stopwatch.GetTimestamp() - start;
        foreach (var coverage in new[] { 0.01, 0.80 })
        {
            var view = owner.Begin();
            long captures = owner.Captures, captureTicks = owner.CaptureTicks;
            long holds = owner.GateHoldTicks, waits = owner.GateWaitTicks;
            int touched = Math.Max(1, (int)(count * coverage));
            start = Stopwatch.GetTimestamp();
            for (int i = 0; i < operations; i++) owner.Set(keys[i % touched], -i);
            long checkpointTicks = Stopwatch.GetTimestamp() - start;
            long measuredHolds = owner.GateHoldTicks - holds, measuredWaits = owner.GateWaitTicks - waits;
            start = Stopwatch.GetTimestamp();
            int rows = 0;
            while (!owner.Done(view)) rows += owner.Read(view).Count;
            long scanTicks = Stopwatch.GetTimestamp() - start;
            owner.Close(view);
            Check(rows == count, "Benchmark count");
            Console.WriteLine(FormattableString.Invariant($"{count},{coverage},{baselineBytes},{ownerBytes},{(ownerBytes-baselineBytes)/(double)count:F2},{baselineHeap},{ownerHeap},{(ownerHeap-baselineHeap)/(double)count:F2},{Ns(baselineTicks,operations):F2},{Ns(normalTicks,operations):F2},{Ns(checkpointTicks,operations):F2},{owner.Captures-captures},{Ns(owner.CaptureTicks-captureTicks,(int)(owner.Captures-captures)):F2},{Ns(measuredHolds,operations):F2},{Ns(measuredWaits,operations):F2},{Ns(scanTicks,count):F2}"));
        }
        GC.KeepAlive(baseline);
    }

    static void Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--memory")
        {
            CostStudy.Memory(args[1], int.Parse(args[2]), int.Parse(args[3]));
            return;
        }
        if (args.Length == 4 && args[0] == "--tree-cost")
        {
            CostStudy.Timing(args[1] != "baseline", args[1] == "scan", int.Parse(args[2]), int.Parse(args[3]));
            return;
        }
        if (args.Length is 4 or 5 && args[0] == "--gc-diag")
        {
            GcDiag.Run(args[1], int.Parse(args[2]), int.Parse(args[3]), args.Length == 5 ? int.Parse(args[4]) : 600);
            return;
        }
        if (args.Length is 4 or 6 && args[0] == "--churn")
        {
            ChurnDiag.Run(args[1], int.Parse(args[2]), int.Parse(args[3]),
                args.Length == 6 ? int.Parse(args[4]) : 1_000_000, args.Length == 6 ? int.Parse(args[5]) : 200_000);
            return;
        }
        // Correctness runs on every implementation and gate mode.
        OwnerOptions[] all = [new("object"), new("object", WriterPriority: true), new("array"), new("array", WriterPriority: true)];
        if (args.SequenceEqual(new[] { "--stress-only" }))
        {
            foreach (var options in all) ConcurrentStress.Run(options);
            return;
        }
        foreach (var options in all) Tests(options);
        foreach (var options in all) ConcurrentStress.Run(options);
        if (args.SequenceEqual(new[] { "--tests-only" })) return;
        Console.WriteLine("members,coverage,baseline_init_alloc_bytes,owner_init_alloc_bytes,extra_init_alloc_per_member,baseline_heap_delta,owner_heap_delta,extra_heap_per_member,baseline_update_ns,owner_normal_update_ns,owner_snapshot_update_ns,captures,capture_ns,set_gate_hold_ns,set_gate_wait_ns,scan_ns_per_member");
        Bench(1000, 10000); // warmup; retain separately from larger measurements
        Bench(100000, 200000);
    }
}
