using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestFramework("LatticeLog.Tests.CleanupTestFramework", "LatticeLog.Tests")]

namespace LatticeLog.Tests;

/// <summary>
/// Owner of the folders <see cref="TestData.TempDir"/> hands out. Each test process gets one run folder,
/// <c>%TEMP%\latticelog-tests\run-&lt;pid&gt;-&lt;process start&gt;</c>, deleted when the run ends. Windows keeps files open
/// by a store nobody disposed locked until the process exits, so the delete is best-effort; the next run sweeps
/// whatever is left once its owner process is gone.
/// </summary>
internal static class TestTempDirs
{
    /// <summary>Folders that are not run folders (the old flat layout, anything else) are swept once this old.</summary>
    private static readonly TimeSpan StaleAge = TimeSpan.FromDays(1);
    private const string StampFormat = "yyyyMMddHHmmss";

    public static readonly string Base = Path.Combine(Path.GetTempPath(), "latticelog-tests");

    private static readonly Lazy<string> Run = new(CreateRun, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>This process's run folder, created (after sweeping leftovers of earlier runs) on first use.</summary>
    public static string RunRoot => Run.Value;

    private static string CreateRun()
    {
        try { Sweep(); }
        catch (IOException) { /* a leftover folder vanished or is in use: housekeeping never fails a test */ }
        catch (UnauthorizedAccessException) { /* a leftover folder we may not touch: left for its owner */ }
        DateTime started;
        using (var self = Process.GetCurrentProcess()) started = self.StartTime.ToUniversalTime();
        var root = Path.Combine(Base, $"run-{Environment.ProcessId}-{started.ToString(StampFormat, CultureInfo.InvariantCulture)}");
        Directory.CreateDirectory(root);
        // Fallback for runners that bypass the assembly runner below; deleting twice is harmless.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
        return root;
    }

    /// <summary>Deletes this run's folder, retrying while files are still held open. Never throws.</summary>
    public static void Cleanup()
    {
        if (Run.IsValueCreated) DeleteTree(Run.Value, attempts: 3); // DeleteTree handles its own I/O errors
    }

    /// <summary>Removes run folders whose process has exited, and anything else older than a day.</summary>
    internal static void Sweep()
    {
        if (!Directory.Exists(Base)) return;
        var now = DateTime.UtcNow;
        foreach (var dir in Directory.EnumerateDirectories(Base))
        {
            var stale = TryParseRun(Path.GetFileName(dir), out var pid, out var started)
                ? !IsAlive(pid, started)
                : now - LastTouchedUtc(dir) > StaleAge;
            if (stale) DeleteTree(dir, attempts: 1);
        }
    }

    internal static bool TryParseRun(string name, out int pid, out DateTime startedUtc)
    {
        pid = 0;
        startedUtc = default;
        var parts = name.Split('-');
        return parts.Length == 3 && parts[0] == "run"
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out pid)
            && DateTime.TryParseExact(parts[2], StampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out startedUtc);
    }

    /// <summary>True when the process that made the run folder still runs (same PID and start time).</summary>
    private static bool IsAlive(int pid, DateTime startedUtc)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return Math.Abs((p.StartTime.ToUniversalTime() - startedUtc).TotalSeconds) < 2;
        }
        catch (ArgumentException) { return false; } // no such process
        catch (InvalidOperationException) { return false; } // exited meanwhile
        catch (Win32Exception) { return true; } // cannot inspect it: leave its folder alone
    }

    private static DateTime LastTouchedUtc(string dir)
    {
        var created = Directory.GetCreationTimeUtc(dir);
        var written = Directory.GetLastWriteTimeUtc(dir);
        return created > written ? created : written;
    }

    /// <summary>
    /// Best-effort recursive delete: a few whole-tree attempts, then file by file so that one locked segment
    /// keeps only itself (and its parent folders) on disk. Read-only files are made writable first.
    /// </summary>
    internal static void DeleteTree(string dir, int attempts)
    {
        for (var i = 0; i < attempts; i++)
        {
            if (!Directory.Exists(dir)) return;
            try
            {
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch (IOException) { /* a file is still open (an undisposed store): retry, then go file by file */ }
            catch (UnauthorizedAccessException) { /* read-only or locked file: the piecewise pass clears attributes */ }
            if (i + 1 < attempts) Thread.Sleep(100 * (i + 1));
        }
        DeletePiecewise(dir);
    }

    private static void DeletePiecewise(string dir)
    {
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir)) DeletePiecewise(sub);
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                catch (IOException) { /* still open: only this file stays; the next run's sweep retries it */ }
                catch (UnauthorizedAccessException) { /* not ours to delete: left in place */ }
            }
            Directory.Delete(dir, recursive: false);
        }
        catch (IOException) { /* not empty (a file above stayed) or gone meanwhile: the next sweep retries */ }
        catch (UnauthorizedAccessException) { /* not ours to delete: left in place */ }
    }
}

/// <summary>
/// The default xUnit framework plus one thing: after the last test of the assembly, and before xUnit reports the
/// assembly finished, the temp folders the tests made are deleted (xUnit 2 has no assembly fixtures; this is the
/// usual stand-in). It has to happen there: once the run is reported, VSTest kills the test host within milliseconds,
/// too soon for framework disposal or ProcessExit to delete a full run's stores.
/// </summary>
public sealed class CleanupTestFramework(IMessageSink messageSink) : XunitTestFramework(messageSink)
{
    protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName)
        => new Executor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);

    private sealed class Executor(AssemblyName assemblyName, ISourceInformationProvider sourceInformationProvider, IMessageSink diagnosticMessageSink)
        : XunitTestFrameworkExecutor(assemblyName, sourceInformationProvider, diagnosticMessageSink)
    {
        protected override async void RunTestCases(IEnumerable<IXunitTestCase> testCases, IMessageSink executionMessageSink, ITestFrameworkExecutionOptions executionOptions)
        {
            using var runner = new AssemblyRunner(TestAssembly, testCases, DiagnosticMessageSink, executionMessageSink, executionOptions);
            await runner.RunAsync();
        }
    }

    private sealed class AssemblyRunner(ITestAssembly testAssembly, IEnumerable<IXunitTestCase> testCases, IMessageSink diagnosticMessageSink, IMessageSink executionMessageSink, ITestFrameworkExecutionOptions executionOptions)
        : XunitTestAssemblyRunner(testAssembly, testCases, diagnosticMessageSink, executionMessageSink, executionOptions)
    {
        protected override async Task BeforeTestAssemblyFinishedAsync()
        {
            TestTempDirs.Cleanup();
            await base.BeforeTestAssemblyFinishedAsync();
        }
    }
}
