// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Tsavorite.core
{
    /// <summary>
    /// Research prototype: proactively serializes the large (v) heap objects of a snapshot checkpoint in the background, largest first,
    /// so that a CopyUpdate on a request thread (and the snapshot flush) reuses the bytes instead of serializing the object inline.
    /// </summary>
    /// <remarks>
    /// Configured by environment variables: TSAVORITE_PRESERIALIZE_THREADS (number of workers; 0 or unset disables it) and
    /// TSAVORITE_PRESERIALIZE_MIN_BYTES (minimum object heap size to pre-serialize; default 1 MB).
    /// </remarks>
    internal sealed class CheckpointPreSerializer
    {
        public static readonly int Threads = ReadEnv("TSAVORITE_PRESERIALIZE_THREADS", 0);
        public static readonly long MinBytes = ReadEnv("TSAVORITE_PRESERIALIZE_MIN_BYTES", 1L << 20);

        public static bool Enabled => Threads > 0;

        readonly List<HeapObjectBase> candidates = new();
        readonly Task task;
        volatile bool stopping;
        int nextCandidate;

        CheckpointPreSerializer(Action<List<HeapObjectBase>> collectCandidates)
            => task = Task.Run(() => Run(collectCandidates));

        /// <summary>
        /// Start pre-serializing. Only call this once no (v) session or transaction can modify (v) objects in place.
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
        /// Stop the workers and return the pre-serialized objects that no CopyUpdate adopted to REST. Call this once the checkpoint's
        /// flush has completed, while (v) objects still cannot be modified in place.
        /// </summary>
        public void StopAndReset()
        {
            stopping = true;
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
            }
        }

        static T ReadEnv<T>(string name, T defaultValue) where T : IParsable<T>
            => T.TryParse(Environment.GetEnvironmentVariable(name), null, out var value) ? value : defaultValue;
    }
}
