using System.Diagnostics;

namespace MemberProtocol;

// The protocol of Owner, with members stored in segmented parallel arrays instead of one Node object per member, so the
// collector sees a handful of arrays per 4096 members rather than one object per member. A slot is a 32-bit index;
// segments are allocated on demand and never move. Unlinked slots go on a free list and are reused; a reused slot gets
// a new monotonic id, so identity and the snapshot limit are always decided by id, never by slot index.
sealed class ArrayOwner : ISnapshotOwner
{
    const int SegmentShift = 12, SegmentSize = 1 << SegmentShift, SegmentMask = SegmentSize - 1;
    const int None = -1;
    const byte LiveFlag = 1, HasOldFlag = 2;

    // Every array stays below the 85,000-byte large-object threshold.
    sealed class Segment
    {
        internal readonly byte[]?[] Keys = new byte[]?[SegmentSize];
        internal readonly long[] Ids = new long[SegmentSize];
        internal readonly double[] Scores = new double[SegmentSize], OldScores = new double[SegmentSize];
        internal readonly int[] Prev = new int[SegmentSize], Next = new int[SegmentSize];
        internal readonly byte[] Flags = new byte[SegmentSize];
    }

    internal sealed class View(ArrayOwner source, long limit, int count, int cursor) : SnapshotView(limit, count)
    {
        internal readonly ArrayOwner Source = source;
        internal int Cursor = cursor;
    }

    readonly Gate gate;
    readonly Dictionary<byte[], int> live = new(new Owner.Bytes());
    readonly List<Segment> segments = new();
    readonly int maxSlots, maxMemberBytes;
    readonly long maxKeyBytes;
    readonly bool borrowKeys;
    int head = None, tail = None, freeHead = None, allocated, slots;
    View? active;
    long lastId, keyBytes;

    public ArrayOwner(int maxSlots = 2_000_000, long maxKeyBytes = 128L << 20, int maxMemberBytes = 4096, bool borrowKeys = false,
        bool writerPriority = false)
    {
        if (maxSlots <= 0 || maxKeyBytes < 0 || maxMemberBytes <= 0 || maxMemberBytes > 16384)
            throw new ArgumentOutOfRangeException();
        (this.maxSlots, this.maxKeyBytes, this.maxMemberBytes) = (maxSlots, maxKeyBytes, maxMemberBytes);
        this.borrowKeys = borrowKeys;
        gate = new Gate(writerPriority);
    }

    public int Slots { get { gate.Enter(); try { return slots; } finally { gate.Exit(); } } }
    public int Count { get { gate.Enter(); try { return live.Count; } finally { gate.Exit(); } } }

    Segment Seg(int slot) => segments[slot >> SegmentShift];
    ref byte[]? Key(int slot) => ref Seg(slot).Keys[slot & SegmentMask];
    ref long Id(int slot) => ref Seg(slot).Ids[slot & SegmentMask];
    ref double Score(int slot) => ref Seg(slot).Scores[slot & SegmentMask];
    ref double OldScore(int slot) => ref Seg(slot).OldScores[slot & SegmentMask];
    ref int Prev(int slot) => ref Seg(slot).Prev[slot & SegmentMask];
    ref int Next(int slot) => ref Seg(slot).Next[slot & SegmentMask];
    ref byte Flags(int slot) => ref Seg(slot).Flags[slot & SegmentMask];

    public void Set(byte[] key, double score)
    {
        gate.EnterWriter(Stopwatch.GetTimestamp());
        try
        {
            if (live.TryGetValue(key, out var slot))
            {
                Capture(slot);
                Score(slot) = score;
                return;
            }
            // Budget rejection is before publication/mutation, as in Owner.
            if (key.Length > maxMemberBytes || slots >= maxSlots || key.Length > maxKeyBytes - keyBytes)
                throw new InvalidOperationException("Protocol capacity exceeded; mutation rejected");
            var id = checked(lastId + 1);
            var owned = borrowKeys ? key : key.ToArray();
            slot = AllocateSlot();
            live.Add(owned, slot);
            lastId = id;
            Key(slot) = owned;
            Id(slot) = id;
            Score(slot) = score;
            Flags(slot) = LiveFlag;
            Prev(slot) = tail;
            Next(slot) = None;
            if (tail == None) head = slot;
            else Next(tail) = slot;
            tail = slot;
            slots++;
            keyBytes += owned.Length;
        }
        finally { gate.Exit(); }
    }

    public bool Remove(byte[] key)
    {
        gate.EnterWriter(Stopwatch.GetTimestamp());
        try
        {
            if (!live.TryGetValue(key, out var slot)) return false;
            Capture(slot);
            live.Remove(key);
            Flags(slot) &= unchecked((byte)~LiveFlag);
            if (!Pending(slot)) Release(slot);
            return true;
        }
        finally { gate.Exit(); }
    }

    public bool TryGetScore(byte[] key, out double score)
    {
        gate.EnterWriter(Stopwatch.GetTimestamp());
        try
        {
            if (live.TryGetValue(key, out var slot)) { score = Score(slot); return true; }
            score = 0;
            return false;
        }
        finally { gate.Exit(); }
    }

    bool Pending(int slot) => active is { Closed: false } view && Id(slot) <= view.Limit
        && view.Cursor != None && Id(slot) >= Id(view.Cursor);

    void Capture(int slot)
    {
        if (!Pending(slot) || (Flags(slot) & HasOldFlag) != 0) return;
        OldScore(slot) = Score(slot);
        Flags(slot) |= HasOldFlag;
    }

    public SnapshotView Begin(Action<IReadOnlyList<Owner.Row>>? captureForTest = null)
    {
        gate.Enter();
        try
        {
            if (active is not null) throw new InvalidOperationException("One active snapshot only");
            // Test oracle only, as in Owner.
            if (captureForTest is not null)
                captureForTest(live.Select(kv => new Owner.Row(kv.Key.ToArray(), Score(kv.Value))).ToArray());
            active = new View(this, lastId, live.Count, head);
            return active;
        }
        finally { gate.Exit(); }
    }

    public List<Owner.Row> Read(SnapshotView view, int maxNodes = 128, int maxBytes = 16384)
    {
        var rows = new List<Owner.Row>(maxNodes);
        ReadInto(view, rows, maxNodes, maxBytes);
        return rows;
    }

    public void ReadInto(SnapshotView view, List<Owner.Row> rows, int maxNodes = 128, int maxBytes = 16384)
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

    void ReadLocked(SnapshotView snapshot, List<Owner.Row> rows, int maxNodes, int maxBytes)
    {
        var view = Validate(snapshot);
        int bytes = 0, visited = 0;
        while (view.Cursor != None && Id(view.Cursor) <= view.Limit && visited < maxNodes)
        {
            var slot = view.Cursor;
            var key = Key(slot)!;
            if (key.Length > maxBytes - bytes) break;
            var flags = Flags(slot);
            rows.Add(new Owner.Row(key, (flags & HasOldFlag) != 0 ? OldScore(slot) : Score(slot)));
            bytes += key.Length;
            visited++;
            view.Cursor = Next(slot);  // read before Release clears the links
            Flags(slot) = (byte)(flags & ~HasOldFlag);
            if ((flags & LiveFlag) == 0) Release(slot);
        }
        // Never retain a cursor on a slot added after the boundary: it can be released and reused immediately.
        if (view.Cursor != None && Id(view.Cursor) > view.Limit) view.Cursor = None;
    }

    public bool Done(SnapshotView view)
    {
        gate.EnterReader();
        try { return Validate(view).Cursor == None; }
        finally { gate.Exit(); }
    }

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

    int AllocateSlot()
    {
        if (freeHead != None)
        {
            var slot = freeHead;
            freeHead = Next(slot);
            return slot;
        }
        if (allocated == segments.Count * SegmentSize) segments.Add(new Segment());
        return allocated++;
    }

    /// <summary>Unlink a slot from the member chain and put it on the free list.</summary>
    void Release(int slot)
    {
        int prev = Prev(slot), next = Next(slot);
        if (prev == None) head = next; else Next(prev) = next;
        if (next == None) tail = prev; else Prev(next) = prev;
        slots--;
        keyBytes -= Key(slot)!.Length;
        Key(slot) = null;  // let the member bytes be collected
        Flags(slot) = 0;
        Prev(slot) = None;
        Next(slot) = freeHead;
        freeHead = slot;
    }

    public void ValidateReclaimedForTest()
    {
        gate.Enter();
        try
        {
            if (active is not null) throw new InvalidOperationException("Still active");
            int count = 0, previous = None;
            long bytes = 0, id = 0;
            for (var slot = head; slot != None; slot = Next(slot))
            {
                var key = Key(slot);
                if (++count > slots || Prev(slot) != previous || Id(slot) <= id || Flags(slot) != LiveFlag ||
                    key is null || !live.TryGetValue(key, out var indexed) || indexed != slot)
                    throw new InvalidOperationException("Invalid reclaimed chain/index");
                bytes += key.Length;
                id = Id(slot);
                previous = slot;
            }
            int free = 0;
            for (var slot = freeHead; slot != None; slot = Next(slot))
            {
                if (++free > allocated || Key(slot) is not null || Flags(slot) != 0)
                    throw new InvalidOperationException("Invalid free list");
            }
            if (count != slots || count != live.Count || bytes != keyBytes || previous != tail || count + free != allocated)
                throw new InvalidOperationException("Invalid reclaimed accounting");
        }
        finally { gate.Exit(); }
    }
}
