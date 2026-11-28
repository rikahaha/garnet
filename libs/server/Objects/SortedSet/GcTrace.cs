// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Threading;

namespace Garnet.server
{
    /// <summary>
    /// Research instrumentation, enabled by setting GARNET_GC_TRACE to an output CSV path:
    /// <list type="bullet">
    /// <item>every 10 ms, the GC counts per generation and the total GC pause time (time managed threads were suspended);</item>
    /// <item>if GARNET_GC_EVENTS is also set (a second CSV path), each GC's start and end (number, generation, type, reason) and
    /// each runtime suspension's start and end, from the runtime's own events, so stalls can be matched with actual pauses.</item>
    /// </list>
    /// </summary>
    internal static class GcTrace
    {
        static int started;
        static GcEventListener listener;

        public static void StartIfConfigured()
        {
            var path = Environment.GetEnvironmentVariable("GARNET_GC_TRACE");
            if (string.IsNullOrEmpty(path) || Interlocked.Exchange(ref started, 1) == 1)
                return;
            var eventsPath = Environment.GetEnvironmentVariable("GARNET_GC_EVENTS");
            if (!string.IsNullOrEmpty(eventsPath))
                listener = new GcEventListener(eventsPath);
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
                    {
                        writer.Flush();
                        listener?.Flush();
                    }
                    next += 10;
                    var wait = next - sw.ElapsedMilliseconds;
                    if (wait > 0)
                        Thread.Sleep((int)wait);
                }
            })
            { IsBackground = true, Name = "GcTrace" };
            thread.Start();
        }

        /// <summary>Writes GC and suspension events of the .NET runtime (Microsoft-Windows-DotNETRuntime, GC keyword).</summary>
        sealed class GcEventListener : EventListener
        {
            const EventKeywords GcKeyword = (EventKeywords)0x1;
            readonly StreamWriter writer;
            readonly object writerLock = new();
            readonly string path;

            public GcEventListener(string path)
            {
                this.path = path;
                writer = new StreamWriter(path, append: false) { AutoFlush = false };
                writer.WriteLine("unix_ms,event,count,depth,type,reason");
            }

            public void Flush()
            {
                lock (writerLock)
                    writer.Flush();
            }

            protected override void OnEventSourceCreated(EventSource source)
            {
                if (source.Name == "Microsoft-Windows-DotNETRuntime")
                    EnableEvents(source, EventLevel.Informational, GcKeyword);
            }

            protected override void OnEventWritten(EventWrittenEventArgs e)
            {
                // GCStart_V2: Count, Depth, Reason, Type; GCEnd_V1: Count, Depth; GCSuspendEEBegin_V1: Reason, Count; GCRestartEEEnd.
                string count = "", depth = "", type = "", reason = "";
                switch (e.EventName)
                {
                    case "GCStart_V2":
                        count = Get(e, "Count"); depth = Get(e, "Depth"); type = Get(e, "Type"); reason = Get(e, "Reason");
                        break;
                    case "GCEnd_V1":
                        count = Get(e, "Count"); depth = Get(e, "Depth");
                        break;
                    case "GCSuspendEEBegin_V1":
                        count = Get(e, "Count"); reason = Get(e, "Reason");
                        break;
                    case "GCRestartEEEnd_V1":
                    case "GCSuspendEEEnd_V1":
                    case "GCRestartEEBegin_V1":
                        break;
                    default:
                        return;
                }
                var line = FormattableString.Invariant($"{e.TimeStamp.ToUniversalTime().Subtract(DateTime.UnixEpoch).TotalMilliseconds:F1},{e.EventName},{count},{depth},{type},{reason}");
                lock (writerLock)
                    writer.WriteLine(line);
            }

            static string Get(EventWrittenEventArgs e, string name)
            {
                var i = e.PayloadNames?.IndexOf(name) ?? -1;
                return i >= 0 ? Convert.ToString(e.Payload[i], System.Globalization.CultureInfo.InvariantCulture) : "";
            }
        }
    }
}
