using System.Text.Json;

namespace Backstory.Core;

/// <summary>Kind of a referenced entity.</summary>
public enum EntityKind
{
    Person,
    Place,
    Org
}

/// <summary>Pointer back to the untouched source record, so nothing is ever lossy.</summary>
/// <param name="FilePath">Path to the original file within the import store.</param>
/// <param name="Locator">A JSON-pointer, line number, or byte offset locating the record in the file.</param>
public sealed record RawRef(string FilePath, string Locator);

/// <summary>One thing that happened, at a time, from a source.</summary>
public sealed record Event
{
    public required string Id { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string Source { get; init; }
    public required string SubType { get; init; }
    public required string Text { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public IReadOnlyList<string> ActorIds { get; init; } = [];
    public string? PlaceId { get; init; }
    public required RawRef Raw { get; init; }
    public JsonElement Metadata { get; init; }
}

/// <summary>A person, place, or org referenced across events.</summary>
public sealed record Entity
{
    public required string Id { get; init; }
    public required EntityKind Kind { get; init; }
    public required string CanonicalName { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public JsonElement Metadata { get; init; }
}

/// <summary>An item produced by an adapter during parsing: either an entity or an event.</summary>
public abstract record ImportItem;

public sealed record EntityItem(Entity Entity) : ImportItem;

public sealed record EventItem(Event Event) : ImportItem;

/// <summary>
/// A diagnostic an adapter emits when it skips something (an unreadable file, a record missing a
/// required field). The pipeline aggregates these by <paramref name="Category"/> so the user can see
/// what was left out and why, instead of data being dropped silently.
/// </summary>
public sealed record Notice(string Category) : ImportItem;
