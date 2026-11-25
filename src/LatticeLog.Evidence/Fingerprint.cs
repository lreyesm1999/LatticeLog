using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LatticeLog.Evidence;

/// <summary>
/// Deterministic error fingerprint (P01-T11, P12-T06). It groups records that share event identity, exception
/// type chain and a normalized message shape. It is a grouping key, never a root-cause claim.
/// Algorithm "fp1": sha256(event | exception types | normalized message)[0..16 hex].
/// </summary>
public static partial class Fingerprint
{
    public const string Algorithm = "fp1";

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant)]
    private static partial Regex Uuid();
    [GeneratedRegex(@"\b(0x)?[0-9a-fA-F]{12,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex LongHex();
    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex Digits();
    [GeneratedRegex(@"'[^']*'|""[^""]*""", RegexOptions.CultureInvariant)]
    private static partial Regex Quoted();

    public static string Normalize(string message)
    {
        if (message.Length > 512) message = message[..512];
        message = Uuid().Replace(message, "<uuid>");
        message = LongHex().Replace(message, "<hex>");
        message = Quoted().Replace(message, "<str>");
        message = Digits().Replace(message, "<n>");
        return message.Trim();
    }

    public static string MessageShape(LogRecord r)
    {
        if (r.Event?.MessageTemplate is { } t) return t;
        if (r.Exception?.Message is { } em) return Normalize(em);
        if (r.HasBody && TerminalText.MessageOf(r.Body) is { } body) return Normalize(body);
        return "";
    }

    public static string Compute(LogRecord r)
    {
        var sb = new StringBuilder();
        sb.Append(r.EffectiveEventName ?? "(none)").Append('|');
        for (var e = r.Exception; e is not null; e = e.Inner.Count > 0 ? e.Inner[0] : null) sb.Append(e.Type).Append('>');
        sb.Append('|').Append(MessageShape(r));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16].ToLowerInvariant();
    }
}
