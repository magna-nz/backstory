using Backstory.Core;

namespace Backstory.Query;

/// <summary>
/// Fuses semantic (vector) and keyword (FTS) retrieval via Reciprocal Rank Fusion, then applies
/// structured filters. RRF needs no score calibration between the two retrievers — it ranks by
/// position — which makes the hybrid robust regardless of the embedding model in use.
/// </summary>
public sealed class HybridSearch(
    IEventStore events,
    IEmbeddingService embeddings,
    IVectorStore vectors)
{
    private const int RrfK = 60;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, EventFilter filter, int limit, CancellationToken ct = default)
    {
        var candidatePool = Math.Max(limit * 4, 20);

        var queryVector = await embeddings.EmbedAsync(query, ct);
        var semantic = await vectors.SearchAsync(queryVector, candidatePool, ct);
        var keyword = await events.SearchTextAsync(query, filter, candidatePool, ct);

        var fused = new Dictionary<string, (double Score, bool Semantic, bool Keyword)>();
        Fuse(fused, semantic.Select(h => h.EventId), markSemantic: true);
        Fuse(fused, keyword, markSemantic: false);

        // Load candidate events once, then filter (the vector retriever isn't filter-aware).
        var byId = (await events.GetManyAsync(fused.Keys, ct)).ToDictionary(e => e.Id);

        var ranked = fused
            .Where(kv => byId.ContainsKey(kv.Key) && Matches(byId[kv.Key], filter))
            .OrderByDescending(kv => kv.Value.Score)
            .Take(limit)
            .Select(kv => new SearchResult(byId[kv.Key], kv.Value.Score, MatchKind(kv.Value)))
            .ToList();

        return ranked;
    }

    private static void Fuse(
        Dictionary<string, (double Score, bool Semantic, bool Keyword)> fused,
        IEnumerable<string> rankedIds,
        bool markSemantic)
    {
        var rank = 0;
        foreach (var id in rankedIds)
        {
            rank++;
            var contribution = 1.0 / (RrfK + rank);
            var current = fused.GetValueOrDefault(id);
            fused[id] = (
                current.Score + contribution,
                current.Semantic || markSemantic,
                current.Keyword || !markSemantic);
        }
    }

    private static string MatchKind((double Score, bool Semantic, bool Keyword) entry) =>
        entry is { Semantic: true, Keyword: true } ? "both"
        : entry.Semantic ? "semantic"
        : "keyword";

    private static bool Matches(Event ev, EventFilter filter)
    {
        if (filter.From is { } from && ev.Timestamp < from) return false;
        if (filter.To is { } to && ev.Timestamp > to) return false;
        if (!string.IsNullOrWhiteSpace(filter.Source) && ev.Source != filter.Source) return false;
        if (!string.IsNullOrWhiteSpace(filter.SubType) && ev.SubType != filter.SubType) return false;
        return true;
    }
}
