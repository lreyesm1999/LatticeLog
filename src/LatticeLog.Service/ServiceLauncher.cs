using System.Diagnostics;

namespace LatticeLog.Service;

/// <summary>Starts the background service on demand (auto-start from the MCP shim, <c>latticelog service start</c>).</summary>
public static class ServiceLauncher
{
    /// <summary>The executable (and leading arguments) that run this latticelog build.</summary>
    public static (string File, List<string> Prefix) SelfCommand()
    {
        var exe = Environment.ProcessPath ?? "latticelog";
        var name = Path.GetFileNameWithoutExtension(exe);
        if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return (exe, [Path.Combine(AppContext.BaseDirectory, "latticelog.dll")]);
        return (exe, []);
    }

    public static ServiceRegistration Registration(string? configDir, ICommandRunner? runner = null)
    {
        var (file, prefix) = SelfCommand();
        if (prefix.Count == 0 && OperatingSystem.IsWindows() && WindowlessCopy(file) is { } w) file = w;
        return new ServiceRegistration(runner ?? new ProcessRunner(), file, prefix, configDir, ServicePaths.ResolveDataDir(configDir));
    }

    /// <summary>
    /// Returns a running service, starting it when needed: through its registration (Task Scheduler, systemd, launchd)
    /// when it is registered, else as a detached process. Null when it did not answer within <paramref name="timeout"/>.
    /// </summary>
    public static ServiceEndpoint? EnsureRunning(string? configDir, TimeSpan timeout, Action<string>? log = null)
    {
        if (ServiceClient.Running(configDir) is { } running) return running;
        var reg = Registration(configDir);
        string? method = null;
        if (RegisteredServiceMatches(configDir))
            try { method = reg.Detect(); } catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
        if (method is not null && reg.StartRegistered(method))
        {
            log?.Invoke($"started the registered LatticeLog service ({method})");
            if (ServiceClient.WaitRunning(configDir, timeout) is { } ep) return ep;
        }
        if (!SpawnDetached(configDir, log)) return null;
        return ServiceClient.WaitRunning(configDir, timeout);
    }

    /// <summary>
    /// Whether starting the registered service (Task Scheduler, systemd, launchd) can give this caller its service. The
    /// OS starts it with the per-user default folders, so a caller pointed elsewhere (<c>--config-dir</c>,
    /// <c>LATTICELOG_CONFIG_DIR</c>, <c>LATTICELOG_DATA_DIR</c>: tests, portable use) would only start the user's real
    /// service and then wait for an endpoint that never appears. Such callers start their own process instead.
    /// </summary>
    public static bool RegisteredServiceMatches(string? configDir) =>
        configDir is null
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LATTICELOG_CONFIG_DIR"))
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LATTICELOG_DATA_DIR"));

    /// <summary>
    /// Starts <c>latticelog service run</c> outside the caller's standard streams: the shim's stdin/stdout belong to the
    /// MCP client and must not be inherited by a process that outlives it.
    /// </summary>
    public static bool SpawnDetached(string? configDir, Action<string>? log = null)
    {
        var (file, prefix) = SelfCommand();
        var args = new List<string>(prefix) { "service", "run" };
        if (configDir is not null) args.AddRange(["--config-dir", Path.GetFullPath(configDir)]);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (prefix.Count == 0 && WindowlessCopy(file) is { } w) file = w;
                // ShellExecute does not inherit handles (CreateProcess with handle inheritance would pass the MCP pipes on).
                var psi = new ProcessStartInfo(file, string.Join(' ', args.Select(ServiceRegistration.WinQuote)))
                {
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetTempPath(),
                };
                using var p = Process.Start(psi);
            }
            else
            {
                var psi = new ProcessStartInfo("/bin/sh")
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = "/",
                };
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add("nohup \"$0\" \"$@\" </dev/null >/dev/null 2>&1 &");
                psi.ArgumentList.Add(file);
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                p.WaitForExit(5000);
            }
            log?.Invoke($"started LatticeLog service: {file} {string.Join(' ', args)}");
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            log?.Invoke($"could not start the LatticeLog service ({ex.Message})");
            return false;
        }
    }

    // ------------------------------------------------------------------ Windows: a console-less copy

    /// <summary>
    /// Windows: <c>latticelogw.exe</c> next to <paramref name="exe"/> — the same program with its PE subsystem set to GUI
    /// (like python.exe / pythonw.exe), so the background service runs without a console window. Created or refreshed
    /// when missing or older than the executable. Null when it cannot be made (read-only folder, not a PE file).
    /// </summary>
    public static string? WindowlessCopy(string exe)
    {
        if (!OperatingSystem.IsWindows() || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        var copy = Path.Combine(Path.GetDirectoryName(exe)!, Path.GetFileNameWithoutExtension(exe) + "w.exe");
        try
        {
            var src = new FileInfo(exe);
            var dst = new FileInfo(copy);
            if (dst.Exists && dst.Length == src.Length && dst.LastWriteTimeUtc >= src.LastWriteTimeUtc) return copy;
            var bytes = File.ReadAllBytes(exe);
            if (!SetGuiSubsystem(bytes)) return null;
            var tmp = copy + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            ReplaceFile(tmp, copy);
            return copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return File.Exists(copy) ? copy : null;
        }
    }

    /// <summary>
    /// Moves <paramref name="newFile"/> over <paramref name="target"/>. On Windows a running executable cannot be
    /// overwritten (an editor keeps <c>latticelog.exe mcp</c> running) but it can be renamed: the old file is moved aside
    /// as <c>&lt;name&gt;.old-&lt;timestamp&gt;</c> and removed by <see cref="DeleteOldCopies"/> once nothing runs it.
    /// Returns the name it was moved to, or null when the target was replaced directly.
    /// </summary>
    public static string? ReplaceFile(string newFile, string target)
    {
        try
        {
            File.Move(newFile, target, overwrite: true);
            return null;
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && File.Exists(target) && ex is UnauthorizedAccessException or IOException)
        {
            var aside = $"{target}.old-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            File.Move(target, aside);
            File.Move(newFile, target);
            return aside;
        }
    }

    /// <summary>Deletes the <c>*.old-*</c> copies <see cref="ReplaceFile"/> left in <paramref name="folder"/> that are no longer in use.</summary>
    public static int DeleteOldCopies(string folder)
    {
        var deleted = 0;
        if (!Directory.Exists(folder)) return 0;
        foreach (var f in Directory.EnumerateFiles(folder, "*.old-*"))
        {
            try { File.Delete(f); deleted++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* still running: next time */ }
        }
        return deleted;
    }

    /// <summary>Flips IMAGE_SUBSYSTEM_WINDOWS_CUI (3) to IMAGE_SUBSYSTEM_WINDOWS_GUI (2) in a PE image.</summary>
    public static bool SetGuiSubsystem(byte[] pe)
    {
        if (pe.Length < 0x40 || pe[0] != 'M' || pe[1] != 'Z') return false;
        var lfanew = BitConverter.ToInt32(pe, 0x3C);
        if (lfanew <= 0 || lfanew + 24 + 70 > pe.Length) return false;
        if (pe[lfanew] != 'P' || pe[lfanew + 1] != 'E' || pe[lfanew + 2] != 0 || pe[lfanew + 3] != 0) return false;
        var subsystem = lfanew + 24 + 68; // same offset in PE32 and PE32+ optional headers
        if (pe[subsystem] == 2 && pe[subsystem + 1] == 0) return true;
        if (pe[subsystem] != 3 || pe[subsystem + 1] != 0) return false;
        pe[subsystem] = 2;
        return true;
    }
}
