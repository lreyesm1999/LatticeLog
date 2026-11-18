using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace LatticeLog.Server;

/// <summary>What a token may do (P17-T05).</summary>
[Flags]
public enum TokenScopes
{
    None = 0,
    /// <summary>Send batches (<c>POST /v1/ingest</c>).</summary>
    Ingest = 1,
    /// <summary>Search, read events/traces, aggregations, context packs, export, catalog.</summary>
    Read = 2,
    /// <summary>Server-wide views (all namespaces in health and listings).</summary>
    Admin = 4,
}

/// <summary>A bearer token: only its SHA-256 is kept. <see cref="Namespaces"/>[0] is where it ingests by default.</summary>
public sealed record TokenInfo(string Id, byte[] Sha256, IReadOnlySet<string> Producers, double RecordsPerSecond, TokenScopes Scopes, IReadOnlyList<string> Namespaces)
{
    public bool AllowsProducer(string producer) => Producers.Contains("*") || Producers.Contains(producer);
    public bool AllowsNamespace(string ns) => Namespaces.Contains("*") || Namespaces.Contains(ns);
    public bool Has(TokenScopes scope) => (Scopes & scope) == scope;
    public string DefaultNamespace => Namespaces.Count == 0 || Namespaces[0] == "*" ? NamespaceNames.Default : Namespaces[0];
}

/// <summary>
/// Bearer tokens from a JSON file (only SHA-256 hashes are stored). The file is re-read when it changes, so tokens can
/// be rotated without a restart: add the new token, move clients, remove the old one (P16-T10).
/// <code>{"tokens":[{"id":"orders-api","sha256":"…hex…","scopes":["ingest"],"namespaces":["acme/prod"],"producers":["*"],"records_per_second":0}]}</code>
/// Defaults: scopes ["ingest"], namespaces ["default"], producers ["*"], no rate limit.
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

    /// <summary>Ingest + read token for the default namespace (tests, single-tenant setups).</summary>
    public static TokenInfo Token(string id, string secret, double recordsPerSecond = 0, params string[] producers)
        => Create(id, secret, TokenScopes.Ingest | TokenScopes.Read, [NamespaceNames.Default], recordsPerSecond, producers);

    public static TokenInfo Create(string id, string secret, TokenScopes scopes, string[] namespaces, double recordsPerSecond = 0, params string[] producers)
    {
        foreach (var ns in namespaces)
            if (ns != "*" && !NamespaceNames.IsValid(ns)) throw new ArgumentException($"invalid namespace '{ns}'", nameof(namespaces));
        return new(id, SHA256.HashData(Encoding.UTF8.GetBytes(secret)),
            new HashSet<string>(producers.Length == 0 ? ["*"] : producers, StringComparer.OrdinalIgnoreCase), recordsPerSecond, scopes, namespaces);
    }

    private void Reload()
    {
        var info = new FileInfo(_path!);
        var root = JsonNode.Parse(File.ReadAllText(_path!)) ?? throw new InvalidDataException("empty token file");
        var list = new List<TokenInfo>();
        foreach (var t in root["tokens"]!.AsArray())
        {
            var id = (string)t!["id"]!;
            var producers = t["producers"]?.AsArray().Select(p => (string)p!).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? ["*"];
            var hash = Convert.FromHexString((string)t["sha256"]!);
            if (hash.Length != 32) throw new InvalidDataException($"token {id}: sha256 must be 64 hex characters");
            var scopes = TokenScopes.None;
            foreach (var s in t["scopes"]?.AsArray().Select(x => (string)x!) ?? ["ingest"])
                scopes |= s switch
                {
                    "ingest" => TokenScopes.Ingest,
                    "read" => TokenScopes.Read,
                    "admin" => TokenScopes.Admin,
                    _ => throw new InvalidDataException($"token {id}: unknown scope '{s}'"),
                };
            var namespaces = t["namespaces"]?.AsArray().Select(x => (string)x!).ToArray() ?? [NamespaceNames.Default];
            foreach (var ns in namespaces)
                if (ns != "*" && !NamespaceNames.IsValid(ns)) throw new InvalidDataException($"token {id}: invalid namespace '{ns}'");
            list.Add(new TokenInfo(id, hash, producers, (double?)t["records_per_second"] ?? 0, scopes, namespaces));
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
                    catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or System.Text.Json.JsonException or NullReferenceException or InvalidOperationException)
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

/// <summary>
/// Pagination cursors handed to clients are wrapped with an HMAC-SHA256 over (namespace, token id, expiry, engine
/// cursor) and an expiry (P17-T07): a tampered, expired or borrowed cursor is rejected instead of being interpreted.
/// The key lives in the data directory (<c>server.key</c>, created on first start).
/// </summary>
public sealed class CursorSigner(byte[] key, TimeSpan ttl)
{
    public static CursorSigner FromDataDirectory(string dataDirectory, TimeSpan ttl)
    {
        var path = Path.Combine(dataDirectory, "server.key");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(dataDirectory);
            var fresh = RandomNumberGenerator.GetBytes(32);
            try
            {
                using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.Write(fresh);
                    fs.Flush(flushToDisk: true);
                }
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); // 0600
            }
            catch (IOException) when (File.Exists(path)) { /* another instance created it first */ }
        }
        var key = File.ReadAllBytes(path);
        if (key.Length != 32) throw new InvalidDataException($"{path} must contain 32 bytes");
        return new CursorSigner(key, ttl);
    }

    public string Sign(string ns, string tokenId, string engineCursor)
    {
        var expires = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds();
        var mac = Mac(ns, tokenId, expires, engineCursor);
        return $"c1.{expires}.{engineCursor}.{mac}";
    }

    /// <summary>Returns the engine cursor, or throws <see cref="ArgumentException"/> with the reason.</summary>
    public string Verify(string ns, string tokenId, string signed)
    {
        var parts = signed.Split('.');
        if (parts.Length != 4 || parts[0] != "c1" || !long.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var expires))
            throw new ArgumentException("malformed cursor");
        var expected = Encoding.ASCII.GetBytes(Mac(ns, tokenId, expires, parts[2]));
        if (!CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(parts[3])))
            throw new ArgumentException("cursor signature does not match (tampered, or issued for another namespace or token)");
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expires) throw new ArgumentException("cursor expired");
        return parts[2];
    }

    private string Mac(string ns, string tokenId, long expires, string cursor)
    {
        var data = Encoding.UTF8.GetBytes($"{ns}\n{tokenId}\n{expires}\n{cursor}");
        return Convert.ToBase64String(HMACSHA256.HashData(key, data)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
