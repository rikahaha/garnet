// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace Garnet.server
{
    /// <summary>
    /// The member → score map of a <see cref="SortedSetObject"/>, shared by the object and its copy-update clones.
    /// </summary>
    /// <remarks>
    /// <para>Research prototype (GARNET_SORTEDSET_MEMBER_CHECKPOINT=1). By default this is a plain dictionary. In member mode it
    /// keeps every member in segmented parallel arrays on a chain ordered by a monotonic id, and supports one snapshot view
    /// at a time: the view fixes the boundary (the last id and the member count) in O(1); while it is open, the first change
    /// to each boundary member records that member's old score, and deleted boundary members stay on the chain until the
    /// reader passes them. A reader then enumerates exactly the boundary contents in batches while writers keep going.</para>
    /// <para>All writes go through this type, so no write can bypass the old-value capture. Writers take a gate to exclude
    /// the reader; readers of the live map do not, because the reader changes only chain links, retained slots and the
    /// old-value flags, never the live map or a live member's score. Foreground writers are already serialized by record locks.</para>
    /// </remarks>
    public sealed class SortedSetMemberStore : IEnumerable<KeyValuePair<byte[], double>>
    {
        /// <summary>Whether new stores use member mode.</summary>
        public static readonly bool MemberMode = Environment.GetEnvironmentVariable("GARNET_SORTEDSET_MEMBER_CHECKPOINT") == "1";

        const int SegmentShift = 12, SegmentSize = 1 << SegmentShift, SegmentMask = SegmentSize - 1;
        const int None = -1;
        const byte LiveFlag = 1, HasOldFlag = 2;

        // Every array stays below the 85,000-byte large-object threshold.
        sealed class Segment
        {
            internal readonly byte[][] Keys = new byte[SegmentSize][];
            internal readonly long[] Ids = new long[SegmentSize];
            internal readonly double[] Scores = new double[SegmentSize], OldScores = new double[SegmentSize];
            internal readonly int[] Prev = new int[SegmentSize], Next = new int[SegmentSize];
            internal readonly byte[] Flags = new byte[SegmentSize];
        }

        /// <summary>A snapshot view: the boundary and the reader's cursor.</summary>
        public sealed class View
        {
            internal readonly SortedSetMemberStore Source;
            internal readonly long Limit;
            internal int Cursor;
            internal bool Closed;

            /// <summary>Number of members at the boundary.</summary>
            public int Count { get; }

            /// <summary>The object that opened the view, so a writer that cannot proceed under the view can materialize it.</summary>
            public object Owner { get; }

            internal View(SortedSetMemberStore source, long limit, int count, int cursor, object owner)
                => (Source, Limit, Count, Cursor, Owner) = (source, limit, count, cursor, owner);
        }

        readonly Dictionary<byte[], double> plain;

        // Member mode only.
        readonly Dictionary<byte[], int> live;
        readonly List<Segment> segments;
        readonly object gate;
        int head = None, tail = None, freeHead = None, allocated;
        int waitingWriters;
        long writerAcquisitions, lastId;
        View active;

        /// <summary>Create an empty store in the configured mode.</summary>
        public SortedSetMemberStore() : this(MemberMode) { }

        /// <summary>Create an empty store.</summary>
        public SortedSetMemberStore(bool memberMode)
        {
            if (memberMode)
            {
                live = new Dictionary<byte[], int>(ByteArrayComparer.Instance);
                segments = new List<Segment>();
                gate = new object();
            }
            else
                plain = new Dictionary<byte[], double>(ByteArrayComparer.Instance);
        }

        /// <summary>Whether this store is in member mode (supports snapshot views).</summary>
        public bool IsMemberMode => live is not null;

        /// <summary>The plain dictionary; null in member mode.</summary>
        internal Dictionary<byte[], double> PlainDictionary => plain;

        /// <summary>The open view, if any.</summary>
        public View ActiveView => Volatile.Read(ref active);

        // ---- Dictionary surface used by SortedSetObject -------------------------------------------------------------

        /// <summary>Number of live members.</summary>
        public int Count => plain?.Count ?? live.Count;

        /// <summary>Whether a member is live.</summary>
        public bool ContainsKey(byte[] key) => plain?.ContainsKey(key) ?? live.ContainsKey(key);

        /// <summary>Score of a live member.</summary>
        public bool TryGetValue(byte[] key, out double score)
        {
            if (plain is not null)
                return plain.TryGetValue(key, out score);
            if (live.TryGetValue(key, out var slot))
            {
                score = Score(slot);
                return true;
            }
            score = 0;
            return false;
        }

        /// <summary>Get or set the score of a member; setting adds the member if absent.</summary>
        public double this[byte[] key]
        {
            get => TryGetValue(key, out var score) ? score : throw new KeyNotFoundException();
            set
            {
                if (plain is not null) plain[key] = value;
                else Set(key, value, mustAdd: false);
            }
        }

        /// <summary>Add a member that must not exist.</summary>
        public void Add(byte[] key, double score)
        {
            if (plain is not null) plain.Add(key, score);
            else Set(key, score, mustAdd: true);
        }

        /// <summary>Remove a member.</summary>
        public bool Remove(byte[] key) => Remove(key, out _);

        /// <summary>Remove a member, returning its score.</summary>
        public bool Remove(byte[] key, out double score)
        {
            if (plain is not null)
                return plain.Remove(key, out score);
            EnterWriter();
            try
            {
                if (!live.TryGetValue(key, out var slot))
                {
                    score = 0;
                    return false;
                }
                score = Score(slot);
                Capture(slot);
                _ = live.Remove(key);
                Flags(slot) &= unchecked((byte)~LiveFlag);
                if (!Pending(slot)) Release(slot);
                return true;
            }
            finally { Monitor.Exit(gate); }
        }

        /// <summary>Enumerate live members, in the same order a dictionary with the same history would.</summary>
        public IEnumerator<KeyValuePair<byte[], double>> GetEnumerator()
        {
            if (plain is not null)
            {
                foreach (var kvp in plain) yield return kvp;
                yield break;
            }
            foreach (var kvp in live)
                yield return new KeyValuePair<byte[], double>(kvp.Key, Score(kvp.Value));
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        // ---- Snapshot views (member mode) ---------------------------------------------------------------------------

        /// <summary>Open the snapshot view at the current state in O(1).</summary>
        /// <param name="owner">The object whose (v) data the view preserves.</param>
        /// <returns>The view, or null if this store is not in member mode or a view is already open.</returns>
        public View TryOpenView(object owner)
        {
            if (live is null) return null;
            lock (gate)
            {
                if (active is not null) return null;
                var view = new View(this, lastId, live.Count, head, owner);
                Volatile.Write(ref active, view);
                return view;
            }
        }

        /// <summary>Read up to <paramref name="maxMembers"/> boundary members into <paramref name="rows"/> (cleared first).</summary>
        public void ReadInto(View view, List<(byte[] Key, double Score)> rows, int maxMembers = 128)
        {
            rows.Clear();
            EnterReader();
            try
            {
                Validate(view);
                while (view.Cursor != None && Id(view.Cursor) <= view.Limit && rows.Count < maxMembers)
                {
                    var slot = view.Cursor;
                    var flags = Flags(slot);
                    rows.Add((Key(slot), (flags & HasOldFlag) != 0 ? OldScore(slot) : Score(slot)));
                    view.Cursor = Next(slot);  // read before Release clears the links
                    Flags(slot) = (byte)(flags & ~HasOldFlag);
                    if ((flags & LiveFlag) == 0) Release(slot);
                }
                // Never keep a cursor on a slot added after the boundary: it can be released and reused at once.
                if (view.Cursor != None && Id(view.Cursor) > view.Limit) view.Cursor = None;
            }
            finally { Monitor.Exit(gate); }
        }

        /// <summary>Whether the reader has passed every boundary member.</summary>
        public bool Done(View view)
        {
            EnterReader();
            try { Validate(view); return view.Cursor == None; }
            finally { Monitor.Exit(gate); }
        }

        /// <summary>Drain what the reader has not read, releasing retained members and old scores, and close the view.</summary>
        public void Close(View view)
        {
            var rows = new List<(byte[], double)>(128);
            while (!Done(view)) ReadInto(view, rows);
            lock (gate)
            {
                Validate(view);
                view.Closed = true;
                Volatile.Write(ref active, null);
            }
        }

        // ---- Internals ---------------------------------------------------------------------------------------------

        Segment Seg(int slot) => segments[slot >> SegmentShift];
        ref byte[] Key(int slot) => ref Seg(slot).Keys[slot & SegmentMask];
        ref long Id(int slot) => ref Seg(slot).Ids[slot & SegmentMask];
        ref double Score(int slot) => ref Seg(slot).Scores[slot & SegmentMask];
        ref double OldScore(int slot) => ref Seg(slot).OldScores[slot & SegmentMask];
        ref int Prev(int slot) => ref Seg(slot).Prev[slot & SegmentMask];
        ref int Next(int slot) => ref Seg(slot).Next[slot & SegmentMask];
        ref byte Flags(int slot) => ref Seg(slot).Flags[slot & SegmentMask];

        void Set(byte[] key, double score, bool mustAdd)
        {
            EnterWriter();
            try
            {
                if (live.TryGetValue(key, out var slot))
                {
                    if (mustAdd) throw new ArgumentException("An item with the same key has already been added.");
                    Capture(slot);
                    Score(slot) = score;
                    return;
                }
                var id = checked(lastId + 1);
                slot = AllocateSlot();
                live.Add(key, slot);
                lastId = id;
                Key(slot) = key;
                Id(slot) = id;
                Score(slot) = score;
                Flags(slot) = LiveFlag;
                Prev(slot) = tail;
                Next(slot) = None;
                if (tail == None) head = slot;
                else Next(tail) = slot;
                tail = slot;
            }
            finally { Monitor.Exit(gate); }
        }

        bool Pending(int slot) => active is { Closed: false } view && Id(slot) <= view.Limit
            && view.Cursor != None && Id(slot) >= Id(view.Cursor);

        void Capture(int slot)
        {
            if (!Pending(slot) || (Flags(slot) & HasOldFlag) != 0) return;
            OldScore(slot) = Score(slot);
            Flags(slot) |= HasOldFlag;
        }

        void Validate(View view)
        {
            if (view.Source != this || active != view || view.Closed)
                throw new InvalidOperationException("Inactive snapshot view");
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

        /// <summary>Unlink a slot from the chain and put it on the free list.</summary>
        void Release(int slot)
        {
            int prev = Prev(slot), next = Next(slot);
            if (prev == None) head = next; else Next(prev) = next;
            if (next == None) tail = prev; else Prev(next) = prev;
            Key(slot) = null;  // let the member bytes be collected
            Flags(slot) = 0;
            Prev(slot) = None;
            Next(slot) = freeHead;
            freeHead = slot;
        }

        // Monitor is not fair: a reader that re-takes the gate right after each batch can keep a blocked writer waiting for
        // many batches. A reader about to take the gate while a writer waits first lets one waiting writer through.
        void EnterWriter()
        {
            Interlocked.Increment(ref waitingWriters);
            Monitor.Enter(gate);
            Interlocked.Decrement(ref waitingWriters);
            Interlocked.Increment(ref writerAcquisitions);
        }

        void EnterReader()
        {
            if (Volatile.Read(ref waitingWriters) > 0)
            {
                var seen = Interlocked.Read(ref writerAcquisitions);
                var spin = new SpinWait();
                while (Volatile.Read(ref waitingWriters) > 0 && Interlocked.Read(ref writerAcquisitions) == seen)
                    spin.SpinOnce(sleep1Threshold: -1);
            }
            Monitor.Enter(gate);
        }

        /// <summary>Run the reader paths once on a private store, so a checkpoint's first batch does not JIT-compile while
        /// holding the gate. Call at startup when member mode is on.</summary>
        public static void WarmUp()
        {
            var store = new SortedSetMemberStore(memberMode: true);
            for (var i = 0; i < 300; i++) store.Add(BitConverter.GetBytes(i), i);
            var view = store.TryOpenView(owner: store);
            store[BitConverter.GetBytes(1)] = -1;
            _ = store.Remove(BitConverter.GetBytes(2));
            var rows = new List<(byte[], double)>();
            while (!store.Done(view)) store.ReadInto(view, rows);
            store.Close(view);
        }
    }
}
