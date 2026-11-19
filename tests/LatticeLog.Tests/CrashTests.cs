using System.Diagnostics;
using System.Runtime.InteropServices;
using LatticeLog.Storage;
using Xunit.Abstractions;

namespace LatticeLog.Tests;

/// <summary>
/// Real crash tests (P14-T01, P05-T10): a child process writes and acknowledges records, the test kills it abruptly at a
/// random moment (no shutdown hooks, no seal) and then verifies the store: every acknowledged record survived, the
/// store holds a gap-free prefix with no duplicates, recovery is clean and writing can resume.
/// A process kill keeps the OS page cache, so this covers process crashes, not power loss.
/// </summary>
public class CrashTests(ITestOutputHelper output)
{
    private static string HarnessDll()
    {
        // tests/LatticeLog.Tests/bin/<Configuration>/net10.0/ -> tests/LatticeLog.CrashHarness/bin/<Configuration>/net10.0/
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var tfm = baseDir.Name;
        var configuration = baseDir.Parent!.Name;
        var testsDir = baseDir.Parent!.Parent!.Parent!.Parent!.FullName;
        var dll = Path.Combine(testsDir, "LatticeLog.CrashHarness", "bin", configuration, tfm, "LatticeLog.CrashHarness.dll");
        Assert.True(File.Exists(dll), "crash harness not built: " + dll);
        return dll;
    }

    private static string DotnetHost()
    {
        // <dotnet root>/shared/Microsoft.NETCore.App/<version>/ -> <dotnet root>/dotnet(.exe)
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        var root = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."));
        var exe = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        Assert.True(File.Exists(exe), "dotnet host not found: " + exe);
        return exe;
    }

    /// <summary>
    /// Runs the harness until it has acknowledged at least one record of this run, waits <paramref name="killAfter"/>
    /// more, kills it and returns the last acknowledged n. Waiting for the first acknowledgement keeps the kill point
    /// random without depending on how fast the machine starts the process and reaches its first flush.
    /// </summary>
    private static long RunAndKill(string store, string mode, long firstN, int ackEvery, TimeSpan killAfter, long segmentMaxBytes)
    {
        var psi = new ProcessStartInfo(DotnetHost())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(psi.FileName);
        foreach (var a in new[] { HarnessDll(), store, mode, firstN.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     ackEvery.ToString(System.Globalization.CultureInfo.InvariantCulture), segmentMaxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            psi.ArgumentList.Add(a);

        long lastAck = firstN - 1;
        var ready = new ManualResetEventSlim();
        var acknowledged = new ManualResetEventSlim();
        var stderr = new System.Text.StringBuilder();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (e.Data == "READY") ready.Set();
            else if (e.Data.StartsWith("ACK ", StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref lastAck, long.Parse(e.Data.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture));
                acknowledged.Set();
            }
        };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(30)), "harness did not start: " + stderr);
        Assert.True(acknowledged.Wait(TimeSpan.FromSeconds(30)), $"harness acknowledged nothing in 30 s ({mode}): " + stderr);
        Thread.Sleep(killAfter);
        Assert.False(p.HasExited, "harness exited early: " + stderr);
        p.Kill(entireProcessTree: true);
        p.WaitForExit(); // also drains the asynchronous stdout reader
        // Every ACK line the child managed to print is an acknowledgement the application could have acted on.
        return Interlocked.Read(ref lastAck);
    }

    private static (List<long> Ns, List<ulong> Seqs, ScanReport Report) ReadStore(string store)
    {
        var reader = StoreReader.Open(store);
        var report = new ScanReport();
        var ns = new List<long>();
        var seqs = new List<ulong>();
        foreach (var r in reader.ReadAll(report))
        {
            Assert.True(r.TryGetAttribute("n", out var n), "record without n");
            ns.Add(n.AsInt64());
            seqs.Add(r.Sequence);
        }
        return (ns, seqs, report);
    }

    [Theory]
    [InlineData("durable", 50, 3)]
    [InlineData("balanced", 200, 3)]
    [InlineData("fast", 500, 3)]
    public void Killed_process_never_loses_acknowledged_records(string mode, int ackEvery, int cycles)
    {
        var store = TestData.TempDir();
        var rnd = new Random(mode.GetHashCode(StringComparison.Ordinal) ^ 7919);
        long nextN = 1;
        var log = new List<string>();
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            var killAfter = TimeSpan.FromMilliseconds(rnd.Next(0, 1250)); // after the first acknowledgement of the run
            var lastAck = RunAndKill(store, mode, nextN, ackEvery, killAfter, segmentMaxBytes: 128 * 1024);

            // Recovery as the next writer would do it: must open, truncate any torn tail and keep everything valid.
            ulong lastSeq;
            using (var w = StoreWriter.Open(store, new StoreWriterOptions { MinFreeDiskBytes = 0 }))
            {
                lastSeq = w.LastSequence;
                foreach (var r in w.Recovery) log.Add($"cycle {cycle}: recovered segment {r.SegmentId}, truncated {r.TruncatedBytes} B");
            }

            var (ns, seqs, report) = ReadStore(store);
            Assert.True(report.IsClean, $"cycle {cycle}: store not clean after recovery:\n" + string.Join("\n", report.Issues));
            Assert.Equal(seqs.Count, seqs.Distinct().Count());
            Assert.Equal(seqs.OrderBy(s => s), seqs);
            Assert.Equal(lastSeq, seqs.Count == 0 ? 0UL : seqs[^1]);
            // Gap-free prefix 1..M with no duplicates, across all previous crashes.
            for (var i = 0; i < ns.Count; i++) Assert.True(ns[i] == i + 1, $"cycle {cycle}: expected n={i + 1} at position {i}, found {ns[i]}");
            var persisted = ns.Count == 0 ? 0 : ns[^1];
            Assert.True(persisted >= lastAck, $"cycle {cycle}: acknowledged n={lastAck} but only n={persisted} persisted ({mode})");
            Assert.True(lastAck >= nextN, $"cycle {cycle}: no acknowledgement observed ({mode})");
            log.Add($"cycle {cycle}: killed after {killAfter.TotalMilliseconds} ms, last ack {lastAck}, persisted {persisted}");
            nextN = persisted + 1;
        }
        var segments = Directory.GetFiles(Path.Combine(store, StoreLayout.SegmentsDir), "*.llog").Length;
        foreach (var line in log) output.WriteLine(line);
        output.WriteLine($"{segments} segments");
        Assert.True(segments > 1, "no rotation happened:\n" + string.Join("\n", log));
    }
}
