using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LatticeLog.Sources;

namespace LatticeLog.Cli;

/// <summary>Developer-flow commands: roots, allowlist, sources, migrate and MCP client setup (specs/local-sources.md).</summary>
public static class SourceCommands
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static SourceCatalog Catalog(Args a) => SourceCatalog.Open(new CatalogOptions { ProjectDir = a.Get("project") });

    /// <summary><c>roots add|remove|list</c> and <c>allow add|remove|list</c>: per-user folder lists.</summary>
    public static int FolderList(Args a, string what)
    {
        var verb = a.Positional.ElementAtOrDefault(0) ?? "list";
        var config = UserConfig.Load();
        var list = what == "roots" ? config.Roots : config.Allow;
        switch (verb)
        {
            case "add":
                {
                    var folder = a.Positional.ElementAtOrDefault(1) ?? throw new CliException(ExitCodes.Usage, $"Usage: latticelog {what} add <folder>");
                    if (!Directory.Exists(folder)) throw new CliException(ExitCodes.NotFound, $"Folder '{Path.GetFullPath(folder)}' does not exist.");
                    var added = UserConfig.AddFolder(list, folder);
                    config.Save();
                    Console.WriteLine(added ? $"Added {PathPolicy.Normalize(folder)} to {what} ({config.Path})." : $"{PathPolicy.Normalize(folder)} is already in {what}.");
                    if (what == "roots")
                    {
                        var c = SourceCatalog.Open(new CatalogOptions { SearchProject = false });
                        var under = c.Sources.Where(s => PathPolicy.IsUnder(s.Path, PathPolicy.Normalize(folder))).ToList();
                        Console.WriteLine(under.Count == 0 ? "No sources found there yet." : $"Sources found there: {string.Join(", ", under.Select(s => s.Name))}");
                    }
                    return ExitCodes.Ok;
                }
            case "remove":
                {
                    var folder = a.Positional.ElementAtOrDefault(1) ?? throw new CliException(ExitCodes.Usage, $"Usage: latticelog {what} remove <folder>");
                    var removed = UserConfig.RemoveFolder(list, folder);
                    config.Save();
                    Console.WriteLine(removed ? $"Removed {PathPolicy.Normalize(folder)} from {what}." : $"{PathPolicy.Normalize(folder)} was not in {what}.");
                    return removed ? ExitCodes.Ok : ExitCodes.NotFound;
                }
            case "list":
                if (a.Format == "json") Console.WriteLine(new JsonArray(list.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()).ToJsonString(Indented));
                else
                {
                    foreach (var r in list) Console.WriteLine(r);
                    Console.Error.WriteLine($"-- {list.Count} folder(s) in {config.Path}");
                }
                return ExitCodes.Ok;
            default:
                throw new CliException(ExitCodes.Usage, $"Usage: latticelog {what} add|remove|list [<folder>]");
        }
    }

    public static int Sources(Args a)
    {
        var c = Catalog(a);
        if (!a.Has("no-refresh"))
            foreach (var o in c.Refresh())
                if (o.Imported > 0 || o.Note is not null) Console.Error.WriteLine($"{o.Source}: indexed {o.Imported} new record(s){(o.Note is null ? "" : " — " + o.Note)}");
        var rows = c.Sources.Select(SourceCatalog.Describe).ToList();
        if (a.Format == "json")
            Console.WriteLine(new JsonObject
            {
                ["sources"] = new JsonArray(rows.Select(r => (JsonNode?)r).ToArray()),
                ["warnings"] = new JsonArray(c.Warnings.Distinct().Select(w => (JsonNode?)JsonValue.Create(w)).ToArray()),
            }.ToJsonString(Indented));
        else
        {
            foreach (var r in rows)
                Console.WriteLine($"{(string?)r["name"],-24} {(string?)r["kind"],-13} {(long?)r["records"] ?? 0,12:N0} records  {(string?)r["first_time"] ?? "-"} .. {(string?)r["last_time"] ?? "-"}  {(string?)r["path"]}");
            foreach (var w in c.Warnings.Distinct()) Console.Error.WriteLine("warning: " + w);
            Console.Error.WriteLine($"-- {rows.Count} source(s); roots: {(c.Config.Roots.Count == 0 ? "(none: latticelog roots add <folder>)" : string.Join("; ", c.Config.Roots))}{(c.Project is null ? "" : "; project: " + c.Project.FilePath)}");
        }
        return ExitCodes.Ok;
    }

    public static int Migrate(Args a)
    {
        var path = a.Positional.ElementAtOrDefault(0) ?? throw new CliException(ExitCodes.Usage, "Usage: latticelog migrate <path|glob> --name <source> [--follow] [--preview]");
        var c = Catalog(a);
        var m = new LogMigrator(c);
        try
        {
            if (a.Has("preview"))
            {
                Console.WriteLine(m.Preview(path).ToJsonString(Indented));
                return ExitCodes.Ok;
            }
            var name = a.Get("name") ?? throw new CliException(ExitCodes.Usage, "migrate requires --name <source> (or --preview).");
            using var cts = new CancellationTokenSource();
            ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cts.Cancel(); Console.Error.WriteLine("\ncancelling after the current batch..."); };
            Console.CancelKeyPress += onCancel;
            var sw = Stopwatch.StartNew();
            var quiet = a.Format == "json" || Console.IsErrorRedirected;
            try
            {
                var report = m.Migrate(path, name, a.Has("follow"), quiet ? null : p =>
                    Console.Error.Write($"\r{(p.BytesTotal == 0 ? 100 : 100.0 * p.BytesDone / p.BytesTotal),5:0.0}%  {p.RecordsImported:N0} records  {sw.Elapsed.TotalSeconds:0}s   "), cts.Token);
                if (!quiet) Console.Error.WriteLine();
                var j = report.Json;
                if (a.Format == "json") Console.WriteLine(j.ToJsonString(Indented));
                else
                {
                    Console.WriteLine($"{(report.Created ? "Created" : "Updated")} source '{j["source"]}': {(long)j["records_imported"]!:N0} record(s) imported, {(long)j["records_total"]!:N0} in total.");
                    Console.WriteLine($"Unparsed lines kept as text: {(long)j["unparsed_lines_kept_as_text"]!:N0} (total {(long)j["unparsed_lines_total"]!:N0}); mapping: {j["loss_report"]}.");
                    Console.WriteLine($"Size: {(long)j["original_bytes"]!:N0} bytes of originals -> {(long)j["final_store_bytes"]!:N0} bytes after compaction; {(double)j["elapsed_ms"]! / 1000:0.0} s.");
                    foreach (var w in j["warnings"]!.AsArray()) Console.WriteLine("warning: " + w);
                    Console.WriteLine("Originals were only read (not modified, moved or deleted).");
                }
                return ExitCodes.Ok;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Migration cancelled: a new source was not registered; an existing one keeps a consistent checkpoint (re-run to continue).");
                return ExitCodes.PartialResults;
            }
            finally { Console.CancelKeyPress -= onCancel; }
        }
        catch (UnauthorizedAccessException ex) { throw new CliException(ExitCodes.Usage, ex.Message); }
    }

    public static int Mcp(Args a)
    {
        if (a.Positional.ElementAtOrDefault(0) == "install") return McpInstall(a);
        var catalog = a.Positional.Count > 0 ? SourceCatalog.ForStore(a.Positional[0]) : Catalog(a);
        return LatticeLog.Mcp.McpServer.Run(catalog, Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error);
    }

    // ------------------------------------------------------------------ mcp install

    public sealed record ClientTarget(string File, string Key, bool VsCodeStyle);

    public static ClientTarget ResolveClient(string client, string scope, string projectDir)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string UserAppDir(string app) => OperatingSystem.IsWindows() ? Path.Combine(appData, app)
            : OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Application Support", app)
            : Path.Combine(home, ".config", app);
        return (client, scope) switch
        {
            ("claude-desktop", _) => new(Path.Combine(UserAppDir("Claude"), "claude_desktop_config.json"), "mcpServers", false),
            ("claude-code", "user") => new(Path.Combine(home, ".claude.json"), "mcpServers", false),
            ("claude-code", _) => new(Path.Combine(projectDir, ".mcp.json"), "mcpServers", false),
            ("cursor", "project") => new(Path.Combine(projectDir, ".cursor", "mcp.json"), "mcpServers", false),
            ("cursor", _) => new(Path.Combine(home, ".cursor", "mcp.json"), "mcpServers", false),
            ("vscode", "user") => new(Path.Combine(UserAppDir("Code"), "User", "mcp.json"), "servers", true),
            ("vscode", _) => new(Path.Combine(projectDir, ".vscode", "mcp.json"), "servers", true),
            _ => throw new CliException(ExitCodes.Usage, "--client must be claude-desktop, claude-code, cursor or vscode."),
        };
    }

    /// <summary>The command line that starts this latticelog build as an MCP server.</summary>
    public static (string Command, List<string> Args) SelfCommand()
    {
        var exe = Environment.ProcessPath ?? "latticelog";
        var name = Path.GetFileNameWithoutExtension(exe);
        if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var dll = Path.Combine(AppContext.BaseDirectory, "latticelog.dll");
            return (exe, [dll, "mcp"]);
        }
        if (name.Equals("latticelog", StringComparison.OrdinalIgnoreCase)) return (exe, ["mcp"]);
        return ("latticelog", ["mcp"]); // hosted elsewhere (tests): rely on PATH
    }

    private static int McpInstall(Args a)
    {
        var client = a.Get("client") ?? throw new CliException(ExitCodes.Usage, "Usage: latticelog mcp install --client claude-desktop|claude-code|cursor|vscode [--scope user|project] [--yes] [--dry-run]");
        var projectDir = Path.GetFullPath(a.Get("project") ?? Environment.CurrentDirectory);
        var scope = a.Get("scope") ?? (client is "claude-code" or "vscode" ? "project" : "user");
        var target = a.Get("config-file") is { } cf ? ResolveClient(client, scope, projectDir) with { File = Path.GetFullPath(cf) } : ResolveClient(client, scope, projectDir);
        var serverName = a.Get("name") ?? "latticelog";
        var (command, args) = a.Get("command") is { } cmd ? (cmd, new List<string> { "mcp" }) : SelfCommand();
        // Clients that do not start the server in the project folder get it explicitly, so latticelog.toml is found.
        if (scope == "user" || client == "claude-desktop")
            if (ProjectConfig.Find(projectDir) is { } toml) args.AddRange(["--project", Path.GetDirectoryName(toml)!]);
        var entry = new JsonObject();
        if (target.VsCodeStyle) entry["type"] = "stdio";
        entry["command"] = command;
        entry["args"] = new JsonArray(args.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        var env = new JsonObject();
        foreach (var v in new[] { "DOTNET_ROOT", "LATTICELOG_CONFIG_DIR", "LATTICELOG_DATA_DIR" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } val) env[v] = val;
        if (env.Count > 0) entry["env"] = env;

        JsonObject root;
        var hadComments = false;
        var existed = File.Exists(target.File);
        if (existed)
        {
            var text = File.ReadAllText(target.File);
            try { root = JsonNode.Parse(text) as JsonObject ?? throw new CliException(ExitCodes.DataError, $"{target.File} is not a JSON object."); }
            catch (JsonException)
            {
                try
                {
                    root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                           ?? throw new CliException(ExitCodes.DataError, $"{target.File} is not a JSON object.");
                    hadComments = true;
                }
                catch (JsonException ex) { throw new CliException(ExitCodes.DataError, $"{target.File} is not valid JSON ({ex.Message}); fix it or pass --config-file."); }
            }
        }
        else root = new JsonObject();
        var servers = root[target.Key] as JsonObject;
        if (root[target.Key] is not null && servers is null) throw new CliException(ExitCodes.DataError, $"{target.File}: '{target.Key}' is not an object.");
        if (servers is null) root[target.Key] = servers = new JsonObject();
        var current = servers[serverName];
        if (current is not null && JsonNode.DeepEquals(current, entry))
        {
            Console.WriteLine($"'{serverName}' is already configured in {target.File}; nothing to do.");
            return ExitCodes.Ok;
        }
        var needsConsent = current is not null || hadComments;
        if (a.Has("dry-run"))
        {
            servers[serverName] = entry;
            Console.WriteLine($"Would write {target.File}{(needsConsent ? " (needs confirmation: " + (current is not null ? "replaces an existing entry" : "comments would be lost") + ")" : "")}:");
            Console.WriteLine(root.ToJsonString(Indented));
            return ExitCodes.Ok;
        }
        if (needsConsent && !a.Has("yes"))
        {
            var why = current is not null
                ? $"{target.File} already has an MCP server named '{serverName}':\n{current.ToJsonString(Indented)}\nReplace it with:\n{entry.ToJsonString(Indented)}"
                : $"{target.File} contains comments, which this JSON rewrite cannot keep (a backup is made).";
            if (Console.IsInputRedirected)
                throw new CliException(ExitCodes.Usage, why + "\nNot changed. Re-run with --yes to overwrite, or --name <other> to add a second entry.");
            Console.Write(why + "\nOverwrite? [y/N] ");
            var answer = Console.ReadLine()?.Trim();
            if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Not changed.");
                return ExitCodes.Ok;
            }
        }
        servers[serverName] = entry;
        Directory.CreateDirectory(Path.GetDirectoryName(target.File)!);
        string? backup = null;
        if (existed)
        {
            var stamp = $"{target.File}.latticelog-backup-{DateTime.UtcNow:yyyyMMddHHmmss}";
            backup = stamp;
            for (var i = 2; File.Exists(backup); i++) backup = $"{stamp}-{i}";
            File.Copy(target.File, backup, overwrite: false);
        }
        var tmp = target.File + ".latticelog-tmp";
        File.WriteAllText(tmp, root.ToJsonString(Indented) + "\n", new UTF8Encoding(false));
        File.Move(tmp, target.File, overwrite: true);
        Console.WriteLine($"{(current is null ? "Added" : "Replaced")} MCP server '{serverName}' in {target.File}{(backup is null ? "" : $" (previous file saved as {backup})")}.");
        Console.WriteLine($"Command: {command} {string.Join(' ', args)}. Restart {client} to load it.");
        return ExitCodes.Ok;
    }
}
