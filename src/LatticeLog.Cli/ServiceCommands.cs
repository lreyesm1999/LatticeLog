using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using LatticeLog.Service;
using LatticeLog.Sources;
using LatticeLog.Storage;

namespace LatticeLog.Cli;

/// <summary>
/// <c>latticelog service run|start|stop|status|install|uninstall|ui</c> and <c>latticelog setup</c>: the per-user
/// background service and the one-step install (specs/local-sources.md, "Background service").
/// </summary>
public static class ServiceCommands
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    internal static string? ConfigDir(Args a) => a.Get("config-dir") is { } d ? Path.GetFullPath(d) : null;

    public static int Service(Args a)
    {
        var verb = a.Positional.ElementAtOrDefault(0) ?? "status";
        return verb switch
        {
            "run" => Run(a),
            "start" => Start(a),
            "stop" => Stop(a),
            "restart" => Restart(a),
            "status" => Status(a),
            "install" => Install(a),
            "uninstall" => Uninstall(a),
            "ui" => Ui(a),
            _ => throw new CliException(ExitCodes.Usage, "Usage: latticelog service run|start|stop|restart|status|install|uninstall|ui"),
        };
    }

    // ------------------------------------------------------------------ run (the service process itself)

    private static int Run(Args a)
    {
        ServiceHost host;
        try
        {
            host = ServiceHost.Start(new ServiceOptions
            {
                ConfigDir = ConfigDir(a),
                Port = a.Int("port", ServicePaths.DefaultPort),
                Version = Program.Version,
                Echo = a.Has("verbose") ? Console.Error : null,
            });
        }
        catch (ServiceAlreadyRunningException ex)
        {
            // Not an error for a service manager: the work is being done.
            Console.Error.WriteLine(ex.Message);
            return ExitCodes.Ok;
        }
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; ThreadPool.QueueUserWorkItem(_ => host.RequestStop()); });
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; ThreadPool.QueueUserWorkItem(_ => host.RequestStop()); };
        Console.Error.WriteLine($"LatticeLog service listening on 127.0.0.1:{host.Port} (pid {Environment.ProcessId}); console {host.Endpoint.BaseUri}ui; log {host.Log.Path}. Ctrl+C stops it.");
        host.WaitForStop(Timeout.InfiniteTimeSpan);
        return ExitCodes.Ok;
    }

    // ------------------------------------------------------------------ start / stop

    private static int Start(Args a)
    {
        var configDir = ConfigDir(a);
        if (ServiceClient.Running(configDir) is { } running)
        {
            Console.WriteLine($"LatticeLog service already running (pid {running.Pid}, 127.0.0.1:{running.Port}).");
            return ExitCodes.Ok;
        }
        var ep = ServiceLauncher.EnsureRunning(configDir, TimeSpan.FromSeconds(30), m => Console.Error.WriteLine(m));
        if (ep is null)
            throw new CliException(ExitCodes.Internal, $"The service did not answer within 30 s. See {ServicePaths.LogFile(ServicePaths.ResolveDataDir(configDir))}, or run 'latticelog service run --verbose' to see it in the foreground.");
        Console.WriteLine($"LatticeLog service running (pid {ep.Pid}, 127.0.0.1:{ep.Port}).");
        return ExitCodes.Ok;
    }

    private static int Stop(Args a)
    {
        var stopped = ServiceClient.Stop(ConfigDir(a), TimeSpan.FromSeconds(30));
        Console.WriteLine(stopped ? "LatticeLog service stopped." : "LatticeLog service was not running.");
        return ExitCodes.Ok;
    }

    private static int Restart(Args a)
    {
        ServiceClient.Stop(ConfigDir(a), TimeSpan.FromSeconds(30));
        return Start(a);
    }

    // ------------------------------------------------------------------ status

    private static int Status(Args a)
    {
        var configDir = ConfigDir(a);
        string? registered = null;
        try { registered = ServiceLauncher.Registration(configDir).Detect(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { /* the service manager could not be asked: shown as not registered */ }
        JsonObject? status = null;
        try { status = ServiceClient.Request(configDir, "status")?["status"] as JsonObject; }
        catch (ServiceRefusedException ex) { Console.Error.WriteLine("warning: " + ex.Message); }
        status ??= Offline(configDir);
        status["registered"] = registered;
        if (a.Format == "json")
        {
            Console.WriteLine(status.ToJsonString(Indented));
            return ExitCodes.Ok;
        }
        var running = status["running"]?.GetValue<bool>() == true;
        if (running)
        {
            var up = TimeSpan.FromSeconds((long)status["uptime_seconds"]!);
            Console.WriteLine($"LatticeLog service: running, pid {status["pid"]}, version {status["version"]}, up {(int)up.TotalHours}h {up.Minutes}m, 127.0.0.1 only ({status["listen"]})");
        }
        else Console.WriteLine("LatticeLog service: not running" + (registered is null ? "" : " (it is registered; 'latticelog service start' starts it now)"));
        Console.WriteLine("Registration:      " + (registered switch
        {
            "task" => "Task Scheduler task 'LatticeLog' (starts at logon)",
            "run-key" => "per-user Run key (starts at logon)",
            "systemd" => "systemd user unit latticelog.service",
            "launchd" => "launchd agent dev.latticelog.service",
            _ => "not registered ('latticelog service install' makes it start at logon)",
        }));
        if (running)
        {
            Console.WriteLine($"Web console:       {status["console"]}   (open it: llog ui)");
            var sessions = status["sessions"]!;
            Console.WriteLine($"MCP sessions:      {sessions["active"]} active, {sessions["served"]} served since start");
            Console.WriteLine($"Last refresh:      {(string?)status["last_refresh"] ?? "-"} (every {status["refresh_interval_seconds"]} s); last maintenance: {(string?)status["last_maintenance"] ?? "-"}");
        }
        var disk = status["disk"]!;
        Console.WriteLine($"Data folder:       {status["data_dir"]}  {Mb((long)disk["data_folder_bytes"]!)} (indexes {Mb((long)disk["index_bytes"]!)}, migrated {Mb((long)disk["migrated_bytes"]!)})");
        Console.WriteLine($"Config folder:     {status["config_dir"]}");
        if (status["log_file"] is { } lf) Console.WriteLine($"Log:               {lf}");
        var sources = status["sources"]!.AsArray();
        Console.WriteLine();
        Console.WriteLine($"{"SOURCE",-24} {"KIND",-13} {"RECORDS",12} {"SIZE",10}  {"LAST INDEXED",-24} STATUS");
        foreach (var s in sources)
        {
            var err = (string?)s!["last_error"];
            var st = err is not null ? "error: " + err : (string?)s["status"] ?? "";
            if ((string?)s["retention"] is { } ret) st += $" (retention: {ret})";
            Console.WriteLine($"{(string?)s["name"],-24} {(string?)s["kind"],-13} {(long?)s["records"] ?? 0,12:N0} {Mb((long?)s["bytes"] ?? 0),10}  {(string?)s["last_indexed"] ?? (s["kind"]?.ToString() == "dotnet-store" ? "(read directly)" : "-"),-24} {st}");
        }
        if (sources.Count == 0) Console.WriteLine("(no sources: 'latticelog roots add <folder>' registers a folder of service logs)");
        foreach (var w in status["warnings"]?.AsArray() ?? []) Console.WriteLine("warning: " + w);
        var errors = status["errors"]?.AsArray() ?? [];
        if (errors.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Recent errors ({errors.Count}, newest last):");
            foreach (var e in errors.TakeLast(10)) Console.WriteLine("  " + e);
        }
        return ExitCodes.Ok;
    }

    private static string Mb(long bytes) => bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / 1048576.0:0.#} MB";

    /// <summary>Status without a running service: sources as on disk (no refresh), last indexed from the checkpoints.</summary>
    private static JsonObject Offline(string? configDir)
    {
        var catalog = SourceCatalog.Open(new CatalogOptions { ConfigDir = configDir, SearchProject = false });
        var sources = new JsonArray();
        foreach (var s in catalog.Sources)
        {
            var o = SourceCatalog.Describe(s);
            if (s.CheckpointPath is { } cp && File.Exists(cp)) o["last_indexed"] = UnixTime.FormatRfc3339((File.GetLastWriteTimeUtc(cp).Ticks - DateTime.UnixEpoch.Ticks) * 100);
            if (s.Kind != SourceKind.DotNet && catalog.Config.RetentionFor(s.Name) is { } r) o["retention"] = r.ToString();
            sources.Add((JsonNode)o);
        }
        long total = 0, index = 0, migrated = 0;
        if (Directory.Exists(catalog.DataDir))
            foreach (var f in new DirectoryInfo(catalog.DataDir).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(catalog.DataDir, f.FullName);
                total += f.Length;
                if (rel.StartsWith("index" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) index += f.Length;
                if (rel.StartsWith("migrated" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) migrated += f.Length;
            }
        var log = ServicePaths.LogFile(catalog.DataDir);
        return new JsonObject
        {
            ["format"] = "latticelog.service.status.v1",
            ["running"] = false,
            ["config_dir"] = ServicePaths.ResolveConfigDir(configDir),
            ["data_dir"] = catalog.DataDir,
            ["log_file"] = File.Exists(log) ? log : null,
            ["disk"] = new JsonObject { ["data_folder_bytes"] = total, ["index_bytes"] = index, ["migrated_bytes"] = migrated },
            ["sources"] = sources,
            ["warnings"] = new JsonArray(catalog.Warnings.Select(w => (JsonNode?)JsonValue.Create(w)).ToArray()),
            ["errors"] = new JsonArray(),
        };
    }

    // ------------------------------------------------------------------ install / uninstall (registration)

    private static int Install(Args a)
    {
        var configDir = ConfigDir(a);
        var reg = ServiceLauncher.Registration(configDir);
        return Register(reg, a.Get("method"), a.Has("dry-run"), start: !a.Has("no-start"), configDir);
    }

    internal static int Register(ServiceRegistration reg, string? method, bool dryRun, bool start, string? configDir, string? exeForSpawn = null)
    {
        var plan = reg.InstallPlan(method);
        Console.WriteLine((dryRun ? "Would register: " : "Registering: ") + plan);
        if (dryRun)
        {
            foreach (var (path, content, _) in plan.WriteFiles)
            {
                Console.WriteLine($"--- {path}");
                Console.WriteLine(content);
            }
            return ExitCodes.Ok;
        }
        var existing = reg.Detect();
        if (existing is not null && existing != plan.Method)
        {
            Console.WriteLine($"Removing the previous registration ({existing}).");
            reg.Apply(reg.UninstallPlan(existing));
        }
        reg.Apply(plan);
        Console.WriteLine($"Registered ({plan.Method}): the service starts at logon, runs as {reg.UserId} and listens on 127.0.0.1 only.");
        if (!start) return ExitCodes.Ok;
        if (ServiceClient.Running(configDir) is { } ep)
        {
            Console.WriteLine($"A service is already running (pid {ep.Pid}); 'latticelog service restart' switches to the registered one.");
            return ExitCodes.Ok;
        }
        if (!reg.StartRegistered(plan.Method)) SpawnInstalled(exeForSpawn, configDir);
        var running = ServiceClient.WaitRunning(configDir, TimeSpan.FromSeconds(30));
        Console.WriteLine(running is null
            ? $"Registered, but it did not answer within 30 s; see {ServicePaths.LogFile(reg.DataDir)}."
            : $"Started: pid {running.Pid}, 127.0.0.1:{running.Port}.");
        return ExitCodes.Ok;
    }

    private static void SpawnInstalled(string? exe, string? configDir)
    {
        if (exe is null) { ServiceLauncher.SpawnDetached(configDir, m => Console.Error.WriteLine(m)); return; }
        var file = OperatingSystem.IsWindows() ? ServiceLauncher.WindowlessCopy(exe) ?? exe : exe;
        var args = new List<string> { "service", "run" };
        if (configDir is not null) args.AddRange(["--config-dir", configDir]);
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(file, string.Join(' ', args.Select(ServiceRegistration.WinQuote))) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden })?.Dispose();
        else
        {
            var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var x in new[] { "-c", "nohup \"$0\" \"$@\" </dev/null >/dev/null 2>&1 &", file }.Concat(args)) psi.ArgumentList.Add(x);
            using var p = Process.Start(psi)!;
            p.WaitForExit(5000);
        }
    }

    private static int Uninstall(Args a)
    {
        var configDir = ConfigDir(a);
        var reg = ServiceLauncher.Registration(configDir);
        var method = reg.Detect();
        if (a.Has("dry-run"))
        {
            Console.WriteLine(method is null ? "Not registered; nothing to remove." : "Would run: " + reg.UninstallPlan(method));
            return ExitCodes.Ok;
        }
        if (ServiceClient.Stop(configDir, TimeSpan.FromSeconds(30))) Console.WriteLine("Stopped the running service.");
        if (method is null)
        {
            Console.WriteLine("The service was not registered.");
            return ExitCodes.Ok;
        }
        reg.Apply(reg.UninstallPlan(method));
        Console.WriteLine($"Unregistered ({method}). Indexes and migrated sources stay in {reg.DataDir}; your original log files were never changed.");
        return ExitCodes.Ok;
    }

    // ------------------------------------------------------------------ ui

    /// <summary><c>latticelog ui</c> (<c>llog ui</c>): opens the console in the browser unless <c>--no-open</c>; <c>-o</c> is accepted too.</summary>
    public static int OpenUi(Args a) => Ui(a, open: !a.Has("no-open"));

    private static int Ui(Args a) => Ui(a, open: a.Has("open"));

    private static int Ui(Args a, bool open)
    {
        var configDir = ConfigDir(a);
        var ep = ServiceClient.Running(configDir) ?? ServiceLauncher.EnsureRunning(configDir, TimeSpan.FromSeconds(30), m => Console.Error.WriteLine(m))
                 ?? throw new CliException(ExitCodes.Internal, "The service is not running and could not be started ('latticelog service status').");
        var url = $"{ep.BaseUri}ui#token={ep.Token}";
        Console.WriteLine(url);
        Console.Error.WriteLine("The link carries the service token (in the #fragment, never sent over the network); it reads every source of this user. Do not share it.");
        if (open)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            Console.Error.WriteLine("Opened in your browser. The page keeps the token and removes it from the address bar.");
        }
        return ExitCodes.Ok;
    }

    // ------------------------------------------------------------------ setup: install once

    public static readonly string[] Clients = ["claude-desktop", "claude-code", "cursor", "vscode"];

    public static string DefaultInstallDir => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LatticeLog")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");

    private static string ExeName => OperatingSystem.IsWindows() ? "latticelog.exe" : "latticelog";

    /// <summary>The short command <c>llog</c>, next to the executable: a .cmd for cmd and PowerShell plus a tiny sh script for
    /// Git Bash on Windows; a symlink elsewhere. Small text files (or a link), so an upgrade never leaves a stale copy.</summary>
    private static string[] ShortAliasFiles => OperatingSystem.IsWindows() ? ["llog.cmd", "llog"] : ["llog"];

    public static void WriteShortAlias(string installDir)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(Path.Combine(installDir, "llog.cmd"), "@\"%~dp0latticelog.exe\" %*\r\n");
                File.WriteAllText(Path.Combine(installDir, "llog"), "#!/bin/sh\nexec \"$(dirname \"$0\")/latticelog.exe\" \"$@\"\n");
            }
            else
            {
                var link = Path.Combine(installDir, "llog");
                if (File.Exists(link) || Directory.Exists(link)) File.Delete(link);
                File.CreateSymbolicLink(link, ExeName);
            }
            Console.WriteLine($"Short command: llog (same as latticelog), in {installDir}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not create the short command 'llog' in {installDir}: {ex.Message}. 'latticelog' still works.");
        }
    }

    /// <summary>
    /// <c>latticelog setup</c>: copies this single-file executable to a per-user folder (and onto the user's PATH),
    /// registers and starts the background service, and adds LatticeLog to the MCP clients the user picks.
    /// <c>latticelog setup --uninstall</c> undoes all of it except the data folder (unless <c>--remove-data</c>).
    /// </summary>
    public static int Setup(Args a)
    {
        if (a.Has("uninstall")) return SetupUninstall(a);
        var self = Environment.ProcessPath ?? throw new CliException(ExitCodes.Internal, "Cannot locate this executable.");
        if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            || File.Exists(Path.Combine(Path.GetDirectoryName(self)!, "latticelog.dll")))
            throw new CliException(ExitCodes.Usage, "setup installs the single-file latticelog executable (Native AOT, no .NET needed). Build it with scripts/publish-aot.ps1 (or download a release) and run setup from it.");
        var configDir = ConfigDir(a);
        var dryRun = a.Has("dry-run");
        var installDir = Path.GetFullPath(a.Get("install-dir") ?? DefaultInstallDir);
        var target = Path.Combine(installDir, ExeName);
        var clients = ChooseClients(a);

        Console.WriteLine("LatticeLog setup");
        Console.WriteLine($"  executable : {target}");
        Console.WriteLine($"  service    : {(a.Has("no-service") ? "not registered (--no-service)" : "registered per user, starts at logon, 127.0.0.1 only")}");
        Console.WriteLine($"  MCP clients: {(clients.Count == 0 ? "none" : string.Join(", ", clients))}");
        Console.WriteLine($"  PATH       : {(a.Has("no-path") ? "unchanged (--no-path)" : installDir)}");
        if (dryRun) { Console.WriteLine("(dry run: nothing changed)"); return ExitCodes.Ok; }

        // 1. copy (stop a running service first: on Windows its executable cannot be replaced while it runs)
        if (!PathPolicy.SamePath(Path.GetFullPath(self), target))
        {
            if (ServiceClient.Stop(configDir, TimeSpan.FromSeconds(30))) Console.WriteLine("Stopped the running service to replace its executable.");
            Directory.CreateDirectory(installDir);
            ServiceLauncher.DeleteOldCopies(installDir);
            var tmp = target + ".new";
            File.Copy(self, tmp, overwrite: true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, (UnixFileMode)0b111_101_101);
            // An editor may be running 'latticelog.exe mcp' right now: then the old file is moved aside, not overwritten.
            var aside = ServiceLauncher.ReplaceFile(tmp, target);
            Console.WriteLine($"Installed {target}.");
            if (aside is not null)
                Console.WriteLine($"  The previous version is still running (an MCP client, probably): it was moved to {Path.GetFileName(aside)} and is removed by the next setup once it has exited. Restart your AI tools to load the new version.");
        }
        WriteShortAlias(installDir);
        // 2. PATH
        if (!a.Has("no-path")) EnsureOnPath(installDir);
        // 3. service
        if (!a.Has("no-service"))
        {
            var command = OperatingSystem.IsWindows() ? ServiceLauncher.WindowlessCopy(target) ?? target : target;
            var reg = new ServiceRegistration(new ProcessRunner(), command, [], configDir, ServicePaths.ResolveDataDir(configDir));
            Register(reg, a.Get("method"), dryRun: false, start: true, configDir, exeForSpawn: target);
        }
        // 4. MCP clients
        foreach (var c in clients)
        {
            var argv = new List<string> { "install", "--client", c, "--scope", "user", "--command", target, "--no-project" };
            if (a.Has("yes")) argv.Add("--yes");
            try { SourceCommands.Mcp(new Args(argv)); }
            catch (CliException ex) { Console.Error.WriteLine($"{c}: {ex.Message}"); }
        }
        Console.WriteLine();
        Console.WriteLine("Done. Next:");
        Console.WriteLine("  latticelog roots add <folder>     register the folder your services write logs into");
        Console.WriteLine("  latticelog service status         sources, last indexed time, disk use, errors");
        Console.WriteLine("  llog ui                           web console in your browser (llog = latticelog)");
        if (clients.Count > 0) Console.WriteLine($"  restart {string.Join(", ", clients)} so they load the LatticeLog MCP server");
        Console.WriteLine("Uninstall: latticelog setup --uninstall [--remove-data]");
        return ExitCodes.Ok;
    }

    private static List<string> ChooseClients(Args a)
    {
        if (a.Get("clients") is { } list)
        {
            if (list is "none" or "") return [];
            var chosen = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            foreach (var c in chosen)
                if (!Clients.Contains(c)) throw new CliException(ExitCodes.Usage, $"Unknown client '{c}'. Use a comma-separated list of {string.Join(", ", Clients)}, or none.");
            return chosen;
        }
        if (Console.IsInputRedirected || a.Has("yes")) return [];
        var picked = new List<string>();
        Console.WriteLine("Add LatticeLog to which MCP clients? (user-wide entries; existing files are backed up)");
        foreach (var c in Clients)
        {
            var detected = ClientDetected(c);
            Console.Write($"  {c}{(detected ? " (found)" : "")}? [{(detected ? "Y/n" : "y/N")}] ");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (answer is "y" or "yes" || (detected && answer is "" or null)) picked.Add(c);
        }
        return picked;
    }

    private static bool ClientDetected(string client)
    {
        try { return Directory.Exists(Path.GetDirectoryName(SourceCommands.ResolveClient(client, "user", Environment.CurrentDirectory).File)); }
        catch (CliException) { return false; }
    }

    private static void EnsureOnPath(string dir)
    {
        var sep = Path.PathSeparator;
        if (OperatingSystem.IsWindows())
        {
            var user = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
            if (user.Split(sep, StringSplitOptions.RemoveEmptyEntries).Any(p => PathPolicy.SamePath(p.TrimEnd('\\'), dir.TrimEnd('\\'))))
                return;
            Environment.SetEnvironmentVariable("Path", user.Length == 0 ? dir : user.TrimEnd(sep) + sep + dir, EnvironmentVariableTarget.User);
            Console.WriteLine($"Added {dir} to your user PATH (new terminals see it).");
        }
        else if (!(Environment.GetEnvironmentVariable("PATH") ?? "").Split(sep).Any(p => PathPolicy.SamePath(p.TrimEnd('/'), dir.TrimEnd('/'))))
            Console.WriteLine($"Add {dir} to PATH in your shell profile (for example: export PATH=\"{dir}:$PATH\").");
    }

    private static void RemoveFromPath(string dir)
    {
        if (!OperatingSystem.IsWindows()) return;
        var user = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
        var parts = user.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.RemoveAll(p => PathPolicy.SamePath(p.TrimEnd('\\'), dir.TrimEnd('\\'))) == 0) return;
        Environment.SetEnvironmentVariable("Path", string.Join(Path.PathSeparator, parts), EnvironmentVariableTarget.User);
        Console.WriteLine($"Removed {dir} from your user PATH.");
    }

    private static int SetupUninstall(Args a)
    {
        var configDir = ConfigDir(a);
        var installDir = Path.GetFullPath(a.Get("install-dir") ?? DefaultInstallDir);
        var dryRun = a.Has("dry-run");
        var dataDir = ServicePaths.ResolveDataDir(configDir);
        Console.WriteLine($"Uninstalling LatticeLog from {installDir}{(dryRun ? " (dry run)" : "")}.");
        if (dryRun)
        {
            Console.WriteLine("  stop the service, remove its registration, remove 'latticelog' from claude-desktop/claude-code/cursor/vscode user configs,");
            Console.WriteLine($"  remove {installDir} from PATH, delete {Path.Combine(installDir, ExeName)}{(a.Has("remove-data") ? $", delete the data folder {dataDir}" : "")}.");
            return ExitCodes.Ok;
        }
        Uninstall(new Args(["uninstall", .. configDir is null ? Array.Empty<string>() : ["--config-dir", configDir]]));
        foreach (var c in Clients)
        {
            try { SourceCommands.McpUninstall(new Args(["uninstall", "--client", c, "--scope", "user"]), quietWhenMissing: true); }
            catch (CliException ex) { Console.Error.WriteLine($"{c}: {ex.Message}"); }
        }
        RemoveFromPath(installDir);
        var oldCopies = Directory.Exists(installDir) ? Directory.EnumerateFiles(installDir, "*.old-*").Select(Path.GetFileName) : [];
        var files = new[] { ExeName, OperatingSystem.IsWindows() ? "latticelogw.exe" : null }.Concat(ShortAliasFiles).Concat(oldCopies)
            .Where(f => f is not null).Select(f => Path.Combine(installDir, f!)).Where(File.Exists).ToList();
        var self = Environment.ProcessPath is { } p ? Path.GetFullPath(p) : "";
        var later = new List<string>();
        foreach (var f in files)
        {
            if (PathPolicy.SamePath(f, self) && OperatingSystem.IsWindows()) { later.Add(f); continue; }
            try { File.Delete(f); Console.WriteLine($"Deleted {f}."); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { later.Add(f); }
        }
        if (later.Count > 0)
        {
            if (OperatingSystem.IsWindows())
            {
                // This executable is running: delete it (and the folder, if left empty) right after this process exits.
                var cmd = $"/d /c ping -n 3 127.0.0.1 >nul & del /f /q {string.Join(' ', later.Select(f => "\"" + f + "\""))} & rmdir \"{installDir}\" 2>nul";
                Process.Start(new ProcessStartInfo("cmd.exe", cmd) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden })?.Dispose();
                Console.WriteLine($"{string.Join(", ", later)} will be deleted a few seconds after this command exits.");
            }
            else Console.WriteLine($"Delete manually: {string.Join(", ", later)}");
        }
        if (a.Has("remove-data"))
        {
            if (Directory.Exists(dataDir)) { Directory.Delete(dataDir, recursive: true); Console.WriteLine($"Deleted the data folder {dataDir} (indexes and migrated sources; your original logs were never inside it)."); }
        }
        else Console.WriteLine($"Kept the data folder {dataDir} and the config folder {ServicePaths.ResolveConfigDir(configDir)} (--remove-data deletes the data folder).");
        return ExitCodes.Ok;
    }

    // ------------------------------------------------------------------ sources retention

    /// <summary><c>latticelog sources retention [&lt;name&gt;|*] [--max-age 30d] [--max-mb N] [--clear]</c></summary>
    public static int Retention(Args a)
    {
        var name = a.Positional.ElementAtOrDefault(1);
        if (name is null)
        {
            var (_, config) = SourceCommands.EditConfig(a, ConfigEdit.Get());
            var entries = (config["retention"] as JsonObject ?? []).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            if (entries.Count == 0) Console.WriteLine("No retention configured: LatticeLog's indexes and migrated sources keep everything.");
            foreach (var (k, v) in entries)
                Console.WriteLine($"{(k == "*" ? "* (default)" : k),-24} {new RetentionSetting((string?)v?["max_age"], (long?)v?["max_mb"])}");
            return ExitCodes.Ok;
        }
        if (a.Has("clear"))
        {
            var (removed, _) = SourceCommands.EditConfig(a, ConfigEdit.Retention(name, null, null, clear: true));
            Console.WriteLine(removed ? $"Retention for {name} removed." : $"No retention was set for {name}.");
            return ExitCodes.Ok;
        }
        var age = a.Get("max-age");
        long? mb = a.Get("max-mb") is null ? null : a.Int("max-mb", 0);
        if (age is null && mb is null) throw new CliException(ExitCodes.Usage, "Usage: latticelog sources retention <name|*> --max-age 30d and/or --max-mb N (or --clear)");
        if (age is not null) _ = Durations.Parse(age);
        if (mb is <= 0) throw new CliException(ExitCodes.Usage, "--max-mb must be positive.");
        SourceCommands.EditConfig(a, ConfigEdit.Retention(name, age, mb, clear: false));
        Console.WriteLine($"Retention for {(name == "*" ? "all sources (default)" : name)}: {new RetentionSetting(age, mb)}. Applied by the background service to LatticeLog's own copies (indexes, migrated sources), never to your original files or .NET stores.");
        return ExitCodes.Ok;
    }
}
