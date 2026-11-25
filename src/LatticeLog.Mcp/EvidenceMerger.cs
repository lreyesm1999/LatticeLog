using System.Text;
using System.Text.Json.Nodes;
using LatticeLog.Evidence;
using LatticeLog.Storage.Json;

namespace LatticeLog.Mcp;

/// <summary>
/// Merges evidence documents (latticelog.evidence.v1) of several sources into one: coverage numbers are summed and
/// flags OR-ed, timelines are merged by time (dt_us recomputed from the merged start), groups are ranked by count,
/// count tables are added up, and every timeline entry, group and schema entry carries <c>source</c>. A single
/// document is returned with the tags added and nothing else changed.
/// </summary>
internal static class EvidenceMerger
{
    private static readonly string[] TaggedArrays = ["timeline", "groups", "events", "relations"];

    public static JsonObject Merge(IReadOnlyList<(string Source, JsonObject Doc)> docs, int top)
    {
        foreach (var (src, doc) in docs) Tag(doc, src);
        if (docs.Count == 1)
        {
            var single = docs[0].Doc;
            single.Insert(3, "source", docs[0].Source);
            return single;
        }
        var merged = docs[0].Doc.DeepClone().AsObject();
        merged.Remove("store_id");
        merged.Insert(2, "sources", new JsonArray(docs.Select(d => (JsonNode?)new JsonObject { ["name"] = d.Source, ["store_id"] = (string?)d.Doc["store_id"] }).ToArray()));
        merged["coverage"] = MergeCoverage(docs.Select(d => d.Doc["coverage"] as JsonObject).OfType<JsonObject>().ToList());
        if (docs.Any(d => d.Doc["summary"] is JsonObject))
            merged["summary"] = MergeSummary(docs.Select(d => d.Doc["summary"] as JsonObject).OfType<JsonObject>().ToList());
        foreach (var name in TaggedArrays.Append("evidence_refs"))
        {
            if (!docs.Any(d => d.Doc[name] is JsonArray)) continue;
            var all = docs.SelectMany(d => (d.Doc[name] as JsonArray ?? []).Select(x => x!.DeepClone())).ToList();
            all = name switch
            {
                "timeline" => Retime(all),
                "groups" => all.OrderByDescending(g => (long?)g!["count"] ?? 0).ThenBy(g => (string?)g!["source"], StringComparer.Ordinal).Take(top).ToList(),
                "evidence_refs" => all.Distinct(JsonNodeDeepComparer.Instance).ToList(),
                _ => all,
            };
            merged[name] = new JsonArray(all.ToArray());
        }
        // Per-source context that does not merge (anchor, grouping notes) is kept under its source.
        var notes = new JsonObject();
        foreach (var (src, doc) in docs)
            if (doc["grouping_scope"] is { } gs) notes[src] = gs.DeepClone();
        if (notes.Count > 0) merged["grouping_scope"] = notes;
        merged["merge"] = "results of several sources: counts are summed, timelines merged by time, groups ranked by count; 'source' tags each item";
        return merged;
    }

    /// <summary>Enforces the complete structured-payload budget after multiple per-source documents are merged.</summary>
    public static void EnforceBudget(JsonObject doc, int budgetBytes)
    {
        budgetBytes = Math.Clamp(budgetBytes, 2_000, 10_000_000);
        var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        int Size() => Encoding.UTF8.GetByteCount(doc.ToJsonString(options));
        while (Size() > budgetBytes)
        {
            if (doc["timeline"] is JsonArray timeline && timeline.Count > 0)
            {
                var remove = LowestPriorityIndex(timeline);
                var reference = (string?)timeline[remove]?["ref"];
                timeline.RemoveAt(remove);
                if (reference is not null && doc["evidence_refs"] is JsonArray refs)
                {
                    var ri = refs.Select((n, i) => ((string?)n, i)).FirstOrDefault(x => x.Item1 == reference).i;
                    if (ri >= 0 && ri < refs.Count && (string?)refs[ri] == reference) refs.RemoveAt(ri);
                }
                AdjustCoverage(doc);
                continue;
            }
            if (doc["groups"] is JsonArray groups && groups.Count > 0) { groups.RemoveAt(groups.Count - 1); continue; }
            if (doc["relations"] is JsonArray relations && relations.Count > 0) { relations.RemoveAt(relations.Count - 1); continue; }
            if (doc["changes"]?["first_fingerprint_occurrences"] is JsonArray changes && changes.Count > 0) { changes.RemoveAt(changes.Count - 1); continue; }
            if (doc["summary"] is JsonObject summary && summary.Remove("by_event")) continue;
            if (doc["summary"] is JsonObject summary2 && summary2.Remove("by_service")) continue;
            if (doc.Remove("changes")) continue;
            if (doc.Remove("selection")) continue;
            if (doc.Remove("execution")) continue;
            if (doc["summary"] is JsonObject summary3 && summary3.Remove("trace_ids")) continue;
            if (doc["summary"] is JsonObject summary4 && summary4.Remove("distribution_scope")) continue;
            if (doc["summary"] is JsonObject summary5 && summary5.Remove("ordering_note")) continue;
            if (doc["evidence_refs"] is JsonArray remainingRefs && remainingRefs.Count > 0)
            {
                remainingRefs.RemoveAt(remainingRefs.Count - 1);
                AdjustCoverage(doc);
                continue;
            }
            break;
        }
    }

    private static int LowestPriorityIndex(JsonArray timeline)
    {
        static int Severity(JsonNode? n) => ((string?)n?["sev"])?.ToLowerInvariant() switch
        {
            "fatal" => 6,
            "error" => 5,
            "warn" => 4,
            "info" => 3,
            "debug" => 2,
            "trace" => 1,
            _ => 0,
        };
        var best = 0;
        for (var i = 1; i < timeline.Count; i++)
            if (Severity(timeline[i]) <= Severity(timeline[best])) best = i;
        return best;
    }

    private static void AdjustCoverage(JsonObject doc)
    {
        if (doc["coverage"] is not JsonObject coverage) return;
        var returned = Math.Max(0, ((int?)coverage["returned_events"] ?? 0) - 1);
        coverage["returned_events"] = returned;
        if (coverage["matched_events"] is JsonValue matched && matched.TryGetValue<long>(out var total)) coverage["omitted_events"] = Math.Max(0, total - returned);
        coverage["truncated"] = true;
        coverage["exhaustive"] = "no";
        coverage["sampling"] = EvidenceService.SelectionAlgorithm;
    }

    private sealed class JsonNodeDeepComparer : IEqualityComparer<JsonNode>
    {
        public static readonly JsonNodeDeepComparer Instance = new();
        public bool Equals(JsonNode? x, JsonNode? y) => JsonNode.DeepEquals(x, y);
        public int GetHashCode(JsonNode obj) => obj.ToJsonString().GetHashCode(StringComparison.Ordinal);
    }

    private static void Tag(JsonObject doc, string source)
    {
        foreach (var name in TaggedArrays)
            if (doc[name] is JsonArray arr)
                foreach (var item in arr.OfType<JsonObject>())
                    if (!item.ContainsKey("source")) item.Insert(0, "source", source);
    }

    private static List<JsonNode> Retime(List<JsonNode> entries)
    {
        long T(JsonNode e) => UnixTime.TryParseRfc3339((string?)e["t"] ?? "", out var t) ? t : 0;
        var ordered = entries.OrderBy(T).ThenBy(e => (string?)e["source"], StringComparer.Ordinal).ToList();
        if (ordered.Count == 0) return ordered;
        var t0 = T(ordered[0]);
        foreach (var e in ordered.OfType<JsonObject>()) if (e.ContainsKey("dt_us")) e["dt_us"] = (T(e) - t0) / 1000;
        return ordered;
    }

    private static JsonObject MergeCoverage(List<JsonObject> covs)
    {
        var o = new JsonObject();
        bool truncated = false, partial = false;
        var reasons = new List<string>();
        var sampling = "none";
        foreach (var c in covs)
        {
            truncated |= (bool?)c["truncated"] ?? false;
            partial |= (bool?)c["partial_results"] ?? false;
            if ((string?)c["partial_reason"] is { } r) reasons.Add(r);
            if ((string?)c["sampling"] is { } s && s != "none") sampling = s;
        }
        var unknownMatched = covs.Any(c => c["matched_events"] is JsonValue v && v.TryGetValue<string>(out _));
        if (unknownMatched) o["matched_events"] = "more than the candidate limit";
        else o["matched_events"] = covs.Sum(c => (long?)c["matched_events"] ?? 0);
        o["returned_events"] = covs.Sum(c => (long?)c["returned_events"] ?? 0);
        if (!unknownMatched) o["omitted_events"] = covs.Sum(c => (long?)c["omitted_events"] ?? 0);
        o["truncated"] = truncated;
        o["partial_results"] = partial;
        if (reasons.Count > 0) o["partial_reason"] = string.Join("; ", reasons);
        o["sampling"] = sampling;
        foreach (var k in new[] { "budget_bytes", "timeline_bytes", "approx_tokens_chars_div_4" })
            if (covs.Any(c => c.ContainsKey(k))) o[k] = covs.Sum(c => (long?)c[k] ?? 0);
        o["exhaustive"] = truncated || partial ? "no" : "yes";
        return o;
    }

    private static JsonObject MergeSummary(List<JsonObject> sums)
    {
        var o = new JsonObject();
        foreach (var s in sums)
        {
            foreach (var (k, v) in s)
            {
                switch (v)
                {
                    case JsonObject counts:
                        {
                            var target = o[k] as JsonObject ?? (JsonObject)(o[k] = new JsonObject());
                            foreach (var (ck, cv) in counts) target[ck] = ((long?)target[ck] ?? 0) + ((long?)cv ?? 0);
                            break;
                        }
                    case JsonArray arr:
                        {
                            var target = o[k] as JsonArray ?? (JsonArray)(o[k] = new JsonArray());
                            foreach (var x in arr)
                                if (target.Count < 20 && !target.Any(t => JsonNode.DeepEquals(t, x))) target.Add(x?.DeepClone());
                            break;
                        }
                    case JsonValue val when k == "first_time":
                        if (o[k] is null || string.CompareOrdinal((string?)val, (string?)o[k]) < 0) o[k] = val.DeepClone();
                        break;
                    case JsonValue val when k == "last_time":
                        if (o[k] is null || string.CompareOrdinal((string?)val, (string?)o[k]) > 0) o[k] = val.DeepClone();
                        break;
                    case JsonValue val when val.TryGetValue<bool>(out var b):
                        o[k] = ((bool?)o[k] ?? true) && b;
                        break;
                    case JsonValue val when val.TryGetValue<string>(out _):
                        o[k] ??= val.DeepClone();
                        break;
                    case JsonValue val:
                        {
                            var sum = ((double?)o[k] ?? 0) + (double)val;
                            o[k] = double.IsInteger(sum) && Math.Abs(sum) < 9e15 ? JsonValue.Create((long)sum) : JsonValue.Create(sum);
                            break;
                        }
                }
            }
        }
        // Re-sort count tables (largest first) so 'top_*' stay meaningful after adding up.
        foreach (var (k, v) in o.ToList())
            if (v is JsonObject counts && k != "by_severity")
                o[k] = new JsonObject(counts.Select(kv => (kv.Key, (long?)kv.Value ?? 0)).OrderByDescending(x => x.Item2).ThenBy(x => x.Key, StringComparer.Ordinal)
                    .Select(x => KeyValuePair.Create(x.Key, (JsonNode?)x.Item2)));
        if (UnixTime.TryParseRfc3339((string?)o["first_time"] ?? "", out var f) && UnixTime.TryParseRfc3339((string?)o["last_time"] ?? "", out var l))
            o["duration_ms"] = (l - f) / 1_000_000.0;
        return o;
    }
}
