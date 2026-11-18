using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using LatticeLog.Storage.Format;

namespace LatticeLog.Server;

/// <summary>
/// Append-only audit trail (P18): who (token id, remote address) did what (action, namespace, detail) and with what
/// outcome, one JSON line per event in <c>&lt;data&gt;/audit/audit-YYYYMMDD.jsonl</c>, each prefixed with its CRC-32
/// (same framing as MANIFEST.jsonl) so truncation or edits are detectable. Never contains secrets or log content: only
/// filters and counts. Written on a background task; the queue is bounded and overflow is counted, never blocking.
/// </summary>
public sealed class AuditLog : IAsyncDisposable
{
    private readonly string _dir;
    private readonly Channel<JsonObject> _queue = Channel.CreateBounded<JsonObject>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Task _writer;
    private long _dropped;

    public AuditLog(string dataDirectory)
    {
        _dir = Path.Combine(dataDirectory, "audit");
        Directory.CreateDirectory(_dir);
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>Audit events lost because the queue was full (also reported in /v1/health for admins).</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public void Record(string action, string? tokenId, string? remote, string? ns, string outcome, string? detail = null)
    {
        var e = new JsonObject
        {
            ["time"] = UnixTime.FormatRfc3339(UnixTime.NowUnixNanos()),
            ["action"] = action,
            ["token"] = tokenId,
            ["remote"] = remote,
            ["namespace"] = ns,
            ["outcome"] = outcome,
        };
        if (detail is not null) e["detail"] = detail.Length > 2_000 ? detail[..2_000] + "…" : detail;
        if (!_queue.Writer.TryWrite(e)) Interlocked.Increment(ref _dropped);
    }

    private async Task WriteLoopAsync()
    {
        FileStream? fs = null;
        var day = "";
        try
        {
            await foreach (var e in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var today = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                if (today != day)
                {
                    if (fs is not null) await fs.DisposeAsync().ConfigureAwait(false);
                    fs = new FileStream(Path.Combine(_dir, $"audit-{today}.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read);
                    day = today;
                }
                var json = e.ToJsonString();
                var line = Checksum.Crc32(Encoding.UTF8.GetBytes(json)).ToString("x8", CultureInfo.InvariantCulture) + " " + json + "\n";
                await fs!.WriteAsync(Encoding.UTF8.GetBytes(line)).ConfigureAwait(false);
                if (_queue.Reader.Count == 0) await fs.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (IOException) { /* disk problem: the server keeps serving; health shows dropped events */ }
        finally
        {
            if (fs is not null)
            {
                fs.Flush(flushToDisk: true);
                await fs.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Reads and checks one audit file: returns the events and the number of lines whose CRC does not match.</summary>
    public static (List<JsonObject> Events, int Corrupt) ReadFile(string path)
    {
        var events = new List<JsonObject>();
        var corrupt = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length < 10 || line[8] != ' ' || !uint.TryParse(line.AsSpan(0, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var crc)
                || Checksum.Crc32(Encoding.UTF8.GetBytes(line[9..])) != crc)
            {
                corrupt++;
                continue;
            }
            events.Add(JsonNode.Parse(line[9..])!.AsObject());
        }
        return (events, corrupt);
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
    }
}
