using Backstory.Adapters;
using Backstory.Core;
using Backstory.Query;

namespace Backstory.Tests;

public class PipelineTests
{
    [Fact]
    public async Task Import_then_hybrid_search_finds_the_message()
    {
        using var vault = new TestVault();
        using var tmp = new TempDir();
        var file = tmp.Write("result.json", """
            {
              "chats": { "list": [ {
                "name": "Sarah K", "type": "personal_chat", "id": 1,
                "messages": [
                  { "id": 1, "type": "message", "date_unixtime": "1710444720",
                    "from": "Sarah", "from_id": "u1", "text": "see you at Dishoom for dinner 8pm" },
                  { "id": 2, "type": "message", "date_unixtime": "1710000000",
                    "from": "Sarah", "from_id": "u1", "text": "did you file the tax return yet?" }
                ]
              } ] }
            }
            """);

        var pipeline = new IngestionPipeline(vault.Events, vault.Entities, vault.Embeddings, vault.Vectors);
        var stats = await pipeline.ImportAsync(new TelegramAdapter(), file);

        Assert.Equal(2, stats.Events);
        Assert.Equal(2, stats.BySubType["telegram_message"]);

        var search = new HybridSearch(vault.Events, vault.Embeddings, vault.Vectors);
        var results = await search.SearchAsync("dinner at dishoom", new EventFilter(), 5);

        Assert.NotEmpty(results);
        Assert.Contains("Dishoom", results[0].Event.Text);
    }

    [Fact]
    public async Task Multiword_query_engages_the_keyword_side()
    {
        using var vault = new TestVault();
        using var tmp = new TempDir();
        var file = tmp.Write("result.json", """
            { "chats": { "list": [ { "name": "c", "type": "personal_chat", "id": 1, "messages": [
              { "id": 1, "type": "message", "date_unixtime": "1710444720", "from": "A", "from_id": "u1", "text": "lets get dinner at Dishoom on friday" }
            ] } ] } }
            """);

        var pipeline = new IngestionPipeline(vault.Events, vault.Entities, vault.Embeddings, vault.Vectors);
        await pipeline.ImportAsync(new TelegramAdapter(), file);

        var search = new HybridSearch(vault.Events, vault.Embeddings, vault.Vectors);
        var results = await search.SearchAsync("dinner plans at dishoom", new EventFilter(), 5);

        // The exact terms "dinner"/"dishoom" must hit FTS, so the match is keyword or both — not semantic-only.
        Assert.Contains(results, r => r.MatchKind is "keyword" or "both");
    }

    [Fact]
    public async Task Reimport_is_idempotent()
    {
        using var vault = new TestVault();
        using var tmp = new TempDir();
        var file = tmp.Write("result.json", """
            { "chats": { "list": [ { "name": "c", "type": "personal_chat", "id": 1,
              "messages": [ { "id": 1, "type": "message", "date_unixtime": "1710444720",
                "from": "A", "from_id": "u1", "text": "hello world" } ] } ] } }
            """);

        var pipeline = new IngestionPipeline(vault.Events, vault.Entities, vault.Embeddings, vault.Vectors);
        await pipeline.ImportAsync(new TelegramAdapter(), file);
        await pipeline.ImportAsync(new TelegramAdapter(), file);

        var all = new List<Event>();
        await foreach (var ev in vault.Events.QueryAsync(new EventFilter(), 100))
            all.Add(ev);

        Assert.Single(all); // same content hash → no duplicate
    }

    [Fact]
    public async Task Search_respects_date_filter()
    {
        using var vault = new TestVault();
        using var tmp = new TempDir();
        var file = tmp.Write("result.json", """
            { "chats": { "list": [ { "name": "c", "type": "personal_chat", "id": 1, "messages": [
              { "id": 1, "type": "message", "date_unixtime": "1710444720", "from": "A", "from_id": "u1", "text": "ramen now" },
              { "id": 2, "type": "message", "date_unixtime": "1262304000", "from": "A", "from_id": "u1", "text": "ramen long ago" }
            ] } ] } }
            """);

        var pipeline = new IngestionPipeline(vault.Events, vault.Entities, vault.Embeddings, vault.Vectors);
        await pipeline.ImportAsync(new TelegramAdapter(), file);

        var search = new HybridSearch(vault.Events, vault.Embeddings, vault.Vectors);
        var recent = await search.SearchAsync("ramen", new EventFilter { From = DateTimeOffset.Parse("2020-01-01T00:00:00Z") }, 5);

        Assert.All(recent, r => Assert.True(r.Event.Timestamp >= DateTimeOffset.Parse("2020-01-01T00:00:00Z")));
        Assert.Contains(recent, r => r.Event.Text == "ramen now");
        Assert.DoesNotContain(recent, r => r.Event.Text == "ramen long ago");
    }
}
