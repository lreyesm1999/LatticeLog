// latticelog-collector — receives batches from forwarders into a local LatticeLog store (P16, ADR-0009).
//
//   latticelog-collector --store <dir> --tokens <tokens.json> [--listen https://0.0.0.0:7400 --cert server.pfx]
//   latticelog-collector hash-token <secret>      prints the SHA-256 to put in tokens.json
//
// The certificate password is read from LATTICELOG_CERT_PASSWORD (never from the command line).
using LatticeLog.Server;

string? Opt(string name) { var i = Array.IndexOf(args, "--" + name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

if (args.Length >= 2 && args[0] == "hash-token")
{
    Console.WriteLine(TokenStore.HashHex(args[1]));
    return 0;
}
var store = Opt("store");
var tokens = Opt("tokens");
if (store is null || tokens is null)
{
    Console.Error.WriteLine("usage: latticelog-collector --store <dir> --tokens <tokens.json> [--listen <url>] [--cert <pfx>] [--allow-insecure-http]");
    Console.Error.WriteLine("       latticelog-collector hash-token <secret>");
    return 2;
}
var options = new CollectorOptions
{
    Ingest = new IngestServiceOptions { StoreDirectory = store, Diagnostics = m => Console.Error.WriteLine(m) },
    Tokens = TokenStore.FromFile(tokens),
    Url = Opt("listen") ?? "http://127.0.0.1:7400",
    CertificatePath = Opt("cert"),
    CertificatePassword = Environment.GetEnvironmentVariable("LATTICELOG_CERT_PASSWORD"),
    AllowInsecureHttp = args.Contains("--allow-insecure-http"),
};
await using var collector = await Collector.StartAsync(options);
Console.Error.WriteLine($"latticelog-collector listening on {collector.BaseAddress} (store {collector.Service.StoreId}); Ctrl+C to stop");
var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
await stop.Task;
return 0;
