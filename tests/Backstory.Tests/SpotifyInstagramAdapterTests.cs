using Backstory.Adapters;
using Backstory.Core;

namespace Backstory.Tests;

public class SpotifyInstagramAdapterTests
{
    private static async Task<List<ImportItem>> Collect(ISourceAdapter adapter, string path)
    {
        var items = new List<ImportItem>();
        await foreach (var item in adapter.ParseAsync(path))
            items.Add(item);
        return items;
    }

    [Fact]
    public async Task Spotify_parses_plays_podcasts_and_searches()
    {
        using var tmp = new TempDir();
        tmp.Write("Streaming_History_Audio_2023.json", """
            [
              { "ts": "2023-05-14T19:32:11Z", "ms_played": 215000,
                "master_metadata_track_name": "So What",
                "master_metadata_album_artist_name": "Miles Davis" },
              { "ts": "2023-05-15T08:00:00Z", "ms_played": 600000,
                "episode_name": "The Daily", "episode_show_name": "NYT" }
            ]
            """);
        tmp.Write("SearchQueries.json", """
            [ { "platform": "android", "searchTime": "2023-05-14T19:00:00Z", "searchQuery": "miles davis" } ]
            """);

        var adapter = new SpotifyAdapter();
        Assert.True(adapter.CanHandle(tmp.Path));
        var items = await Collect(adapter, tmp.Path);
        var events = items.OfType<EventItem>().Select(e => e.Event).ToList();
        var entities = items.OfType<EntityItem>().Select(e => e.Entity).ToList();

        Assert.Contains(events, e => e.SubType == "music_play" && e.Text.Contains("So What") && e.Text.Contains("Miles Davis"));
        Assert.Contains(events, e => e.SubType == "podcast_play" && e.Text.Contains("The Daily"));
        Assert.Contains(events, e => e.SubType == "spotify_search" && e.Text.Contains("miles davis"));
        Assert.Contains(entities, e => e.Kind == EntityKind.Org && e.CanonicalName == "Miles Davis");
    }

    [Fact]
    public async Task Spotify_parses_account_data_streaming_history()
    {
        using var tmp = new TempDir();
        tmp.Write("StreamingHistory_music_0.json", """
            [ { "endTime": "2023-05-14 19:32", "artistName": "Radiohead", "trackName": "Reckoner", "msPlayed": 290000 } ]
            """);

        var events = (await Collect(new SpotifyAdapter(), tmp.Path)).OfType<EventItem>().Select(e => e.Event).ToList();
        Assert.Contains(events, e => e.SubType == "music_play" && e.Text.Contains("Reckoner") && e.Text.Contains("Radiohead"));
    }

    [Fact]
    public async Task Instagram_parses_dms_and_repairs_encoding()
    {
        using var tmp = new TempDir();
        // sender + a message whose text is Instagram's Latin-1-encoded UTF-8 ("café" stored as "cafÃ©").
        tmp.Write(Path.Combine("inbox", "alex_123", "message_1.json"), """
            {
              "participants": [ { "name": "Alex" }, { "name": "You" } ],
              "title": "Alex",
              "messages": [
                { "sender_name": "Alex", "timestamp_ms": 1710444720000, "content": "see you at the cafÃ©" },
                { "sender_name": "You",  "timestamp_ms": 1710444780000, "content": "sounds good" },
                { "sender_name": "Alex", "timestamp_ms": 1710444900000 }
              ]
            }
            """);

        var adapter = new InstagramAdapter();
        Assert.True(adapter.CanHandle(tmp.Path));
        var items = await Collect(adapter, tmp.Path);
        var events = items.OfType<EventItem>().Select(e => e.Event).ToList();
        var entities = items.OfType<EntityItem>().Select(e => e.Entity).ToList();

        Assert.Equal(2, events.Count); // the content-less message is skipped
        Assert.Contains(events, e => e.SubType == "instagram_message" && e.Text == "see you at the café");
        Assert.Contains(entities, e => e.Kind == EntityKind.Person && e.CanonicalName == "Alex");
    }

    [Fact]
    public async Task Instagram_parses_posts_comments_and_searches()
    {
        using var tmp = new TempDir();
        tmp.Write(Path.Combine("content", "posts_1.json"), """
            [ { "media": [ { "uri": "x.jpg", "creation_timestamp": 1710444720 } ],
                "title": "sunset at the beach", "creation_timestamp": 1710444720 } ]
            """);
        tmp.Write(Path.Combine("comments", "post_comments_1.json"), """
            [ { "string_map_data": { "Comment": { "value": "love this", "timestamp": 1710444800 },
                                     "Media Owner": { "value": "friend" } } } ]
            """);
        tmp.Write(Path.Combine("searches", "word_or_phrase_searches.json"), """
            { "searches_keyword": [ { "string_map_data": { "Search": { "value": "ramen tokyo", "timestamp": 1710444900 } } } ] }
            """);

        var events = (await Collect(new InstagramAdapter(), tmp.Path)).OfType<EventItem>().Select(e => e.Event).ToList();

        Assert.Contains(events, e => e.SubType == "instagram_post" && e.Text.Contains("sunset"));
        Assert.Contains(events, e => e.SubType == "instagram_comment" && e.Text.Contains("love this"));
        Assert.Contains(events, e => e.SubType == "instagram_search" && e.Text.Contains("ramen tokyo"));
    }
}
