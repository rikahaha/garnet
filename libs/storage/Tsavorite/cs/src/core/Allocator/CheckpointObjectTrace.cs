// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.IO;

namespace Tsavorite.core
{
    /// <summary>
    /// Research instrumentation: traces heap-object serialization on the checkpoint paths (CopyUpdate on the
    /// request thread, and Serialize from flush). Enabled by setting TSAVORITE_COU_TRACE to an output CSV path.
    /// </summary>
    internal static class CheckpointObjectTrace
    {
        /// <summary>Flush events shorter than this are not recorded; CopyUpdate and pre-serialize events always are.</summary>
        const double kMinFlushUs = 100;

        public static readonly bool Enabled;
        static readonly StreamWriter writer;
        static readonly object writerLock = new();

        static CheckpointObjectTrace()
        {
            var path = Environment.GetEnvironmentVariable("TSAVORITE_COU_TRACE");
            if (string.IsNullOrEmpty(path))
                return;
            writer = new StreamWriter(path, append: true) { AutoFlush = true };
            writer.WriteLine("unix_ms,kind,thread,object,total_us,wait_us,serialize_us,toarray_us,gc_pause_us,bytes");
            Enabled = true;
        }

        public static long Now() => Stopwatch.GetTimestamp();

        public static double Us(long fromTicks, long toTicks) => (toTicks - fromTicks) * 1e6 / Stopwatch.Frequency;

        public static void Record(string kind, object obj, double totalUs, double waitUs, double serializeUs, double toArrayUs, TimeSpan gcPauseBefore, long bytes)
        {
            if (totalUs < kMinFlushUs && kind == "flush_serialize")
                return;
            var gcPauseUs = (GC.GetTotalPauseDuration() - gcPauseBefore).TotalMilliseconds * 1000;
            var line = FormattableString.Invariant(
                $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},{kind},{Environment.CurrentManagedThreadId},{obj.GetType().Name}:{obj.GetHashCode():x},{totalUs:F1},{waitUs:F1},{serializeUs:F1},{toArrayUs:F1},{gcPauseUs:F1},{bytes}");
            lock (writerLock)
                writer.WriteLine(line);
        }
    }
}
