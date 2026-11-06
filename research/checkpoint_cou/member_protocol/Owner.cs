using System.Diagnostics;

namespace MemberProtocol;

// Standalone protocol only. No Garnet, CPR, persistence or command integration.
sealed class Owner
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
    internal sealed class View
    {
        internal readonly Owner Source;
        internal readonly long Limit;
        internal Node? Cursor;
        internal bool Closed;
        // Diagnostics: gate wait and hold of the reader's batches.
        internal long Reads, ReadWaitTicks, ReadWaitMaxTicks, ReadHoldTicks, ReadHoldMaxTicks, ReadHoldMaxAt, FirstReadHoldTicks;
        public int Count { get; }
        internal View(Owner source, long limit, int count, Node? cursor)
            => (Source, Limit, Count, Cursor) = (source, limit, count, cursor);
    }
    readonly object gate = new();
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

    // Diagnostics: gate wait of the calling thread's writer operations (Set, Remove, TryGetScore), accumulated until taken.
    [ThreadStatic] static long threadGateWaitTicks;
    internal static long TakeThreadGateWaitTicks()
    {
        var ticks = threadGateWaitTicks;
        threadGateWaitTicks = 0;
        return ticks;
    }
    public int Slots { get { lock (gate) return slots; } }
    public long KeyBytes { get { lock (gate) return keyBytes; } }
    public int Count { get { lock (gate) return live.Count; } }

    public Owner(int maxSlots = 2_000_000, long maxKeyBytes = 128L << 20, int maxMemberBytes = 4096, bool borrowKeys = false)
    {
        if (maxSlots <= 0 || maxKeyBytes < 0 || maxMemberBytes <= 0 || maxMemberBytes > 16384)
            throw new ArgumentOutOfRangeException();
        (this.maxSlots, this.maxKeyBytes, this.maxMemberBytes) = (maxSlots, maxKeyBytes, maxMemberBytes);
        // Benchmark only: caller guarantees these arrays remain immutable and alive.
        this.borrowKeys = borrowKeys;
    }

    public void Set(byte[] key, double score)
    {
        var start = Stopwatch.GetTimestamp();
        lock (gate)
        {
            var acquired = Stopwatch.GetTimestamp();
            GateWaitTicks += acquired - start;
            threadGateWaitTicks += acquired - start;
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
            finally { GateHoldTicks += Stopwatch.GetTimestamp() - acquired; }
        }
    }

    public bool Remove(byte[] key)
    {
        var start = Stopwatch.GetTimestamp();
        lock (gate)
        {
            threadGateWaitTicks += Stopwatch.GetTimestamp() - start;
            if (!live.TryGetValue(key, out var node)) return false;
            Capture(node);
            live.Remove(key);
            node.Live = false;
            if (!Pending(node)) Unlink(node);
            return true;
        }
    }

    internal bool TryGetScore(byte[] key, out double score)
    {
        var start = Stopwatch.GetTimestamp();
        lock (gate)
        {
            threadGateWaitTicks += Stopwatch.GetTimestamp() - start;
            if (live.TryGetValue(key, out var node)) { score = node.Score; return true; }
            score = 0;
            return false;
        }
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

    public View Begin(Action<IReadOnlyList<Row>>? captureForTest = null)
    {
        lock (gate)
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
    }

    // At most maxNodes visited, at most maxBytes payload retained by this result.
    // Encoding/output belongs to caller outside gate. Caller must bound outstanding batches.
    public List<Row> Read(View view, int maxNodes = 128, int maxBytes = 16384)
    {
        if (maxNodes <= 0 || maxNodes > 1024 || maxBytes < maxMemberBytes || maxBytes > 1048576)
            throw new ArgumentOutOfRangeException();
        var rows = new List<Row>(maxNodes);
        var waitStart = Stopwatch.GetTimestamp();
        lock (gate)
        {
            var acquired = Stopwatch.GetTimestamp();
            try { ReadLocked(view, rows, maxNodes, maxBytes); }
            finally
            {
                var released = Stopwatch.GetTimestamp();
                view.Reads++;
                view.ReadWaitTicks += acquired - waitStart;
                view.ReadWaitMaxTicks = Math.Max(view.ReadWaitMaxTicks, acquired - waitStart);
                view.ReadHoldTicks += released - acquired;
                if (view.Reads == 1) view.FirstReadHoldTicks = released - acquired;
                if (released - acquired > view.ReadHoldMaxTicks)
                {
                    view.ReadHoldMaxTicks = released - acquired;
                    view.ReadHoldMaxAt = view.Reads;  // 1-based batch number
                }
            }
        }
        return rows;
    }

    void ReadLocked(View view, List<Row> rows, int maxNodes, int maxBytes)
    {
        Validate(view);
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

    public bool Done(View view)
    {
        lock (gate) { Validate(view); return view.Cursor is null; }
    }

    // Finish/abort drain in bounded critical sections. Writers keep running;
    // Begin remains rejected until all retained boundary nodes have been released.
    public void Close(View view)
    {
        while (!Done(view)) _ = Read(view);
        lock (gate) { Validate(view); view.Closed = true; active = null; }
    }

    void Validate(View view)
    {
        if (view.Source != this || active != view || view.Closed)
            throw new InvalidOperationException("Inactive snapshot");
    }

    internal void ValidateReclaimedForTest()
    {
        lock (gate)
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
