using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using LatticeLog.Ingest;
using LatticeLog.Server;
using LatticeLog.Storage;

namespace LatticeLog.Tests;

/// <summary>
/// End-to-end TLS (P16-T10, P18-T04): the server listens with a real certificate, a forwarder ships over HTTPS and the
/// read API answers over HTTPS. A client that does not trust the certificate gets nowhere.
/// </summary>
public class TlsTests
{
    /// <summary>A self-signed certificate for 127.0.0.1/localhost, written as a password-protected PFX.</summary>
    private static (string Path, string Password, X509Certificate2 Certificate) SelfSigned()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=latticelog-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // server auth
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var password = Guid.NewGuid().ToString("N");
        var path = Path.Combine(TestData.TempDir(), "server.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, password));
        return (path, password, X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert)));
    }

    /// <summary>Trusts exactly this certificate (pinning by thumbprint), like a client given the server's CA file.</summary>
    private static SocketsHttpHandler Trusting(X509Certificate2 certificate) => new()
    {
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, presented, _, _) =>
                presented is not null && presented.GetCertHashString() == certificate.Thumbprint,
        },
    };

    [Fact]
    public async Task Ingestion_and_reads_work_over_https_and_an_untrusted_client_is_refused()
    {
        var (pfx, password, certificate) = SelfSigned();
        await using var server = await LatticeServer.StartAsync(new ServerOptions
        {
            DataDirectory = TestData.TempDir(),
            Tokens = TokenStore.FromTokens(TokenStore.Create("app", "app-secret", TokenScopes.Ingest | TokenScopes.Read, ["tls/test"])),
            IngestTemplate = new IngestServiceOptions { StoreDirectory = "", Store = new StoreWriterOptions { MinFreeDiskBytes = 0 } },
            Url = "https://127.0.0.1:0",
            CertificatePath = pfx,
            CertificatePassword = password,
        });
        Assert.Equal(Uri.UriSchemeHttps, server.BaseAddress.Scheme);

        // Ship 500 records with the forwarder, the way an application would.
        var local = TestData.TempDir();
        using (var w = StoreWriter.Open(local, new StoreWriterOptions { MinFreeDiskBytes = 0 }))
            w.WriteBatch(Enumerable.Range(0, 500).Select(i => new LogRecord
            {
                Severity = i % 50 == 0 ? Severity.Error : Severity.Info,
                EventName = "tls.tick",
                Attributes = [new("i", i)],
            }).ToList());
        await using (var fwd = Forwarder.Start(local, new ForwarderOptions
        {
            Endpoint = server.BaseAddress,
            Token = "app-secret",
            Namespace = "tls/test",
            Handler = Trusting(certificate),
            PollInterval = TimeSpan.FromMilliseconds(10),
        }))
            Assert.True(await fwd.WaitForAsync(500, TimeSpan.FromSeconds(30)), fwd.LastError);

        // Read them back over HTTPS.
        using (var http = new HttpClient(Trusting(certificate)))
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(server.BaseAddress, "/v1/search?ns=tls%2Ftest&limit=1000"));
            req.Headers.Authorization = new("Bearer", "app-secret");
            using var resp = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
            Assert.Equal(500, json["events"]!.AsArray().Count);
        }

        // A client with default validation does not trust a self-signed certificate: no request reaches the API.
        using (var untrusting = new HttpClient())
            await Assert.ThrowsAsync<HttpRequestException>(() => untrusting.GetAsync(new Uri(server.BaseAddress, "/v1/health")));

        // Plain HTTP to the TLS port is not answered as an API request either.
        var plain = new UriBuilder(server.BaseAddress) { Scheme = Uri.UriSchemeHttp }.Uri;
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
        {
            HttpStatusCode? status = null;
            try { status = (await http.GetAsync(new Uri(plain, "/v1/health"))).StatusCode; }
            catch (HttpRequestException) { }
            Assert.NotEqual(HttpStatusCode.OK, status);
        }
    }
}
