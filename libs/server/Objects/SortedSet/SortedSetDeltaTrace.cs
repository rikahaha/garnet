// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.IO;

namespace Garnet.server
{
    /// <summary>
    /// Research instrumentation (ckpt-delta, step 1): for each snapshot view written, how many bytes a full write takes and how
    /// many a member-level delta against the previous written view would take. Nothing is written differently; this only
    /// estimates. Enabled by setting GARNET_DELTA_TRACE to an output CSV path.
    /// </summary>
    internal static class SortedSetDeltaTrace
    {
        public static readonly bool Enabled;
        static readonly StreamWriter writer;
        static readonly object writerLock = new();

        static SortedSetDeltaTrace()
        {
            var path = Environment.GetEnvironmentVariable("GARNET_DELTA_TRACE");
            if (string.IsNullOrEmpty(path))
                return;
            writer = new StreamWriter(path, append: true) { AutoFlush = true };
            writer.WriteLine("unix_ms,object,members,changed,deleted,base_epoch,full_bytes,delta_bytes,written,file_bytes,scanned,scan_us,encode_us,commit_us,fsync_us,total_us");
            Enabled = true;
        }

        /// <summary>Phase timing of a side store write: members read from the view, time reading them (finding the rows to write),
        /// encoding rows into the file stream, committing the file (of which fsync), and the whole write.</summary>
        public readonly record struct Phases(long Scanned, double ScanUs, double EncodeUs, double CommitUs, double FsyncUs, double TotalUs);

        public static void Record(object obj, long members, long changed, long deleted, int baseEpoch, long fullBytes, long deltaBytes, string written, long fileBytes,
            Phases phases = default)
        {
            var line = FormattableString.Invariant(
                $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},{obj.GetType().Name}:{obj.GetHashCode():x},{members},{changed},{deleted},{baseEpoch},{fullBytes},{deltaBytes},{written},{fileBytes},{phases.Scanned},{phases.ScanUs:F0},{phases.EncodeUs:F0},{phases.CommitUs:F0},{phases.FsyncUs:F0},{phases.TotalUs:F0}");
            lock (writerLock)
                writer.WriteLine(line);
        }
    }
}
