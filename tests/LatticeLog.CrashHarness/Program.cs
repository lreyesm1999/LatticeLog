// Child process for real crash tests (P14-T01, P05-T10). It writes records carrying a contiguous counter "n" from a
// single producer and prints "ACK <n> <level>" after every acknowledged flush. The parent kills the process at a random
// moment and then checks that every acknowledged record survived and that the store holds a gap-free prefix.
//
// usage: LatticeLog.CrashHarness <store-dir> <fast|balanced|durable> <first-n> <ack-every> [segment-max-bytes]
using LatticeLog;
using LatticeLog.Core;
using LatticeLog.Storage;

if (args.Length < 4)
{
    Console.Error.WriteLine("usage: LatticeLog.CrashHarness <store-dir> <fast|balanced|durable> <first-n> <ack-every> [segment-max-bytes]");
    return 2;
}

var dir = args[0];
var mode = args[1] switch
{
    "fast" => DurabilityMode.Fast,
    "balanced" => DurabilityMode.Balanced,
    "durable" => DurabilityMode.Durable,
    _ => throw new ArgumentException("unknown mode " + args[1]),
};
var n = long.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
var ackEvery = int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
var segmentMax = args.Length > 4 ? long.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 256 * 1024;

// Fast acknowledges WrittenToOs: a process kill keeps the OS page cache, so those records must survive too.
var ackLevel = mode == DurabilityMode.Fast ? AckLevel.WrittenToOs : AckLevel.FsyncConfirmed;

var logger = LatticeLogger.CreateLocal(dir, o =>
{
    o.Durability = mode;
    o.FlushOnProcessExit = false; // the test is about abrupt termination
    o.Resource.Add(new("service.name", "crash-harness"));
}, new StoreWriterOptions { SegmentMaxBytes = segmentMax, MinFreeDiskBytes = 0 });

Console.Out.WriteLine("READY");
Console.Out.Flush();
var pad = new string('x', 64);
while (true)
{
    for (var i = 0; i < ackEvery; i++, n++)
        logger.Info("crash harness record {n}", new LogAttribute("n", n), new LogAttribute("pad", pad));
    var result = await logger.FlushAsync(ackLevel);
    if (result.Level >= ackLevel)
    {
        Console.Out.WriteLine($"ACK {n - 1} {result.Level}");
        Console.Out.Flush();
    }
}
