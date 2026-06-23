using Backstory.Core;

namespace Backstory.Query;

public sealed record ImportStats
{
    public int Events { get; init; }
    public int Entities { get; init; }
    public IReadOnlyDictionary<string, int> BySubType { get; init; } = new Dictionary<string, int>();
}

/// <summary>
/// Drives an adapter's parsed stream into the stores: entities are upserted, events are embedded
/// and persisted alongside their vector. Deterministic ids make re-imports idempotent.
/// </summary>
public sealed class IngestionPipeline(
    IEventStore events,
    IEntityStore entities,
    IEmbeddingService embeddings,
    IVectorStore vectors)
{
    public async Task<ImportStats> ImportAsync(ISourceAdapter adapter, string path, CancellationToken ct = default)
    {
        var eventCount = 0;
        var entityCount = 0;
        var bySubType = new Dictionary<string, int>();

        await foreach (var item in adapter.ParseAsync(path, ct))
        {
            ct.ThrowIfCancellationRequested();
            switch (item)
            {
                case EntityItem(var entity):
                    await entities.UpsertAsync(entity, ct);
                    entityCount++;
                    break;

                case EventItem(var ev):
                    var vector = await embeddings.EmbedAsync(ev.Text, ct);
                    await events.UpsertAsync(ev, ct);
                    await vectors.AddAsync(ev.Id, vector, ct);
                    eventCount++;
                    bySubType[ev.SubType] = bySubType.GetValueOrDefault(ev.SubType) + 1;
                    break;
            }
        }

        return new ImportStats { Events = eventCount, Entities = entityCount, BySubType = bySubType };
    }
}
