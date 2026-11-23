using System.Diagnostics;

namespace LatticeLog.Tests;

/// <summary>
/// Builds the TypeScript SDK (<c>sdks/typescript</c>) once per test run and returns node plus its smoke program
/// (<c>examples/smoke.mjs</c>). Null when node is not installed (the tests are then skipped with a note) or when the
/// build fails (the reason is kept). Installs the dev dependencies with <c>npm ci</c> first when TypeScript is missing.
/// </summary>
internal static class TypeScriptSdk
{
    private static readonly Lazy<(string? Node, string? Smoke, string Note)> Built = new(Build);

    public static (string? Node, string? Smoke, string Note) Smoke => Built.Value;

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "LatticeLog.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static string? Find(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var name in OperatingSystem.IsWindows() ? new[] { exe + ".exe", exe + ".cmd" } : [exe])
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p)) return p;
            }
        if (OperatingSystem.IsWindows())
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", exe + (exe == "node" ? ".exe" : ".cmd"));
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static (int Exit, string Output) Run(string exe, string dir, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(10 * 60_000)) { p.Kill(true); return (-1, "timed out"); }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    private static (string? Node, string? Smoke, string Note) Build()
    {
        var node = Find("node");
        if (node is null) return (null, null, "node not found: skipped");
        var sdk = Path.Combine(RepoRoot(), "sdks", "typescript");
        var tsc = Path.Combine(sdk, "node_modules", "typescript", "bin", "tsc");
        if (!File.Exists(tsc))
        {
            var npm = Find("npm") ?? Path.Combine(Path.GetDirectoryName(node)!, OperatingSystem.IsWindows() ? "npm.cmd" : "npm");
            if (!File.Exists(npm)) return (null, null, "npm not found next to node: cannot install the TypeScript compiler");
            var (exit, text) = Run(npm, sdk, "ci", "--no-audit", "--no-fund");
            if (exit != 0) return (null, null, "npm ci failed:\n" + text);
        }
        var build = Run(node, sdk, tsc, "-p", "tsconfig.json");
        if (build.Exit != 0) return (null, null, "tsc failed:\n" + build.Output);
        return (node, Path.Combine(sdk, "examples", "smoke.mjs"), "built");
    }
}
