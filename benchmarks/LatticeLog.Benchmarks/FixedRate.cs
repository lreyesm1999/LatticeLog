// Fixed-rate latency benchmark (P02-T08, P14-T03): producers emit typed events on a fixed schedule into a real store
// and every call's latency is recorded. Two numbers per call:
//   - service time: how long the logging call itself took;
//   - response time: from the instant the call was *scheduled* to its return. It includes the time a producer spent
//     behind schedule because an earlier call was slow, so it does not hide stalls (coordinated omission).
// All samples are kept and sorted: percentiles are exact, not approximated.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using LatticeLog;
using LatticeLog.Core;
using LatticeLog.Storage;

public static class FixedRate
{
    private sealed record Result(string Sink, string Mode, int Producers, int Rate, long Calls, double SecondsBehind,
        double[] Service, double[] Response, int Gen0, int Gen1, int Gen2, LoggerStats Stats);

    public static int Run(string[] args)
    {
        var seconds = Arg(args, "--seconds", 5);
        var warmup = Arg(args, "--warmup", 1);
        var outDir = args.SkipWhile(a => a != "--out").Skip(1).FirstOrDefault() ?? "research/results";
        var rates = (args.SkipWhile(a => a != "--rates").Skip(1).FirstOrDefault() ?? "10000,100000,500000")
            .Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        var only = args.SkipWhile(a => a != "--only").Skip(1).FirstOrDefault(); // e.g. "store-Balanced-1"
        var repeat = Arg(args, "--repeat", 1);
        FsyncBytes = (long)Arg(args, "--fsync-mib", 0) * 1024 * 1024; // 0 = library default (off)
        SegmentMiB = Arg(args, "--segment-mib", 64);
        Diag = args.Contains("--diag");
        Queue = Arg(args, "--queue", 65_536);
        var results = new List<Result>();
        foreach (var (sink, mode) in new[] { ("store", DurabilityMode.Balanced), ("store", DurabilityMode.Durable), ("null", DurabilityMode.Balanced) })
            foreach (var producers in new[] { 1, 4 })
                foreach (var rate in rates)
                    for (var rep = 0; rep < repeat; rep++)
                    {
                        if (only is not null && only != $"{sink}-{mode}-{producers}") continue;
                        var r = Measure(sink, mode, producers, rate, seconds, warmup);
                        results.Add(r);
                        Console.WriteLine(Row(r));
                    }

        Directory.CreateDirectory(outDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var csv = Path.Combine(outDir, $"latency-{stamp}.csv");
        using (var w = new StreamWriter(csv))
        {
            w.WriteLine("sink,mode,producers,rate_per_s,calls,service_p50_ns,service_p99_ns,service_p999_ns,service_max_ns,response_p50_ns,response_p99_ns,response_p999_ns,response_max_ns,seconds_behind_schedule,gen0,gen1,gen2,accepted,written,dropped_saturation");
            foreach (var r in results)
                w.WriteLine(string.Join(',', r.Sink, r.Mode, r.Producers, r.Rate, r.Calls,
                    P(r.Service, 50), P(r.Service, 99), P(r.Service, 99.9), r.Service[^1],
                    P(r.Response, 50), P(r.Response, 99), P(r.Response, 99.9), r.Response[^1],
                    r.SecondsBehind.ToString("F3", CultureInfo.InvariantCulture), r.Gen0, r.Gen1, r.Gen2,
                    r.Stats.Accepted, r.Stats.Written, r.Stats.DroppedSaturation));
        }
        Console.WriteLine("results: " + csv);
        return 0;
    }

    private static long FsyncBytes;
    private static int SegmentMiB = 64;
    private static bool Diag;
    private static int Queue = 65_536;

    /// <summary>GC suspensions (SuspendEE begin -> RestartEE end) from the runtime's own event source, with generation and reason.</summary>
    private sealed class GcListener : EventListener
    {
        public readonly ConcurrentQueue<(DateTime Start, double Ms, string What)> Pauses = new();
        private DateTime _suspend;
        private string _current = "";
        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(source, EventLevel.Informational, (EventKeywords)0x1);
        }
        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            switch (e.EventName)
            {
                case "GCSuspendEEBegin_V1": _suspend = e.TimeStamp; break;
                case "GCStart_V2":
                    _current = $"gen{e.Payload?[1]} reason={e.Payload?[2]} type={e.Payload?[3]}";
                    break;
                case "GCRestartEEEnd_V1":
                    if (_suspend != default) Pauses.Enqueue((_suspend, (e.TimeStamp - _suspend).TotalMilliseconds, _current));
                    _suspend = default;
                    break;
            }
        }
    }

    private static int Arg(string[] args, string name, int fallback)
    {
        var v = args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        return v is null ? fallback : int.Parse(v, CultureInfo.InvariantCulture);
    }

    private static double P(double[] sorted, double percentile)
        => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1)];

    private static string Row(Result r) => string.Create(CultureInfo.InvariantCulture,
        $"| {r.Sink} | {r.Mode} | {r.Producers} | {r.Rate:N0} | {r.Calls:N0} | {P(r.Service, 50):F0} / {P(r.Service, 99):F0} / {P(r.Service, 99.9):F0} / {r.Service[^1]:F0} | {P(r.Response, 50):F0} / {P(r.Response, 99):F0} / {P(r.Response, 99.9):F0} / {r.Response[^1]:F0} | {r.Gen0}/{r.Gen1}/{r.Gen2} | {r.Stats.DroppedSaturation} |");

    private static Result Measure(string sinkKind, DurabilityMode mode, int producers, int totalRate, int seconds, int warmupSeconds)
    {
        var dir = Path.Combine(Path.GetTempPath(), "latticelog-latency", Guid.NewGuid().ToString("N"));
        // Diagnostic timeline: everything is stamped in ms since the measured run started (Stopwatch based).
        var clock = Stopwatch.StartNew();
        var wallStart = DateTime.UtcNow;
        var timeline = new ConcurrentQueue<(double AtMs, string What)>();
        void Note(string what) => timeline.Enqueue((clock.Elapsed.TotalMilliseconds, what));
        using var gc = Diag ? new GcListener() : null;
        IRecordSink sink = sinkKind == "store"
            ? StoreWriter.Open(dir, new StoreWriterOptions { MinFreeDiskBytes = 0, SegmentMaxBytes = SegmentMiB * 1024L * 1024, Diagnostics = Diag ? Note : null })
            : new NullSink();
        var logger = new LatticeLogger(sink, new LatticeLoggerOptions
        {
            Durability = mode,
            FlushOnProcessExit = false,
            BalancedFsyncBytes = FsyncBytes,
            QueueCapacity = Queue,
            InternalDiagnostics = Diag ? Note : null,
        });
        try
        {
            var perProducer = totalRate / producers;
            var tickPerCall = Stopwatch.Frequency / (double)perProducer;
            var nsPerTick = 1e9 / Stopwatch.Frequency;
            var measured = (long)perProducer * seconds;
            var service = new double[producers][];
            var response = new double[producers][];
            var behind = new double[producers];
            var gc0 = 0; var gc1 = 0; var gc2 = 0;
            using var start = new Barrier(producers + 1);
            var threads = Enumerable.Range(0, producers).Select(p => new Thread(() =>
            {
                var s = new double[measured];
                var rsp = new double[measured];
                long warm = (long)perProducer * warmupSeconds;
                start.SignalAndWait();
                var t0 = Stopwatch.GetTimestamp();
                long i = 0;
                for (; i < warm + measured; i++)
                {
                    var scheduled = t0 + (long)(i * tickPerCall);
                    while (Stopwatch.GetTimestamp() < scheduled) Thread.SpinWait(8);
                    var before = Stopwatch.GetTimestamp();
                    BenchEvents.OrderPlaced(logger, i, p, "SKU-00042", 19.99);
                    var after = Stopwatch.GetTimestamp();
                    if (Diag && after - before > Stopwatch.Frequency / 100) // producer call > 10 ms
                        Note($"PRODUCER {p}: call took {(after - before) * nsPerTick / 1e6:F1} ms");
                    if (i >= warm)
                    {
                        s[i - warm] = (after - before) * nsPerTick;
                        rsp[i - warm] = (after - scheduled) * nsPerTick;
                    }
                }
                var end = t0 + (long)(i * tickPerCall);
                behind[p] = Math.Max(0, Stopwatch.GetTimestamp() - end) / (double)Stopwatch.Frequency;
                service[p] = s;
                response[p] = rsp;
            })
            { IsBackground = true, Priority = ThreadPriority.AboveNormal }).ToList();
            foreach (var t in threads) t.Start();
            clock.Restart();
            wallStart = DateTime.UtcNow;
            var g0 = GC.CollectionCount(0); var g1 = GC.CollectionCount(1); var g2 = GC.CollectionCount(2);
            start.SignalAndWait();
            foreach (var t in threads) t.Join();
            gc0 = GC.CollectionCount(0) - g0; gc1 = GC.CollectionCount(1) - g1; gc2 = GC.CollectionCount(2) - g2;
            logger.Flush(AckLevel.FsyncConfirmed, TimeSpan.FromSeconds(30));
            var stats = logger.GetStats();
            var allService = service.SelectMany(x => x).ToArray();
            var allResponse = response.SelectMany(x => x).ToArray();
            Array.Sort(allService);
            Array.Sort(allResponse);
            if (Diag) Console.WriteLine($"-- peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576} MiB, GC heap {GC.GetTotalMemory(false) / 1048576} MiB");
            if (Diag && allService[^1] > 10e6)
            {
                // Merge the timeline with GC suspensions > 2 ms; each line: start ms, duration, what.
                var lines = timeline.Select(x => (x.AtMs, x.What)).ToList();
                foreach (var (start0, ms, what) in gc!.Pauses)
                    if (ms >= 2) lines.Add(((start0 - wallStart).TotalMilliseconds + ms, $"GC pause {ms:F1} ms ({what})"));
                Console.WriteLine($"-- timeline ({sinkKind} {mode} {producers}p {totalRate}/s): event END times in ms since start");
                foreach (var (at, what) in lines.OrderBy(x => x.AtMs).Take(80)) Console.WriteLine($"   {at,9:F1}  {what}");
            }
            return new Result(sinkKind, mode.ToString(), producers, totalRate, allService.Length, behind.Max(), allService, allResponse, gc0, gc1, gc2, stats);
        }
        finally
        {
            logger.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
