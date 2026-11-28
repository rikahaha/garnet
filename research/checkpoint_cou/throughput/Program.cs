using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

// Closed-loop throughput benchmark for large sorted sets across snapshot checkpoints.
//
// Loads `sets` sorted sets z0..z{sets-1} of `members` members m0..m{members-1} (score = index), then runs `clients`
// connections for `run` seconds. Each connection sends `pipeline` commands, waits for all replies, and repeats. A command
// picks a set and an existing member uniformly at random and is ZSCORE with probability `readpct`%, else ZINCRBY by 1, so
// set sizes stay constant. With `zipf`=<theta> (e.g. 0.9) the member is drawn from a Zipf(theta) distribution over ranks
// instead, with ranks mapped to members by a fixed random permutation (the same hot members for every client). BGSAVE is issued at the `saveat` offsets (seconds). Output (JSON on stdout): completed operations
// per 10 ms, and per 100 ms a log-scale latency histogram (5% bins) with the exact maximum. Every operation of a batch is
// charged the batch's latency. Times are relative to `startUnixMs` so they can be aligned with the server's checkpoint trace.
static class Program
{
    const int TimelineMs = 10, HistMs = 100, Bins = 400;
    static readonly double LogBase = Math.Log(1.05);

    static int Bin(double us) => Math.Min(Bins - 1, us <= 1 ? 0 : (int)(Math.Log(us) / LogBase));

    static int Main(string[] args)
    {
        var o = args.Select(a => a.Split('=', 2)).ToDictionary(p => p[0].TrimStart('-'), p => p.Length > 1 ? p[1] : "true");
        string Get(string k, string d) => o.TryGetValue(k, out var v) ? v : d;
        int port = int.Parse(Get("port", "6398")), sets = int.Parse(Get("sets", "20")), members = int.Parse(Get("members", "100000"));
        int clients = int.Parse(Get("clients", "16")), pipeline = int.Parse(Get("pipeline", "1")), readPct = int.Parse(Get("readpct", "50"));
        double runS = double.Parse(Get("run", "20"));
        var saveAt = Get("saveat", "5,12").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(double.Parse).ToArray();
        bool load = Get("load", "true") == "true";
        double zipf = double.Parse(Get("zipf", "0"));

        // Zipf: cumulative weights over ranks 1..members, and a fixed rank -> member permutation.
        double[]? zipfCdf = null;
        int[]? zipfPerm = null;
        if (zipf > 0)
        {
            zipfCdf = new double[members];
            double acc = 0;
            for (var r = 0; r < members; r++) zipfCdf[r] = acc += 1.0 / Math.Pow(r + 1, zipf);
            for (var r = 0; r < members; r++) zipfCdf[r] /= acc;
            zipfPerm = Enumerable.Range(0, members).ToArray();
            new Random(42).Shuffle(zipfPerm);
        }
        int PickMember(Random rng)
        {
            if (zipfCdf is null) return rng.Next(members);
            var i = Array.BinarySearch(zipfCdf, rng.NextDouble());
            return zipfPerm![Math.Min(members - 1, i < 0 ? ~i : i)];
        }

        if (load)
        {
            var sw = Stopwatch.StartNew();
            Parallel.For(0, sets, new ParallelOptions { MaxDegreeOfParallelism = 8 }, s =>
            {
                using var c = new Conn(port);
                const int perCmd = 1000, window = 64;
                int sent = 0, read = 0;
                for (var b = 0; b < members; b += perCmd)
                {
                    var n = Math.Min(perCmd, members - b);
                    c.Begin(2 + 2 * n).Bulk("ZADD").Bulk($"z{s}");
                    for (var i = b; i < b + n; i++) c.Bulk(i).Bulk($"m{i}");
                    sent++;
                    if (sent - read >= window) { c.Flush(); c.SkipReply(); read++; }
                }
                c.Flush();
                while (read < sent) { c.SkipReply(); read++; }
            });
            Console.Error.WriteLine($"loaded {sets} x {members} in {sw.Elapsed.TotalSeconds:F1} s");
        }

        int timelineSlots = (int)(runS * 1000 / TimelineMs) + 1, histSlots = (int)(runS * 1000 / HistMs) + 1;
        var timeline = new long[clients][];
        var hist = new long[clients][][];
        var histMax = new double[clients][];
        using var go = new ManualResetEventSlim();
        long t0 = 0, endTicks = 0;
        var workers = Enumerable.Range(0, clients).Select(w => new Thread(() =>
        {
            var tl = timeline[w] = new long[timelineSlots];
            var h = hist[w] = Enumerable.Range(0, histSlots).Select(_ => new long[Bins]).ToArray();
            var hm = histMax[w] = new double[histSlots];
            var rng = new Random(1000 + w);
            using var c = new Conn(port);
            go.Wait();
            while (true)
            {
                var start = Stopwatch.GetTimestamp();
                if (start >= endTicks) break;
                for (var i = 0; i < pipeline; i++)
                {
                    int s = rng.Next(sets), m = PickMember(rng);
                    if (rng.Next(100) < readPct) c.Begin(3).Bulk("ZSCORE").Bulk($"z{s}").Bulk($"m{m}");
                    else c.Begin(4).Bulk("ZINCRBY").Bulk($"z{s}").Bulk(1).Bulk($"m{m}");
                }
                c.Flush();
                for (var i = 0; i < pipeline; i++) c.SkipReply();
                var end = Stopwatch.GetTimestamp();
                var us = (end - start) * 1e6 / Stopwatch.Frequency;
                var ms = (end - t0) * 1000.0 / Stopwatch.Frequency;
                var ti = Math.Min(timelineSlots - 1, (int)(ms / TimelineMs));
                var hi = Math.Min(histSlots - 1, (int)(ms / HistMs));
                tl[ti] += pipeline;
                h[hi][Bin(us)] += pipeline;
                if (us > hm[hi]) hm[hi] = us;
            }
        }) { IsBackground = true }).ToArray();
        foreach (var t in workers) t.Start();
        Thread.Sleep(500);  // connections established

        using var ctl = new Conn(port);
        var startUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        t0 = Stopwatch.GetTimestamp();
        endTicks = t0 + (long)(runS * Stopwatch.Frequency);
        go.Set();
        var saves = new List<double>();
        foreach (var at in saveAt)
        {
            var wait = at - (Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency;
            if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds(wait));
            saves.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            ctl.Begin(1).Bulk("BGSAVE").Flush();
            ctl.SkipReply();
        }
        foreach (var t in workers) t.Join();

        var mergedTimeline = new long[timelineSlots];
        foreach (var tl in timeline) for (var i = 0; i < timelineSlots; i++) mergedTimeline[i] += tl[i];
        var histOut = new List<object>();
        for (var i = 0; i < histSlots; i++)
        {
            var bins = new Dictionary<int, long>();
            double max = 0;
            for (var w = 0; w < clients; w++)
            {
                for (var b = 0; b < Bins; b++)
                    if (hist[w][i][b] != 0) bins[b] = bins.GetValueOrDefault(b) + hist[w][i][b];
                max = Math.Max(max, histMax[w][i]);
            }
            histOut.Add(new { maxUs = max, bins = bins.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value) });
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            sets, members, clients, pipeline, readPct, zipf, runS, startUnixMs, saveIssuedMs = saves, timelineMs = TimelineMs, histMs = HistMs,
            binBase = 1.05, totalOps = mergedTimeline.Sum(), timeline = mergedTimeline, hist = histOut,
        }));
        return 0;
    }

    /// <summary>Minimal RESP connection: buffered command writer and a reply skipper.</summary>
    sealed class Conn : IDisposable
    {
        readonly Socket socket;
        readonly byte[] outBuf = new byte[1 << 20], inBuf = new byte[1 << 16];
        int outLen, inPos, inLen;

        public Conn(int port)
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            socket.Connect("127.0.0.1", port);
        }

        void Raw(ReadOnlySpan<byte> b)
        {
            if (outLen + b.Length > outBuf.Length) Flush();
            b.CopyTo(outBuf.AsSpan(outLen));
            outLen += b.Length;
        }

        void Ascii(char prefix, long n)
        {
            Span<byte> tmp = stackalloc byte[24];
            tmp[0] = (byte)prefix;
            n.TryFormat(tmp[1..], out var w);
            tmp[1 + w] = (byte)'\r'; tmp[2 + w] = (byte)'\n';
            Raw(tmp[..(3 + w)]);
        }

        public Conn Begin(int argc) { Ascii('*', argc); return this; }

        public Conn Bulk(string s)
        {
            Span<byte> tmp = stackalloc byte[64];
            var n = Encoding.ASCII.GetBytes(s, tmp);
            Ascii('$', n); Raw(tmp[..n]); Raw("\r\n"u8);
            return this;
        }

        public Conn Bulk(long v) => Bulk(v.ToString());

        public Conn Flush()
        {
            var sent = 0;
            while (sent < outLen) sent += socket.Send(outBuf, sent, outLen - sent, SocketFlags.None);
            outLen = 0;
            return this;
        }

        byte Next()
        {
            if (inPos == inLen)
            {
                inLen = socket.Receive(inBuf);
                if (inLen <= 0) throw new IOException("connection closed");
                inPos = 0;
            }
            return inBuf[inPos++];
        }

        long Line(out string text)
        {
            var sb = new StringBuilder();
            byte b;
            while ((b = Next()) != '\r') sb.Append((char)b);
            Next();
            text = sb.ToString();
            return long.TryParse(text, out var n) ? n : 0;
        }

        public void SkipReply()
        {
            var t = Next();
            var n = Line(out var text);
            switch ((char)t)
            {
                case '-': throw new InvalidOperationException(text);
                case '+' or ':': return;
                case '$': for (long i = 0; i < n + 2 && n >= 0; i++) Next(); return;
                case '*': for (long i = 0; i < n; i++) SkipReply(); return;
                default: throw new InvalidOperationException($"bad reply type {(char)t}");
            }
        }

        public void Dispose() => socket.Dispose();
    }
}
