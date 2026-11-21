using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LatticeLog.Sources;

/// <summary>
/// Per-user configuration (specs/local-sources.md): registered roots, the explicit migration allowlist and the data
/// folder. Stored as JSON in <c>%APPDATA%\LatticeLog\config.json</c> (Windows) or <c>~/.config/latticelog/config.json</c>.
/// <c>LATTICELOG_CONFIG_DIR</c> and <c>LATTICELOG_DATA_DIR</c> override the locations (tests, portable installs).
/// </summary>
public sealed class UserConfig
{
    public const string FileName = "config.json";
    public List<string> Roots { get; } = [];
    public List<string> Allow { get; } = [];
    public string? DataDir { get; set; }
    /// <summary>
    /// Retention of the stores LatticeLog owns (Python indexes and migrated sources), applied by the background
    /// service: key <c>*</c> is the default, other keys are source names. Originals and .NET stores are never touched.
    /// </summary>
    public Dictionary<string, RetentionSetting> Retention { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string Path { get; }

    private UserConfig(string path) => Path = path;

    public static string DefaultConfigDir => Environment.GetEnvironmentVariable("LATTICELOG_CONFIG_DIR") is { Length: > 0 } d
        ? d
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            OperatingSystem.IsWindows() ? "LatticeLog" : "latticelog");

    public static string DefaultDataDir => Environment.GetEnvironmentVariable("LATTICELOG_DATA_DIR") is { Length: > 0 } d
        ? d
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            OperatingSystem.IsWindows() ? "LatticeLog" : "latticelog");

    /// <summary>The data folder: config value, else <see cref="DefaultDataDir"/>.</summary>
    public string EffectiveDataDir => System.IO.Path.GetFullPath(Environment.GetEnvironmentVariable("LATTICELOG_DATA_DIR") is { Length: > 0 } env ? env : DataDir ?? DefaultDataDir);

    public static UserConfig Load(string? configDir = null)
    {
        var path = System.IO.Path.Combine(configDir ?? DefaultConfigDir, FileName);
        var c = new UserConfig(path);
        if (!File.Exists(path)) return c;
        var node = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                   ?? throw new InvalidDataException($"{path}: expected a JSON object.");
        foreach (var r in node["roots"] as JsonArray ?? []) if ((string?)r is { Length: > 0 } s) c.Roots.Add(s);
        foreach (var r in node["allow"] as JsonArray ?? []) if ((string?)r is { Length: > 0 } s) c.Allow.Add(s);
        c.DataDir = (string?)node["data_dir"];
        if (node["retention"] is JsonObject ret)
            foreach (var (name, v) in ret)
                if (v is JsonObject o) c.Retention[name] = new RetentionSetting((string?)o["max_age"], (long?)o["max_mb"]);
        return c;
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var node = new JsonObject
        {
            ["format"] = "latticelog.config",
            ["version"] = 1,
            ["roots"] = new JsonArray(Roots.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
            ["allow"] = new JsonArray(Allow.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
        };
        if (DataDir is not null) node["data_dir"] = DataDir;
        if (Retention.Count > 0)
        {
            var ret = new JsonObject();
            foreach (var (name, r) in Retention.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var o = new JsonObject();
                if (r.MaxAge is not null) o["max_age"] = r.MaxAge;
                if (r.MaxMb is not null) o["max_mb"] = r.MaxMb;
                ret[name] = o;
            }
            node["retention"] = ret;
        }
        AtomicFile.WriteAllText(Path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>The retention that applies to a source: its own entry, else the <c>*</c> default, else none.</summary>
    public RetentionSetting? RetentionFor(string source) =>
        Retention.TryGetValue(source, out var r) ? r : Retention.TryGetValue("*", out var d) ? d : null;

    /// <summary>Adds a normalized folder to a list; false when it is already there.</summary>
    public static bool AddFolder(List<string> list, string folder)
    {
        var full = PathPolicy.Normalize(folder);
        if (list.Any(x => PathPolicy.SamePath(PathPolicy.Normalize(x), full))) return false;
        list.Add(full);
        return true;
    }

    public static bool RemoveFolder(List<string> list, string folder)
    {
        var full = PathPolicy.Normalize(folder);
        return list.RemoveAll(x => PathPolicy.SamePath(PathPolicy.Normalize(x), full)) > 0;
    }
}

/// <summary>Retention for one source: maximum age (<c>30d</c>, <c>12h</c>) and/or maximum size in MiB.</summary>
public sealed record RetentionSetting(string? MaxAge, long? MaxMb)
{
    public TimeSpan? MaxAgeSpan => MaxAge is null ? null : Durations.Parse(MaxAge);
    public override string ToString() => string.Join(", ", new[] { MaxAge is null ? null : "max age " + MaxAge, MaxMb is null ? null : $"max {MaxMb} MiB" }.Where(x => x is not null));
}

public static class Durations
{
    /// <summary>Parses <c>90s</c>, <c>15m</c>, <c>2h</c>, <c>30d</c>.</summary>
    public static TimeSpan Parse(string s)
    {
        if (s.Length >= 2 && double.TryParse(s.AsSpan(0, s.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n > 0)
        {
            var unit = s[^1] switch { 's' => 1.0, 'm' => 60.0, 'h' => 3600.0, 'd' => 86400.0, _ => -1 };
            if (unit > 0) return TimeSpan.FromSeconds(n * unit);
        }
        throw new FormatException($"'{s}' is not a duration like 90s, 15m, 2h or 30d.");
    }
}

/// <summary>
/// Optional project file <c>latticelog.toml</c>, found in the working directory or any parent:
/// <code>
/// roots = ["logs"]
/// [sources]
/// demo-api = "logs/demo-api"
/// billing = { path = "../billing/logs", environment = "prod" }
/// [migrate]
/// allow = ["old-logs"]
/// </code>
/// Relative paths are relative to the file's folder. Only this subset of TOML is read (see <see cref="Toml"/>).
/// </summary>
public sealed class ProjectConfig
{
    public const string FileName = "latticelog.toml";
    public required string FilePath { get; init; }
    public string Directory => System.IO.Path.GetDirectoryName(FilePath)!;
    public List<(string Name, string Path, string? Environment)> Sources { get; } = [];
    public List<string> Roots { get; } = [];
    public List<string> Allow { get; } = [];

    /// <summary>Nearest latticelog.toml in <paramref name="startDir"/> or its parents, or null.</summary>
    public static string? Find(string startDir)
    {
        for (var d = new DirectoryInfo(System.IO.Path.GetFullPath(startDir)); d is not null; d = d.Parent)
        {
            var f = System.IO.Path.Combine(d.FullName, FileName);
            if (File.Exists(f)) return f;
        }
        return null;
    }

    public static ProjectConfig Load(string file)
    {
        var doc = Toml.Parse(File.ReadAllText(file), file);
        var pc = new ProjectConfig { FilePath = System.IO.Path.GetFullPath(file) };
        string Resolve(string p) => System.IO.Path.GetFullPath(System.IO.Path.Combine(pc.Directory, p));
        foreach (var r in doc.GetValueOrDefault("roots") as List<object> ?? []) if (r is string s) pc.Roots.Add(Resolve(s));
        if (doc.GetValueOrDefault("migrate") is Dictionary<string, object> m)
            foreach (var r in m.GetValueOrDefault("allow") as List<object> ?? []) if (r is string s) pc.Allow.Add(Resolve(s));
        if (doc.GetValueOrDefault("sources") is Dictionary<string, object> sources)
        {
            foreach (var (name, v) in sources)
            {
                switch (v)
                {
                    case string p: pc.Sources.Add((name, Resolve(p), null)); break;
                    case Dictionary<string, object> t when t.GetValueOrDefault("path") is string p:
                        pc.Sources.Add((name, Resolve(p), t.GetValueOrDefault("environment") as string));
                        break;
                    default: throw new InvalidDataException($"{file}: [sources] {name} must be a path string or {{ path = \"...\" }}.");
                }
            }
        }
        return pc;
    }
}

/// <summary>
/// Minimal TOML reader for latticelog.toml: comments, [table] and [a.b] headers, bare/quoted keys, basic and literal
/// strings, integers, booleans, arrays and inline tables. Anything else is a clear error, never a silent misread.
/// </summary>
public static class Toml
{
    public static Dictionary<string, object> Parse(string text, string fileName = "latticelog.toml")
    {
        var root = new Dictionary<string, object>(StringComparer.Ordinal);
        var table = root;
        var lineNo = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            lineNo++;
            var p = new Cursor(raw, fileName, lineNo);
            p.SkipWs();
            if (p.End || p.Peek == '#') continue;
            if (p.Peek == '[')
            {
                p.Next();
                if (!p.End && p.Peek == '[') throw p.Error("arrays of tables are not supported");
                table = root;
                foreach (var k in p.ReadKeyPath(']'))
                {
                    if (!table.TryGetValue(k, out var t)) table[k] = t = new Dictionary<string, object>(StringComparer.Ordinal);
                    table = t as Dictionary<string, object> ?? throw p.Error($"'{k}' is not a table");
                }
                p.Expect(']');
                p.SkipWs();
                if (!p.End && p.Peek != '#') throw p.Error("unexpected text after table header");
                continue;
            }
            var keys = p.ReadKeyPath('=');
            p.Expect('=');
            var value = p.ReadValue();
            p.SkipWs();
            if (!p.End && p.Peek != '#') throw p.Error("unexpected text after value");
            var target = table;
            for (var i = 0; i < keys.Count - 1; i++)
            {
                if (!target.TryGetValue(keys[i], out var t)) target[keys[i]] = t = new Dictionary<string, object>(StringComparer.Ordinal);
                target = t as Dictionary<string, object> ?? throw p.Error($"'{keys[i]}' is not a table");
            }
            if (!target.TryAdd(keys[^1], value)) throw p.Error($"duplicate key '{keys[^1]}'");
        }
        return root;
    }

    private sealed class Cursor(string s, string file, int line)
    {
        private int _i;
        public bool End => _i >= s.Length;
        public char Peek => s[_i];
        public char Next() => s[_i++];
        public InvalidDataException Error(string msg) => new($"{file}:{line}: {msg} (only a subset of TOML is supported).");
        public void SkipWs() { while (!End && (Peek == ' ' || Peek == '\t')) _i++; }
        public void Expect(char c)
        {
            SkipWs();
            if (End || Peek != c) throw Error($"expected '{c}'");
            _i++;
        }

        public List<string> ReadKeyPath(char terminator)
        {
            var keys = new List<string>();
            while (true)
            {
                SkipWs();
                if (End) throw Error("expected a key");
                if (Peek is '"' or '\'') keys.Add(ReadString());
                else
                {
                    var start = _i;
                    while (!End && (char.IsLetterOrDigit(Peek) || Peek is '_' or '-')) _i++;
                    if (start == _i) throw Error("expected a key");
                    keys.Add(s[start.._i]);
                }
                SkipWs();
                if (!End && Peek == '.') { _i++; continue; }
                if (End || Peek != terminator) throw Error($"expected '{terminator}'");
                return keys;
            }
        }

        public object ReadValue()
        {
            SkipWs();
            if (End) throw Error("missing value");
            switch (Peek)
            {
                case '"' or '\'': return ReadString();
                case '[':
                    {
                        _i++;
                        var list = new List<object>();
                        while (true)
                        {
                            SkipWs();
                            if (End) throw Error("unterminated array (multi-line arrays are not supported)");
                            if (Peek == ']') { _i++; return list; }
                            list.Add(ReadValue());
                            SkipWs();
                            if (!End && Peek == ',') { _i++; continue; }
                            if (!End && Peek == ']') { _i++; return list; }
                            throw Error("expected ',' or ']'");
                        }
                    }
                case '{':
                    {
                        _i++;
                        var t = new Dictionary<string, object>(StringComparer.Ordinal);
                        SkipWs();
                        if (!End && Peek == '}') { _i++; return t; }
                        while (true)
                        {
                            var k = ReadKeyPath('=');
                            Expect('=');
                            if (k.Count != 1) throw Error("dotted keys in inline tables are not supported");
                            if (!t.TryAdd(k[0], ReadValue())) throw Error($"duplicate key '{k[0]}'");
                            SkipWs();
                            if (!End && Peek == ',') { _i++; continue; }
                            if (!End && Peek == '}') { _i++; return t; }
                            throw Error("expected ',' or '}'");
                        }
                    }
            }
            var start = _i;
            while (!End && !(Peek is ',' or ']' or '}' or '#' or ' ' or '\t')) _i++;
            var tok = s[start.._i];
            if (tok == "true") return true;
            if (tok == "false") return false;
            if (long.TryParse(tok.Replace("_", ""), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) return n;
            throw Error($"unsupported value '{tok}'");
        }

        private string ReadString()
        {
            var q = Next();
            if (_i + 1 < s.Length && s[_i] == q && s[_i + 1] == q) throw Error("multi-line strings are not supported");
            var sb = new StringBuilder();
            while (true)
            {
                if (End) throw Error("unterminated string");
                var c = Next();
                if (c == q) return sb.ToString();
                if (c == '\\' && q == '"')
                {
                    if (End) throw Error("unterminated escape");
                    var e = Next();
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'u' or 'U':
                            {
                                var len = e == 'u' ? 4 : 8;
                                if (_i + len > s.Length) throw Error("bad unicode escape");
                                sb.Append(char.ConvertFromUtf32(int.Parse(s.AsSpan(_i, len), NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
                                _i += len;
                                break;
                            }
                        default: throw Error($"unknown escape '\\{e}' (use '/' or '\\\\' in Windows paths, or a 'literal string')");
                    }
                }
                else sb.Append(c);
            }
        }
    }
}

/// <summary>Path normalization and containment checks used by every path the MCP or CLI accepts.</summary>
public static class PathPolicy
{
    private static readonly StringComparison Cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Absolute path with the final symbolic link resolved and no trailing separator.</summary>
    public static string Normalize(string path)
    {
        var full = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        try
        {
            FileSystemInfo fi = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
            if (fi.LinkTarget is not null && fi.ResolveLinkTarget(returnFinalTarget: true) is { } target) full = target.FullName;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return full.Length > 3 ? full.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) : full;
    }

    public static bool SamePath(string a, string b) => string.Equals(a, b, Cmp);

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it (both normalized).</summary>
    public static bool IsUnder(string path, string folder)
    {
        if (SamePath(path, folder)) return true;
        var f = folder.EndsWith(System.IO.Path.DirectorySeparatorChar) ? folder : folder + System.IO.Path.DirectorySeparatorChar;
        return path.StartsWith(f, Cmp);
    }
}

internal static class AtomicFile
{
    public static void WriteAllText(string path, string text)
    {
        var tmp = path + ".tmp-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(new UTF8Encoding(false).GetBytes(text));
            fs.Flush(true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
