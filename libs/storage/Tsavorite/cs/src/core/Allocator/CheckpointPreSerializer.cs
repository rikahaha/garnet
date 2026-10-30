// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Tsavorite.core
{
    /// <summary>
    /// Research prototype: shares the serialized (v) data of large heap objects during a snapshot checkpoint, so that a CopyUpdate on a
    /// request thread reuses it instead of serializing the object inline. The data comes from two sources:
    /// <list type="bullet">
    /// <item>background workers that serialize the large (v) objects ahead of time, largest first (TSAVORITE_PRESERIALIZE_THREADS); and</item>
    /// <item>the snapshot flush, which serializes each object into memory before writing it, instead of holding the object while it
    /// serializes straight to the device (TSAVORITE_FLUSH_CAPTURE=1).</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Configured by environment variables: TSAVORITE_PRESERIALIZE_THREADS (number of workers; 0 or unset disables them),
    /// TSAVORITE_PRESERIALIZE_MIN_BYTES (minimum object heap size to pre-serialize; default 1 MB) and TSAVORITE_FLUSH_CAPTURE.
    /// Only one checkpoint is tracked at a time (<see cref="Current"/>), which suffices for a single-database server.
    /// </remarks>
    internal sealed class CheckpointPreSerializer
    {
        public static readonly int Threads = ReadEnv("TSAVORITE_PRESERIALIZE_THREADS", 0);
        public static readonly long MinBytes = ReadEnv("TSAVORITE_PRESERIALIZE_MIN_BYTES", 1L << 20);
        public static readonly bool FlushCapture = Environment.GetEnvironmentVariable("TSAVORITE_FLUSH_CAPTURE") == "1";

        public static bool Enabled => Threads > 0 || FlushCapture;

        static CheckpointPreSerializer current;

        /// <summary>The checkpoint currently sharing serialized data, if any.</summary>
        public static CheckpointPreSerializer Current => Volatile.Read(ref current);

        readonly List<HeapObjectBase> candidates = new();
        readonly Task task;
        volatile bool stopping;
        int nextCandidate;

        /// <summary>Objects whose flush capture was published; guarded by itself, together with <see cref="published"/>.</summary>
        readonly List<HeapObjectBase> flushCaptured = new();
        bool published = true;

        CheckpointPreSerializer(Action<List<HeapObjectBase>> collectCandidates)
        {
            Volatile.Write(ref current, this);
            task = Threads > 0 ? Task.Run(() => Run(collectCandidates)) : Task.CompletedTask;
        }

        /// <summary>
        /// Start sharing serialized data. Only call this once no (v) session or transaction can modify (v) objects in place.
        /// </summary>
        /// <param name="collectCandidates">Fills the list with the candidate (v) objects</param>
        public static CheckpointPreSerializer Start(Action<List<HeapObjectBase>> collectCandidates)
            => Enabled ? new CheckpointPreSerializer(collectCandidates) : null;

        void Run(Action<List<HeapObjectBase>> collectCandidates)
        {
            collectCandidates(candidates);

            // Largest first: these stall a CopyUpdate the longest, and the request rate does not affect them.
            candidates.Sort((a, b) => b.HeapMemorySize.CompareTo(a.HeapMemorySize));

            var workers = new Task[Math.Min(Threads, candidates.Count)];
            for (var i = 0; i < workers.Length; i++)
                workers[i] = Task.Factory.StartNew(Work, TaskCreationOptions.LongRunning);
            Task.WaitAll(workers);
        }

        void Work()
        {
            int i;
            while (!stopping && (i = Interlocked.Increment(ref nextCandidate) - 1) < candidates.Count)
                _ = candidates[i].TryPreSerialize();
        }

        /// <summary>
        /// Publish a flush capture of <paramref name="heapObject"/>, which the caller holds in SERIALIZING, as PRESERIALIZED so that a
        /// CopyUpdate can reuse it. Fails once <see cref="StopAndReset"/> has started, so that no object is left PRESERIALIZED after it.
        /// </summary>
        public bool TryPublishFlushCapture(HeapObjectBase heapObject, SerializedObjectData data)
        {
            lock (flushCaptured)
            {
                if (!published)
                    return false;
                flushCaptured.Add(heapObject);
                heapObject.PublishPreSerialized(data);
                return true;
            }
        }

        /// <summary>
        /// Stop sharing, and return the pre-serialized objects that no CopyUpdate adopted to REST. Call this once the checkpoint's
        /// flush has completed, while (v) objects still cannot be modified in place.
        /// </summary>
        public void StopAndReset()
        {
            stopping = true;
            _ = Interlocked.CompareExchange(ref current, null, this);
            lock (flushCaptured)
                published = false;
            try
            {
                task.Wait();
            }
            catch (AggregateException)
            {
                // Pre-serialization is only an optimization: objects it did not reach are serialized by CopyUpdate or the flush as before.
            }
            finally
            {
                foreach (var candidate in candidates)
                    candidate.ResetPreSerialized();
                foreach (var captured in flushCaptured)
                    captured.ResetPreSerialized();
            }
        }

        static T ReadEnv<T>(string name, T defaultValue) where T : IParsable<T>
            => T.TryParse(Environment.GetEnvironmentVariable(name), null, out var value) ? value : defaultValue;
    }
}
