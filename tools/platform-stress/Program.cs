// Control experiment: the same kind of work as the collector (gzip inflate + JSON line parsing + RFC 3339 dates) on
// many threads, with no LatticeLog code at all. Every result is checked; any mismatch or crash is reported.
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
var threads = int.Parse(args.Length > 0 ? args[0] : "32");
var seconds = int.Parse(args.Length > 1 ? args[1] : "60");
var sb = new StringBuilder();
for (var i = 0; i < 2000; i++)
    sb.Append("{\"type\":\"record\",\"seq\":\"").Append(i + 1).Append("\",\"time\":\"2026-11-18T10:00:00.").Append((i % 1000).ToString("D3")).Append("123456Z\",\"severity_number\":9,\"attributes\":{\"orderId\":").Append(i).Append(",\"sku\":\"SKU-").Append(i % 500).Append("\",\"amount\":{\"double\":19.99}}}\n");
var text = Encoding.UTF8.GetBytes(sb.ToString());
using var ms = new MemoryStream();
using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true)) gz.Write(text);
var compressed = ms.ToArray();
long iterations = 0, errors = 0;
var deadline = DateTime.UtcNow.AddSeconds(seconds);
var workers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
{
    while (DateTime.UtcNow < deadline)
    {
        using var input = new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        var lines = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        long sum = 0;
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            sum += long.Parse(e.GetProperty("seq").GetString()!, CultureInfo.InvariantCulture);
            if (!DateTimeOffset.TryParse(e.GetProperty("time").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset _)) Interlocked.Increment(ref errors);
            sum += e.GetProperty("attributes").GetProperty("orderId").GetInt64();
        }
        if (lines.Length != 2000 || sum != 2000L * 2001 / 2 + 1999L * 2000 / 2) Interlocked.Increment(ref errors);
        Interlocked.Increment(ref iterations);
    }
})).ToList();
workers.ForEach(t => t.Start());
workers.ForEach(t => t.Join());
Console.WriteLine($"threads {threads}, {seconds}s: {iterations} iterations ({iterations * 2000:N0} lines), {errors} wrong results");
