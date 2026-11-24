using LatticeLog.Sources;

namespace LatticeLog.Service;

/// <summary>
/// Windows app packages (MSIX). A packaged app such as Claude desktop runs the processes it starts (MCP servers, their
/// shells) inside its package: what they create under <c>%LOCALAPPDATA%</c> and <c>%APPDATA%</c> goes to a private
/// copy (<c>%LOCALAPPDATA%\Packages\&lt;family&gt;\LocalCache\{Local,Roaming}</c>) that only that app sees. A service
/// started from there wrote its endpoint file and config.json into that copy, and a <c>latticelog</c> started from a
/// terminal read the real, stale ones. The endpoint and the lock now live outside AppData
/// (<see cref="ServicePaths.RendezvousDir(string)"/>); this class detects the redirection and adopts what an earlier
/// service left in such a copy.
/// </summary>
public static class PackageIsolation
{
    /// <summary>
    /// The package folder (<c>%LOCALAPPDATA%\Packages\&lt;name&gt;</c>) whose private copy receives what this process
    /// creates under <paramref name="dataDir"/>, or null when its writes land where they are aimed. Checked by effect
    /// (<see cref="PackageContext.RedirectedCopy(string, DateTime)"/>): processes Claude desktop starts are redirected
    /// while <c>GetCurrentPackageFullName</c> still reports no package.
    /// </summary>
    public static string? RedirectedInto(string dataDir)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var probe = Path.Combine(ServicePaths.Dir(dataDir), $".redirect-probe-{Environment.ProcessId}");
        var before = DateTime.UtcNow.AddSeconds(-2);
        try
        {
            File.WriteAllText(probe, "");
            var copy = PackageContext.RedirectedCopy(probe, before);
            File.Delete(probe);
            if (copy is null) return null;
            var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Packages");
            var rel = Path.GetRelativePath(packages, copy);
            return rel.Split(Path.DirectorySeparatorChar)[0];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // cannot tell: behave as before
        }
    }

    /// <summary>The private AppData copies (<c>LocalCache\Local</c> and <c>LocalCache\Roaming</c>) of every installed package.</summary>
    internal static IEnumerable<(string Package, string Local, string Roaming)> PackageCaches(string packagesDir)
    {
        if (!Directory.Exists(packagesDir)) yield break;
        string[] dirs;
        try { dirs = Directory.GetDirectories(packagesDir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var d in dirs)
        {
            var cache = Path.Combine(d, "LocalCache");
            yield return (Path.GetFileName(d), Path.Combine(cache, "Local"), Path.Combine(cache, "Roaming"));
        }
    }

    /// <summary>
    /// Merges into <paramref name="configDir"/>'s config.json what packaged processes wrote into their private copies
    /// of it (roots, allowlist, retention), then renames each copy to <c>config.json.adopted-&lt;time&gt;</c> so those
    /// processes read the real file again; deletes the endpoint files they left (stale: this service owns the lock).
    /// Only for the per-user default folders, from a process whose writes are not redirected (<paramref name="redirected"/>
    /// false; a redirected one would read its own copy as the real file). Returns one line per change for the service
    /// log. Never throws.
    /// </summary>
    public static List<string> AdoptPrivateCopies(string configDir, string dataDir, bool redirected) =>
        !OperatingSystem.IsWindows() || redirected
            ? []
            : AdoptPrivateCopies(configDir, dataDir,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Packages"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify), "LatticeLog"),
                ServicePaths.WindowsDefaultDataDir,
                DateTime.UtcNow);

    internal static List<string> AdoptPrivateCopies(string configDir, string dataDir, string packagesDir, string defaultConfigDir, string defaultDataDir, DateTime nowUtc)
    {
        var lines = new List<string>();
        var adoptConfig = ServicePaths.SamePath(configDir, defaultConfigDir);
        var adoptEndpoint = ServicePaths.SamePath(dataDir, defaultDataDir);
        if (!adoptConfig && !adoptEndpoint) return lines;
        var configName = Path.GetFileName(Path.TrimEndingDirectorySeparator(defaultConfigDir));
        var dataName = Path.GetFileName(Path.TrimEndingDirectorySeparator(defaultDataDir));
        foreach (var (package, local, roaming) in PackageCaches(packagesDir))
        {
            try
            {
                if (adoptConfig)
                {
                    var copyDir = Path.Combine(roaming, configName);
                    var copy = Path.Combine(copyDir, UserConfig.FileName);
                    if (File.Exists(copy))
                    {
                        var copyConfig = UserConfig.Load(copyDir);
                        var added = Merge(copyConfig, configDir);
                        var aside = $"{copy}.adopted-{nowUtc:yyyyMMddHHmmss}";
                        File.Move(copy, aside, overwrite: true);
                        // Read again: had this process been redirected after all, the real file it now sees lacks
                        // what the copy held, and writing it back keeps nothing from being lost.
                        added.AddRange(Merge(copyConfig, configDir));
                        lines.Add(added.Count > 0
                            ? $"adopted the config.json that {package} kept privately: added {string.Join(", ", added)}; its copy is now {aside}"
                            : $"set aside the config.json that {package} kept privately (nothing new in it): {aside}");
                    }
                }
                if (adoptEndpoint)
                {
                    var stale = Path.Combine(local, dataName, "service", "service.json");
                    if (File.Exists(stale))
                    {
                        File.Delete(stale);
                        lines.Add($"deleted the stale endpoint file {package} kept privately: {stale}");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                lines.Add($"could not adopt what {package} kept privately ({ex.Message})");
            }
        }
        return lines;
    }

    /// <summary>Adds the copy's roots, allowlist entries and retention settings the real config lacks; returns what was added.</summary>
    private static List<string> Merge(UserConfig copy, string configDir)
    {
        var real = UserConfig.Load(configDir);
        var added = new List<string>();
        static bool Has(List<string> list, string path) => list.Exists(p => string.Equals(
            Path.TrimEndingDirectorySeparator(p), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase));
        foreach (var r in copy.Roots.Where(r => !Has(real.Roots, r))) { real.Roots.Add(r); added.Add($"root {r}"); }
        foreach (var a in copy.Allow.Where(a => !Has(real.Allow, a))) { real.Allow.Add(a); added.Add($"allowed folder {a}"); }
        foreach (var (name, setting) in copy.Retention.Where(kv => !real.Retention.ContainsKey(kv.Key)))
        {
            real.Retention[name] = setting;
            added.Add($"retention for {name}");
        }
        if (added.Count > 0) real.Save();
        return added;
    }
}
