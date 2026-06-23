using System.Text.Json;
using Backstory.Core;

namespace Backstory.Tests;

public class StorageTests
{
    private static Event SampleEvent(string id, string text, DateTimeOffset ts, string source = "telegram") => new()
    {
        Id = id,
        Timestamp = ts,
        Source = source,
        SubType = "telegram_message",
        Text = text,
        Raw = new RawRef("f", "loc"),
        Metadata = default
    };

    [Fact]
    public async Task Event_round_trips_through_store()
    {
        using var vault = new TestVault();
        var ev = SampleEvent("e1", "see you at Dishoom", DateTimeOffset.Parse("2024-03-14T19:32:00Z"));

        await vault.Events.UpsertAsync(ev);
        var loaded = await vault.Events.GetAsync("e1");

        Assert.NotNull(loaded);
        Assert.Equal("see you at Dishoom", loaded!.Text);
        Assert.Equal(ev.Timestamp, loaded.Timestamp);
    }

    [Fact]
    public async Task Upsert_is_idempotent_on_same_id()
    {
        using var vault = new TestVault();
        var ev = SampleEvent("e1", "hello", DateTimeOffset.UtcNow);
        await vault.Events.UpsertAsync(ev);
        await vault.Events.UpsertAsync(ev with { Text = "hello again" });

        var loaded = await vault.Events.GetAsync("e1");
        Assert.Equal("hello again", loaded!.Text);

        // FTS should reflect the latest text, not duplicate.
        var hits = await vault.Events.SearchTextAsync("again", new EventFilter(), 10);
        Assert.Equal(["e1"], hits);
    }

    [Fact]
    public async Task Fts_search_honours_source_filter()
    {
        using var vault = new TestVault();
        await vault.Events.UpsertAsync(SampleEvent("t1", "ramen dinner", DateTimeOffset.UtcNow, "telegram"));
        await vault.Events.UpsertAsync(SampleEvent("g1", "ramen recipe", DateTimeOffset.UtcNow, "google_takeout"));

        var all = await vault.Events.SearchTextAsync("ramen", new EventFilter(), 10);
        var googleOnly = await vault.Events.SearchTextAsync("ramen", new EventFilter { Source = "google_takeout" }, 10);

        Assert.Equal(2, all.Count);
        Assert.Equal(["g1"], googleOnly);
    }

    [Fact]
    public async Task Vector_store_finds_nearest()
    {
        using var vault = new TestVault();
        var a = await vault.Embeddings.EmbedAsync("ramen noodles tokyo");
        var b = await vault.Embeddings.EmbedAsync("quarterly tax return");
        await vault.Vectors.AddAsync("a", a);
        await vault.Vectors.AddAsync("b", b);

        var query = await vault.Embeddings.EmbedAsync("ramen in japan");
        var hits = await vault.Vectors.SearchAsync(query, 1);

        Assert.Single(hits);
        Assert.Equal("a", hits[0].EventId);
    }

    [Fact]
    public async Task Entity_lookup_by_alias_is_case_insensitive()
    {
        using var vault = new TestVault();
        await vault.Entities.UpsertAsync(new Entity
        {
            Id = "p1",
            Kind = EntityKind.Person,
            CanonicalName = "Sarah K",
            Aliases = ["+15551234"]
        });

        var byName = await vault.Entities.FindByAliasAsync(EntityKind.Person, "sarah k");
        var byPhone = await vault.Entities.FindByAliasAsync(EntityKind.Person, "+15551234");

        Assert.Equal("p1", byName!.Id);
        Assert.Equal("p1", byPhone!.Id);
    }
}
