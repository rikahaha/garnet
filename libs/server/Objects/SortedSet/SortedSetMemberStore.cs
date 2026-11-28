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

        // Compile the reader paths before the first checkpoint, so its first batch does not JIT-compile while holding the gate.
        static SortedSetMemberStore()
        {
            if (MemberMode)
                WarmUp();
        }

        const int SegmentShift = 12, SegmentSize = 1 << SegmentShift, SegmentMask = SegmentSize - 1;
        const int None = -1;
        const byte LiveFlag = 1, HasOldFlag = 2, CapturedFlag = 4;

        /// <summary>Delta writes read only the members changed since the base (from the change list) instead of scanning every
        /// boundary member (GARNET_SORTEDSET_DELTA_LIST, default on; "0" scans, for comparison).</summary>
        public static readonly bool ListMode = Environment.GetEnvironmentVariable("GARNET_SORTEDSET_DELTA_LIST") != "0";

        // Every array stays below the 85,000-byte large-object threshold.
        sealed class Segment
        {
            internal readonly byte[][] Keys = new byte[SegmentSize][];
            internal readonly long[] Ids = new long[SegmentSize];
            internal readonly double[] Scores = new double[SegmentSize], OldScores = new double[SegmentSize];
            internal readonly int[] Prev = new int[SegmentSize], Next = new int[SegmentSize];
            internal readonly byte[] Flags = new byte[SegmentSize];
            internal readonly int[] ModEpochs = new int[SegmentSize], OldModEpochs = new int[SegmentSize];
        }

        /// <summary>A snapshot view: the boundary and the reader's cursor.</summary>
        public sealed class View
        {
            internal readonly SortedSetMemberStore Source;
            internal readonly long Limit;
            internal int Cursor;
            internal bool Closed;

            /// <summary>Change epoch at the boundary: members last changed at or before it are in the view.</summary>
            internal readonly int Epoch;

            /// <summary>Epoch of the base this view would be a delta against; 0 if there is none.</summary>
            public int BaseEpoch { get; }

            /// <summary>Members that existed at the base and were deleted before the boundary (delta estimate).</summary>
            public long DeletedSinceBase { get; internal set; }

            /// <summary>Key bytes of <see cref="DeletedSinceBase"/>.</summary>
            public long DeletedKeyBytesSinceBase { get; internal set; }

            /// <summary>Upper bound on the members changed or added since the base (exact when the base is the previous epoch).</summary>
            public long ChangedSinceBase { get; internal set; }

            /// <summary>Keys deleted since the base, of members that may have existed at the base (delta mode only; else null).</summary>
            public List<byte[]> DeletedKeysSinceBase { get; internal set; }

            internal int ChainGeneration;

            // Change-list read (delta writes): read changes[0, ChangesEnd) instead of following the chain. While set, every boundary
            // member is pending (no cursor), so changed and deleted members keep their boundary data until the view closes.
            internal bool ListRead;
            internal int ChangesEnd, ListIndex;
            internal HashSet<long> ListSeen;

            /// <summary>Number of members at the boundary.</summary>
            public int Count { get; }

            /// <summary>The object that opened the view, so a writer that cannot proceed under the view can materialize it.</summary>
            public object Owner { get; }

            internal View(SortedSetMemberStore source, long limit, int count, int cursor, object owner, int epoch, int baseEpoch)
                => (Source, Limit, Count, Cursor, Owner, Epoch, BaseEpoch) = (source, limit, count, cursor, owner, epoch, baseEpoch);
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

        // Change tracking for the incremental-checkpoint estimate (ckpt-delta, step 1). Each opened view ends an epoch; every
        // write stamps the member with the current epoch. The base is the last view read to the end, assuming its checkpoint
        // completed. Deletes of members that existed at the last opened boundary are counted per epoch.
        int epoch = 1, baseEpoch;
        long lastViewLimit;
        readonly List<(int Epoch, long Count, long KeyBytes)> deletes;
        readonly List<(int Epoch, long Count)> changes;
        readonly List<(int Epoch, byte[] Key)> deletedKeys;  // delta mode only

        // Delta mode: each member's first change in an epoch (insertions included), pruned up to the base; and the slots that keep
        // boundary data for the open view (old score, or a deleted member), released when the view closes.
        readonly List<(int Epoch, int Slot, long Id)> changedSlots;
        readonly List<int> captured;
        bool dropChangesOnClose;

        /// <summary>Random id of this store, naming its chain in the side store.</summary>
        public long Uid { get; } = Random.Shared.NextInt64(1, long.MaxValue);

        /// <summary>Sequence number of the last chain file written, its chain's full file, and deltas since that full file
        /// (side store chain state; changed only by the snapshot flush that holds this store's view).</summary>
        public long BaseSeq { get; private set; }

        /// <summary>The full file of the chain ending at <see cref="BaseSeq"/>.</summary>
        public long ChainStart { get; private set; }

        /// <summary>Deltas in the chain ending at <see cref="BaseSeq"/>.</summary>
        public int ChainDeltas { get; private set; }

        // Bumped when the delta bookkeeping is dropped, so a chain file being written meanwhile does not become a base.
        int chainGeneration;

        /// <summary>Create an empty store in the configured mode.</summary>
        public SortedSetMemberStore() : this(MemberMode) { }

        /// <summary>Create an empty store.</summary>
        public SortedSetMemberStore(bool memberMode) : this(memberMode, SortedSetSideStore.DeltaMode) { }

        /// <summary>Create an empty store; <paramref name="trackChanges"/> keeps the delta bookkeeping (deleted keys, change list).</summary>
        public SortedSetMemberStore(bool memberMode, bool trackChanges)
        {
            if (memberMode)
            {
                live = new Dictionary<byte[], int>(ByteArrayComparer.Instance);
                segments = new List<Segment>();
                gate = new object();
                deletes = new List<(int, long, long)>();
                changes = new List<(int, long)>();
                captured = new List<int>();
                if (trackChanges)
                {
                    deletedKeys = new List<(int, byte[])>();
                    changedSlots = new List<(int, int, long)>();
                }
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
                if (Id(slot) <= lastViewLimit)
                {
                    CountDelete(key.Length);
                    if (deletedKeys is not null)
                    {
                        deletedKeys.Add((epoch, key));
                        // Bound the retained keys when no snapshot write moves the base (e.g. the record stays below the snapshot range):
                        // drop them and the base, so the next chain file is full.
                        if (deletedKeys.Count > (live.Count >> 2) + 1024)
                            DropBase();
                    }
                }
                _ = live.Remove(key);
                Flags(slot) &= unchecked((byte)~LiveFlag);
                if (!Pending(slot)) Release(slot);
                else MarkCaptured(slot);  // retained for the view until the reader passes it or the view closes
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
                var view = new View(this, lastId, live.Count, head, owner, epoch, baseEpoch);
                foreach (var d in deletes)
                {
                    if (d.Epoch > baseEpoch && d.Epoch <= epoch)
                    {
                        view.DeletedSinceBase += d.Count;
                        view.DeletedKeyBytesSinceBase += d.KeyBytes;
                    }
                }
                foreach (var c in changes)
                {
                    if (c.Epoch > baseEpoch && c.Epoch <= epoch)
                        view.ChangedSinceBase += c.Count;
                }
                if (deletedKeys is not null)
                {
                    view.DeletedKeysSinceBase = new List<byte[]>();
                    foreach (var d in deletedKeys)
                    {
                        if (d.Epoch > baseEpoch && d.Epoch <= epoch)
                            view.DeletedKeysSinceBase.Add(d.Key);
                    }
                }
                epoch++;
                lastViewLimit = lastId;
                view.ChainGeneration = chainGeneration;
                view.ChangesEnd = changedSlots?.Count ?? 0;  // later entries are epoch > view.Epoch
                Volatile.Write(ref active, view);
                return view;
            }
        }

        /// <summary>Read up to <paramref name="maxMembers"/> boundary members into <paramref name="rows"/> (cleared first).</summary>
        /// <param name="changed">If not null, cleared and filled with whether each row changed since the view's base.</param>
        public void ReadInto(View view, List<(byte[] Key, double Score)> rows, int maxMembers = 128, List<bool> changed = null)
        {
            rows.Clear();
            changed?.Clear();
            EnterReader();
            try
            {
                Validate(view);
                if (view.ListRead)
                    throw new InvalidOperationException("View is being read from the change list");
                while (view.Cursor != None && Id(view.Cursor) <= view.Limit && rows.Count < maxMembers)
                {
                    var slot = view.Cursor;
                    var flags = Flags(slot);
                    var hasOld = (flags & HasOldFlag) != 0;
                    rows.Add((Key(slot), hasOld ? OldScore(slot) : Score(slot)));
                    changed?.Add((hasOld ? OldModEpoch(slot) : ModEpoch(slot)) > view.BaseEpoch);
                    view.Cursor = Next(slot);  // read before Release clears the links
                    Flags(slot) = (byte)(flags & ~(HasOldFlag | CapturedFlag));
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
            try { Validate(view); return view.ListRead ? view.ListIndex >= view.ChangesEnd : view.Cursor == None; }
            finally { Monitor.Exit(gate); }
        }

        /// <summary>
        /// Switch an unread view to reading only the members changed since its base (delta writes): afterwards
        /// <see cref="ReadChanged"/> returns each boundary member whose boundary value changed in (base, boundary], once.
        /// </summary>
        /// <returns>False if the change list cannot serve this view (not delta mode, already reading, or the list was dropped).</returns>
        public bool TryBeginChangeListRead(View view)
        {
            lock (gate)
            {
                Validate(view);
                // No base (never written, or dropped with the change list): a delta has nothing to apply to.
                if (changedSlots is null || BaseSeq == 0 || view.ListRead || view.Cursor != head || view.ChainGeneration != chainGeneration)
                    return false;
                view.ListRead = true;
                view.ListIndex = 0;
                view.ListSeen = new HashSet<long>();
                return true;
            }
        }

        /// <summary>Read up to <paramref name="maxMembers"/> changed boundary members (see <see cref="TryBeginChangeListRead"/>).</summary>
        public void ReadChanged(View view, List<(byte[] Key, double Score)> rows, int maxMembers = 128)
        {
            rows.Clear();
            EnterReader();
            try
            {
                Validate(view);
                if (!view.ListRead)
                    throw new InvalidOperationException("View is not reading from the change list");
                var scanned = 0;
                while (view.ListIndex < view.ChangesEnd && rows.Count < maxMembers && scanned < 4 * maxMembers)
                {
                    var (e, slot, id) = changedSlots[view.ListIndex++];
                    scanned++;
                    if (e <= view.BaseEpoch || e > view.Epoch || id > view.Limit || !view.ListSeen.Add(id))
                        continue;
                    // The slot must still hold this member: live, or deleted after the boundary and retained for the view.
                    var flags = Flags(slot);
                    if (Id(slot) != id || (flags & (LiveFlag | CapturedFlag)) == 0)
                        continue;
                    var hasOld = (flags & HasOldFlag) != 0;
                    var modEpoch = hasOld ? OldModEpoch(slot) : ModEpoch(slot);
                    if (modEpoch <= view.BaseEpoch || modEpoch > view.Epoch)
                        continue;
                    rows.Add((Key(slot), hasOld ? OldScore(slot) : Score(slot)));
                }
            }
            finally { Monitor.Exit(gate); }
        }

        /// <summary>The view was read to the end and written: make it the base for the next delta estimate.</summary>
        public void CommitBase(View view)
        {
            lock (gate)
            {
                Validate(view);
                baseEpoch = view.Epoch;
                _ = deletes.RemoveAll(d => d.Epoch <= baseEpoch);
                _ = changes.RemoveAll(c => c.Epoch <= baseEpoch);
                _ = deletedKeys?.RemoveAll(d => d.Epoch <= baseEpoch);
                if (changedSlots is not null && view.ListRead)
                {
                    // Entries up to the base are no longer needed; later ones (after this view's boundary) are kept.
                    _ = changedSlots.RemoveAll(c => c.Epoch <= baseEpoch);
                    view.ChangesEnd = view.ListIndex = 0;
                }
                else
                    _ = changedSlots?.RemoveAll(c => c.Epoch <= baseEpoch);
            }
        }

        /// <summary>The view was written to side store file <paramref name="seq"/> (flushed to disk): it is the new base.</summary>
        public void CommitChainFile(View view, long seq, bool full)
        {
            CommitBase(view);
            lock (gate)
            {
                if (view.ChainGeneration != chainGeneration)
                {
                    // Deletes after this view were dropped meanwhile: a delta against this file could miss them.
                    BaseSeq = ChainStart = 0;
                    ChainDeltas = 0;
                    return;
                }
                ChainStart = full ? seq : ChainStart;
                ChainDeltas = full ? 0 : ChainDeltas + 1;
                BaseSeq = seq;
            }
        }

        /// <summary>The view was written into the snapshot file in full, not to the side store: the next chain file must be full.</summary>
        public void ResetChain(View view)
        {
            CommitBase(view);
            lock (gate)
                DropBase();
        }

        // Caller holds the gate.
        void DropBase()
        {
            BaseSeq = ChainStart = 0;
            ChainDeltas = 0;
            deletedKeys?.Clear();
            chainGeneration++;
            // A view may be reading the change list by index: clear it when that view closes.
            if (active is { Closed: false, ListRead: true }) dropChangesOnClose = true;
            else changedSlots?.Clear();
        }

        /// <summary>Release what the view retained (old scores, deleted members) and close it. Only the captured slots are visited,
        /// in batches that let writers through, so closing does not scan the set.</summary>
        public void Close(View view)
        {
            var index = 0;
            while (true)
            {
                EnterReader();
                try
                {
                    Validate(view);
                    var end = Math.Min(captured.Count, index + 1024);
                    for (; index < end; index++)
                    {
                        var slot = captured[index];
                        var flags = Flags(slot);
                        if ((flags & CapturedFlag) == 0) continue;  // already released by the reader (and maybe reused)
                        Flags(slot) = (byte)(flags & ~(HasOldFlag | CapturedFlag));
                        if ((flags & LiveFlag) == 0) Release(slot);
                    }
                    if (index < captured.Count) continue;
                    // Nothing retained remains: close under the same gate hold, so no writer can capture in between.
                    captured.Clear();
                    view.Closed = true;
                    view.ListSeen = null;
                    Volatile.Write(ref active, null);
                    if (dropChangesOnClose)
                    {
                        changedSlots?.Clear();
                        dropChangesOnClose = false;
                    }
                    return;
                }
                finally { Monitor.Exit(gate); }
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
        ref int ModEpoch(int slot) => ref Seg(slot).ModEpochs[slot & SegmentMask];
        ref int OldModEpoch(int slot) => ref Seg(slot).OldModEpochs[slot & SegmentMask];

        void CountChange(int slot)
        {
            if (changedSlots is not null)
            {
                changedSlots.Add((epoch, slot, Id(slot)));
                // Bound the memory: past half the set (far above the delta threshold) drop the list and the base. Not tied to the
                // threshold, so that writes during a long checkpoint do not drop the base the next delta needs.
                if (changedSlots.Count > (live.Count >> 1) + 1024 && !dropChangesOnClose)
                    DropBase();
            }
            if (changes.Count > 0 && changes[^1].Epoch == epoch)
                changes[^1] = (epoch, changes[^1].Count + 1);
            else
                changes.Add((epoch, 1));
        }

        void CountDelete(int keyBytes)
        {
            if (deletes.Count > 0 && deletes[^1].Epoch == epoch)
            {
                var last = deletes[^1];
                deletes[^1] = (epoch, last.Count + 1, last.KeyBytes + keyBytes);
            }
            else
                deletes.Add((epoch, 1, keyBytes));
        }

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
                    if (ModEpoch(slot) != epoch)
                    {
                        ModEpoch(slot) = epoch;
                        CountChange(slot);
                    }
                    return;
                }
                var id = checked(lastId + 1);
                slot = AllocateSlot();
                live.Add(key, slot);
                lastId = id;
                Key(slot) = key;
                Id(slot) = id;
                Score(slot) = score;
                ModEpoch(slot) = epoch;
                Flags(slot) = LiveFlag;
                CountChange(slot);
                Prev(slot) = tail;
                Next(slot) = None;
                if (tail == None) head = slot;
                else Next(tail) = slot;
                tail = slot;
            }
            finally { Monitor.Exit(gate); }
        }

        bool Pending(int slot) => active is { Closed: false } view && Id(slot) <= view.Limit
            && (view.ListRead || (view.Cursor != None && Id(slot) >= Id(view.Cursor)));

        void Capture(int slot)
        {
            if (!Pending(slot) || (Flags(slot) & HasOldFlag) != 0) return;
            OldScore(slot) = Score(slot);
            OldModEpoch(slot) = ModEpoch(slot);
            Flags(slot) |= HasOldFlag;
            MarkCaptured(slot);
        }

        void MarkCaptured(int slot)
        {
            if ((Flags(slot) & CapturedFlag) != 0) return;
            Flags(slot) |= CapturedFlag;
            captured.Add(slot);
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
