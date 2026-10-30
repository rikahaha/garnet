using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text.Json;

namespace MemberProtocol;

// GC and slow-operation diagnosis for the dictionary+tree adapter, plus a single snapshot scan.
// Variants: "baseline" (dictionary+tree), "owner" (Owner+tree, no scan), "scan1" (Owner+tree, one scan started
// at operation ScanAt).
//
// Two kinds of GC evidence are recorded per operation, and they mean different things:
// - countChanged: a GC collection count changed between reads taken just before and just after the operation.
//   The reads bracket the timed interval, so this is "a collection completed near this operation", not proof
//   that a pause overlapped it.
// - pauseOverlapUs: overlap between the operation's timed interval and a runtime-suspension interval
//   (GCSuspendEEBegin..GCRestartEEEnd) taken from runtime GC events. This is the attribution evidence.
static class GcDiag
{
    const int Operations = 100000, Warmup = 20000, ScanAt = 25000;
    const double SlowUs = 1000, SuspectUs = 100;
    // GCDIAG_SETTLE=1 (default) runs two full blocking GCs after build and warmup, before measuring; 0 does not.
    // Neither setting implies which generation the structure is in: collections during build/warmup are reported.
    static readonly bool Settle = Environment.GetEnvironmentVariable("GCDIAG_SETTLE") != "0";

    /// <summary>Records runtime-suspension intervals of GCs from the runtime's own event source.</summary>
    sealed class GcPauseListener : EventListener
    {
        readonly object sync = new();
        readonly List<(DateTime Start, DateTime End, int Generation)> pauses = new();
        DateTime? suspendStart;
        int lastGeneration = -1;

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, EventLevel.Informational, (EventKeywords)0x1 /* GC */);
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            var name = e.EventName ?? "";
            lock (sync)
            {
                if (name.StartsWith("GCStart", StringComparison.Ordinal) && e.PayloadNames is { } names)
                {
                    var depth = names.IndexOf("Depth");
                    if (depth >= 0) lastGeneration = Convert.ToInt32(e.Payload![depth]);
                }
                else if (name.StartsWith("GCSuspendEEBegin", StringComparison.Ordinal))
                    suspendStart = e.TimeStamp;
                else if (name.StartsWith("GCRestartEEEnd", StringComparison.Ordinal) && suspendStart is { } start)
                {
                    pauses.Add((start, e.TimeStamp, lastGeneration));
                    suspendStart = null;
                }
            }
        }

        public List<(DateTime Start, DateTime End, int Generation)> Snapshot()
        {
            lock (sync) return pauses.ToList();
        }
    }

    static int[] Counts() => [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
    static int[] Delta(int[] to, int[] from) => [to[0] - from[0], to[1] - from[1], to[2] - from[2]];

    /// <param name="windowOps">Size of the comparison window for baseline/owner; pass the scan1 window size of the
    /// same configuration so that percentiles and maxima are taken over equal sample counts.</param>
    internal static void Run(string variant, int count, int length, int windowOps)
    {
        if (variant is not ("baseline" or "owner" or "scan1"))
            throw new ArgumentException(variant);
        using var listener = new GcPauseListener();
        // Map Stopwatch ticks to UTC so operation intervals can be compared with event timestamps.
        long swOrigin = Stopwatch.GetTimestamp();
        var utcOrigin = DateTime.UtcNow;
        DateTime Utc(long ticks) => utcOrigin.AddTicks((long)((ticks - swOrigin) * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency)));

        var atStart = Counts();
        var keys = CostStudy.Keys(count, length);
        var store = new CostStudy.TreeStore(variant != "baseline");
        foreach (var key in keys) store.Set(key, 0);
        var afterBuild = Counts();
        for (int i = 0; i < Warmup; i++) store.Set(keys[i % count], i);
        var afterWarmup = Counts();
        if (Settle)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        var beforeMeasure = Counts();

        using var go = new ManualResetEventSlim();
        long scanStartTicks = 0, scanEndTicks = 0;
        int batches = 0;
        Task? scanner = null;
        if (variant == "scan1")
            scanner = Task.Factory.StartNew(() =>
            {
                go.Wait();
                var owner = store.SnapshotOwner!;
                scanStartTicks = Stopwatch.GetTimestamp();
                var view = owner.Begin();
                try { while (!owner.Done(view)) { _ = owner.Read(view); batches++; Thread.Yield(); } }
                finally { owner.Close(view); }
                scanEndTicks = Stopwatch.GetTimestamp();
            }, TaskCreationOptions.LongRunning);

        var starts = new long[Operations];
        var ends = new long[Operations];
        var countChanged = new sbyte[Operations];  // -1: no change; otherwise highest generation whose count changed
        var pauseBefore = GC.GetTotalPauseDuration();
        for (int i = 0; i < Operations; i++)
        {
            if (i == ScanAt) go.Set();
            int c0 = GC.CollectionCount(0), c1 = GC.CollectionCount(1), c2 = GC.CollectionCount(2);
            long start = Stopwatch.GetTimestamp();
            store.Set(keys[(int)((long)i * 7919 % count)], -i);
            long end = Stopwatch.GetTimestamp();
            starts[i] = start;
            ends[i] = end;
            countChanged[i] = (sbyte)(GC.CollectionCount(2) != c2 ? 2 : GC.CollectionCount(1) != c1 ? 1 : GC.CollectionCount(0) != c0 ? 0 : -1);
        }
        var pauseMs = (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds;
        var afterMeasure = Counts();
        scanner?.GetAwaiter().GetResult();
        store.SnapshotOwner?.ValidateReclaimedForTest();

        // Runtime events are delivered asynchronously; give the listener time to receive the last ones.
        Thread.Sleep(1000);
        var pauses = listener.Snapshot();
        var measureFrom = Utc(starts[0]);
        var measureTo = Utc(ends[^1]);
        var measuredPauses = pauses.Where(p => p.End >= measureFrom && p.Start <= measureTo).ToList();

        // Per-operation overlap with GC suspension intervals; both lists are in time order.
        var overlapUs = new double[Operations];
        var overlapGen = new int[Operations];
        Array.Fill(overlapGen, -1);
        for (int i = 0, p = 0; i < Operations; i++)
        {
            var s = Utc(starts[i]);
            var e = Utc(ends[i]);
            while (p < measuredPauses.Count && measuredPauses[p].End < s) p++;
            for (int q = p; q < measuredPauses.Count && measuredPauses[q].Start <= e; q++)
            {
                var from = measuredPauses[q].Start > s ? measuredPauses[q].Start : s;
                var to = measuredPauses[q].End < e ? measuredPauses[q].End : e;
                if (to > from)
                {
                    overlapUs[i] += (to - from).TotalMilliseconds * 1000;
                    overlapGen[i] = Math.Max(overlapGen[i], measuredPauses[q].Generation);
                }
            }
        }

        double Us(long ticks) => ticks * 1e6 / Stopwatch.Frequency;
        double Latency(int i) => Us(ends[i] - starts[i]);

        // Window: operations whose timed interval overlaps the scan (scan1), so an operation that started just before
        // the scan and then waited for the gate is included; or windowOps operations from ScanAt (others).
        int windowFrom, windowTo;
        bool scanFinishedInRun = true;
        if (variant == "scan1")
        {
            windowFrom = Array.FindIndex(ends, e => e >= scanStartTicks);
            windowTo = Array.FindIndex(starts, s => s > scanEndTicks);
            if (windowTo < 0) { windowTo = Operations; scanFinishedInRun = false; }
            if (windowFrom < 0) windowFrom = windowTo;
        }
        else
            (windowFrom, windowTo) = (ScanAt, Math.Min(Operations, ScanAt + windowOps));

        object Stats(int from, int to)
        {
            var slice = Enumerable.Range(from, to - from).Select(Latency).OrderBy(x => x).ToArray();
            double Q(double p) => slice.Length == 0 ? 0 : slice[Math.Min(slice.Length - 1, (int)(p * slice.Length))];
            int changed = 0, inPause = 0;
            for (int i = from; i < to; i++)
            {
                if (countChanged[i] >= 0) changed++;
                if (overlapUs[i] > 0) inPause++;
            }
            return new { ops = slice.Length, p50Us = Q(.5), p99Us = Q(.99), p999Us = Q(.999), maxUs = slice.Length == 0 ? 0 : slice[^1],
                opsWithCountChange = changed, opsOverlappingPause = inPause };
        }

        object Slow(double thresholdUs)
        {
            int total = 0, countChange = 0, overlapping = 0, mostlyPause = 0, inWindow = 0;
            for (int i = 0; i < Operations; i++)
            {
                var latency = Latency(i);
                if (latency < thresholdUs) continue;
                total++;
                if (countChanged[i] >= 0) countChange++;
                if (overlapUs[i] > 0) overlapping++;
                if (overlapUs[i] >= latency / 2) mostlyPause++;
                if (i >= windowFrom && i < windowTo) inWindow++;
            }
            return new { thresholdUs, total, withCountChange = countChange, overlappingPause = overlapping,
                pauseAtLeastHalf = mostlyPause, inWindow };
        }

        var slowest = Enumerable.Range(0, Operations).OrderByDescending(Latency).Take(10)
            .Select(i => new { index = i, us = Latency(i), countChangedGen = (int)countChanged[i], pauseOverlapUs = overlapUs[i],
                pauseGen = overlapGen[i], inWindow = i >= windowFrom && i < windowTo }).ToArray();

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            variant, count, length, operations = Operations, settle = Settle,
            tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "(unset)",
            overall = Stats(0, Operations),
            window = Stats(windowFrom, windowTo),
            windowFrom, windowTo,
            scanMs = variant == "scan1" ? Us(scanEndTicks - scanStartTicks) / 1000 : 0,
            scanBatches = batches, scanFinishedInRun,
            gc = new
            {
                server = System.Runtime.GCSettings.IsServerGC,
                concurrent = System.Runtime.GCSettings.LatencyMode != System.Runtime.GCLatencyMode.Batch,
                duringBuild = Delta(afterBuild, atStart),
                duringWarmup = Delta(afterWarmup, afterBuild),
                duringMeasure = Delta(afterMeasure, beforeMeasure),
                pauseMs,
                pauseEventsInMeasure = measuredPauses.Count,
                pauseEventsMs = measuredPauses.Sum(p => (p.End - p.Start).TotalMilliseconds),
            },
            slow = new[] { Slow(SlowUs), Slow(SuspectUs) },
            slowest,
        }));
    }
}
