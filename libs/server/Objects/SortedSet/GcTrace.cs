// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Garnet.server
{
    /// <summary>
    /// Research instrumentation: every 10 ms, the process's GC counts per generation and total GC pause time, so that request stalls
    /// can be matched with GC pauses. Enabled by setting GARNET_GC_TRACE to an output CSV path.
    /// </summary>
    internal static class GcTrace
    {
        static int started;

        public static void StartIfConfigured()
        {
            var path = Environment.GetEnvironmentVariable("GARNET_GC_TRACE");
            if (string.IsNullOrEmpty(path) || Interlocked.Exchange(ref started, 1) == 1)
                return;
            var thread = new Thread(() =>
            {
                using var writer = new StreamWriter(path, append: false) { AutoFlush = false };
                writer.WriteLine("unix_ms,gen0,gen1,gen2,pause_ms,heap_mb");
                var sw = Stopwatch.StartNew();
                long next = 0, lines = 0;
                while (true)
                {
                    writer.WriteLine(FormattableString.Invariant(
                        $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},{GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)},{GC.GetTotalPauseDuration().TotalMilliseconds:F2},{GC.GetTotalMemory(false) >> 20}"));
                    if (++lines % 100 == 0)
                        writer.Flush();
                    next += 10;
                    var wait = next - sw.ElapsedMilliseconds;
                    if (wait > 0)
                        Thread.Sleep((int)wait);
                }
            })
            { IsBackground = true, Name = "GcTrace" };
            thread.Start();
        }
    }
}
