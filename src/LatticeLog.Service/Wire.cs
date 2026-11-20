using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LatticeLog.Service;

/// <summary>
/// The service's line protocol on 127.0.0.1 (specs/local-sources.md, "Background service"). A client opens a TCP
/// connection and sends one JSON line:
/// <code>{"latticelog":"service/1","token":"…","op":"mcp|status|stop|ping","project_dir":"…","config_dir":"…"}</code>
/// The service answers one JSON line (<c>{"ok":true,…}</c> or <c>{"ok":false,"error":"…"}</c>). For <c>mcp</c> the
/// connection then carries MCP JSON-RPC lines both ways until either side closes it. The same port also answers
/// HTTP GET (web console, status), told apart by the first byte.
/// </summary>
public static class Wire
{
    public const string Protocol = "service/1";
    public const int MaxHandshakeBytes = 64 * 1024;

    /// <summary>Reads one '\n'-terminated UTF-8 line byte by byte (never reads past it). Null at end of stream.</summary>
    public static string? ReadLine(Stream s, int maxBytes = MaxHandshakeBytes, byte? first = null)
    {
        var buf = new List<byte>(256);
        if (first is { } f) { if (f == (byte)'\n') return ""; buf.Add(f); }
        var one = new byte[1];
        while (true)
        {
            var n = s.Read(one, 0, 1);
            if (n == 0) return buf.Count == 0 && first is null ? null : Encoding.UTF8.GetString(buf.ToArray());
            if (one[0] == (byte)'\n') break;
            buf.Add(one[0]);
            if (buf.Count > maxBytes) throw new InvalidDataException("line too long");
        }
        if (buf.Count > 0 && buf[^1] == (byte)'\r') buf.RemoveAt(buf.Count - 1);
        return Encoding.UTF8.GetString(buf.ToArray());
    }

    public static void WriteLine(Stream s, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        s.Write(bytes, 0, bytes.Length);
        s.Flush();
    }

    public static JsonObject? TryParse(string? line)
    {
        if (line is null) return null;
        try { return JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return null; }
    }

    /// <summary>Constant-time token comparison.</summary>
    public static bool TokenEquals(string? a, string b) =>
        a is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
