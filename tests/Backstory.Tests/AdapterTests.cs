using Backstory.Adapters;
using Backstory.Core;

namespace Backstory.Tests;

public class AdapterTests
{
    private static async Task<List<ImportItem>> Collect(ISourceAdapter adapter, string path)
    {
        var items = new List<ImportItem>();
        await foreach (var item in adapter.ParseAsync(path))
            items.Add(item);
        return items;
    }

    [Fact]
    public async Task Telegram_parses_messages_contacts_and_senders()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("result.json", TelegramFixture);
        var adapter = new TelegramAdapter();

        Assert.True(adapter.CanHandle(file));
        var items = await Collect(adapter, file);

        var events = items.OfType<EventItem>().Select(e => e.Event).ToList();
        var entities = items.OfType<EntityItem>().Select(e => e.Entity).ToList();

        // One real message (the service message is skipped).
        Assert.Single(events);
        Assert.Contains("Dishoom", events[0].Text);
        Assert.Equal("telegram_message", events[0].SubType);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1710444720), events[0].Timestamp);

        // Contact + message sender.
        Assert.Contains(entities, e => e.CanonicalName == "Sarah K" && e.Kind == EntityKind.Person);
        Assert.Single(events[0].ActorIds);
    }

    [Fact]
    public async Task Takeout_parses_search_and_youtube_history()
    {
        using var tmp = new TempDir();
        tmp.Write(Path.Combine("My Activity", "Search", "MyActivity.json"), SearchFixture);
        tmp.Write(Path.Combine("YouTube and YouTube Music", "history", "watch-history.json"), YouTubeFixture);

        var adapter = new GoogleTakeoutAdapter();
        Assert.True(adapter.CanHandle(tmp.Path));
        var events = (await Collect(adapter, tmp.Path)).OfType<EventItem>().Select(e => e.Event).ToList();

        Assert.Contains(events, e => e.SubType == "search_query" && e.Text.Contains("ramen"));
        Assert.Contains(events, e => e.SubType == "youtube_watch" && e.Text.Contains("Ramen"));
        Assert.Contains(events, e => e.Text.Contains("ChefChannel")); // channel folded into text
    }

    private const string TelegramFixture = """
        {
          "about": "export",
          "contacts": { "list": [ { "first_name": "Sarah", "last_name": "K", "phone_number": "+15551234" } ] },
          "chats": { "list": [ {
            "name": "Sarah K", "type": "personal_chat", "id": 1,
            "messages": [
              { "id": 1, "type": "message", "date": "2024-03-14T19:32:00", "date_unixtime": "1710444720",
                "from": "Sarah", "from_id": "user12345", "text": "see you at Dishoom 8pm" },
              { "id": 2, "type": "service", "date": "2024-03-14T19:33:00", "date_unixtime": "1710444780",
                "action": "pin_message" }
            ]
          } ] }
        }
        """;

    private const string SearchFixture = """
        [
          { "header": "Search", "title": "Searched for ramen near me", "time": "2024-03-10T12:00:00.000Z" },
          { "header": "Maps", "title": "Viewed area", "time": "2024-03-10T12:05:00.000Z" }
        ]
        """;

    private const string YouTubeFixture = """
        [
          { "header": "YouTube", "title": "Watched How to make Ramen", "time": "2024-03-11T12:00:00Z",
            "subtitles": [ { "name": "ChefChannel" } ] }
        ]
        """;
}
