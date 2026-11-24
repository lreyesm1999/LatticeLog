using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using LatticeLog.Sources;

namespace LatticeLog.Service;

public sealed class ServiceRefusedException(string error, string message) : IOException(message)
{
    public string Error { get; } = error;
}

/// <summary>Talks to a running service (<see cref="Wire"/>): status, stop, ping, and MCP sessions for the shim.</summary>
public static class ServiceClient
{
    /// <summary>
    /// The endpoint file of the service for this config folder, if one is there (it may be stale). Falls back to where
    /// versions up to 0.1.0-alpha.9 wrote it, so a client finds a service of the previous version during an upgrade.
    /// </summary>
    public static ServiceEndpoint? FindEndpoint(string? configDir)
    {
        var dataDir = ServicePaths.ResolveDataDir(configDir);
        return ServiceEndpoint.TryLoad(ServicePaths.EndpointFile(dataDir)) ?? ServiceEndpoint.TryLoad(ServicePaths.LegacyEndpointFile(dataDir));
    }

    /// <summary>What answers at the endpoint file of this config folder.</summary>
    public enum ProbeResult { None, Running, Refused }

    /// <summary>
    /// Pings the service of this config folder: <see cref="ProbeResult.Running"/> (with its endpoint),
    /// <see cref="ProbeResult.Refused"/> when something answers on that port but rejects the token (the endpoint file
    /// belongs to another service), or <see cref="ProbeResult.None"/> when nothing usable listens.
    /// </summary>
    public static (ProbeResult Result, ServiceEndpoint? Endpoint) Probe(string? configDir)
    {
        var ep = FindEndpoint(configDir);
        if (ep is null) return (ProbeResult.None, null);
        try { return Request(ep, "ping") is not null ? (ProbeResult.Running, ep) : (ProbeResult.None, ep); }
        catch (ServiceRefusedException) { return (ProbeResult.Refused, ep); }
    }

    /// <summary>
    /// Opens a connection and performs the handshake. Returns the connected client and the service's reply; throws
    /// <see cref="ServiceRefusedException"/> when the service answers but refuses, <see cref="IOException"/> or
    /// <see cref="SocketException"/> when nothing usable listens there.
    /// </summary>
    public static (TcpClient Client, NetworkStream Stream, JsonObject Reply) Connect(ServiceEndpoint ep, string op, TimeSpan timeout, JsonObject? extra = null)
    {
        var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        try
        {
            using (var cts = new CancellationTokenSource(timeout))
            {
                try { client.ConnectAsync(IPAddress.Loopback, ep.Port, cts.Token).AsTask().GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { throw new IOException($"timed out connecting to 127.0.0.1:{ep.Port}"); }
            }
            var stream = client.GetStream();
            stream.ReadTimeout = (int)Math.Max(1000, timeout.TotalMilliseconds);
            var hello = new JsonObject { ["latticelog"] = Wire.Protocol, ["token"] = ep.Token, ["op"] = op };
            if (extra is not null) foreach (var (k, v) in extra) hello[k] = v?.DeepClone();
            Wire.WriteLine(stream, hello.ToJsonString());
            var reply = Wire.TryParse(Wire.ReadLine(stream)) ?? throw new IOException("the service closed the connection during the handshake");
            if (reply["ok"]?.GetValue<bool>() != true)
                throw new ServiceRefusedException((string?)reply["error"] ?? "refused", (string?)reply["message"] ?? "the service refused the request");
            stream.ReadTimeout = Timeout.Infinite;
            return (client, stream, reply);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>One request/reply operation (ping, status, stop, refresh, maintenance). Null when no service answers.</summary>
    public static JsonObject? Request(string? configDir, string op, TimeSpan? timeout = null)
    {
        var ep = FindEndpoint(configDir);
        if (ep is null) return null;
        return Request(ep, op, timeout);
    }

    public static JsonObject? Request(ServiceEndpoint ep, string op, TimeSpan? timeout = null)
    {
        try
        {
            var (client, _, reply) = Connect(ep, op, timeout ?? TimeSpan.FromSeconds(op is "status" or "refresh" or "maintenance" ? 60 : 3));
            client.Dispose();
            return reply;
        }
        catch (ServiceRefusedException) { throw; }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { return null; }
    }

    /// <summary>
    /// Sends one <see cref="ConfigEdit"/> to the running service for this config folder, which applies it to the
    /// config.json it reads. Returns the reply (<c>changed</c>, <c>config</c>), or null when no service answers or it
    /// predates the <c>config</c> operation: the caller then writes the file itself. Throws
    /// <see cref="ServiceRefusedException"/> when the service rejects the edit.
    /// </summary>
    public static JsonObject? EditConfig(string? configDir, JsonObject edit)
    {
        var ep = Running(configDir);
        if (ep is null) return null;
        try
        {
            var extra = new JsonObject { ["edit"] = edit.DeepClone(), ["config_dir"] = ServicePaths.ResolveConfigDir(configDir) };
            var (client, _, reply) = Connect(ep, "config", TimeSpan.FromSeconds(10), extra);
            client.Dispose();
            return reply;
        }
        catch (ServiceRefusedException ex) when (ex.Error == "unknown_op") { return null; }
        catch (Exception ex) when (ex is not ServiceRefusedException && ex is IOException or SocketException or ObjectDisposedException) { return null; }
    }

    /// <summary>The endpoint of a service that answers a ping, or null.</summary>
    public static ServiceEndpoint? Running(string? configDir) =>
        Probe(configDir) is (ProbeResult.Running, { } ep) ? ep : null;

    /// <summary>Waits until a service answers (after a start) or <paramref name="timeout"/> passes.</summary>
    public static ServiceEndpoint? WaitRunning(string? configDir, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (Running(configDir) is { } ep) return ep;
            if (sw.Elapsed > timeout) return null;
            Thread.Sleep(100);
        }
    }

    /// <summary>Asks the service to stop and waits until it no longer answers. False when none was running.</summary>
    public static bool Stop(string? configDir, TimeSpan timeout)
    {
        var ep = FindEndpoint(configDir);
        if (ep is null || Request(ep, "stop") is null) return false;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (Running(configDir) is null) return true;
            Thread.Sleep(100);
        }
        return true;
    }
}
