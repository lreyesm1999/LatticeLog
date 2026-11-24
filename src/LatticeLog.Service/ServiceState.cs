using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LatticeLog.Sources;

namespace LatticeLog.Service;

/// <summary>
/// Where a running service can be found: written by the service into <c>&lt;data&gt;/service/service.json</c> when it
/// listens, deleted when it stops. The token in it authenticates clients (MCP shim, CLI, web console); the file lives in
/// the user's own data folder (mode 0600 on Unix), so only that user can connect.
/// </summary>
public sealed record ServiceEndpoint
{
    public required int Pid { get; init; }
    public required int Port { get; init; }
    public required string Token { get; init; }
    public required string Version { get; init; }
    public required DateTime StartedUtc { get; init; }
    public required string ConfigDir { get; init; }
    public required string DataDir { get; init; }
    public string? Exe { get; init; }

    public Uri BaseUri => new($"http://127.0.0.1:{Port.ToString(CultureInfo.InvariantCulture)}/");

    public JsonObject ToJson() => new()
    {
        ["format"] = "latticelog.service.endpoint",
        ["version"] = 1,
        ["pid"] = Pid,
        ["port"] = Port,
        ["token"] = Token,
        ["service_version"] = Version,
        ["started"] = StartedUtc.ToString("O", CultureInfo.InvariantCulture),
        ["config_dir"] = ConfigDir,
        ["data_dir"] = DataDir,
        ["exe"] = Exe,
    };

    public static ServiceEndpoint? TryLoad(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var n = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
            if (n is null || (string?)n["format"] != "latticelog.service.endpoint") return null;
            return new ServiceEndpoint
            {
                Pid = (int)n["pid"]!,
                Port = (int)n["port"]!,
                Token = (string)n["token"]!,
                Version = (string?)n["service_version"] ?? "?",
                StartedUtc = DateTime.Parse((string)n["started"]!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                ConfigDir = (string)n["config_dir"]!,
                DataDir = (string)n["data_dir"]!,
                Exe = (string?)n["exe"],
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or NullReferenceException)
        {
            return null;
        }
    }

    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            fs.Write(new UTF8Encoding(false).GetBytes(ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
            fs.Flush(true);
        }
        File.Move(tmp, file, overwrite: true);
    }
}

/// <summary>Files of the service: its log under the data folder, its endpoint and lock where every client finds them.</summary>
public static class ServicePaths
{
    public const string DefaultServiceName = "LatticeLog";
    /// <summary>First port tried on 127.0.0.1; when taken, any free port is used (clients read it from the endpoint file).</summary>
    public const int DefaultPort = 7462;

    public static string Dir(string dataDir) => Path.Combine(dataDir, "service");
    public static string LogFile(string dataDir) => Path.Combine(Dir(dataDir), "service.log");

    /// <summary>
    /// Where the endpoint file and the lock live. Usually <see cref="Dir"/>. On Windows, for the per-user default data
    /// folder, <c>%USERPROFILE%\.latticelog\service</c> instead: a packaged (MSIX) app such as Claude desktop gives the
    /// processes it starts a private copy of what they create under AppData, so a service started by an MCP client
    /// and a <c>latticelog</c> started from a terminal would each read their own endpoint file (and token). Windows
    /// never redirects the profile folder itself.
    /// </summary>
    public static string RendezvousDir(string dataDir) =>
        RendezvousDir(dataDir, OperatingSystem.IsWindows(), WindowsDefaultDataDir, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify));

    internal static string RendezvousDir(string dataDir, bool windows, string defaultDataDir, string profileDir) =>
        windows && profileDir.Length > 0 && SamePath(dataDir, defaultDataDir)
            ? Path.Combine(profileDir, ".latticelog", "service")
            : Dir(dataDir);

    public static string EndpointFile(string dataDir) => Path.Combine(RendezvousDir(dataDir), "service.json");
    public static string LockFile(string dataDir) => Path.Combine(RendezvousDir(dataDir), "service.lock");

    /// <summary>Where versions up to 0.1.0-alpha.9 kept the endpoint file and the lock (equal to the current ones off the default folder).</summary>
    public static string LegacyEndpointFile(string dataDir) => Path.Combine(Dir(dataDir), "service.json");
    public static string LegacyLockFile(string dataDir) => Path.Combine(Dir(dataDir), "service.lock");

    /// <summary>The per-user default data folder on Windows, ignoring overrides (<c>%LOCALAPPDATA%\LatticeLog</c>).</summary>
    internal static string WindowsDefaultDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "LatticeLog");

    internal static bool SamePath(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        static string N(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
        return string.Equals(N(a), N(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>Config folder as the service and its clients agree on it (explicit, else the per-user default).</summary>
    public static string ResolveConfigDir(string? configDir) => Path.GetFullPath(configDir ?? UserConfig.DefaultConfigDir);

    /// <summary>The data folder for a config folder (config value, LATTICELOG_DATA_DIR, else the per-user default).</summary>
    public static string ResolveDataDir(string? configDir) => UserConfig.Load(ResolveConfigDir(configDir)).EffectiveDataDir;
}

/// <summary>Append-only service log with one rotation (service.log → service.log.1 at 5 MiB). Never throws.</summary>
public sealed class ServiceLog(string path, TextWriter? echo = null)
{
    private const long MaxBytes = 5L * 1024 * 1024;
    private readonly object _gate = new();

    public string Path { get; } = path;

    public void Write(string message)
    {
        var line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {message}";
        lock (_gate)
        {
            try
            {
                echo?.WriteLine(line);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var fi = new FileInfo(Path);
                if (fi.Exists && fi.Length > MaxBytes) File.Move(Path, Path + ".1", overwrite: true);
                File.AppendAllText(Path, line + "\n", new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) { /* the service log must never stop the service */ }
        }
    }

    /// <summary>A TextWriter view (for components that log lines, such as the MCP server).</summary>
    public TextWriter AsWriter(string prefix = "") => new LogWriter(this, prefix);

    private sealed class LogWriter(ServiceLog log, string prefix) : TextWriter
    {
        private readonly StringBuilder _line = new();
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value)
        {
            if (value == '\n') { log.Write(prefix + _line.ToString().TrimEnd('\r')); _line.Clear(); }
            else _line.Append(value);
        }
        public override void WriteLine(string? value) => log.Write(prefix + value);
    }
}
