// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Tsavorite.core
{
    /// <summary>
    /// The serialized (v) data a heap object caches during a checkpoint.
    /// </summary>
    /// <remarks>
    /// By default this is one array, as before. With TSAVORITE_SERIALIZE_POOLED=1 (research prototype) it is a list of pooled chunks
    /// below the large-object-heap threshold: a large object then neither grows a MemoryStream through repeated LOH allocations nor
    /// copies it with ToArray, and the chunks are reused across checkpoints instead of being left to the GC. Because chunks go back to
    /// the pool, readers hold a reference (<see cref="TryAcquire"/>/<see cref="Release"/>) and the chunks are returned only after the
    /// last reader and the owning object have released it.
    /// </remarks>
    internal sealed class SerializedObjectData
    {
        public static readonly bool Pooled = Environment.GetEnvironmentVariable("TSAVORITE_SERIALIZE_POOLED") == "1";

        /// <summary>Below the 85,000-byte large-object-heap threshold.</summary>
        const int kChunkSize = 64 * 1024;

        /// <summary>Retains at most 16K chunks (1 GB) between checkpoints.</summary>
        static readonly ArrayPool<byte> chunkPool = ArrayPool<byte>.Create(kChunkSize, 16 * 1024);

        readonly byte[][] chunks;
        readonly int lastChunkLength;
        readonly bool pooled;
        int refCount = 1;   // the owning object's reference

        public readonly long Length;

        SerializedObjectData(byte[][] chunks, int lastChunkLength, long length, bool pooled)
        {
            this.chunks = chunks;
            this.lastChunkLength = lastChunkLength;
            this.pooled = pooled;
            Length = length;
        }

        /// <summary>Serialize <paramref name="heapObject"/>, without its type header.</summary>
        public static SerializedObjectData Capture(HeapObjectBase heapObject)
        {
            if (!Pooled)
            {
                using var ms = new MemoryStream();
                using var msWriter = new BinaryWriter(ms, Encoding.UTF8);
                heapObject.DoSerialize(msWriter);
                var bytes = ms.ToArray();
                return new SerializedObjectData([bytes], bytes.Length, bytes.Length, pooled: false);
            }

            using var stream = new ChunkWriteStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
                heapObject.DoSerialize(writer);
            return stream.Detach();
        }

        /// <summary>Take a reader reference; fails if the data has already been released.</summary>
        public bool TryAcquire()
        {
            while (true)
            {
                var count = Volatile.Read(ref refCount);
                if (count == 0)
                    return false;
                if (Interlocked.CompareExchange(ref refCount, count + 1, count) == count)
                    return true;
            }
        }

        /// <summary>Drop a reference (a reader's, or the owning object's); the last one returns the chunks to the pool.</summary>
        public void Release()
        {
            if (Interlocked.Decrement(ref refCount) != 0 || !pooled)
                return;
            foreach (var chunk in chunks)
                chunkPool.Return(chunk);
        }

        /// <summary>Write the data to <paramref name="writer"/>. Call only while holding a reference.</summary>
        public void WriteTo(BinaryWriter writer)
        {
            for (var i = 0; i < chunks.Length; i++)
                writer.Write(chunks[i], 0, i == chunks.Length - 1 ? lastChunkLength : (pooled ? kChunkSize : chunks[i].Length));
        }

        /// <summary>Write-only stream into pooled chunks.</summary>
        sealed class ChunkWriteStream : Stream
        {
            List<byte[]> chunks = new();
            int position = kChunkSize;  // position in the last chunk; full, so the first write rents one
            long length;

            public SerializedObjectData Detach()
            {
                var result = new SerializedObjectData(chunks.ToArray(), chunks.Count == 0 ? 0 : position, length, pooled: true);
                chunks = null;
                return result;
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                while (!buffer.IsEmpty)
                {
                    if (position == kChunkSize)
                    {
                        chunks.Add(chunkPool.Rent(kChunkSize));
                        position = 0;
                    }
                    var count = Math.Min(buffer.Length, kChunkSize - position);
                    buffer[..count].CopyTo(chunks[^1].AsSpan(position));
                    position += count;
                    length += count;
                    buffer = buffer[count..];
                }
            }

            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

            public override void WriteByte(byte value) => Write([value]);

            protected override void Dispose(bool disposing)
            {
                // Not detached: serialization failed, so return what was rented.
                if (chunks is not null)
                {
                    foreach (var chunk in chunks)
                        chunkPool.Return(chunk);
                    chunks = null;
                }
                base.Dispose(disposing);
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => length;
            public override long Position { get => length; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
