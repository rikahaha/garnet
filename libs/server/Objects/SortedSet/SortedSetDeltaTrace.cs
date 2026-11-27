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
            writer.WriteLine("unix_ms,object,members,changed,deleted,base_epoch,full_bytes,delta_bytes");
            Enabled = true;
        }

        public static void Record(object obj, long members, long changed, long deleted, int baseEpoch, long fullBytes, long deltaBytes)
        {
            var line = FormattableString.Invariant(
                $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},{obj.GetType().Name}:{obj.GetHashCode():x},{members},{changed},{deleted},{baseEpoch},{fullBytes},{deltaBytes}");
            lock (writerLock)
                writer.WriteLine(line);
        }
    }
}
