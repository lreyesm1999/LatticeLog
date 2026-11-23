using System.Diagnostics;
using System.Text.Json;

namespace LatticeLog.Tests;

/// <summary>
/// Builds the Rust SDK's smoke program (<c>sdks/rust/examples/smoke.rs</c>) once per test run with cargo. Null when
/// cargo is not installed (the Rust tests are then skipped with a note) or when the build fails (the reason is kept).
/// </summary>
internal static class RustSdk
{
    private static readonly Lazy<(string? Exe, string Note)> Smoke = new(Build);

    public static (string? Exe, string Note) SmokeExe => Smoke.Value;

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "LatticeLog.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static string? FindCargo()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var p = Path.Combine(dir, OperatingSystem.IsWindows() ? "cargo.exe" : "cargo");
            if (File.Exists(p)) return p;
        }
        var home = Environment.GetEnvironmentVariable("CARGO_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cargo");
        var fallback = Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "cargo.exe" : "cargo");
        return File.Exists(fallback) ? fallback : null;
    }

    private static (string? Exe, string Note) Build()
    {
        var cargo = FindCargo();
        if (cargo is null)
        {
            // Locally a missing toolchain skips the Rust tests; in CI it fails them, so a runner without cargo cannot
            // pass the cross-language tests without running them.
            return (null, string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))
                ? "cargo not found: skipped"
                : "CI runner without cargo: the Rust SDK tests must run in CI (install a Rust toolchain in the workflow)");
        }
        var manifest = Path.Combine(RepoRoot(), "sdks", "rust", "Cargo.toml");
        // Plain HTTP only (no TLS): no C compiler is needed for the build.
        var psi = new ProcessStartInfo(cargo)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "build", "--example", "smoke", "--no-default-features", "--features", "server", "--manifest-path", manifest, "--message-format=json" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(15 * 60_000)) { p.Kill(true); return (null, "cargo build timed out"); }
        if (p.ExitCode != 0) return (null, "cargo build failed:\n" + stderr.Result);
        foreach (var line in stdout.Result.Split('\n'))
        {
            if (!line.Contains("\"compiler-artifact\"", StringComparison.Ordinal)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.GetProperty("target").GetProperty("name").GetString() == "smoke"
                && root.TryGetProperty("executable", out var exe) && exe.ValueKind == JsonValueKind.String)
                return (exe.GetString(), "built");
        }
        return (null, "cargo build produced no smoke executable");
    }
}
