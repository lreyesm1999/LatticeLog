using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LatticeLog.Cli;

namespace LatticeLog.Tests;

/// <summary><c>latticelog update</c> against a fake GitHub: nothing here touches the network or the real install.</summary>
public class UpdateTests : IDisposable
{
    private sealed class FakeGitHub : HttpMessageHandler
    {
        public readonly Dictionary<string, (HttpStatusCode Status, byte[] Body)> Routes = new(StringComparer.Ordinal);
        public readonly List<string> Requested = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            lock (Requested) Requested.Add(url);
            Assert.False(string.IsNullOrEmpty(request.Headers.UserAgent.ToString())); // GitHub requires one
            var (status, body) = Routes.TryGetValue(url, out var r) ? r : (HttpStatusCode.NotFound, Encoding.UTF8.GetBytes("{}"));
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
        }
    }

    private readonly FakeGitHub _gh = new();

    public UpdateTests() => UpdateCommands.HandlerFactory = () => _gh;

    public void Dispose() => UpdateCommands.HandlerFactory = null;

    private static JsonObject Release(string tag, bool pre = false, params string[] assets) => new()
    {
        ["tag_name"] = tag,
        ["prerelease"] = pre,
        ["draft"] = false,
        ["html_url"] = $"https://github.com/o/r/releases/tag/{tag}",
        ["assets"] = new JsonArray(assets.Select(a => (JsonNode?)new JsonObject { ["name"] = a, ["browser_download_url"] = $"https://dl.test/{tag}/{a}" }).ToArray()),
    };

    private void ServeReleases(params JsonObject[] releases) =>
        _gh.Routes["https://api.github.com/repos/o/r/releases?per_page=30"] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes(new JsonArray(releases).ToJsonString()));

    [Theory]
    [InlineData("0.1.0-alpha.1", "0.1.0-alpha.2", -1)]
    [InlineData("0.1.0-alpha.10", "0.1.0-alpha.2", 1)]   // numeric identifiers compare as numbers
    [InlineData("0.1.0-alpha", "0.1.0-alpha.1", -1)]     // fewer identifiers rank lower
    [InlineData("0.1.0-beta", "0.1.0-alpha.9", 1)]
    [InlineData("0.1.0", "0.1.0-rc.1", 1)]               // a release ranks above its pre-releases
    [InlineData("0.2.0", "0.10.0", -1)]
    [InlineData("v1.2.3+build.7", "1.2.3", 0)]           // build metadata and a leading v are ignored
    public void Versions_follow_semver_precedence(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(SemVer.Compare(a, b)));

    [Fact]
    public void Latest_prefers_the_newest_stable_release()
    {
        ServeReleases(Release("v0.3.0-beta.1", pre: true), Release("v0.2.0"), Release("v0.1.0"), Release("not-a-version"));
        using var http = new HttpClient(_gh, disposeHandler: false);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("test/1");
        Assert.Equal("0.2.0", UpdateCommands.Latest(http, "o/r", includePrerelease: false)!.Version);
        Assert.Equal("0.3.0-beta.1", UpdateCommands.Latest(http, "o/r", includePrerelease: true)!.Version);

        ServeReleases(Release("v0.1.0-alpha.3", pre: true), Release("v0.1.0-alpha.2", pre: true));
        Assert.Equal("0.1.0-alpha.3", UpdateCommands.Latest(http, "o/r", includePrerelease: false)!.Version); // only pre-releases exist
    }

    [Fact]
    public void Checksums_are_read_as_sha256sum_writes_them()
    {
        var a = new string('a', 64);
        var b = new string('B', 64);
        var sums = $"{a}  latticelog-linux-x64\n{b} *latticelog-win-x64.exe\r\n";
        Assert.Equal(a, UpdateCommands.ExpectedHash(sums, "latticelog-linux-x64"));
        Assert.Equal(b.ToLowerInvariant(), UpdateCommands.ExpectedHash(sums, "latticelog-win-x64.exe"));
        Assert.Null(UpdateCommands.ExpectedHash(sums, "latticelog-osx-arm64"));
    }

    [Fact]
    public void Check_reports_without_downloading()
    {
        var asset = UpdateCommands.AssetName() ?? "latticelog-win-x64.exe";
        ServeReleases(Release("v99.0.0", false, asset, "SHA256SUMS"));
        if (UpdateCommands.AssetName() is null)
        {
            Assert.Equal(ExitCodes.Usage, Assert.Throws<CliException>(() => UpdateCommands.Update(new Args(["--check", "--repo", "o/r"]))).Code);
            return;
        }
        Assert.Equal(ExitCodes.Ok, UpdateCommands.Update(new Args(["--check", "--repo", "o/r"])));
        Assert.DoesNotContain(_gh.Requested, u => u.StartsWith("https://dl.test/", StringComparison.Ordinal));
    }

    [Fact]
    public void A_checksum_mismatch_installs_nothing()
    {
        if (UpdateCommands.AssetName() is not { } asset) return; // no release build for this platform
        ServeReleases(Release("v99.0.0", false, asset, "SHA256SUMS"));
        var payload = Encoding.UTF8.GetBytes("not the real executable");
        _gh.Routes[$"https://dl.test/v99.0.0/{asset}"] = (HttpStatusCode.OK, payload);
        _gh.Routes["https://dl.test/v99.0.0/SHA256SUMS"] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes($"{new string('0', 64)}  {asset}\n"));
        var ex = Assert.Throws<CliException>(() => UpdateCommands.Update(new Args(["--repo", "o/r", "--yes"])));
        Assert.Equal(ExitCodes.DataError, ex.Code);
        Assert.Contains("Checksum mismatch", ex.Message);
        Assert.Contains(Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(), ex.Message);
    }

    [Fact]
    public void Up_to_date_does_nothing_and_a_bad_repo_is_refused()
    {
        ServeReleases(Release("v" + Program.Version, false, UpdateCommands.AssetName() ?? "x", "SHA256SUMS"));
        if (UpdateCommands.AssetName() is not null)
        {
            Assert.Equal(ExitCodes.Ok, UpdateCommands.Update(new Args(["--repo", "o/r", "--yes"])));
            Assert.DoesNotContain(_gh.Requested, u => u.StartsWith("https://dl.test/", StringComparison.Ordinal));
        }
        foreach (var bad in new[] { "../evil", "o/..", "o/r/x", "o", ".o/r" })
            Assert.Equal(ExitCodes.Usage, Assert.Throws<CliException>(() => UpdateCommands.Update(new Args(["--repo", bad]))).Code);
    }
}
