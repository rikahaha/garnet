using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;

namespace MemberProtocol;

// Sustained insert/delete churn: does the Owner's extra per-member object make GC more frequent or its pauses longer?
// Variants: "baseline" (dictionary+tree), "owner" (Owner+tree), "scan1" (Owner+tree, one scan started mid-run).
// All variants execute the identical operation sequence: a fixed seed chooses which live member to delete, every
// delete is followed by an insert of a fresh member, so the live member count stays at `count`.
static class ChurnDiag
{
    const int Seed = 42;
    const double SlowUs = 1000, SuspectUs = 100;

    static byte[] NewKey(int id, int length)
    {
        var key = new byte[length];
        key.AsSpan().Fill(97);
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(length - 4), id);
        return key;
    }

    internal static void Run(string variant, int count, int length, int operations, int warmup)
    {
        if (variant is not ("baseline" or "owner" or "scan1"))
            throw new ArgumentException(variant);
        if (length < 4 || operations % 2 != 0 || warmup % 2 != 0)
            throw new ArgumentException("length >= 4 and even operation counts required");
        using var listener = new GcDiag.GcPauseListener();
        long swOrigin = Stopwatch.GetTimestamp();
        var utcOrigin = DateTime.UtcNow;
        DateTime Utc(long ticks) => utcOrigin.AddTicks((long)((ticks - swOrigin) * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency)));

        var random = new Random(Seed);
        var live = CostStudy.Keys(count, length);
        int nextId = count, slot = -1;
        var store = new CostStudy.TreeStore(variant != "baseline");
        foreach (var key in live) store.Set(key, random.NextDouble());

        // One step of the fixed sequence: even steps delete a random live member, odd steps insert a fresh one in its place.
        // The fresh member array is allocated by the caller before timing, as a server would when parsing the request.
        byte[]? Prepare(int step) => step % 2 == 0 ? null : NewKey(nextId++, length);
        void Execute(byte[]? fresh)
        {
            if (fresh is null)
            {
                slot = random.Next(count);
                if (!store.Remove(live[slot])) throw new Exception("Missing live member");
            }
            else
            {
                store.Set(fresh, random.NextDouble());
                live[slot] = fresh;
            }
        }

        for (int i = 0; i < warmup; i++) Execute(Prepare(i));
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        using var go = new ManualResetEventSlim();
        long scanStartTicks = 0, scanEndTicks = 0;
        Task? scanner = null;
        if (variant == "scan1")
            scanner = Task.Factory.StartNew(() =>
            {
                go.Wait();
                var owner = store.SnapshotOwner!;
                scanStartTicks = Stopwatch.GetTimestamp();
                var view = owner.Begin();
                try { while (!owner.Done(view)) { _ = owner.Read(view); Thread.Yield(); } }
                finally { owner.Close(view); }
                scanEndTicks = Stopwatch.GetTimestamp();
            }, TaskCreationOptions.LongRunning);

        var starts = new long[operations];
        var ends = new long[operations];
        int[] gcBefore = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var pauseBefore = GC.GetTotalPauseDuration();
        long runStart = Stopwatch.GetTimestamp();
        for (int i = 0; i < operations; i++)
        {
            if (i == operations / 2) go.Set();
            var fresh = Prepare(i);
            long start = Stopwatch.GetTimestamp();
            Execute(fresh);
            ends[i] = Stopwatch.GetTimestamp();
            starts[i] = start;
        }
        long runEnd = Stopwatch.GetTimestamp();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var pauseMs = (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds;
        int[] gcDuring = [GC.CollectionCount(0) - gcBefore[0], GC.CollectionCount(1) - gcBefore[1], GC.CollectionCount(2) - gcBefore[2]];
        scanner?.GetAwaiter().GetResult();
        store.SnapshotOwner?.ValidateReclaimedForTest();
        var heap = GC.GetGCMemoryInfo();

        Thread.Sleep(1000);  // let the listener receive the last runtime events
        var measureFrom = Utc(starts[0]);
        var measureTo = Utc(ends[^1]);
        var pauses = listener.Snapshot().Where(p => p.End >= measureFrom && p.Start <= measureTo).ToList();

        var overlapUs = new double[operations];
        for (int i = 0, p = 0; i < operations; i++)
        {
            var s = Utc(starts[i]);
            var e = Utc(ends[i]);
            while (p < pauses.Count && pauses[p].End < s) p++;
            for (int q = p; q < pauses.Count && pauses[q].Start <= e; q++)
            {
                var from = pauses[q].Start > s ? pauses[q].Start : s;
                var to = pauses[q].End < e ? pauses[q].End : e;
                if (to > from) overlapUs[i] += (to - from).TotalMilliseconds * 1000;
            }
        }

        double Us(long ticks) => ticks * 1e6 / Stopwatch.Frequency;
        var latency = Enumerable.Range(0, operations).Select(i => Us(ends[i] - starts[i])).ToArray();

        object Dist(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(x => x).ToArray();
            double Q(double p) => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(p * sorted.Length))];
            return new { n = sorted.Length, p50 = Q(.5), p99 = Q(.99), p999 = Q(.999), max = sorted.Length == 0 ? 0 : sorted[^1], sum = sorted.Sum() };
        }

        object Slow(double thresholdUs)
        {
            int total = 0, overlapping = 0, mostlyPause = 0;
            for (int i = 0; i < operations; i++)
            {
                if (latency[i] < thresholdUs) continue;
                total++;
                if (overlapUs[i] > 0) overlapping++;
                if (overlapUs[i] >= latency[i] / 2) mostlyPause++;
            }
            return new { thresholdUs, total, overlappingPause = overlapping, pauseAtLeastHalf = mostlyPause };
        }

        // Affinity is only exposed on Linux and Windows; on macOS it is reported as null.
        int[]? affinity = null;
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
        {
            var mask = (long)Process.GetCurrentProcess().ProcessorAffinity;
            affinity = Enumerable.Range(0, 64).Where(b => (mask & (1L << b)) != 0).ToArray();
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            variant, count, length, operations, warmup, seed = Seed,
            environment = new
            {
                processorCount = Environment.ProcessorCount,
                affinity,
                gcConfiguration = GC.GetConfigurationVariables().ToDictionary(kv => kv.Key, kv => kv.Value.ToString()),
                tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "(unset)",
                numa = Environment.GetEnvironmentVariable("CHURN_NUMA") ?? "(not recorded)",
            },
            completedOperations = operations,
            elapsedMs = Us(runEnd - runStart) / 1000,
            allocatedBytes = allocated,
            allocatedBytesPerOperation = allocated / (double)operations,
            heapSizeBytesAfter = heap.HeapSizeBytes,
            peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
            gc = new { gen0 = gcDuring[0], gen1 = gcDuring[1], gen2 = gcDuring[2], totalPauseMs = pauseMs },
            pausesUs = Dist(pauses.Select(p => (p.End - p.Start).TotalMilliseconds * 1000)),
            pausesByGeneration = pauses.GroupBy(p => p.Generation).OrderBy(g => g.Key)
                .ToDictionary(g => $"gen{g.Key}", g => Dist(g.Select(p => (p.End - p.Start).TotalMilliseconds * 1000))),
            latencyUs = Dist(latency),
            latencyOverlappingPauseUs = Dist(Enumerable.Range(0, operations).Where(i => overlapUs[i] > 0).Select(i => latency[i])),
            latencyNotOverlappingPauseUs = Dist(Enumerable.Range(0, operations).Where(i => overlapUs[i] == 0).Select(i => latency[i])),
            slow = new[] { Slow(SlowUs), Slow(SuspectUs) },
            scanMs = variant == "scan1" ? Us(scanEndTicks - scanStartTicks) / 1000 : 0,
        }));
    }
}
