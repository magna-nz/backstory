namespace Backstory.Core;

/// <summary>
/// Parses one export format. Each adapter fully encapsulates the quirks of one source
/// (locale dates, coordinate-format variance, HTML-vs-JSON) and emits normalized items.
/// </summary>
public interface ISourceAdapter
{
    /// <summary>Stable source key, e.g. "google_takeout" or "telegram".</summary>
    string Source { get; }

    /// <summary>True if this adapter recognises the export at <paramref name="path"/>.</summary>
    bool CanHandle(string path);

    /// <summary>
    /// Streams entities and events parsed from the export. Entities a referencing event
    /// depends on should be emitted before (or alongside) the event that references them.
    /// </summary>
    IAsyncEnumerable<ImportItem> ParseAsync(string path, CancellationToken ct = default);
}

/// <summary>Produces vector embeddings locally. No network calls at embed time.</summary>
public interface IEmbeddingService
{
    /// <summary>Dimension of vectors produced by this service.</summary>
    int Dimension { get; }

    ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default);

    ValueTask<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}

/// <summary>A single vector-search result.</summary>
public readonly record struct VectorHit(string EventId, double Score);

/// <summary>Stores and searches event embeddings. v1 implementation is brute-force cosine.</summary>
public interface IVectorStore
{
    ValueTask AddAsync(string eventId, float[] vector, CancellationToken ct = default);

    ValueTask AddBatchAsync(IReadOnlyList<(string EventId, float[] Vector)> items, CancellationToken ct = default);

    ValueTask<IReadOnlyList<VectorHit>> SearchAsync(float[] query, int limit, CancellationToken ct = default);
}

/// <summary>Structured filter applied to timeline / search queries.</summary>
public sealed record EventFilter
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public string? Source { get; init; }
    public string? SubType { get; init; }
}

/// <summary>Persistence and structured/keyword retrieval for events.</summary>
public interface IEventStore
{
    ValueTask UpsertAsync(Event ev, CancellationToken ct = default);

    ValueTask<Event?> GetAsync(string id, CancellationToken ct = default);

    ValueTask<IReadOnlyList<Event>> GetManyAsync(IEnumerable<string> ids, CancellationToken ct = default);

    /// <summary>Timeline / filtered scan, newest first.</summary>
    IAsyncEnumerable<Event> QueryAsync(EventFilter filter, int limit, CancellationToken ct = default);

    /// <summary>FTS5 keyword search, returning matching event ids.</summary>
    ValueTask<IReadOnlyList<string>> SearchTextAsync(string query, EventFilter filter, int limit, CancellationToken ct = default);
}

/// <summary>Persistence and lookup for entities.</summary>
public interface IEntityStore
{
    ValueTask UpsertAsync(Entity entity, CancellationToken ct = default);

    ValueTask<Entity?> GetAsync(string id, CancellationToken ct = default);

    ValueTask<Entity?> FindByAliasAsync(EntityKind kind, string alias, CancellationToken ct = default);
}

/// <summary>A fused search result: an event plus how and how strongly it matched.</summary>
public sealed record SearchResult(Event Event, double Score, string MatchKind);
