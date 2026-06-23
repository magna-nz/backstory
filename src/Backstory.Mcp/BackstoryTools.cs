using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Backstory.Core;
using Backstory.Query;
using ModelContextProtocol.Server;

namespace Backstory.Mcp;

/// <summary>MCP tools that let an agent query the user's personal timeline.</summary>
[McpServerToolType]
public static class BackstoryTools
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    [McpServerTool(Name = "search_timeline")]
    [Description("Search the user's life timeline with a natural-language query. Returns ranked events with timestamp, source and text.")]
    public static async Task<string> SearchTimeline(
        HybridSearch search,
        [Description("Natural-language query, e.g. 'dinner plans with Sarah'.")] string query,
        [Description("Optional ISO-8601 lower bound on event time.")] string? from = null,
        [Description("Optional ISO-8601 upper bound on event time.")] string? to = null,
        [Description("Optional source filter: 'telegram' or 'google_takeout'.")] string? source = null,
        [Description("Max results (default 10).")] int limit = 10)
    {
        var filter = BuildFilter(from, to, source);
        var results = await search.SearchAsync(query, filter, limit);
        return Serialize(results.Select(r => new
        {
            id = r.Event.Id,
            time = r.Event.Timestamp.ToString("O"),
            r.Event.Source,
            r.Event.SubType,
            r.Event.Text,
            r.Score,
            match = r.MatchKind
        }));
    }

    [McpServerTool(Name = "get_events")]
    [Description("Fetch full event records by id, including the pointer to the original source record.")]
    public static async Task<string> GetEvents(
        IEventStore events,
        [Description("Comma-separated event ids.")] string ids)
    {
        var idList = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var found = await events.GetManyAsync(idList);
        return Serialize(found.Select(e => new
        {
            e.Id,
            time = e.Timestamp.ToString("O"),
            e.Source,
            e.SubType,
            e.Text,
            e.Latitude,
            e.Longitude,
            raw = new { e.Raw.FilePath, e.Raw.Locator }
        }));
    }

    [McpServerTool(Name = "lookup_entity")]
    [Description("Look up a person or place by name and return the matching entity.")]
    public static async Task<string> LookupEntity(
        IEntityStore entities,
        [Description("Name or alias to look up.")] string name)
    {
        var person = await entities.FindByAliasAsync(EntityKind.Person, name);
        var place = await entities.FindByAliasAsync(EntityKind.Place, name);
        var match = person ?? place;
        if (match is null) return "{\"found\":false}";
        return Serialize(new { found = true, match.Id, kind = match.Kind.ToString(), match.CanonicalName, match.Aliases });
    }

    [McpServerTool(Name = "summarize_period")]
    [Description("Return all events in a time range so the agent can summarize what happened.")]
    public static async Task<string> SummarizePeriod(
        IEventStore events,
        [Description("ISO-8601 start of range.")] string from,
        [Description("ISO-8601 end of range.")] string to,
        [Description("Optional source filter.")] string? source = null,
        [Description("Max events (default 100).")] int limit = 100)
    {
        var filter = BuildFilter(from, to, source);
        var list = new List<object>();
        await foreach (var e in events.QueryAsync(filter, limit))
            list.Add(new { time = e.Timestamp.ToString("O"), e.Source, e.SubType, e.Text });
        return Serialize(list);
    }

    [McpServerTool(Name = "list_sources")]
    [Description("List the sources ingested into this vault and how many events each contributed.")]
    public static async Task<string> ListSources(IEventStore events)
    {
        var counts = new Dictionary<string, int>();
        await foreach (var e in events.QueryAsync(new EventFilter(), int.MaxValue))
            counts[e.Source] = counts.GetValueOrDefault(e.Source) + 1;
        return Serialize(counts);
    }

    private static EventFilter BuildFilter(string? from, string? to, string? source) => new()
    {
        From = ParseDate(from),
        To = ParseDate(to),
        Source = string.IsNullOrWhiteSpace(source) ? null : source
    };

    private static DateTimeOffset? ParseDate(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts)
            ? ts
            : null;

    private static string Serialize(object value) => JsonSerializer.Serialize(value, Json);
}
