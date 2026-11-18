// Loghub-2.0 experiment (P02-T01): first N lines of each dataset, read straight from the downloaded zips (nothing is
// extracted to disk). Two LatticeLog imports per dataset:
//   - text: byte-exact raw lines (RawTextImporter), verified by re-exporting and comparing bytes;
//   - structured: one typed event per Loghub template (EventId), header columns + template parameters as attributes,
//     verified by rebuilding every Content string from its template and parameters. Header formatting (e.g. the
//     exact spacing of the original line) is not part of this variant, so it is compared with the structured CSV.
// Data: Loghub-2.0, Zenodo record 8275861, CC-BY-4.0 (cite: Jiang et al., "A Large-Scale Evaluation for Log Parsing
// Techniques: How Far Are We?", ISSTA 2024). Downloaded files are untrusted input: parsed, never executed.
using System.Globalization;
using System.IO.Compression;
using System.Text;
using LatticeLog;
using LatticeLog.OpenTelemetry;
using LatticeLog.Storage;
using LatticeLog.Storage.Format;

namespace LatticeLog.Lab;

public static class Loghub
{
    private sealed record Row(string Dataset, int Lines, long Raw, long Gzip, long Zstd3, long Zstd19, long LlogText, bool TextRoundTrip,
        long Csv, long CsvZstd3, long CsvZstd19, long LlogStructured, int Templates, int Fallbacks, int ContentMismatches);

    public static int Run(string zipDir, int maxLines, string outDir, string work)
    {
        var rows = new List<Row>();
        foreach (var zip in Directory.GetFiles(zipDir, "*.zip").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(zip);
            using var archive = ZipFile.OpenRead(zip);
            var logEntry = archive.Entries.Single(e => e.Name == $"{name}_full.log");
            var csvEntry = archive.Entries.Single(e => e.Name == $"{name}_full.log_structured.csv");

            // ---- text, byte exact
            List<LogRecord> textRecords;
            using (var s = logEntry.Open())
                textRecords = RawTextImporter.Read(s, logEntry.Name, 1_700_000_000_000_000_000).Take(maxLines).ToList();
            for (var i = 0; i < textRecords.Count; i++) textRecords[i].Sequence = (ulong)i + 1;
            byte[] raw;
            using (var ms = new MemoryStream()) { RawTextImporter.Export(textRecords, ms); raw = ms.ToArray(); }
            var (llogText, _) = Encode.Llog(textRecords, CodecId.Structural, Compression.Deflate, work);
            bool textOk;
            using (var back = new MemoryStream())
            {
                RawTextImporter.Export(ReadBack(llogText, work), back);
                textOk = back.ToArray().AsSpan().SequenceEqual(raw);
            }

            // ---- structured (Loghub templates)
            var (structured, csvBytes, templates, fallbacks, mismatches) = ReadStructured(csvEntry, name, maxLines);
            var (llogStructured, _) = Encode.Llog(structured, CodecId.Structural, Compression.Deflate, work);
            var reread = ReadBack(llogStructured, work);
            if (reread.Count != structured.Count || !reread.Zip(structured).All(p => p.First.Equals(p.Second)))
                throw new InvalidDataException($"{name}: structured round trip through .llog failed");

            var row = new Row(name, textRecords.Count, raw.Length, Encode.Gzip(raw, CompressionLevel.Optimal).Length,
                Encode.Zstd(raw, 3).Length, Encode.Zstd(raw, 19).Length, llogText.Length, textOk,
                csvBytes.Length, Encode.Zstd(csvBytes, 3).Length, Encode.Zstd(csvBytes, 19).Length, llogStructured.Length,
                templates, fallbacks, mismatches);
            rows.Add(row);
            Console.WriteLine($"{name,-12} lines {row.Lines,8:N0} raw {Mb(row.Raw)} zstd3 {Mb(row.Zstd3)} zstd19 {Mb(row.Zstd19)} llog-text {Mb(row.LlogText)} ({(textOk ? "byte-exact" : "MISMATCH")}) | csv-zstd3 {Mb(row.CsvZstd3)} llog-struct {Mb(row.LlogStructured)} templates {templates} fallbacks {fallbacks}");
        }

        Directory.CreateDirectory(outDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var csvPath = Path.Combine(outDir, $"loghub2-{stamp}.csv");
        using (var w = new StreamWriter(csvPath))
        {
            w.WriteLine("dataset,lines,raw_bytes,gzip_bytes,zstd3_bytes,zstd19_bytes,llog_text_s_deflate_bytes,text_round_trip,structured_csv_bytes,csv_zstd3_bytes,csv_zstd19_bytes,llog_structured_s_deflate_bytes,templates,template_fallbacks,content_mismatches");
            foreach (var r in rows)
                w.WriteLine(string.Join(',', r.Dataset, r.Lines, r.Raw, r.Gzip, r.Zstd3, r.Zstd19, r.LlogText, r.TextRoundTrip ? "ok" : "MISMATCH",
                    r.Csv, r.CsvZstd3, r.CsvZstd19, r.LlogStructured, r.Templates, r.Fallbacks, r.ContentMismatches));
        }
        Console.WriteLine("results: " + csvPath);
        return rows.All(r => r.TextRoundTrip && r.ContentMismatches == 0) ? 0 : 1;
    }

    private static string Mb(long b) => (b / 1e6).ToString("F2", CultureInfo.InvariantCulture).PadLeft(7) + "M";

    private static List<LogRecord> ReadBack(byte[] llog, string work)
    {
        var path = Path.Combine(work, $"lh-{Guid.NewGuid():N}.llog");
        File.WriteAllBytes(path, llog);
        try
        {
            using var r = SegmentReader.Open(path);
            var rep = new ScanReport();
            var list = r.ReadBlocks(rep).SelectMany(b => b.Records).ToList();
            if (!rep.IsClean) throw new InvalidDataException(rep.Issues[0].Message);
            return list;
        }
        finally { File.Delete(path); }
    }

    private static (List<LogRecord> Records, byte[] Csv, int Templates, int Fallbacks, int Mismatches) ReadStructured(ZipArchiveEntry entry, string dataset, int maxLines)
    {
        var csv = new MemoryStream();
        var records = new List<LogRecord>(maxLines);
        var descriptors = new Dictionary<string, (EventDescriptor Desc, string[] Literals)>(StringComparer.Ordinal);
        var context = new ContextSnapshot(ContextKind.Resource, null, [new("service.name", dataset)], null);
        int fallbacks = 0, mismatches = 0;
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, new UTF8Encoding(false, false));
        var header = Csv.ReadRecord(reader) ?? throw new InvalidDataException("empty csv");
        int iContent = Array.IndexOf(header, "Content"), iEvent = Array.IndexOf(header, "EventId"), iTemplate = Array.IndexOf(header, "EventTemplate"), iLine = Array.IndexOf(header, "LineId"), iLevel = Array.IndexOf(header, "Level");
        var headerFields = Enumerable.Range(0, header.Length).Where(i => i != iContent && i != iEvent && i != iTemplate && i != iLine).ToArray();
        Csv.Write(csv, header);
        string[]? row;
        while (records.Count < maxLines && (row = Csv.ReadRecord(reader)) is not null)
        {
            Csv.Write(csv, row);
            var content = row[iContent];
            var template = row[iTemplate];
            var literals = template.Split("<*>");
            var parameters = Match(content, literals);
            if (!descriptors.TryGetValue(row[iEvent] + "\u0000" + template, out var d))
            {
                var fields = headerFields.Select(i => new EventField(header[i], ValueKind.String))
                    .Concat(Enumerable.Range(0, literals.Length - 1).Select(p => new EventField("p" + p, ValueKind.String))).ToArray();
                var id = uint.TryParse(row[iEvent].TrimStart('E'), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : (uint)(descriptors.Count + 100_000);
                d = (new EventDescriptor(id, $"loghub.{dataset.ToLowerInvariant()}.{row[iEvent]}", (ushort)(descriptors.Count(x => x.Value.Desc.Id == id) + 1), Severity.Info, null, fields, template), literals);
                descriptors[row[iEvent] + "\u0000" + template] = d;
            }
            var r = new LogRecord { Context = context, Sequence = (ulong)records.Count + 1 };
            if (iLevel >= 0 && SeverityExtensions.TryParse(row[iLevel], out var sev)) r.Severity = sev;
            var attrs = new List<LogAttribute>(headerFields.Length + literals.Length);
            foreach (var i in headerFields) attrs.Add(new(header[i], Typed(row[i])));
            if (parameters is null)
            {
                // Template does not reproduce the content: keep the exact content as body, no parameters.
                fallbacks++;
                r.EventName = $"loghub.{dataset.ToLowerInvariant()}.{row[iEvent]}.unmatched";
                r.SetBody(Value.From(content));
            }
            else
            {
                r.Event = d.Desc;
                for (var p = 0; p < parameters.Length; p++) attrs.Add(new("p" + p, Typed(parameters[p])));
                if (Rebuild(d.Literals, parameters) != content) mismatches++;
            }
            r.Attributes = attrs.ToArray();
            records.Add(r);
        }
        return (records, csv.ToArray(), descriptors.Count, fallbacks, mismatches);
    }

    /// <summary>Integers without leading zeros become Int64 (what a typed producer would log); everything else stays text.</summary>
    private static Value Typed(string s)
        => s.Length is > 0 and <= 18 && (s == "0" || (s[0] is >= '1' and <= '9')) && s.All(char.IsAsciiDigit)
            ? Value.From(long.Parse(s, CultureInfo.InvariantCulture)) : Value.From(s);

    private static string Rebuild(string[] literals, string[] parameters)
    {
        var sb = new StringBuilder(literals[0]);
        for (var i = 0; i < parameters.Length; i++) sb.Append(parameters[i]).Append(literals[i + 1]);
        return sb.ToString();
    }

    /// <summary>Extracts the wildcard values of a Loghub template; null when the template does not reproduce the content.</summary>
    private static string[]? Match(string content, string[] literals)
    {
        if (!content.StartsWith(literals[0], StringComparison.Ordinal)) return null;
        var pos = literals[0].Length;
        var values = new string[literals.Length - 1];
        for (var i = 1; i < literals.Length; i++)
        {
            var lit = literals[i];
            int at;
            if (i == literals.Length - 1)
            {
                if (!content.EndsWith(lit, StringComparison.Ordinal) || content.Length - lit.Length < pos) return null;
                at = content.Length - lit.Length;
            }
            else
            {
                at = lit.Length == 0 ? pos : content.IndexOf(lit, pos, StringComparison.Ordinal);
                if (at < 0) return null;
            }
            values[i - 1] = content[pos..at];
            pos = at + lit.Length;
        }
        return Rebuild(literals, values) == content ? values : null;
    }

    /// <summary>Minimal RFC 4180 reader/writer (quoted fields, doubled quotes, newlines inside quotes).</summary>
    private static class Csv
    {
        public static string[]? ReadRecord(TextReader r)
        {
            var fields = new List<string>();
            var sb = new StringBuilder();
            var quoted = false;
            var any = false;
            while (true)
            {
                var c = r.Read();
                if (c < 0)
                {
                    if (!any) return null;
                    fields.Add(sb.ToString());
                    return fields.ToArray();
                }
                any = true;
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (r.Peek() == '"') { r.Read(); sb.Append('"'); }
                        else quoted = false;
                    }
                    else sb.Append((char)c);
                    continue;
                }
                switch (c)
                {
                    case '"': quoted = true; break;
                    case ',': fields.Add(sb.ToString()); sb.Clear(); break;
                    case '\r': break;
                    case '\n': fields.Add(sb.ToString()); return fields.ToArray();
                    default: sb.Append((char)c); break;
                }
            }
        }

        public static void Write(Stream s, string[] fields)
        {
            var line = string.Join(',', fields.Select(f => f.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + f.Replace("\"", "\"\"") + "\"" : f)) + "\n";
            var bytes = Encoding.UTF8.GetBytes(line);
            s.Write(bytes);
        }
    }
}
