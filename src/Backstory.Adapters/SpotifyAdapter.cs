using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Backstory.Core;

namespace Backstory.Adapters;

/// <summary>
/// Parses a Spotify "Download your data" export. Handles both the extended streaming history
/// (Streaming_History_Audio_*.json / endsong_*.json) and the account-data history
/// (StreamingHistory*.json), plus search queries. Each play becomes one event.
/// </summary>
public sealed class SpotifyAdapter : ISourceAdapter
{
    public string Source => "spotify";

    public bool CanHandle(string path)
    {
        if (!Directory.Exists(path)) return false;
        return Files(path, "Streaming_History_Audio_*.json").Any()
            || Files(path, "endsong_*.json").Any()
            || Files(path, "StreamingHistory*.json").Any()
            || Files(path, "SearchQueries.json").Any();
    }

    public async IAsyncEnumerable<ImportItem> ParseAsync(string path, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var file in Files(path, "Streaming_History_Audio_*.json").Concat(Files(path, "endsong_*.json")))
            await foreach (var item in ParseExtended(file, ct)) yield return item;

        foreach (var file in Files(path, "StreamingHistory*.json"))
            await foreach (var item in ParseAccountHistory(file, ct)) yield return item;

        foreach (var file in Files(path, "SearchQueries.json"))
            await foreach (var item in ParseSearches(file, ct)) yield return item;
    }

    // Extended history: { ts, ms_played, master_metadata_track_name, master_metadata_album_artist_name, episode_name, ... }
    // The extended history can cover years of plays, so it is streamed.
    private async IAsyncEnumerable<ImportItem> ParseExtended(string file, [EnumeratorCancellation] CancellationToken ct)
    {
        var index = 0;
        await foreach (var record in JsonStream.ArrayAsync(file, ct))
        {
            index++;
            if (ParseTime(record.Str("ts")) is not { } ts) continue;

            var track = record.Str("master_metadata_track_name");
            var artist = record.Str("master_metadata_album_artist_name");
            if (!string.IsNullOrWhiteSpace(track))
            {
                foreach (var item in Play(ts, track!, artist, file, index)) yield return item;
                continue;
            }

            var episode = record.Str("episode_name");
            var show = record.Str("episode_show_name");
            if (!string.IsNullOrWhiteSpace(episode))
            {
                yield return new EventItem(new Event
                {
                    Id = Ids.ContentHash("spotify_podcast", episode, ts.ToString("O")),
                    Timestamp = ts,
                    Source = Source,
                    SubType = "podcast_play",
                    Text = show is null ? $"Listened to {episode}" : $"Listened to {episode} on {show}",
                    Raw = new RawRef(file, $"index={index}")
                });
            }
        }
    }

    // Account-data history: { endTime: "2023-05-14 19:32", artistName, trackName, msPlayed }
    private async IAsyncEnumerable<ImportItem> ParseAccountHistory(string file, [EnumeratorCancellation] CancellationToken ct)
    {
        var index = 0;
        await foreach (var record in JsonStream.ArrayAsync(file, ct))
        {
            index++;
            var track = record.Str("trackName");
            if (string.IsNullOrWhiteSpace(track)) continue;
            if (ParseTime(record.Str("endTime")) is not { } ts) continue;
            foreach (var item in Play(ts, track!, record.Str("artistName"), file, index)) yield return item;
        }
    }

    private async IAsyncEnumerable<ImportItem> ParseSearches(string file, [EnumeratorCancellation] CancellationToken ct)
    {
        var index = 0;
        await foreach (var record in JsonStream.ArrayAsync(file, ct))
        {
            index++;
            var query = record.Str("searchQuery");
            if (string.IsNullOrWhiteSpace(query)) continue;
            if (ParseTime(record.Str("searchTime")) is not { } ts) continue;
            yield return new EventItem(new Event
            {
                Id = Ids.ContentHash("spotify_search", query, ts.ToString("O")),
                Timestamp = ts,
                Source = Source,
                SubType = "spotify_search",
                Text = $"Searched Spotify for {query}",
                Raw = new RawRef(file, $"index={index}")
            });
        }
    }

    private IEnumerable<ImportItem> Play(DateTimeOffset ts, string track, string? artist, string file, int index)
    {
        string? artistId = null;
        if (!string.IsNullOrWhiteSpace(artist))
        {
            artistId = Ids.ContentHash("spotify_artist", artist!);
            yield return new EntityItem(new Entity { Id = artistId, Kind = EntityKind.Org, CanonicalName = artist! });
        }

        yield return new EventItem(new Event
        {
            Id = Ids.ContentHash("spotify_play", track, artist, ts.ToString("O")),
            Timestamp = ts,
            Source = Source,
            SubType = "music_play",
            Text = artist is null ? $"Played {track}" : $"Played {track} by {artist}",
            ActorIds = artistId is null ? [] : [artistId],
            Raw = new RawRef(file, $"index={index}")
        });
    }

    private static DateTimeOffset? ParseTime(string? time)
    {
        if (string.IsNullOrWhiteSpace(time)) return null;
        // Spotify uses ISO ("2023-05-14T19:32:11Z") and account-data ("2023-05-14 19:32", UTC).
        var cleaned = time.Replace("[UTC]", "").Trim();
        return DateTimeOffset.TryParse(cleaned, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts)
            ? ts
            : null;
    }

    private static IEnumerable<string> Files(string root, string pattern)
    {
        try { return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories); }
        catch { return []; }
    }
}
