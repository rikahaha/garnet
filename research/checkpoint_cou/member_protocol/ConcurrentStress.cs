using System.Diagnostics;
using System.Text;

namespace MemberProtocol;

static class ConcurrentStress
{
    public static void Run(int rounds = 2000)
    {
        const int writers = 4;
        var owner = new Owner(maxSlots: 2048);
        var keys = Enumerable.Range(0, 256).Select(i => Encoding.UTF8.GetBytes($"stress-{i:D4}")).ToArray();
        foreach (var key in keys) owner.Set(key, 0);
        var operations = new long[writers];
        int stopRound = 0;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var phases = new Barrier(writers + 1);
        var token = cancellation.Token;
        var tasks = Enumerable.Range(0, writers).Select(worker => Task.Factory.StartNew(() =>
        {
            try
            {
                var random = new Random(12345 + worker);
                for (int round = 0; round < rounds; round++)
                {
                    phases.SignalAndWait(token);
                    while (Volatile.Read(ref stopRound) == 0)
                    {
                        token.ThrowIfCancellationRequested();
                        var key = keys[random.Next(keys.Length)];
                        if (random.Next(3) == 0) owner.Remove(key);
                        else owner.Set(key, worker * 1_000_000_000d + Interlocked.Read(ref operations[worker]));
                        Interlocked.Increment(ref operations[worker]);
                    }
                    phases.SignalAndWait(token);
                }
            }
            catch { cancellation.Cancel(); throw; }
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        void WaitForWrites(long[] before)
        {
            var spin = new SpinWait();
            while (Enumerable.Range(0, writers).Any(i => Interlocked.Read(ref operations[i]) <= before[i]))
            {
                token.ThrowIfCancellationRequested();
                spin.SpinOnce();
            }
        }
        long[] Counts() => Enumerable.Range(0, writers).Select(i => Interlocked.Read(ref operations[i])).ToArray();
        var timer = Stopwatch.StartNew();
        int completed = 0, aborted = 0, roundsWithScanWrites = 0;
        try
        {
            for (int round = 0; round < rounds; round++)
            {
                var before = Counts();
                Volatile.Write(ref stopRound, 0);
                phases.SignalAndWait(token);
                WaitForWrites(before); // Writers are running before Begin, not released after it.
                Dictionary<string, double>? expected = null;
                var view = owner.Begin(rows => expected = rows.ToDictionary(
                    row => Convert.ToHexString(row.Key.Span), row => row.Score));
                var output = new Dictionary<string, double>();
                // Prove every writer completed an operation during the active view.
                WaitForWrites(Counts());
                bool abort = round % 10 == 0;
                var scanStart = Counts();
                try
                {
                    while (!owner.Done(view))
                    {
                        token.ThrowIfCancellationRequested();
                        foreach (var row in owner.Read(view, 3))
                        {
                            var key = Convert.ToHexString(row.Key.Span);
                            if (!output.TryAdd(key, row.Score) || !expected!.TryGetValue(key, out var score) || score != row.Score)
                                throw new Exception($"Concurrent mismatch/duplicate: round={round}, key={key}");
                        }
                        Thread.Yield();
                        if (abort) break; // Simulated consumer failure: discard this partial output.
                    }
                    if (!abort && (output.Count != view.Count || output.Count != expected!.Count))
                        throw new Exception($"Concurrent missing members: round={round}");
                }
                finally { owner.Close(view); } // Close also races writers, including abort drain.
                if (Counts().Zip(scanStart).Any(pair => pair.First > pair.Second)) roundsWithScanWrites++;
                Volatile.Write(ref stopRound, 1);
                phases.SignalAndWait(token); // Quiesce writers only for reclamation assertions.
                owner.ValidateReclaimedForTest();
                if (abort) aborted++; else completed++;
            }
        }
        finally
        {
            cancellation.Cancel();
            try { Task.WhenAll(tasks).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
        }
        if (roundsWithScanWrites == 0) throw new Exception("No writer progress during scan/close intervals");
        Console.WriteLine($"PASS: concurrent rounds={rounds}, writers={writers}, complete={completed}, abort={aborted}, rounds_with_scan_or_close_writes={roundsWithScanWrites}, operations={operations.Sum()}, elapsed_ms={timer.ElapsedMilliseconds}");
    }
}
