using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LatticeLog.Storage;
using LatticeLog.Storage.Format;

namespace LatticeLog.Sources;

public enum SourceKind { Python, DotNet, Migrated }

/// <summary>A named source of logs. Every source is queried through a LatticeLog store (<see cref="StoreRoot"/>).</summary>
public sealed class SourceInfo
{
    public required string Name { get; init; }
    public required SourceKind Kind { get; init; }
    /// <summary>The service folder (Python), the store folder (.NET) or the migrated source folder in the data folder.</summary>
    public required string Path { get; init; }
    /// <summary>The store that answers queries: the incremental index (Python), the store itself (.NET) or the migrated store.</summary>
    public required string StoreRoot { get; init; }
    public string? Environment { get; init; }
    public string? Service { get; init; }
    public required string Origin { get; init; }
    public int Processes { get; init; }
    /// <summary>Local sources: the SDK that wrote the folder (manifest <c>producer</c>; absent = the Python SDK).</summary>
    public string? Producer { get; init; }
    public bool Follow { get; init; }
    public IReadOnlyList<string> Patterns { get; init; } = [];
    public string? CheckpointPath { get; init; }
    public bool HasStore => StoreLayout.IsStore(StoreRoot);
}

public sealed record CatalogOptions
{
    public string? ConfigDir { get; init; }
    /// <summary>Folder where latticelog.toml is searched (and its parents). Null: the current directory.</summary>
    public string? ProjectDir { get; init; }
    public bool SearchProject { get; init; } = true;
}

/// <summary>
/// Finds sources by name (specs/local-sources.md): folders under the per-user roots and the project's latticelog.toml
/// holding a <c>latticelog.source.json</c> manifest (Python local mode) or a <c>STORE.json</c> (.NET store), plus
/// sources migrated into the data folder. Python sources are indexed incrementally into a .llog store per source;
/// .NET stores are read directly (also while their writer runs).
/// </summary>
public sealed class SourceCatalog
{
    public const string ManifestFile = "latticelog.source.json";
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase) { "node_modules", "__pycache__", "site-packages", "venv", "env" };
    private const int MaxDepth = 8;
    private const int MaxDirsPerRoot = 50_000;

    public UserConfig Config { get; }
    public ProjectConfig? Project { get; }
    public string DataDir { get; }
    /// <summary>Replaced as a whole by <see cref="Discover"/>: a reference read is a consistent snapshot.</summary>
    public IReadOnlyList<SourceInfo> Sources { get; private set; } = [];
    private readonly List<string> _warnings = [];
    private readonly object _discoverLock = new();

    /// <summary>Snapshot of the current warnings (discovery problems, indexing errors). Safe to read from any thread.</summary>
    public IReadOnlyList<string> Warnings { get { lock (_warnings) return _warnings.ToList(); } }

    public void AddWarning(string warning) { lock (_warnings) _warnings.Add(warning); }
    /// <summary>Single-store mode (<c>latticelog mcp &lt;dir&gt;</c>): no discovery, no migration.</summary>
    public bool IsSingleStore { get; }

    private readonly DateTime _configStamp, _projectStamp;

    private SourceCatalog(UserConfig config, ProjectConfig? project, string dataDir, bool single)
    {
        Config = config; Project = project; DataDir = dataDir; IsSingleStore = single;
        _configStamp = Stamp(config.Path);
        _projectStamp = project is null ? default : Stamp(project.FilePath);
    }

    private static DateTime Stamp(string file)
    {
        try { return File.Exists(file) ? File.GetLastWriteTimeUtc(file) : default; }
        catch (IOException) { return default; }
        catch (UnauthorizedAccessException) { return default; }
    }

    /// <summary>The options this catalog was opened with (a long-lived owner reopens it when <see cref="IsStale"/>).</summary>
    public CatalogOptions? Options { get; private init; }

    /// <summary>True when config.json or the project's latticelog.toml changed since this catalog was opened.</summary>
    public bool IsStale => !IsSingleStore && (Stamp(Config.Path) != _configStamp || (Project is not null && Stamp(Project.FilePath) != _projectStamp));

    public static SourceCatalog Open(CatalogOptions? options = null)
    {
        options ??= new CatalogOptions();
        var config = UserConfig.Load(options.ConfigDir);
        ProjectConfig? project = null;
        if (options.SearchProject && ProjectConfig.Find(options.ProjectDir ?? Environment.CurrentDirectory) is { } f) project = ProjectConfig.Load(f);
        var c = new SourceCatalog(config, project, config.EffectiveDataDir, single: false) { Options = options };
        c.Discover();
        return c;
    }

    /// <summary>One .NET store, named after its folder (or its manifest's service).</summary>
    public static SourceCatalog ForStore(string storeRoot)
    {
        var root = System.IO.Path.GetFullPath(storeRoot);
        if (!StoreLayout.IsStore(root)) throw new DirectoryNotFoundException($"'{storeRoot}' is not a LatticeLog store (missing {StoreLayout.StoreFile}).");
        var c = new SourceCatalog(UserConfig.Load(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "latticelog-none")), null, System.IO.Path.GetTempPath(), single: true);
        var m = ReadManifest(root);
        c.Sources = [new SourceInfo { Name = m?.Service ?? DirName(root), Kind = SourceKind.DotNet, Path = root, StoreRoot = root, Service = m?.Service, Environment = m?.Environment, Origin = "store" }];
        return c;
    }

    // ------------------------------------------------------------------ allowlist

    /// <summary>Folders inside which paths may be previewed or migrated: registered roots and the explicit allowlists.</summary>
    public IReadOnlyList<string> AllowedFolders
    {
        get
        {
            var list = new List<string>();
            list.AddRange(Config.Roots);
            list.AddRange(Config.Allow);
            if (Project is not null) { list.AddRange(Project.Roots); list.AddRange(Project.Allow); }
            return list.Select(PathPolicy.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>Null when <paramref name="path"/> may be read for migration; otherwise the reason, phrased for the user.</summary>
    public string? CheckAllowed(string path)
    {
        if (IsSingleStore) return "Migration is not available when the MCP server is bound to a single store (start 'latticelog mcp' without a store).";
        var full = PathPolicy.Normalize(path);
        if (PathPolicy.IsUnder(full, PathPolicy.Normalize(DataDir)))
            return $"'{full}' is inside the LatticeLog data folder; only original log files can be migrated.";
        var allowed = AllowedFolders;
        if (allowed.Any(a => PathPolicy.IsUnder(full, a))) return null;
        return $"'{full}' is outside the registered roots and the migration allowlist, so it was not read. "
               + "Ask the user to run 'latticelog roots add <folder>' or 'latticelog allow add <folder>' (or add it to [migrate] allow in latticelog.toml)."
               + (allowed.Count == 0 ? " No folder is registered yet." : " Allowed: " + string.Join("; ", allowed));
    }

    // ------------------------------------------------------------------ discovery

    /// <summary>Re-reads the roots and the data folder. Concurrent calls are serialized; readers keep the previous snapshot.</summary>
    public void Discover()
    {
        if (IsSingleStore) return;
        lock (_discoverLock)
        {
            var warnings = new List<string>();
            DiscoverInto(warnings);
            lock (_warnings) { _warnings.Clear(); _warnings.AddRange(warnings); }
        }
    }

    private void DiscoverInto(List<string> warnings)
    {
        var found = new List<SourceInfo>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var data = PathPolicy.Normalize(DataDir);
        // Project-declared sources first: their names win.
        if (Project is not null)
        {
            foreach (var (name, path, env) in Project.Sources)
            {
                var full = PathPolicy.Normalize(path);
                if (Describe(full, name, env, "toml:" + Project.FilePath, warnings) is { } s) { if (seenPaths.Add(full)) found.Add(s); }
                else warnings.Add($"latticelog.toml source '{name}': '{full}' has no {ManifestFile} or {StoreLayout.StoreFile} (yet).");
            }
        }
        var roots = Config.Roots.Select(r => ("root", r)).Concat((Project?.Roots ?? []).Select(r => ("toml-root", r)));
        foreach (var (kind, root) in roots)
        {
            var full = PathPolicy.Normalize(root);
            if (!Directory.Exists(full)) { warnings.Add($"root '{full}' does not exist."); continue; }
            var visited = 0;
            var queue = new Queue<(string Dir, int Depth)>();
            queue.Enqueue((full, 0));
            while (queue.Count > 0)
            {
                var (dir, depth) = queue.Dequeue();
                if (++visited > MaxDirsPerRoot) { warnings.Add($"root '{full}': stopped after {MaxDirsPerRoot} folders."); break; }
                if (PathPolicy.IsUnder(dir, data)) continue;
                var s = Describe(dir, null, null, kind + ":" + full, warnings);
                if (s is not null)
                {
                    if (seenPaths.Add(dir)) found.Add(s);
                    continue; // a source's subfolders (process folders, segments) are part of it
                }
                if (depth >= MaxDepth) continue;
                IEnumerable<string> subs;
                try { subs = Directory.EnumerateDirectories(dir).Order(StringComparer.Ordinal).ToList(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                foreach (var sub in subs)
                {
                    var n = System.IO.Path.GetFileName(sub);
                    if (n.StartsWith('.') || SkipDirs.Contains(n)) continue;
                    try { if (new DirectoryInfo(sub).LinkTarget is not null) continue; } // no symlink loops
                    catch (IOException) { continue; }
                    queue.Enqueue((sub, depth + 1));
                }
            }
        }
        found.AddRange(MigratedSources());
        Sources = AssignUniqueNames(found, warnings);
    }

    private SourceInfo? Describe(string dir, string? name, string? env, string origin, List<string> warnings)
    {
        if (File.Exists(System.IO.Path.Combine(dir, ManifestFile)) && !StoreLayout.IsStore(dir))
        {
            var m = ReadManifest(dir);
            if (m is null) { warnings.Add($"{System.IO.Path.Combine(dir, ManifestFile)}: unreadable manifest, skipped."); return null; }
            var index = IndexDir(dir, m.Service);
            return new SourceInfo
            {
                Name = name ?? m.Service,
                Kind = SourceKind.Python,
                Path = dir,
                StoreRoot = System.IO.Path.Combine(index, "store"),
                CheckpointPath = System.IO.Path.Combine(index, "checkpoint.json"),
                Service = m.Service,
                Environment = env ?? m.Environment,
                Origin = origin,
                Processes = ProcessFolders(dir).Count,
                Producer = m.Producer,
            };
        }
        if (StoreLayout.IsStore(dir))
        {
            var m = ReadManifest(dir);
            return new SourceInfo { Name = name ?? m?.Service ?? DirName(dir), Kind = SourceKind.DotNet, Path = dir, StoreRoot = dir, Service = m?.Service, Environment = env ?? m?.Environment, Origin = origin };
        }
        return null;
    }

    private IEnumerable<SourceInfo> MigratedSources()
    {
        var dir = System.IO.Path.Combine(DataDir, "migrated");
        if (!Directory.Exists(dir)) yield break;
        foreach (var d in Directory.EnumerateDirectories(dir).Order(StringComparer.Ordinal))
        {
            if (System.IO.Path.GetFileName(d).StartsWith('.')) continue; // staging folders of migrations in progress
            var state = MigrationState.TryLoad(d);
            if (state is null) continue;
            yield return new SourceInfo
            {
                Name = state.Name,
                Kind = SourceKind.Migrated,
                Path = d,
                StoreRoot = System.IO.Path.Combine(d, "store"),
                CheckpointPath = System.IO.Path.Combine(d, "checkpoint.json"),
                Service = state.Name,
                Origin = "migrated",
                Follow = state.Follow,
                Patterns = state.Patterns,
            };
        }
    }

    /// <summary>Collisions keep every source: later ones get <c>name@environment</c> or <c>name#2</c>.</summary>
    private static List<SourceInfo> AssignUniqueNames(List<SourceInfo> found, List<string> warnings)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SourceInfo>();
        // Migrated names were chosen explicitly: they keep them.
        foreach (var s in found.OrderBy(s => s.Kind == SourceKind.Migrated ? 0 : 1))
        {
            var name = s.Name;
            if (!names.Add(name))
            {
                var alt = s.Environment is { Length: > 0 } e ? $"{name}@{e}" : null;
                if (alt is null || !names.Add(alt))
                {
                    var i = 2;
                    while (!names.Add(alt = $"{name}#{i}")) i++;
                }
                warnings.Add($"two sources are named '{name}': '{s.Path}' is listed as '{alt}'.");
                name = alt!;
            }
            result.Add(name == s.Name ? s : Copy(s, name));
        }
        return result.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static SourceInfo Copy(SourceInfo s, string name) => new()
    {
        Name = name,
        Kind = s.Kind,
        Path = s.Path,
        StoreRoot = s.StoreRoot,
        Environment = s.Environment,
        Service = s.Service,
        Origin = s.Origin,
        Processes = s.Processes,
        Follow = s.Follow,
        Patterns = s.Patterns,
        CheckpointPath = s.CheckpointPath,
    };

    /// <summary>Sources for a selector: null/empty or "*" = all; otherwise names (case-insensitive). Unknown names throw.</summary>
    public IReadOnlyList<SourceInfo> Resolve(IReadOnlyList<string>? selector)
    {
        if (selector is null || selector.Count == 0 || selector.Contains("*")) return Sources;
        var result = new List<SourceInfo>();
        foreach (var n in selector)
        {
            var s = Sources.FirstOrDefault(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"Unknown source '{n}'. Known sources: {(Sources.Count == 0 ? "(none; register a folder with 'latticelog roots add <folder>')" : string.Join(", ", Sources.Select(x => x.Name)))}.");
            if (!result.Contains(s)) result.Add(s);
        }
        return result;
    }

    // ------------------------------------------------------------------ refresh (index Python sources, follow migrations)

    public sealed record RefreshOutcome(string Source, long Imported, string? Note);

    /// <summary>Rediscovers sources and brings every Python index (and followed migration) up to date.</summary>
    public List<RefreshOutcome> Refresh(CancellationToken ct = default)
    {
        var outcomes = new List<RefreshOutcome>();
        if (IsSingleStore) return outcomes;
        Discover();
        foreach (var s in Sources)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (s.Kind == SourceKind.Python) outcomes.Add(new(s.Name, IndexPython(s, ct), null));
                else if (s.Kind == SourceKind.Migrated && s.Follow) outcomes.Add(new(s.Name, new LogMigrator(this).Update(s, compact: false, ct: ct).Imported, null));
            }
            catch (StoreLockedException) { outcomes.Add(new(s.Name, 0, "being updated by another process; showing what is indexed")); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                outcomes.Add(new(s.Name, 0, ex.Message));
                AddWarning($"{s.Name}: {ex.Message}");
            }
        }
        return outcomes;
    }

    /// <summary>Indexes new complete lines of a Python source (all process folders) into its .llog store.</summary>
    public long IndexPython(SourceInfo s, CancellationToken ct = default)
    {
        var files = PythonFiles(s.Path);
        if (!Changed(s, files)) return 0;
        var res = IncrementalImporter.Run(s.StoreRoot, s.CheckpointPath!, files.Select(f => new InputFile(f, LogFormat.LatticeJsonl)).ToList(),
            new ImportRunOptions { DedupeProducerSequence = true }, ct);
        foreach (var w in res.Warnings) AddWarning($"{s.Name}: {w}");
        // Each refresh seals a small segment; keep the index compact.
        if (StoreReader.Open(s.StoreRoot).SegmentIds.Count > 32)
        {
            using var w = StoreWriter.Open(s.StoreRoot);
            Compactor.CompactAll(s.StoreRoot, CompactionPolicy.Balanced, w.AllocateSegmentId, ct: ct);
        }
        return res.Imported;
    }

    /// <summary>Cheap check (sizes against the checkpoint) so an idle source costs no store open.</summary>
    private static bool Changed(SourceInfo s, List<string> files)
    {
        if (!File.Exists(s.CheckpointPath) || !s.HasStore) return files.Count > 0;
        var cp = ImportCheckpoint.Load(s.CheckpointPath!);
        if (cp.Pending is not null) return true;
        foreach (var f in files)
        {
            var st = cp.Files.FirstOrDefault(x => PathPolicy.SamePath(x.Path, f));
            if (st is null) return true;
            long len;
            try { len = new FileInfo(f).Length; }
            catch (IOException) { continue; }
            if (len != st.Offset) return true;
        }
        return false;
    }

    public static List<string> PythonFiles(string sourceDir) =>
        ProcessFolders(sourceDir)
            .SelectMany(p => Directory.EnumerateFiles(p, "latticelog-*.jsonl").Order(StringComparer.Ordinal))
            .ToList();

    public static List<string> ProcessFolders(string sourceDir)
    {
        if (!Directory.Exists(sourceDir)) return [];
        return Directory.EnumerateDirectories(sourceDir, "p-*")
            .Where(d => int.TryParse(System.IO.Path.GetFileName(d)[2..], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .OrderBy(d => int.Parse(System.IO.Path.GetFileName(d)[2..], CultureInfo.InvariantCulture))
            .ToList();
    }

    // ------------------------------------------------------------------ helpers

    internal sealed record Manifest(string Service, string? Environment, string? ProducerId, string? Producer = null);

    internal static Manifest? ReadManifest(string dir)
    {
        var path = System.IO.Path.Combine(dir, ManifestFile);
        if (!File.Exists(path)) return null;
        try
        {
            var n = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (n is null || (string?)n["format"] != "latticelog.source" || (int?)n["version"] != 1 || (string?)n["service"] is not { Length: > 0 } svc) return null;
            return new Manifest(svc, (string?)n["environment"], (string?)n["producer_id"], (string?)n["producer"]);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or FormatException) { return null; }
    }

    private string IndexDir(string sourceDir, string service)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? sourceDir.ToUpperInvariant() : sourceDir)))[..12].ToLowerInvariant();
        return System.IO.Path.Combine(DataDir, "index", Slug(service) + "-" + hash);
    }

    internal static string Slug(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s) sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        return sb.Length == 0 ? "source" : sb.ToString(0, Math.Min(sb.Length, 48));
    }

    private static string DirName(string dir)
    {
        var name = System.IO.Path.GetFileName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        return name.Length == 0 ? dir : name;
    }

    /// <summary>Summary of a source's store for list_sources (footer indexes only; no record is decoded).</summary>
    public static JsonObject Describe(SourceInfo s)
    {
        var o = new JsonObject
        {
            ["name"] = s.Name,
            ["kind"] = s.Kind switch
            {
                SourceKind.Python => s.Producer switch
                {
                    "latticelog-rust" => "rust-local",
                    "latticelog-typescript" => "typescript-local",
                    _ => "python-local",
                },
                SourceKind.DotNet => "dotnet-store",
                _ => "migrated",
            },
            ["path"] = s.Path,
        };
        if (s.Service is not null) o["service"] = s.Service;
        if (s.Environment is not null) o["environment"] = s.Environment;
        if (s.Kind == SourceKind.Python) o["processes"] = s.Processes;
        if (s.Kind == SourceKind.Migrated)
        {
            o["follow"] = s.Follow;
            o["migrated_from"] = new JsonArray(s.Patterns.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
        }
        o["origin"] = s.Origin;
        if (!s.HasStore) { o["records"] = 0; o["status"] = "nothing indexed yet"; return o; }
        try
        {
            var r = StoreReader.Open(s.StoreRoot);
            long records = 0, bytes = 0, min = long.MaxValue, max = long.MinValue;
            var unsealed = 0;
            foreach (var id in r.SegmentIds)
            {
                try { bytes += new FileInfo(r.SegmentPath(id)).Length; } catch (IOException) { /* file removed meanwhile (retention or compaction): left out of the total */ }
                if (r.Indexes.GetValueOrDefault(id) is { HasRecords: true } x)
                {
                    records += x.RecordCount;
                    min = Math.Min(min, x.MinTimestamp);
                    max = Math.Max(max, x.MaxTimestamp);
                }
                else unsealed++;
            }
            o["store_id"] = r.Info.StoreId;
            o["records"] = records;
            if (unsealed > 0) o["records_note"] = $"{unsealed} active segment(s) not counted here; queries include them";
            o["bytes"] = bytes;
            if (records > 0)
            {
                o["first_time"] = UnixTime.FormatRfc3339(min);
                o["last_time"] = UnixTime.FormatRfc3339(max);
            }
            o["status"] = "ok";
        }
        catch (Exception ex) when (ex is IOException or LlogFormatException or InvalidDataException)
        {
            o["status"] = "unreadable: " + ex.Message;
        }
        return o;
    }
}
