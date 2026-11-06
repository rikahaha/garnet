using System.Diagnostics;

namespace MemberProtocol;

// Standalone protocol only. No Garnet, CPR, persistence or command integration.
// One Node object per member; see ArrayOwner for the segmented-array layout of the same protocol.
sealed class Owner : ISnapshotOwner
{
    internal sealed class Bytes : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] x)
        {
            var hash = new HashCode();
            hash.AddBytes(x);
            return hash.ToHashCode();
        }
    }

    internal sealed class Node(byte[] key, double score, long id)
    {
        internal readonly byte[] Key = key;
        internal readonly long Id = id;
        internal double Score = score, OldScore;
        internal bool HasOld, Live = true;
        internal Node? Prev, Next;
    }

    // Key storage is privately owned and never mutated or pooled. Consumers must not
    // mutate memory through interop; references remain valid after unlink/close.
    internal readonly record struct Row(ReadOnlyMemory<byte> Key, double Score);
    internal sealed class View(Owner source, long limit, int count, Node? cursor) : SnapshotView(limit, count)
    {
        internal readonly Owner Source = source;
        internal Node? Cursor = cursor;
    }
    readonly Gate gate;
    readonly Dictionary<byte[], Node> live = new(new Bytes());
    readonly int maxSlots, maxMemberBytes;
    readonly long maxKeyBytes;
    readonly bool borrowKeys;
    Node? head, tail;
    View? active;
    long lastId, keyBytes;
    int slots;
    public long Captures { get; private set; }
    public long CaptureTicks { get; private set; }
    public long GateWaitTicks { get; private set; }
    public long GateHoldTicks { get; private set; }

    internal static long TakeThreadGateWaitTicks() => Gate.TakeThreadWaitTicks();

    public int Slots { get { gate.Enter(); try { return slots; } finally { gate.Exit(); } } }
    public long KeyBytes { get { gate.Enter(); try { return keyBytes; } finally { gate.Exit(); } } }
    public int Count { get { gate.Enter(); try { return live.Count; } finally { gate.Exit(); } } }

    public Owner(int maxSlots = 2_000_000, long maxKeyBytes = 128L << 20, int maxMemberBytes = 4096, bool borrowKeys = false,
        bool writerPriority = false)
    {
        if (maxSlots <= 0 || maxKeyBytes < 0 || maxMemberBytes <= 0 || maxMemberBytes > 16384)
            throw new ArgumentOutOfRangeException();
        (this.maxSlots, this.maxKeyBytes, this.maxMemberBytes) = (maxSlots, maxKeyBytes, maxMemberBytes);
        // Benchmark only: caller guarantees these arrays remain immutable and alive.
        this.borrowKeys = borrowKeys;
        gate = new Gate(writerPriority);
    }

    public void Set(byte[] key, double score)
    {
        var start = Stopwatch.GetTimestamp();
        var acquired = gate.EnterWriter(start);
        GateWaitTicks += acquired - start;
        try
        {
            if (live.TryGetValue(key, out var node))
            {
                Capture(node);
                node.Score = score;
                return;
            }
            // Budget rejection is before publication/mutation. Slot retention is
            // bounded even if insert/delete runs forever during a paused snapshot.
            if (key.Length > maxMemberBytes || slots >= maxSlots || key.Length > maxKeyBytes - keyBytes)
                throw new InvalidOperationException("Protocol capacity exceeded; mutation rejected");
            var id = checked(lastId + 1);
            node = new Node(borrowKeys ? key : key.ToArray(), score, id);
            live.Add(node.Key, node);
            lastId = id;
            node.Prev = tail;
            if (tail is null) head = node;
            else tail.Next = node;
            tail = node;
            slots++;
            keyBytes += node.Key.Length;
        }
        finally
        {
            GateHoldTicks += Stopwatch.GetTimestamp() - acquired;
            gate.Exit();
        }
    }

    public bool Remove(byte[] key)
    {
        gate.EnterWriter(Stopwatch.GetTimestamp());
        try
        {
            if (!live.TryGetValue(key, out var node)) return false;
            Capture(node);
            live.Remove(key);
            node.Live = false;
            if (!Pending(node)) Unlink(node);
            return true;
        }
        finally { gate.Exit(); }
    }

    public bool TryGetScore(byte[] key, out double score)
    {
        gate.EnterWriter(Stopwatch.GetTimestamp());
        try
        {
            if (live.TryGetValue(key, out var node)) { score = node.Score; return true; }
            score = 0;
            return false;
        }
        finally { gate.Exit(); }
    }

    bool Pending(Node node) => active is { Closed: false } view && node.Id <= view.Limit
        && view.Cursor is Node next && node.Id >= next.Id;

    void Capture(Node node)
    {
        if (!Pending(node) || node.HasOld) return;
        var start = Stopwatch.GetTimestamp();
        node.OldScore = node.Score;
        node.HasOld = true;
        Captures++;
        CaptureTicks += Stopwatch.GetTimestamp() - start;
    }

    public SnapshotView Begin(Action<IReadOnlyList<Row>>? captureForTest = null)
    {
        gate.Enter();
        try
        {
            if (active is not null) throw new InvalidOperationException("One active snapshot only");
            // Test oracle only: full copy under the same gate as the boundary.
            // Normal callers pass null and never enumerate/copy the live dictionary.
            // Invoke before publishing active so a throwing hook leaves no active view.
            if (captureForTest is not null)
                captureForTest(live.Values.Select(node => new Row(node.Key.ToArray(), node.Score)).ToArray());
            active = new View(this, lastId, live.Count, head);
            return active;
        }
        finally { gate.Exit(); }
    }

    // At most maxNodes visited, at most maxBytes payload retained by this result.
    // Encoding/output belongs to caller outside gate. Caller must bound outstanding batches.
    public List<Row> Read(SnapshotView view, int maxNodes = 128, int maxBytes = 16384)
    {
        var rows = new List<Row>(maxNodes);
        ReadInto(view, rows, maxNodes, maxBytes);
        return rows;
    }

    public void ReadInto(SnapshotView view, List<Row> rows, int maxNodes = 128, int maxBytes = 16384)
    {
        if (maxNodes <= 0 || maxNodes > 1024 || maxBytes < maxMemberBytes || maxBytes > 1048576)
            throw new ArgumentOutOfRangeException();
        rows.Clear();
        var waitStart = Stopwatch.GetTimestamp();
        gate.EnterReader();
        var acquired = Stopwatch.GetTimestamp();
        try { ReadLocked(view, rows, maxNodes, maxBytes); }
        finally
        {
            view.RecordRead(waitStart, acquired, Stopwatch.GetTimestamp());
            gate.Exit();
        }
    }

    void ReadLocked(SnapshotView snapshot, List<Row> rows, int maxNodes, int maxBytes)
    {
        var view = Validate(snapshot);
        int bytes = 0, visited = 0;
        while (view.Cursor is Node node && node.Id <= view.Limit && visited < maxNodes)
        {
            if (node.Key.Length > maxBytes - bytes) break;
            rows.Add(new Row(node.Key, node.HasOld ? node.OldScore : node.Score));
            bytes += node.Key.Length;
            visited++;
            view.Cursor = node.Next;
            node.HasOld = false;
            if (!node.Live) Unlink(node);
        }
        // Never retain a pointer to a new node: it can be removed immediately.
        if (view.Cursor is Node next && next.Id > view.Limit) view.Cursor = null;
    }

    public bool Done(SnapshotView view)
    {
        gate.EnterReader();
        try { return Validate(view).Cursor is null; }
        finally { gate.Exit(); }
    }

    // Finish/abort drain in bounded critical sections. Writers keep running;
    // Begin remains rejected until all retained boundary nodes have been released.
    public void Close(SnapshotView view)
    {
        while (!Done(view)) _ = Read(view);
        gate.Enter();
        try { Validate(view).Closed = true; active = null; }
        finally { gate.Exit(); }
    }

    View Validate(SnapshotView snapshot)
    {
        if (snapshot is not View view || view.Source != this || active != view || view.Closed)
            throw new InvalidOperationException("Inactive snapshot");
        return view;
    }

    public void ValidateReclaimedForTest()
    {
        gate.Enter();
        try
        {
            if (active is not null) throw new InvalidOperationException("Still active");
            int count = 0;
            long bytes = 0, id = 0;
            Node? previous = null;
            for (var node = head; node is not null; node = node.Next)
            {
                if (++count > slots || node.Prev != previous || node.Id <= id ||
                    !node.Live || node.HasOld || !live.TryGetValue(node.Key, out var indexed) || indexed != node)
                    throw new InvalidOperationException("Invalid reclaimed chain/index");
                bytes += node.Key.Length;
                id = node.Id;
                previous = node;
            }
            if (count != slots || count != live.Count || bytes != keyBytes || previous != tail)
                throw new InvalidOperationException("Invalid reclaimed accounting");
        }
        finally { gate.Exit(); }
    }

    void Unlink(Node node)
    {
        if (node.Prev is null) head = node.Next; else node.Prev.Next = node.Next;
        if (node.Next is null) tail = node.Prev; else node.Next.Prev = node.Prev;
        node.Prev = node.Next = null;
        slots--;
        keyBytes -= node.Key.Length;
    }
}
