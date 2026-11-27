// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Garnet.common;

namespace Garnet.server
{
    /// <summary>
    /// Research prototype (ckpt-delta, GARNET_SORTEDSET_DELTA=1): member data of large sorted sets written by snapshot checkpoints
    /// outside the log, as one chain per member store: a full file followed by up to <see cref="MaxChainDeltas"/> delta files.
    /// The snapshot file holds only a stub (store id and sequence number) that points into this store.
    /// </summary>
    /// <remarks>
    /// <para>Layout: <c>&lt;checkpoint dir&gt;/sortedset-members/&lt;uid as 16 hex digits&gt;/&lt;seq as 12 digits&gt;.m</c>.</para>
    /// <para>File: header (magic, version, kind, uid, seq, base seq, chain start), then entries (tag 1: upsert key and score; tag 2: delete
    /// key), then tag 0, the entry count and an FNV-1a 64 checksum of everything before it. A delta applies its deletes before its
    /// upserts, so a member deleted and added again in one delta ends up present.</para>
    /// <para>Ordering: a file is written and flushed to disk on the snapshot flush thread before the stub that refers to it is written
    /// into the snapshot file, and the checkpoint commits only after its snapshot flush completes. So a committed stub always refers to
    /// durable files. Files are deleted only when the latest completed checkpoint (Garnet keeps only that one) does not reference them.</para>
    /// </remarks>
    public static class SortedSetSideStore
    {
        /// <summary>Whether new snapshot checkpoints write stubs and side files for large member-mode sorted sets.</summary>
        public static readonly bool DeltaMode = SortedSetMemberStore.MemberMode
            && Environment.GetEnvironmentVariable("GARNET_SORTEDSET_DELTA") == "1";

        /// <summary>Sets with fewer members are written into the snapshot file as before.</summary>
        public static readonly int MinMembers = int.TryParse(Environment.GetEnvironmentVariable("GARNET_SORTEDSET_DELTA_MIN"),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var min) ? min : 10000;

        /// <summary>Write a full file instead of another delta once a chain has this many deltas.</summary>
        public const int MaxChainDeltas = 4;

        /// <summary>Write a full file instead of a delta when the estimated changed members exceed this share of the set.</summary>
        public const double MaxDeltaRatio = 0.25;

        /// <summary>The stub's marker in place of the member count.</summary>
        public const int StubMarker = -1;

        const int Magic = 0x4D535347;  // "GSSM"
        const int Version = 1;
        const byte TagEnd = 0, TagUpsert = 1, TagDelete = 2;
        const string FileSuffix = ".m";

        static readonly object gate = new();
        static string root;
        static bool writable;

        // Chains referenced since the last completed checkpoint (uid -> lowest chain start), and by the recovered checkpoint.
        static readonly Dictionary<long, long> checkpointRefs = new(), recoveryRefs = new();

        /// <summary>Whether snapshot flushes may write stubs now.</summary>
        public static bool Enabled => DeltaMode && writable && root is not null;

        /// <summary>Set the directory. Reading stubs works whenever a directory is set; writing them also needs <paramref name="allowWrite"/>.</summary>
        public static void Configure(string directory, bool allowWrite)
        {
            lock (gate)
            {
                root = directory;
                writable = allowWrite;
                checkpointRefs.Clear();
                recoveryRefs.Clear();
            }
        }

        /// <summary>Stop writing stubs (e.g. another database is checkpointed; the side store serves DB 0 only).</summary>
        public static void DisableWrites()
        {
            lock (gate)
                writable = false;
        }

        static string Root => root ?? throw new InvalidOperationException("Sorted set side store is not configured");

        static string UidDirectory(long uid) => Path.Combine(Root, uid.ToString("x16", CultureInfo.InvariantCulture));

        /// <summary>Path of a chain file.</summary>
        public static string PathFor(long uid, long seq) => Path.Combine(UidDirectory(uid), seq.ToString("D12", CultureInfo.InvariantCulture) + FileSuffix);

        /// <summary>Whether a chain file exists.</summary>
        public static bool Exists(long uid, long seq) => seq > 0 && File.Exists(PathFor(uid, seq));

        /// <summary>Begin writing a chain file.</summary>
        public static Writer CreateWriter(long uid, long seq, bool full, long baseSeq, long chainStart)
            => new(uid, seq, full, baseSeq, chainStart);

        /// <summary>A stub written into this checkpoint's snapshot refers to the chain of <paramref name="uid"/> from <paramref name="chainStart"/>.</summary>
        public static void RecordCheckpointReference(long uid, long chainStart) => Record(checkpointRefs, uid, chainStart);

        static void Record(Dictionary<long, long> refs, long uid, long chainStart)
        {
            lock (gate)
            {
                if (!refs.TryGetValue(uid, out var start) || chainStart < start)
                    refs[uid] = chainStart;
            }
        }

        /// <summary>A checkpoint completed: keep only the chains it references.</summary>
        /// <remarks>References recorded by checkpoints that failed since the last completed one are kept too; that is only conservative.</remarks>
        public static void OnCheckpointCompleted()
        {
            lock (gate)
            {
                if (root is null)
                    return;
                Collect(checkpointRefs);
                checkpointRefs.Clear();
            }
        }

        /// <summary>Recovery completed: keep only the chains the recovered checkpoint references.</summary>
        public static void OnRecoveryCompleted()
        {
            lock (gate)
            {
                if (root is null)
                    return;
                Collect(recoveryRefs);
                recoveryRefs.Clear();
            }
        }

        static void Collect(Dictionary<long, long> keep)
        {
            if (!Directory.Exists(root))
                return;
            foreach (var dir in Directory.GetDirectories(root))
            {
                if (!long.TryParse(Path.GetFileName(dir), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var uid) || !keep.TryGetValue(uid, out var chainStart))
                {
                    Directory.Delete(dir, recursive: true);
                    continue;
                }
                foreach (var file in Directory.GetFiles(dir))
                {
                    var name = Path.GetFileName(file);
                    if (!name.EndsWith(FileSuffix, StringComparison.Ordinal)
                        || !long.TryParse(name.AsSpan(0, name.Length - FileSuffix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq)
                        || seq < chainStart)
                        File.Delete(file);
                }
            }
        }

        /// <summary>List the chain files under the store, for tests and diagnostics.</summary>
        public static IReadOnlyList<(long Uid, long Seq)> ListFiles()
        {
            var result = new List<(long, long)>();
            if (root is null || !Directory.Exists(root))
                return result;
            foreach (var dir in Directory.GetDirectories(root))
            {
                var uid = long.Parse(Path.GetFileName(dir), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                foreach (var file in Directory.GetFiles(dir, "*" + FileSuffix))
                    result.Add((uid, long.Parse(Path.GetFileNameWithoutExtension(file), CultureInfo.InvariantCulture)));
            }
            result.Sort();
            return result;
        }

        /// <summary>
        /// Load the members of <paramref name="uid"/> as of chain file <paramref name="seq"/>: the chain's full file and then each delta
        /// in order. Every file is checked (header, chain links, entry count, checksum); anything missing or inconsistent throws.
        /// </summary>
        public static Dictionary<byte[], double> LoadChain(long uid, long seq)
        {
            // Walk back from seq to the full file using the headers.
            var chain = new List<(long Seq, Header Header)>();
            var cur = seq;
            while (true)
            {
                if (chain.Count > MaxChainDeltas + 1)
                    throw new GarnetException($"Sorted set side store chain of {uid:x16} at {seq} is longer than allowed");
                var header = ReadHeader(uid, cur);
                chain.Add((cur, header));
                if (header.Full)
                    break;
                if (header.BaseSeq <= 0 || header.BaseSeq >= cur || header.ChainStart > header.BaseSeq)
                    throw new GarnetException($"Sorted set side store file {PathFor(uid, cur)} has an invalid base {header.BaseSeq}");
                cur = header.BaseSeq;
            }
            if (chain[^1].Header.ChainStart != chain[^1].Seq || chain[0].Header.ChainStart != chain[^1].Seq)
                throw new GarnetException($"Sorted set side store chain of {uid:x16} at {seq} has inconsistent chain starts");

            var members = new Dictionary<byte[], double>(ByteArrayComparer.Instance);
            for (var i = chain.Count - 1; i >= 0; i--)
                Apply(uid, chain[i].Seq, members);
            Record(recoveryRefs, uid, chain[^1].Seq);
            return members;
        }

        readonly struct Header
        {
            public readonly bool Full;
            public readonly long BaseSeq, ChainStart;
            public Header(bool full, long baseSeq, long chainStart) => (Full, BaseSeq, ChainStart) = (full, baseSeq, chainStart);
        }

        static Header ReadHeader(long uid, long seq)
        {
            var path = PathFor(uid, seq);
            if (!File.Exists(path))
                throw new GarnetException($"Sorted set side store file {path} is missing");
            using var reader = new BinaryReader(File.OpenRead(path));
            return ReadHeader(reader, uid, seq, path);
        }

        static Header ReadHeader(BinaryReader reader, long uid, long seq, string path)
        {
            try
            {
                if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version)
                    throw new GarnetException($"Sorted set side store file {path} has a bad header");
                var kind = reader.ReadByte();
                var fileUid = reader.ReadInt64();
                var fileSeq = reader.ReadInt64();
                var baseSeq = reader.ReadInt64();
                var chainStart = reader.ReadInt64();
                if (kind > 1 || fileUid != uid || fileSeq != seq)
                    throw new GarnetException($"Sorted set side store file {path} does not match uid {uid:x16} seq {seq}");
                return new Header(kind == 0, baseSeq, chainStart);
            }
            catch (EndOfStreamException e)
            {
                throw new GarnetException($"Sorted set side store file {path} is truncated", e);
            }
        }

        static void Apply(long uid, long seq, Dictionary<byte[], double> members)
        {
            var path = PathFor(uid, seq);
            using var hashing = new Fnv64Stream(new BufferedStream(File.OpenRead(path), 1 << 16));
            using var reader = new BinaryReader(hashing);
            try
            {
                var header = ReadHeader(reader, uid, seq, path);
                if (header.Full)
                    members.Clear();
                var upserts = new List<(byte[] Key, double Score)>();
                long entries = 0;
                while (true)
                {
                    var tag = reader.ReadByte();
                    if (tag == TagEnd)
                        break;
                    var key = reader.ReadBytes(reader.ReadInt32());
                    if (tag == TagUpsert)
                        upserts.Add((key, reader.ReadDouble()));
                    else if (tag == TagDelete)
                        _ = members.Remove(key);
                    else
                        throw new GarnetException($"Sorted set side store file {path} has a bad entry tag {tag}");
                    entries++;
                }
                var count = reader.ReadInt64();
                var expected = hashing.Hash;
                var checksum = reader.ReadUInt64();
                if (count != entries || checksum != expected)
                    throw new GarnetException($"Sorted set side store file {path} failed its checksum");
                foreach (var (key, score) in upserts)
                    members[key] = score;
            }
            catch (EndOfStreamException e)
            {
                throw new GarnetException($"Sorted set side store file {path} is truncated", e);
            }
        }

        /// <summary>Writes one chain file. <see cref="Commit"/> finishes it and flushes it to disk; disposing without commit deletes it.</summary>
        public sealed class Writer : IDisposable
        {
            readonly string path;
            readonly FileStream file;
            readonly Fnv64Stream hashing;
            readonly BinaryWriter writer;
            long entries;
            bool committed;

            /// <summary>Bytes written so far.</summary>
            public long Length => hashing.Position;

            internal Writer(long uid, long seq, bool full, long baseSeq, long chainStart)
            {
                path = PathFor(uid, seq);
                _ = Directory.CreateDirectory(Path.GetDirectoryName(path));
                file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                hashing = new Fnv64Stream(file);
                writer = new BinaryWriter(hashing);
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write((byte)(full ? 0 : 1));
                writer.Write(uid);
                writer.Write(seq);
                writer.Write(baseSeq);
                writer.Write(chainStart);
            }

            /// <summary>Add or update a member.</summary>
            public void Upsert(byte[] key, double score)
            {
                writer.Write(TagUpsert);
                writer.Write(key.Length);
                writer.Write(key);
                writer.Write(score);
                entries++;
            }

            /// <summary>Delete a member (deltas only).</summary>
            public void Delete(byte[] key)
            {
                writer.Write(TagDelete);
                writer.Write(key.Length);
                writer.Write(key);
                entries++;
            }

            /// <summary>Finish the file and flush it to disk.</summary>
            public void Commit()
            {
                writer.Write(TagEnd);
                writer.Write(entries);
                writer.Flush();
                var checksum = hashing.Hash;
                writer.Write(checksum);
                writer.Flush();
                file.Flush(flushToDisk: true);
                committed = true;
            }

            /// <inheritdoc />
            public void Dispose()
            {
                writer.Dispose();
                if (!committed)
                {
                    try { File.Delete(path); } catch { /* best effort: an uncommitted file is never referenced */ }
                }
            }
        }

        /// <summary>Pass-through stream that keeps an FNV-1a 64 hash of the bytes read or written.</summary>
        sealed class Fnv64Stream : Stream
        {
            const ulong Offset = 14695981039346656037UL, Prime = 1099511628211UL;
            readonly Stream inner;
            ulong hash = Offset;
            long position;

            public Fnv64Stream(Stream inner) => this.inner = inner;

            public ulong Hash => hash;

            void Update(ReadOnlySpan<byte> data)
            {
                var h = hash;
                foreach (var b in data)
                    h = (h ^ b) * Prime;
                hash = h;
                position += data.Length;
            }

            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => inner.CanWrite;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => position; set => throw new NotSupportedException(); }
            public override void Flush() => inner.Flush();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                var n = inner.Read(buffer);
                Update(buffer[..n]);
                return n;
            }

            public override int ReadByte()
            {
                Span<byte> one = stackalloc byte[1];
                return Read(one) == 1 ? one[0] : -1;
            }

            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                Update(buffer);
                inner.Write(buffer);
            }

            public override void WriteByte(byte value) => Write([value]);

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    inner.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
