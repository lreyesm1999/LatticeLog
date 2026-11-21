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

/// <summary>Files of the service under the data folder.</summary>
public static class ServicePaths
{
    public const string DefaultServiceName = "LatticeLog";
    /// <summary>First port tried on 127.0.0.1; when taken, any free port is used (clients read it from the endpoint file).</summary>
    public const int DefaultPort = 7462;

    public static string Dir(string dataDir) => Path.Combine(dataDir, "service");
    public static string EndpointFile(string dataDir) => Path.Combine(Dir(dataDir), "service.json");
    public static string LockFile(string dataDir) => Path.Combine(Dir(dataDir), "service.lock");
    public static string LogFile(string dataDir) => Path.Combine(Dir(dataDir), "service.log");

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
