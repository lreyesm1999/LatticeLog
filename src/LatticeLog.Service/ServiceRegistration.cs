using System.Diagnostics;
using System.Security;
using System.Text;

namespace LatticeLog.Service;

/// <summary>Runs external commands (schtasks, reg, systemctl, launchctl). Tests substitute a recorder.</summary>
public interface ICommandRunner
{
    (int ExitCode, string Output) Run(string file, IReadOnlyList<string> args);
}

public sealed class ProcessRunner : ICommandRunner
{
    public (int ExitCode, string Output) Run(string file, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var err = p.StandardError.ReadToEndAsync();
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, (output + err.GetAwaiter().GetResult()).Trim());
        }
        catch (System.ComponentModel.Win32Exception ex) { return (-1, $"{file}: {ex.Message}"); }
    }
}

/// <summary>What install/uninstall will do: files written or removed, commands run, in order.</summary>
public sealed class RegistrationPlan
{
    public required string Method { get; init; }
    public required string Description { get; init; }
    public List<(string Path, string Content, Encoding Encoding)> WriteFiles { get; } = [];
    public List<string> DeleteFiles { get; } = [];
    public List<(string File, string[] Args, bool MayFail)> Commands { get; } = [];

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Description);
        foreach (var (p, _, _) in WriteFiles) sb.AppendLine($"  write  {p}");
        foreach (var (f, a, _) in Commands) sb.AppendLine($"  run    {f} {string.Join(' ', a.Select(Quote))}");
        foreach (var p in DeleteFiles) sb.AppendLine($"  delete {p}");
        return sb.ToString().TrimEnd();
    }

    private static string Quote(string s) => s.Length == 0 || s.Contains(' ', StringComparison.Ordinal) || s.Contains('"', StringComparison.Ordinal) ? "\"" + s.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : s;
}

/// <summary>
/// Per-user registration of the background service (no administrator rights, nothing system-wide):
/// <list type="bullet">
/// <item>Windows: a Task Scheduler task started at the user's logon (<c>--method task</c>, default), or the per-user
/// Run key (<c>--method run-key</c>). The task runs a copy of the executable marked as a GUI program
/// (<c>latticelogw.exe</c>), so no console window opens.</item>
/// <item>Linux: a systemd user unit (<c>~/.config/systemd/user/latticelog.service</c>).</item>
/// <item>macOS: a launchd agent (<c>~/Library/LaunchAgents/dev.latticelog.service.plist</c>).</item>
/// </list>
/// The service itself listens only on 127.0.0.1.
/// </summary>
public sealed class ServiceRegistration
{
    public const string WindowsTaskName = "LatticeLog";
    public const string RunKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
    public const string SystemdUnit = "latticelog.service";
    public const string LaunchdLabel = "dev.latticelog.service";

    private readonly ICommandRunner _runner;

    /// <param name="command">Executable that runs the service.</param>
    /// <param name="prefixArgs">Arguments before <c>service run</c> (for example the dll when hosted by dotnet).</param>
    /// <param name="platform">windows, linux or macos (default: this OS).</param>
    /// <param name="home">User home (default: this user's).</param>
    public ServiceRegistration(ICommandRunner runner, string command, IReadOnlyList<string>? prefixArgs, string? configDir, string dataDir,
        string? platform = null, string? home = null, string? userId = null)
    {
        _runner = runner;
        Command = command;
        PrefixArgs = prefixArgs ?? [];
        ConfigDir = configDir;
        DataDir = dataDir;
        Platform = platform ?? (OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux");
        Home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        UserId = userId ?? (Platform == "windows" ? $"{Environment.UserDomainName}\\{Environment.UserName}" : Environment.UserName);
    }

    public string Command { get; }
    public IReadOnlyList<string> PrefixArgs { get; }
    public string? ConfigDir { get; }
    public string DataDir { get; }
    public string Platform { get; }
    public string Home { get; }
    public string UserId { get; }

    public string DefaultMethod => Platform switch { "windows" => "task", "macos" => "launchd", _ => "systemd" };

    public string SystemdUnitPath => Path.Combine(Home, ".config", "systemd", "user", SystemdUnit);
    public string LaunchdPlistPath => Path.Combine(Home, "Library", "LaunchAgents", LaunchdLabel + ".plist");
    public string TaskXmlPath => Path.Combine(ServicePaths.Dir(DataDir), "latticelog-task.xml");

    /// <summary>Arguments after the executable: prefix + <c>service run</c> (+ the config folder when it is not the default).</summary>
    public List<string> RunArgs()
    {
        var a = new List<string>(PrefixArgs) { "service", "run" };
        if (ConfigDir is not null) a.AddRange(["--config-dir", ConfigDir]);
        return a;
    }

    /// <summary>Windows command-line quoting (CommandLineToArgvW rules).</summary>
    public static string WinQuote(string s)
    {
        if (s.Length > 0 && s.IndexOfAny([' ', '\t', '"']) < 0) return s;
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in s)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
            else sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    private static string SystemdQuote(string s) =>
        "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("%", "%%", StringComparison.Ordinal) + "\"";

    // ------------------------------------------------------------------ plans

    public RegistrationPlan InstallPlan(string? method = null)
    {
        method ??= DefaultMethod;
        var args = RunArgs();
        switch (Platform, method)
        {
            case ("windows", "task"):
                {
                    var p = new RegistrationPlan { Method = method, Description = $"Task Scheduler task '{WindowsTaskName}' for {UserId}, started at logon (no administrator rights needed)." };
                    p.WriteFiles.Add((TaskXmlPath, TaskXml(args), Encoding.Unicode));
                    p.Commands.Add(("schtasks", ["/Create", "/TN", WindowsTaskName, "/XML", TaskXmlPath, "/F"], false));
                    return p;
                }
            case ("windows", "run-key"):
                {
                    var p = new RegistrationPlan { Method = method, Description = $"Per-user Run key value '{WindowsTaskName}' (starts at logon)." };
                    p.Commands.Add(("reg", ["add", RunKey, "/v", WindowsTaskName, "/t", "REG_SZ", "/d", string.Join(' ', new[] { Command }.Concat(args).Select(WinQuote)), "/f"], false));
                    return p;
                }
            case ("linux", "systemd"):
                {
                    var p = new RegistrationPlan { Method = method, Description = $"systemd user unit {SystemdUnit} (systemctl --user), enabled at login." };
                    p.WriteFiles.Add((SystemdUnitPath, SystemdUnitText(args), new UTF8Encoding(false)));
                    p.Commands.Add(("systemctl", ["--user", "daemon-reload"], false));
                    p.Commands.Add(("systemctl", ["--user", "enable", SystemdUnit], false));
                    return p;
                }
            case ("macos", "launchd"):
                {
                    var p = new RegistrationPlan { Method = method, Description = $"launchd agent {LaunchdLabel}, loaded at login." };
                    p.WriteFiles.Add((LaunchdPlistPath, LaunchdPlist(args), new UTF8Encoding(false)));
                    p.Commands.Add(("launchctl", ["bootstrap", $"gui/{Uid()}", LaunchdPlistPath], false));
                    return p;
                }
            default:
                throw new ArgumentException($"Method '{method}' is not available on {Platform} (use {DefaultMethod}{(Platform == "windows" ? " or run-key" : "")}).");
        }
    }

    public RegistrationPlan UninstallPlan(string? method = null)
    {
        method ??= Detect() ?? DefaultMethod;
        switch (Platform, method)
        {
            case ("windows", "task"):
                {
                    var p = new RegistrationPlan { Method = method, Description = $"Remove the Task Scheduler task '{WindowsTaskName}'." };
                    p.Commands.Add(("schtasks", ["/Delete", "/TN", WindowsTaskName, "/F"], true));
                    p.DeleteFiles.Add(TaskXmlPath);
                    return p;
                }
            case ("windows", "run-key"):
                {
                    var p = new RegistrationPlan { Method = method, Description = $"Remove the per-user Run key value '{WindowsTaskName}'." };
                    p.Commands.Add(("reg", ["delete", RunKey, "/v", WindowsTaskName, "/f"], true));
                    return p;
                }
            case ("linux", "systemd"):
                {
                    var p = new RegistrationPlan { Method = method, Description = $"Disable and remove the systemd user unit {SystemdUnit}." };
                    p.Commands.Add(("systemctl", ["--user", "disable", "--now", SystemdUnit], true));
                    p.DeleteFiles.Add(SystemdUnitPath);
                    p.Commands.Add(("systemctl", ["--user", "daemon-reload"], true));
                    return p;
                }
            case ("macos", "launchd"):
                {
                    var p = new RegistrationPlan { Method = method, Description = $"Unload and remove the launchd agent {LaunchdLabel}." };
                    p.Commands.Add(("launchctl", ["bootout", $"gui/{Uid()}/{LaunchdLabel}"], true));
                    p.DeleteFiles.Add(LaunchdPlistPath);
                    return p;
                }
            default:
                throw new ArgumentException($"Method '{method}' is not available on {Platform}.");
        }
    }

    /// <summary>Writes the plan's files and runs its commands; throws on the first required step that fails.</summary>
    public void Apply(RegistrationPlan plan)
    {
        foreach (var (path, content, enc) in plan.WriteFiles)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, enc);
        }
        foreach (var (file, args, mayFail) in plan.Commands)
        {
            var (code, output) = _runner.Run(file, args);
            if (code != 0 && !mayFail)
                throw new InvalidOperationException($"'{file} {string.Join(' ', args)}' failed (exit {code}): {output}");
        }
        foreach (var path in plan.DeleteFiles)
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    /// <summary>The installed method, or null when the service is not registered.</summary>
    public string? Detect()
    {
        switch (Platform)
        {
            case "windows":
                if (_runner.Run("schtasks", ["/Query", "/TN", WindowsTaskName]).ExitCode == 0) return "task";
                if (_runner.Run("reg", ["query", RunKey, "/v", WindowsTaskName]).ExitCode == 0) return "run-key";
                return null;
            case "macos":
                return File.Exists(LaunchdPlistPath) ? "launchd" : null;
            default:
                return File.Exists(SystemdUnitPath) ? "systemd" : null;
        }
    }

    /// <summary>Starts the registered service through its manager (outside the caller's process tree). False when the
    /// method cannot start on demand (run-key) or the manager refused.</summary>
    public bool StartRegistered(string method) => method switch
    {
        "task" => _runner.Run("schtasks", ["/Run", "/TN", WindowsTaskName]).ExitCode == 0,
        "systemd" => _runner.Run("systemctl", ["--user", "start", SystemdUnit]).ExitCode == 0,
        "launchd" => _runner.Run("launchctl", ["kickstart", $"gui/{Uid()}/{LaunchdLabel}"]).ExitCode == 0,
        _ => false,
    };

    private string Uid()
    {
        var (code, output) = _runner.Run("id", ["-u"]);
        return code == 0 && output.Trim().Length > 0 ? output.Trim() : throw new InvalidOperationException("could not read the user id ('id -u').");
    }

    // ------------------------------------------------------------------ file contents

    public string TaskXml(IReadOnlyList<string> args)
    {
        static string X(string s) => SecurityElement.Escape(s);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>LatticeLog background service: indexes local log sources and serves MCP clients on 127.0.0.1 only. Remove with 'latticelog service uninstall'.</Description>
                <URI>\{WindowsTaskName}</URI>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{X(UserId)}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{X(UserId)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
                <RestartOnFailure>
                  <Interval>PT1M</Interval>
                  <Count>3</Count>
                </RestartOnFailure>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{X(Command)}</Command>
                  <Arguments>{X(string.Join(' ', args.Select(WinQuote)))}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    public string SystemdUnitText(IReadOnlyList<string> args) => $"""
        [Unit]
        Description=LatticeLog background service (indexes local log sources, serves MCP clients on 127.0.0.1)
        Documentation=https://github.com/lreyesm1999/LatticeLog

        [Service]
        Type=simple
        ExecStart={string.Join(' ', new[] { Command }.Concat(args).Select(SystemdQuote))}
        Restart=on-failure
        RestartSec=5

        [Install]
        WantedBy=default.target

        """;

    public string LaunchdPlist(IReadOnlyList<string> args)
    {
        static string X(string s) => SecurityElement.Escape(s);
        var items = string.Join("\n", new[] { Command }.Concat(args).Select(a => $"    <string>{X(a)}</string>"));
        var log = Path.Combine(ServicePaths.Dir(DataDir), "launchd.log");
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key>
              <string>{LaunchdLabel}</string>
              <key>ProgramArguments</key>
              <array>
            {items}
              </array>
              <key>RunAtLoad</key>
              <true/>
              <key>KeepAlive</key>
              <dict>
                <key>SuccessfulExit</key>
                <false/>
              </dict>
              <key>StandardOutPath</key>
              <string>{X(log)}</string>
              <key>StandardErrorPath</key>
              <string>{X(log)}</string>
            </dict>
            </plist>

            """;
    }
}
