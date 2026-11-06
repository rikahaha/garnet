using System.Diagnostics;

namespace MemberProtocol;

/// <summary>
/// The protocol's gate. With writer priority, a reader that is about to take the gate while a writer is waiting first
/// lets at least one waiting writer through (or waits until none is waiting). Monitor is not fair: without this, a
/// reader that releases and immediately re-takes the gate between batches can keep a blocked writer waiting for many
/// batches. Writers never wait for each other's priority, and the reader is delayed by at most one writer per batch.
/// </summary>
sealed class Gate(bool writerPriority)
{
    readonly object sync = new();
    int waitingWriters;
    long writerAcquisitions;

    // Diagnostics: gate wait of the calling thread's writer operations, accumulated until taken.
    [ThreadStatic] static long threadWaitTicks;
    internal static long TakeThreadWaitTicks()
    {
        var ticks = threadWaitTicks;
        threadWaitTicks = 0;
        return ticks;
    }

    /// <returns>The timestamp at which the gate was acquired; the wait is added to the thread's diagnostics.</returns>
    internal long EnterWriter(long start)
    {
        if (writerPriority) Interlocked.Increment(ref waitingWriters);
        Monitor.Enter(sync);
        if (writerPriority)
        {
            Interlocked.Decrement(ref waitingWriters);
            Interlocked.Increment(ref writerAcquisitions);
        }
        var acquired = Stopwatch.GetTimestamp();
        threadWaitTicks += acquired - start;
        return acquired;
    }

    internal void EnterReader()
    {
        if (writerPriority && Volatile.Read(ref waitingWriters) > 0)
        {
            var seen = Interlocked.Read(ref writerAcquisitions);
            var spin = new SpinWait();
            while (Volatile.Read(ref waitingWriters) > 0 && Interlocked.Read(ref writerAcquisitions) == seen)
                spin.SpinOnce(sleep1Threshold: -1);
        }
        Monitor.Enter(sync);
    }

    /// <summary>Uncontended administrative entry (Begin, Close, diagnostics).</summary>
    internal void Enter() => Monitor.Enter(sync);
    internal void Exit() => Monitor.Exit(sync);
}

/// <summary>A snapshot view; implementations add their own cursor.</summary>
abstract class SnapshotView(long limit, int count)
{
    internal readonly long Limit = limit;
    internal bool Closed;
    // Diagnostics: gate wait and hold of the reader's batches.
    internal long Reads, ReadWaitTicks, ReadWaitMaxTicks, ReadHoldTicks, ReadHoldMaxTicks, ReadHoldMaxAt, FirstReadHoldTicks;
    public int Count { get; } = count;

    internal void RecordRead(long waitStart, long acquired, long released)
    {
        Reads++;
        ReadWaitTicks += acquired - waitStart;
        ReadWaitMaxTicks = Math.Max(ReadWaitMaxTicks, acquired - waitStart);
        ReadHoldTicks += released - acquired;
        if (Reads == 1) FirstReadHoldTicks = released - acquired;
        if (released - acquired > ReadHoldMaxTicks)
        {
            ReadHoldMaxTicks = released - acquired;
            ReadHoldMaxAt = Reads;  // 1-based batch number
        }
    }
}

/// <summary>Member-level snapshot protocol: writers record a member's boundary score on first change; one reader enumerates the boundary state in batches.</summary>
interface ISnapshotOwner
{
    int Count { get; }
    int Slots { get; }
    void Set(byte[] key, double score);
    bool Remove(byte[] key);
    bool TryGetScore(byte[] key, out double score);
    SnapshotView Begin(Action<IReadOnlyList<Owner.Row>>? captureForTest = null);
    List<Owner.Row> Read(SnapshotView view, int maxNodes = 128, int maxBytes = 16384);
    /// <summary>As <see cref="Read"/>, but clears and fills <paramref name="rows"/> instead of allocating a list.</summary>
    void ReadInto(SnapshotView view, List<Owner.Row> rows, int maxNodes = 128, int maxBytes = 16384);
    bool Done(SnapshotView view);
    void Close(SnapshotView view);
    void ValidateReclaimedForTest();
}

/// <param name="Impl">"object": one Node object per member (<see cref="Owner"/>); "array": segmented arrays (<see cref="ArrayOwner"/>).</param>
/// <param name="WriterPriority">The reader gives way between batches while a writer waits for the gate.</param>
/// <param name="ReuseBatch">Benchmark drivers reuse one batch list instead of allocating one per batch.</param>
sealed record OwnerOptions(string Impl = "object", bool WriterPriority = false, bool ReuseBatch = false)
{
    /// <summary>MEMBER_IMPL (object|array), MEMBER_WRITER_PRIORITY (0|1), MEMBER_REUSE_BATCH (0|1).</summary>
    internal static OwnerOptions FromEnvironment() => new(
        Environment.GetEnvironmentVariable("MEMBER_IMPL") ?? "object",
        Environment.GetEnvironmentVariable("MEMBER_WRITER_PRIORITY") == "1",
        Environment.GetEnvironmentVariable("MEMBER_REUSE_BATCH") == "1");
}

static class OwnerFactory
{
    internal static ISnapshotOwner Create(OwnerOptions options, int maxSlots = 2_000_000, long maxKeyBytes = 128L << 20,
        int maxMemberBytes = 4096, bool borrowKeys = false) => options.Impl switch
    {
        "object" => new Owner(maxSlots, maxKeyBytes, maxMemberBytes, borrowKeys, options.WriterPriority),
        "array" => new ArrayOwner(maxSlots, maxKeyBytes, maxMemberBytes, borrowKeys, options.WriterPriority),
        _ => throw new ArgumentException($"Unknown MEMBER_IMPL {options.Impl}"),
    };

    /// <summary>Run every reader code path once on a small private instance, so that a later checkpoint's first batch
    /// does not JIT-compile while holding the gate. Call at startup.</summary>
    internal static void WarmUpScanPath(OwnerOptions options)
    {
        var owner = Create(options);
        for (int i = 0; i < 300; i++) owner.Set(BitConverter.GetBytes(i), i);
        var view = owner.Begin();
        owner.Set(BitConverter.GetBytes(1), -1);
        owner.Remove(BitConverter.GetBytes(2));
        _ = owner.Read(view);
        owner.ReadInto(view, new List<Owner.Row>());
        owner.Close(view);
    }

    /// <summary>Benchmark scanner: drain a view batch by batch, yielding between batches.</summary>
    internal static void Drain(ISnapshotOwner owner, SnapshotView view, OwnerOptions options)
    {
        var rows = options.ReuseBatch ? new List<Owner.Row>(128) : null;
        try
        {
            while (!owner.Done(view))
            {
                if (rows is null) _ = owner.Read(view);
                else owner.ReadInto(view, rows);
                Thread.Yield();
            }
        }
        finally { owner.Close(view); }
    }
}
