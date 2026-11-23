using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace LatticeLog.Cli;

/// <summary>
/// <c>latticelog update</c> (<c>llog update</c>): installs the newest GitHub release of LatticeLog over this one.
/// It reads the release list, downloads the executable for this platform plus <c>SHA256SUMS</c>, checks the hash,
/// and runs the downloaded executable's own <c>setup</c>, which stops the service, swaps the executable (moving a running
/// one aside), registers and restarts the service. Data, config and MCP client entries are kept.
/// Releases are made by <c>.github/workflows/release.yml</c> when a <c>v*</c> tag is pushed.
/// </summary>
public static class UpdateCommands
{
    public const string DefaultRepository = "lreyesm1999/LatticeLog";

    /// <summary>Tests replace the network (no request leaves the machine in tests).</summary>
    public static Func<HttpMessageHandler>? HandlerFactory { get; set; }

    public sealed record Release(string Tag, string Version, string PageUrl, bool Prerelease, IReadOnlyDictionary<string, string> Assets);

    public static int Update(Args a)
    {
        var repo = a.Get("repo") ?? Environment.GetEnvironmentVariable("LATTICELOG_UPDATE_REPO") ?? DefaultRepository;
        // owner/name as GitHub allows them; neither part may start with a dot (no '..' path tricks in the API URL).
        if (!System.Text.RegularExpressions.Regex.IsMatch(repo, "^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9_][A-Za-z0-9_.-]*$"))
            throw new CliException(ExitCodes.Usage, $"'{repo}' is not an owner/name repository.");
        var asset = AssetName() ?? throw new CliException(ExitCodes.Usage, $"There is no release build for {RuntimeInformation.RuntimeIdentifier}; build from source and run 'latticelog setup'.");
        using var http = CreateClient();

        var wanted = a.Get("version");
        var release = wanted is null
            ? Latest(http, repo, a.Has("prerelease")) ?? throw new CliException(ExitCodes.NotFound, $"No release found in {repo}.")
            : ByTag(http, repo, wanted.StartsWith('v') ? wanted : "v" + wanted);
        var current = Program.Version;
        var cmp = SemVer.Compare(release.Version, current);
        Term.Header("update");
        Term.Field("Installed", current);
        Term.Field(wanted is null ? "Latest" : "Requested", Term.Bold(release.Version) + (release.Prerelease ? "  " + Term.Dim("pre-release") : ""));
        Term.Field("Release", Term.Dim(release.PageUrl));
        Console.WriteLine();

        if (a.Has("check"))
        {
            if (cmp > 0) Term.Step("An update is available", "run 'llog update'");
            else Term.Ok("Up to date");
            return ExitCodes.Ok;
        }
        if (wanted is null && cmp <= 0 && !a.Has("force"))
        {
            Term.Ok("Up to date", "nothing to install");
            return ExitCodes.Ok;
        }
        if (!release.Assets.TryGetValue(asset, out var assetUrl) || !release.Assets.TryGetValue("SHA256SUMS", out var sumsUrl))
            throw new CliException(ExitCodes.NotFound, $"Release {release.Tag} has no {asset} or SHA256SUMS.");
        if (!a.Has("yes") && !Console.IsInputRedirected)
        {
            if (!Term.ConfirmYes($"Install {Term.Bold(release.Version)} now? The service restarts; data and settings are kept."))
            {
                Term.Ok("Nothing changed");
                return ExitCodes.Ok;
            }
            Console.WriteLine();
        }

        var work = Path.Combine(Path.GetTempPath(), "latticelog-update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            var exe = Path.Combine(work, OperatingSystem.IsWindows() ? "latticelog.exe" : "latticelog");
            if (!Term.Color) Console.WriteLine($"Downloading {asset}...");
            var size = Download(http, assetUrl, exe, asset);
            Term.Ok($"Downloaded {asset}", Term.Size(size));
            var sums = http.GetStringAsync(sumsUrl).GetAwaiter().GetResult();
            var expected = ExpectedHash(sums, asset) ?? throw new CliException(ExitCodes.DataError, $"SHA256SUMS of {release.Tag} has no line for {asset}.");
            var actual = Sha256(exe);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new CliException(ExitCodes.DataError, $"Checksum mismatch for {asset}: expected {expected}, got {actual}. Nothing was installed.");
            Term.Ok("Checksum verified", $"SHA-256 {actual[..16]}{Term.Ellipsis}");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(exe, (UnixFileMode)0b111_101_101);

            // The new version installs itself: same install folder as this one, no change to the MCP client entries.
            // --updating: setup leaves out its own summary and next steps (this command prints its own); older
            // versions, reached with --version, ignore it.
            var args = new List<string> { "setup", "--yes", "--updating", "--clients", "none" };
            if (a.Get("install-dir") is { } dir) args.AddRange(["--install-dir", Path.GetFullPath(dir)]);
            else if (InstalledDir() is { } here) args.AddRange(["--install-dir", here]);
            if (a.Get("config-dir") is { } cd) args.AddRange(["--config-dir", Path.GetFullPath(cd)]);
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var x in args) psi.ArgumentList.Add(x);
            using var p = Process.Start(psi) ?? throw new CliException(ExitCodes.Internal, "Could not start the downloaded executable.");
            p.WaitForExit();
            if (p.ExitCode == 0)
            {
                Console.WriteLine();
                Term.Ok(Term.Bold($"Updated to LatticeLog {release.Version}"), $"{current} {Term.Arrow} {release.Version}");
                Term.Note("Restart your AI tools to load the new MCP server.");
                Console.WriteLine();
            }
            return p.ExitCode;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"(left {work}: {ex.Message})"); }
        }
    }

    /// <summary>This executable's folder when it is a <c>setup</c> install (it has the <c>llog</c> alias next to it).</summary>
    private static string? InstalledDir()
    {
        if (Environment.ProcessPath is not { } p) return null;
        var dir = Path.GetDirectoryName(Path.GetFullPath(p))!;
        return File.Exists(Path.Combine(dir, "llog")) || File.Exists(Path.Combine(dir, "llog.cmd")) ? dir : null;
    }

    /// <summary>The release asset for this OS and architecture, as release.yml names it; null when none is built.</summary>
    public static string? AssetName() => (OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), RuntimeInformation.OSArchitecture) switch
    {
        (true, _, Architecture.X64) => "latticelog-win-x64.exe",
        (_, true, Architecture.X64) => "latticelog-linux-x64",
        _ => null,
    };

    private static HttpClient CreateClient()
    {
        var http = HandlerFactory is { } f ? new HttpClient(f(), disposeHandler: true) : new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(5);
        // GitHub requires a User-Agent; a generic one, nothing about this user or machine.
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("latticelog-updater", "1"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>The newest release by version (pre-releases only with <paramref name="includePrerelease"/> or when nothing else exists).</summary>
    public static Release? Latest(HttpClient http, string repo, bool includePrerelease)
    {
        var json = Get(http, $"https://api.github.com/repos/{repo}/releases?per_page=30") as JsonArray ?? [];
        var all = json.OfType<JsonObject>().Where(r => (bool?)r["draft"] != true).Select(Parse).Where(r => SemVer.IsValid(r.Version)).ToList();
        var stable = all.Where(r => !r.Prerelease).ToList();
        var pool = includePrerelease || stable.Count == 0 ? all : stable;
        return pool.OrderByDescending(r => r.Version, Comparer<string>.Create(SemVer.Compare)).FirstOrDefault();
    }

    public static Release ByTag(HttpClient http, string repo, string tag) =>
        Parse(Get(http, $"https://api.github.com/repos/{repo}/releases/tags/{Uri.EscapeDataString(tag)}") as JsonObject
              ?? throw new CliException(ExitCodes.NotFound, $"No release {tag} in {repo}."));

    private static JsonNode? Get(HttpClient http, string url)
    {
        using var resp = http.GetAsync(url).GetAwaiter().GetResult();
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) throw new CliException(ExitCodes.NotFound, $"Not found: {url}");
        if (!resp.IsSuccessStatusCode) throw new CliException(ExitCodes.Internal, $"GitHub answered {(int)resp.StatusCode} for {url}.");
        return JsonNode.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
    }

    private static Release Parse(JsonObject r)
    {
        var tag = (string?)r["tag_name"] ?? "";
        var assets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var x in r["assets"] as JsonArray ?? [])
            if ((string?)x?["name"] is { } n && (string?)x?["browser_download_url"] is { } u) assets[n] = u;
        return new Release(tag, tag.TrimStart('v'), (string?)r["html_url"] ?? "", (bool?)r["prerelease"] == true, assets);
    }

    /// <summary>Downloads <paramref name="url"/> to <paramref name="path"/> with a progress bar on a terminal; returns the size.</summary>
    private static long Download(HttpClient http, string url, string path, string label)
    {
        using var resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode) throw new CliException(ExitCodes.Internal, $"Download failed ({(int)resp.StatusCode}): {url}");
        using var src = resp.Content.ReadAsStream();
        using var dst = File.Create(path);
        using var progress = new Term.Progress($"Downloading {label}", resp.Content.Headers.ContentLength);
        var buffer = new byte[81920];
        long done = 0;
        int n;
        while ((n = src.Read(buffer)) > 0)
        {
            dst.Write(buffer, 0, n);
            done += n;
            progress.Report(done);
        }
        progress.Report(done, force: true);
        return done;
    }

    /// <summary>The hash <c>sha256sum</c> recorded for <paramref name="file"/> in a SHA256SUMS text.</summary>
    public static string? ExpectedHash(string sums, string file)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.Trim();
            var space = line.IndexOf(' ');
            if (space != 64) continue;
            var name = line[space..].TrimStart(' ', '*');
            if (name == file) return line[..64].ToLowerInvariant();
        }
        return null;
    }

    public static string Sha256(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }
}

/// <summary>Semantic Versioning 2.0 precedence (build metadata ignored).</summary>
public static class SemVer
{
    public static bool IsValid(string v) => TryParse(v, out _, out _);

    public static int Compare(string? a, string? b)
    {
        var okA = TryParse(a ?? "", out var coreA, out var preA);
        var okB = TryParse(b ?? "", out var coreB, out var preB);
        if (!okA || !okB) return okA.CompareTo(okB);
        for (var i = 0; i < 3; i++)
            if (coreA[i] != coreB[i]) return coreA[i].CompareTo(coreB[i]);
        // A release ranks above its pre-releases: 1.2.0 > 1.2.0-beta.1.
        if (preA.Length == 0 && preB.Length == 0) return 0;
        if (preA.Length == 0) return 1;
        if (preB.Length == 0) return -1;
        for (var i = 0; i < Math.Min(preA.Length, preB.Length); i++)
        {
            var numA = long.TryParse(preA[i], NumberStyles.None, CultureInfo.InvariantCulture, out var na);
            var numB = long.TryParse(preB[i], NumberStyles.None, CultureInfo.InvariantCulture, out var nb);
            var c = (numA, numB) switch
            {
                (true, true) => na.CompareTo(nb),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(preA[i], preB[i]),
            };
            if (c != 0) return Math.Sign(c);
        }
        return preA.Length.CompareTo(preB.Length);
    }

    private static bool TryParse(string v, out long[] core, out string[] pre)
    {
        core = [0, 0, 0];
        pre = [];
        v = v.Trim().TrimStart('v').Split('+')[0];
        var dash = v.IndexOf('-');
        var main = dash < 0 ? v : v[..dash];
        if (dash >= 0) pre = v[(dash + 1)..].Split('.');
        var parts = main.Split('.');
        if (parts.Length != 3) return false;
        for (var i = 0; i < 3; i++)
            if (!long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out core[i])) return false;
        return pre.All(p => p.Length > 0);
    }
}
