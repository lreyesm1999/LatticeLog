using System.Diagnostics;
using System.Text;

namespace LatticeLog.Cli;

/// <summary>
/// Styled output for the interactive commands (setup, update, service install): the brand mark, status marks, aligned
/// fields and a download progress bar. Colors and the progress bar only when stdout is a terminal and NO_COLOR is not
/// set; redirected output (scripts, CI logs) gets the same words with ASCII marks and no escape codes.
/// </summary>
internal static class Term
{
    // The logo's colors: cyan → blue → violet; plus status colors.
    private const string Cyan = "38;2;0;191;255", Violet = "38;2;138;92;255", Green = "38;2;52;211;153",
        Yellow = "38;2;251;191;36", Gray = "38;2;136;146;166";

    private static readonly Lazy<(bool Color, bool Unicode)> Caps = new(Detect);

    /// <summary>ANSI colors are on (stdout is a terminal that takes them).</summary>
    public static bool Color => Caps.Value.Color;

    private static bool Unicode => Caps.Value.Unicode;

    private static string Paint(string sgr, string text) => Color && text.Length > 0 ? $"\u001b[{sgr}m{text}\u001b[0m" : text;

    public static string Bold(string text) => Paint("1", text);
    public static string Dim(string text) => Paint(Gray, text);
    public static string Accent(string text) => Paint(Cyan, text);

    private static string Mark => Color ? Paint("1;" + Cyan, "/") + Paint("1;" + Violet, "/") : "//";

    /// <summary>A blank line, <c>// LatticeLog · title  note</c>, a blank line.</summary>
    public static void Header(string title, string? note = null)
    {
        Console.WriteLine();
        Console.WriteLine($"  {Mark} {Bold("LatticeLog")} {Dim(Unicode ? "·" : "-")} {title}{(note is null ? "" : "  " + Dim(note))}");
        Console.WriteLine();
    }

    /// <summary>A label (dimmed, padded) and its value, for a short summary block.</summary>
    public static void Field(string label, string value, int width = 12) =>
        Console.WriteLine($"  {Dim(label.PadRight(width))} {value}");

    /// <summary>A step that succeeded: <c>✓ text  detail</c>.</summary>
    public static void Ok(string text, string? detail = null) => Status(Green, Unicode ? "✓" : "+", text, detail);

    /// <summary>Something the user should know about: <c>! text  detail</c>.</summary>
    public static void Warn(string text, string? detail = null) => Status(Yellow, "!", text, detail);

    /// <summary>A step being taken: <c>› text  detail</c>.</summary>
    public static void Step(string text, string? detail = null) => Status(Cyan, Unicode ? "›" : ">", text, detail);

    /// <summary>A dimmed line indented under the previous status line.</summary>
    public static void Note(string text) => Console.WriteLine("    " + Dim(text));

    private static void Status(string color, string mark, string text, string? detail) =>
        Console.WriteLine($"  {Paint("1;" + color, mark)} {text}{(string.IsNullOrEmpty(detail) ? "" : "  " + Dim(detail))}");

    /// <summary>Joins the non-empty parts with a middle dot.</summary>
    public static string Join(params string?[] parts) => string.Join(Unicode ? " · " : ", ", parts.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>The ellipsis for shortened values.</summary>
    public static string Ellipsis => Unicode ? "…" : "...";

    /// <summary>From → to.</summary>
    public static string Arrow => Unicode ? "→" : "->";

    /// <summary>A titled list of commands, aligned: <c>command   what it does</c>.</summary>
    public static void Commands(string title, IReadOnlyList<(string Command, string What)> rows)
    {
        Console.WriteLine();
        Console.WriteLine("  " + Bold(title));
        var width = rows.Max(r => r.Command.Length) + 3;
        foreach (var (command, what) in rows) Console.WriteLine($"    {Accent(command.PadRight(width))}{Dim(what)}");
    }

    /// <summary>Asks a yes/no question that defaults to yes; false only for an explicit "n"/"no".</summary>
    public static bool ConfirmYes(string question)
    {
        Console.Write($"  {Paint("1;" + Cyan, "?")} {question} {Dim("[Y/n]")} ");
        return Console.ReadLine()?.Trim().ToLowerInvariant() is not ("n" or "no");
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1048576.0:0.0} MB",
    };

    /// <summary>
    /// A one-line progress bar redrawn in place (<c>↓ label  ━━━━━━────  62%  27.4 / 44.0 MB</c>). Draws nothing unless
    /// colors are on, so redirected output has no carriage returns in it. Dispose clears the line.
    /// </summary>
    public sealed class Progress(string label, long? total) : IDisposable
    {
        private const int Cells = 28;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _lastDraw = -1000;
        private bool _drawn;

        public void Report(long done, bool force = false)
        {
            if (!Color || (!force && _clock.ElapsedMilliseconds - _lastDraw < 80)) return;
            _lastDraw = _clock.ElapsedMilliseconds;
            var sb = new StringBuilder("\r  ").Append(Paint("1;" + Cyan, Unicode ? "↓" : "v")).Append(' ').Append(label).Append("  ");
            if (total is > 0 and var t)
            {
                var filled = (int)Math.Clamp(done * Cells / t, 0, Cells);
                for (var i = 0; i < Cells; i++)
                {
                    if (i < filled)
                    {
                        // The bar fades from the logo's cyan to its violet.
                        var f = i / (double)(Cells - 1);
                        sb.Append($"\u001b[38;2;{Lerp(0, 138, f)};{Lerp(191, 92, f)};255m").Append(Unicode ? '━' : '#');
                    }
                    else sb.Append($"\u001b[{Gray}m").Append(Unicode ? '─' : '-');
                }
                sb.Append("\u001b[0m  ").Append(Bold($"{done * 100 / t,3}%")).Append("  ").Append(Dim($"{Size(done)} / {Size(t)}"));
            }
            else sb.Append(Dim(Size(done)));
            Console.Write(sb.Append("\u001b[K").ToString());
            _drawn = true;
        }

        private static int Lerp(int a, int b, double f) => (int)Math.Round(a + (b - a) * f);

        public void Dispose()
        {
            if (_drawn) Console.Write("\r\u001b[K");
        }
    }

    // ------------------------------------------------------------------ terminal detection

    private static (bool Color, bool Unicode) Detect()
    {
        if (Console.IsOutputRedirected) return (false, false);
        // The classic Windows console window shows escape codes as text unless the program turns VT processing on, which
        // takes native calls; this tool makes none, so that window gets the plain output.
        var modern = !OperatingSystem.IsWindows() || VirtualTerminalHost();
        var unicode = modern && UnicodeOutput();
        if (!string.IsNullOrEmpty(Env("NO_COLOR")) || Env("TERM") == "dumb") return (false, unicode);
        return (modern, unicode);
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    /// <summary>
    /// Windows hosts that process ANSI escape codes for every program: Windows Terminal, and the terminals built on
    /// ConPTY that say so (VS Code and IDE terminals set TERM or TERM_PROGRAM, as do mintty and Git Bash), ConEmu, ANSICON.
    /// </summary>
    private static bool VirtualTerminalHost() =>
        Env("WT_SESSION") is not null || Env("TERM") is not null || Env("TERM_PROGRAM") is not null
        || Env("ConEmuANSI") == "ON" || Env("ANSICON") is not null;

    /// <summary>
    /// Whether ✓ and friends can be written. Elsewhere the terminal is UTF-8 in practice. On Windows, UTF-16 output makes
    /// .NET write with WriteConsoleW, which draws any character and leaves the console code page (the shell's) alone.
    /// Not when stderr is redirected: the encoding applies to it too, and a file would get UTF-16.
    /// </summary>
    private static bool UnicodeOutput()
    {
        if (!OperatingSystem.IsWindows()) return true;
        if (Console.IsErrorRedirected) return false;
        try
        {
            Console.OutputEncoding = Encoding.Unicode;
            return true;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) { return false; }
    }
}
