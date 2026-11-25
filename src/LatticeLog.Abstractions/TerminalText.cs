using System.Text;
using System.Text.Unicode;

namespace LatticeLog;

/// <summary>
/// Readable text of raw log lines. Programs that log to a terminal (uvicorn, structlog's console renderer, cargo, npm …)
/// colour their output with ANSI escape sequences, and those sequences end up in the file when the output is
/// redirected. They are kept byte for byte in storage; this class only removes them where a line is parsed or shown.
/// </summary>
public static class TerminalText
{
    private const char Esc = '\u001b';

    /// <summary>
    /// <paramref name="s"/> without ANSI escape sequences: CSI (<c>ESC [ … final</c>, colours and cursor moves), OSC
    /// (<c>ESC ] … BEL</c> or <c>ESC ] … ESC \</c>, titles and hyperlinks), nF (<c>ESC ( B</c>) and the two-byte <c>ESC x</c> forms.
    /// Returns the same instance when there is nothing to remove.
    /// </summary>
    public static string StripAnsi(string s)
    {
        var i = s.IndexOf(Esc);
        if (i < 0) return s;
        var sb = new StringBuilder(s.Length);
        sb.Append(s, 0, i);
        while (i < s.Length)
        {
            var c = s[i];
            if (c != Esc) { sb.Append(c); i++; continue; }
            i++;
            if (i >= s.Length) break;
            var kind = s[i++];
            if (kind == '[')
            {
                // Parameter and intermediate bytes 0x20–0x3F, then one final byte 0x40–0x7E.
                while (i < s.Length && s[i] is >= ' ' and <= '?') i++;
                if (i < s.Length && s[i] is >= '@' and <= '~') i++;
            }
            else if (kind == ']')
            {
                while (i < s.Length && s[i] != '\a' && !(s[i] == Esc && i + 1 < s.Length && s[i + 1] == '\\')) i++;
                if (i < s.Length) i += s[i] == '\a' ? 1 : 2;
            }
            else if (kind is >= ' ' and <= '/')
            {
                // nF escapes (ESC ( B charset, ESC # 8 …): intermediate bytes 0x20–0x2F, then one final byte.
                while (i < s.Length && s[i] is >= ' ' and <= '/') i++;
                if (i < s.Length) i++;
            }
            // Any other ESC x (ESC =, ESC 7, ESC M …) is two characters long and already consumed.
        }
        return sb.ToString();
    }

    /// <summary>
    /// The text of a raw log line when its bytes are valid UTF-8: ANSI escape sequences removed and a trailing
    /// <c>\r</c> (CRLF files) dropped. False for bytes that are not UTF-8 text (Latin-1 files, binary data).
    /// </summary>
    /// <summary>The message text of a body: a string as is, or a raw-imported UTF-8 line (<see cref="TryDecodeLine"/>); otherwise null.</summary>
    public static string? MessageOf(Value body) => body.Kind switch
    {
        ValueKind.String => body.AsString(),
        ValueKind.Bytes when TryDecodeLine(body.AsBytes(), out var text) => text,
        _ => null,
    };

    public static bool TryDecodeLine(ReadOnlySpan<byte> bytes, out string text)
    {
        if (!Utf8.IsValid(bytes)) { text = ""; return false; }
        if (bytes.Length > 0 && bytes[^1] == (byte)'\r') bytes = bytes[..^1];
        text = StripAnsi(Encoding.UTF8.GetString(bytes));
        return true;
    }
}
