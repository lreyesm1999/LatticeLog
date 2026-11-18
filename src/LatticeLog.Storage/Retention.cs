using System.Text.Json.Nodes;

namespace LatticeLog.Storage;

/// <summary>
/// Explicit, opt-in retention (P24 / server admin): whole sealed segments are deleted when every record in them is
/// older than <see cref="MaxAge"/>, and/or the oldest sealed segments while the store exceeds <see cref="MaxBytes"/>.
/// The active segment is never touched. Quotas still never delete anything on their own: this only runs when a
/// policy is configured or requested.
/// </summary>
public sealed record RetentionPolicy
{
    /// <summary>Delete sealed segments whose newest record is older than now − MaxAge.</summary>
    public TimeSpan? MaxAge { get; init; }
    /// <summary>Delete the oldest sealed segments until the store is at most this size.</summary>
    public long? MaxBytes { get; init; }

    public bool IsEmpty => MaxAge is null && MaxBytes is null;
}

public sealed record RetainedSegmentDeletion(ulong SegmentId, string Reason, long Records, long Bytes, long MaxTimestampUnixNanos, ulong FirstSequence, ulong LastSequence);

public sealed record RetentionResult(IReadOnlyList<RetainedSegmentDeletion> Deleted, bool DryRun)
{
    public long Records => Deleted.Sum(d => d.Records);
    public long Bytes => Deleted.Sum(d => d.Bytes);
}

public static class Retention
{
    /// <summary>
    /// Plans deletions in sequence order (oldest first). <paramref name="eligible"/> excludes segments the caller is
    /// still writing or sealing. Segments without a readable footer are never deleted by retention.
    /// </summary>
    public static List<RetainedSegmentDeletion> Plan(StoreReader reader, RetentionPolicy policy, long nowUnixNanos, Func<ulong, bool> eligible)
    {
        var plan = new List<RetainedSegmentDeletion>();
        if (policy.IsEmpty) return plan;
        var candidates = new List<(ulong Id, Format.SegmentIndex Index, long Bytes)>();
        long total = 0;
        foreach (var id in reader.SegmentIds)
        {
            long bytes;
            try { bytes = new FileInfo(reader.SegmentPath(id)).Length; } catch (IOException) { continue; }
            total += bytes;
            if (!eligible(id) || reader.Indexes.GetValueOrDefault(id) is not { } idx) continue;
            candidates.Add((id, idx, bytes));
        }
        var chosen = new HashSet<ulong>();
        if (policy.MaxAge is { } age)
        {
            var cutoff = nowUnixNanos - age.Ticks * 100;
            foreach (var c in candidates)
                if (!c.Index.HasRecords || c.Index.MaxTimestamp < cutoff)
                {
                    chosen.Add(c.Id);
                    plan.Add(new RetainedSegmentDeletion(c.Id, c.Index.HasRecords ? $"max_age {age}" : "empty", c.Index.RecordCount, c.Bytes, c.Index.MaxTimestamp, c.Index.FirstSequence, c.Index.LastSequence));
                    total -= c.Bytes;
                }
        }
        if (policy.MaxBytes is { } maxBytes)
        {
            foreach (var c in candidates) // already ordered by first sequence: oldest first
            {
                if (total <= maxBytes) break;
                if (chosen.Contains(c.Id)) continue;
                chosen.Add(c.Id);
                plan.Add(new RetainedSegmentDeletion(c.Id, $"max_bytes {maxBytes}", c.Index.RecordCount, c.Bytes, c.Index.MaxTimestamp, c.Index.FirstSequence, c.Index.LastSequence));
                total -= c.Bytes;
            }
        }
        return plan;
    }

    /// <summary>
    /// Journals then deletes each planned segment: "segment_deleted" is appended (fsync) first, so readers stop seeing
    /// the segment even if the process dies before the file is removed (recovery removes the leftover).
    /// </summary>
    internal static void Execute(string root, IEnumerable<RetainedSegmentDeletion> plan)
    {
        foreach (var d in plan)
        {
            Manifest.Append(root, new JsonObject
            {
                ["op"] = "segment_deleted",
                ["id"] = d.SegmentId,
                ["reason"] = d.Reason,
                ["records"] = d.Records,
                ["bytes"] = d.Bytes,
                ["first_seq"] = d.FirstSequence,
                ["last_seq"] = d.LastSequence,
                ["max_time"] = UnixTime.FormatRfc3339(d.MaxTimestampUnixNanos),
            });
            try { File.Delete(StoreLayout.SegmentPath(root, d.SegmentId)); }
            catch (IOException) { /* still open somewhere: recovery deletes it, readers already ignore it */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
