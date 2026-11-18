using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace LatticeLog.Server;

public sealed record TokenInfo(string Id, byte[] Sha256, IReadOnlySet<string> Producers, double RecordsPerSecond)
{
    public bool AllowsProducer(string producer) => Producers.Contains("*") || Producers.Contains(producer);
}

/// <summary>
/// Bearer tokens from a JSON file (only SHA-256 hashes are stored). The file is re-read when it changes, so tokens can
/// be rotated without a restart: add the new token, move clients, remove the old one (P16-T10).
/// <code>{"tokens":[{"id":"orders-api","sha256":"…hex…","producers":["*"],"records_per_second":0}]}</code>
/// </summary>
public sealed class TokenStore
{
    private readonly string? _path;
    private readonly object _gate = new();
    private IReadOnlyList<TokenInfo> _tokens = [];
    private DateTime _loadedWriteTime;
    private DateTime _lastCheck;

    private TokenStore(string? path, IReadOnlyList<TokenInfo> fixedTokens)
    {
        _path = path;
        _tokens = fixedTokens;
        if (path is not null) Reload();
    }

    public static TokenStore FromFile(string path) => new(path, []);

    public static TokenStore FromTokens(params TokenInfo[] tokens) => new(null, tokens);

    public static string HashHex(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static TokenInfo Token(string id, string secret, double recordsPerSecond = 0, params string[] producers)
        => new(id, SHA256.HashData(Encoding.UTF8.GetBytes(secret)), new HashSet<string>(producers.Length == 0 ? ["*"] : producers, StringComparer.OrdinalIgnoreCase), recordsPerSecond);

    private void Reload()
    {
        var info = new FileInfo(_path!);
        var root = JsonNode.Parse(File.ReadAllText(_path!)) ?? throw new InvalidDataException("empty token file");
        var list = new List<TokenInfo>();
        foreach (var t in root["tokens"]!.AsArray())
        {
            var producers = t!["producers"]?.AsArray().Select(p => (string)p!).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? ["*"];
            var hash = Convert.FromHexString((string)t["sha256"]!);
            if (hash.Length != 32) throw new InvalidDataException($"token {(string?)t["id"]}: sha256 must be 64 hex characters");
            list.Add(new TokenInfo((string)t["id"]!, hash, producers, (double?)t["records_per_second"] ?? 0));
        }
        _tokens = list;
        _loadedWriteTime = info.LastWriteTimeUtc;
    }

    /// <summary>Returns the token matching <paramref name="presented"/> (constant-time comparison of hashes) or null.</summary>
    public TokenInfo? Authenticate(string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return null;
        if (_path is not null)
        {
            lock (_gate)
            {
                if (DateTime.UtcNow - _lastCheck > TimeSpan.FromSeconds(1))
                {
                    _lastCheck = DateTime.UtcNow;
                    try
                    {
                        if (File.GetLastWriteTimeUtc(_path) != _loadedWriteTime) Reload();
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or System.Text.Json.JsonException or NullReferenceException)
                    {
                        // Keep the previous set: a half-written file must not lock everybody out or let anybody in.
                    }
                }
            }
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        TokenInfo? match = null;
        foreach (var t in _tokens)
            if (CryptographicOperations.FixedTimeEquals(t.Sha256, hash)) match = t;
        return match;
    }

    /// <summary>Forces the next authentication to re-check the file (tests).</summary>
    internal void ExpireCache() { lock (_gate) _lastCheck = DateTime.MinValue; }
}

/// <summary>Token bucket per token (records/s, burst = 2 s of rate). 0 = unlimited (P16-T11).</summary>
public sealed class QuotaLimiter
{
    private sealed class Bucket { public double Tokens; public long Last; }
    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    public bool TryTake(TokenInfo token, int records, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (token.RecordsPerSecond <= 0) return true;
        var burst = token.RecordsPerSecond * 2;
        lock (_buckets)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!_buckets.TryGetValue(token.Id, out var b)) _buckets[token.Id] = b = new Bucket { Tokens = burst, Last = now };
            b.Tokens = Math.Min(burst, b.Tokens + (now - b.Last) / (double)System.Diagnostics.Stopwatch.Frequency * token.RecordsPerSecond);
            b.Last = now;
            if (b.Tokens >= records || (records > burst && b.Tokens >= burst))
            {
                b.Tokens -= records;
                return true;
            }
            retryAfter = TimeSpan.FromSeconds(Math.Ceiling(Math.Max(1, (records - b.Tokens) / token.RecordsPerSecond)));
            return false;
        }
    }
}
