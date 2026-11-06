using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace MemberProtocol;

static class CostStudy
{
    // Explicit static roots prevent early JIT liveness decisions changing heap deltas.
    static object? root;
    static byte[][]? keyRoot;
    internal sealed class Order : IComparer<(double Score, byte[] Key)>
    {
        public int Compare((double Score, byte[] Key) a, (double Score, byte[] Key) b)
        {
            int score = a.Score.CompareTo(b.Score);
            return score != 0 ? score : a.Key.AsSpan().SequenceCompareTo(b.Key);
        }
    }
    /// <summary>Implementation and gate options of every protocol instance in the benchmarks (MEMBER_* variables).</summary>
    internal static readonly OwnerOptions Options = OwnerOptions.FromEnvironment();

    internal sealed class TreeStore(bool protocol)
    {
        readonly object gate = new();
        readonly Dictionary<byte[], double> dictionary = new(new Owner.Bytes());
        readonly SortedSet<(double Score, byte[] Key)> tree = new(new Order());
        internal readonly ISnapshotOwner? SnapshotOwner = protocol ? OwnerFactory.Create(Options, borrowKeys: true) : null;
        internal void Set(byte[] key, double score)
        {
            lock (gate)
            {
                double old;
                bool found = SnapshotOwner is null ? dictionary.TryGetValue(key, out old) : SnapshotOwner.TryGetScore(key, out old);
                if (found && !tree.Remove((old, key))) throw new Exception("Tree divergence");
                if (SnapshotOwner is null) dictionary[key] = score; else SnapshotOwner.Set(key, score);
                if (!tree.Add((score, key))) throw new Exception("Tree duplicate");
            }
        }
        internal bool Remove(byte[] key)
        {
            lock (gate)
            {
                double old;
                bool found = SnapshotOwner is null ? dictionary.TryGetValue(key, out old) : SnapshotOwner.TryGetScore(key, out old);
                if (!found) return false;
                if (!tree.Remove((old, key))) throw new Exception("Tree divergence");
                if (SnapshotOwner is null) dictionary.Remove(key); else SnapshotOwner.Remove(key);
                return true;
            }
        }
    }
    internal static byte[][] Keys(int count, int length) => Enumerable.Range(0, count).Select(i =>
    {
        var key = new byte[length];
        key.AsSpan().Fill(97);
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(length - 4), i);
        return key;
    }).ToArray();
    static long Heap()
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        var info = GC.GetGCMemoryInfo();
        return info.HeapSizeBytes - info.FragmentedBytes;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static object Build(string kind, byte[][] keys)
    {
        if (kind == "nodes") return keys.Select((key, i) => new Owner.Node(key, i, i)).ToArray();
        if (kind == "dict")
        {
            var dict = new Dictionary<byte[], double>(new Owner.Bytes());
            foreach (var key in keys) dict.Add(key, 0);
            return dict;
        }
        if (kind is "owner-copy" or "owner-share")
        {
            var owner = OwnerFactory.Create(Options, borrowKeys: kind == "owner-share");
            foreach (var key in keys) owner.Set(key, 0);
            return owner;
        }
        var store = new TreeStore(kind == "tree-owner");
        foreach (var key in keys) store.Set(key, 0);
        return store;
    }
    internal static void Memory(string kind, int count, int length)
    {
        keyRoot = Keys(count, length);
        // Warm up code/runtime before the measured allocation and full-GC delta.
        root = Build(kind, Keys(100, length)); root = null;
        long before = Heap(), allocated = GC.GetAllocatedBytesForCurrentThread();
        root = Build(kind, keyRoot);
        long initialization = GC.GetAllocatedBytesForCurrentThread() - allocated;
        long retained = Heap() - before;
        long snapshot = 0, deleted = 0, afterClose = 0;
        if (root is ISnapshotOwner owner)
        {
            var view = owner.Begin();
            snapshot = Heap() - before;
            for (int i = 0; i < count / 2; i++) owner.Remove(keyRoot[i]);
            deleted = Heap() - before;
            owner.Close(view);
            owner.ValidateReclaimedForTest();
            afterClose = Heap() - before;
            GC.KeepAlive(view);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { kind, impl = Options.Impl, count, length, initialization, retained,
            snapshot, deleted, afterClose, peakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64 }));
        GC.KeepAlive(root); GC.KeepAlive(keyRoot);
    }
    internal static void Timing(bool protocol, bool scan, int count, int length)
    {
        var keys = Keys(count, length);
        var store = new TreeStore(protocol);
        foreach (var key in keys) store.Set(key, 0);
        // Equal warmup of the full tree update path.
        for (int i = 0; i < 20000; i++) store.Set(keys[i % count], i);
        using var cancel = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        long scans = 0;
        Task? scanner = null;
        if (scan)
            scanner = Task.Factory.StartNew(() =>
            {
                do
                {
                    var owner = store.SnapshotOwner!;
                    var view = owner.Begin();
                    started.Set();
                    try { while (!owner.Done(view)) { _ = owner.Read(view); Thread.Yield(); } }
                    finally { owner.Close(view); }
                    Interlocked.Increment(ref scans);
                } while (!cancel.IsCancellationRequested);
            }, TaskCreationOptions.LongRunning);
        if (scan) started.Wait();
        const int operations = 100000;
        var samples = new long[operations];
        long startAll = Stopwatch.GetTimestamp();
        try
        {
            for (int i = 0; i < operations; i++)
            {
                long start = Stopwatch.GetTimestamp();
                store.Set(keys[(int)((long)i * 7919 % count)], -i);
                samples[i] = Stopwatch.GetTimestamp() - start;
            }
        }
        finally { cancel.Cancel(); scanner?.GetAwaiter().GetResult(); }
        // Overall elapsed includes scanner drain; per-operation samples exclude it.
        double seconds = (Stopwatch.GetTimestamp() - startAll) / (double)Stopwatch.Frequency;
        Array.Sort(samples);
        double Ns(long value) => value * (1e9 / Stopwatch.Frequency);
        Console.WriteLine(JsonSerializer.Serialize(new { protocol, scan, count, length, operations,
            medianNs = Ns(samples[operations / 2]), p99Ns = Ns(samples[(int)(operations * .99) - 1]),
            p999Ns = Ns(samples[(int)(operations * .999) - 1]), maxNs = Ns(samples[^1]),
            opsPerSecondIncludingDrain = operations / seconds, scans }));
        store.SnapshotOwner?.ValidateReclaimedForTest();
    }
}
