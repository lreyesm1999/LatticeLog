// latticelog-server — ingestion (forwarders) and read API per namespace (P16, P17).
//
//   latticelog-server --data <dir> --tokens <tokens.json> [--listen https://0.0.0.0:7400 --cert server.pfx]
//   latticelog-server hash-token <secret>       prints the SHA-256 to put in tokens.json
//
// Every option can also come from the environment (containers): LATTICELOG_DATA, LATTICELOG_TOKENS,
// LATTICELOG_LISTEN, LATTICELOG_CERT. The certificate password is read only from LATTICELOG_CERT_PASSWORD.
using LatticeLog.Server;

string? Opt(string name, string env) { var i = Array.IndexOf(args, "--" + name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : Environment.GetEnvironmentVariable(env); }

if (args.Length >= 2 && args[0] == "hash-token")
{
    Console.WriteLine(TokenStore.HashHex(args[1]));
    return 0;
}
var data = Opt("data", "LATTICELOG_DATA");
var tokens = Opt("tokens", "LATTICELOG_TOKENS");
if (data is null || tokens is null)
{
    Console.Error.WriteLine("usage: latticelog-server --data <dir> --tokens <tokens.json> [--listen <url>] [--cert <pfx>] [--allow-insecure-http]");
    Console.Error.WriteLine("       latticelog-server hash-token <secret>");
    return 2;
}
var options = new ServerOptions
{
    DataDirectory = data,
    Tokens = TokenStore.FromFile(tokens),
    IngestTemplate = new IngestServiceOptions { StoreDirectory = "", Diagnostics = m => Console.Error.WriteLine(m) },
    Url = Opt("listen", "LATTICELOG_LISTEN") ?? "http://127.0.0.1:7400",
    CertificatePath = Opt("cert", "LATTICELOG_CERT"),
    CertificatePassword = Environment.GetEnvironmentVariable("LATTICELOG_CERT_PASSWORD"),
    AllowInsecureHttp = args.Contains("--allow-insecure-http") || Environment.GetEnvironmentVariable("LATTICELOG_ALLOW_INSECURE_HTTP") == "1",
};
await using var server = await LatticeServer.StartAsync(options);
Console.Error.WriteLine($"latticelog-server listening on {server.BaseAddress} (data {Path.GetFullPath(data)}); Ctrl+C to stop");
var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
await stop.Task;
return 0;
