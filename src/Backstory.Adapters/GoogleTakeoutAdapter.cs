using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Backstory.Core;

namespace Backstory.Adapters;

/// <summary>
/// Parses a Google Takeout export directory. v1 covers Search history, YouTube history,
/// Maps saved places, and Semantic Location History. Each data type is parsed independently and
/// defensively — a malformed or absent file is skipped, never fatal to the whole import.
/// </summary>
public sealed class GoogleTakeoutAdapter : ISourceAdapter
{
    public string Source => "google_takeout";

    private static readonly string[] Markers =
        ["My Activity", "YouTube and YouTube Music", "Maps (your places)", "Location History", "Takeout"];

    public bool CanHandle(string path) =>
        Directory.Exists(path) &&
        Markers.Any(m => Directory.Exists(Path.Combine(path, m)) || FindDir(path, m) is not null);

    public async IAsyncEnumerable<ImportItem> ParseAsync(string path, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var item in ParseActivity(path, Path.Combine("Search", "MyActivity.json"), "Search", "search_query"))
            yield return item;

        foreach (var item in ParseActivity(path, "watch-history.json", "YouTube", "youtube_watch"))
            yield return item;

        foreach (var item in ParseMapsSaved(path))
            yield return item;

        foreach (var item in ParseSemanticLocation(path, ct))
            yield return item;

        await Task.CompletedTask;
    }

    // ---- Search & YouTube share the "My Activity" record shape. ----
    private List<ImportItem> ParseActivity(string root, string fileSuffix, string header, string subType)
    {
        var items = new List<ImportItem>();
        var file = FindFile(root, Path.GetFileName(fileSuffix), fileSuffix);
        if (file is null) return items;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return items;

            var index = 0;
            foreach (var record in doc.RootElement.EnumerateArray())
            {
                index++;
                if (record.Str("header") != header) continue;
                var title = record.Str("title");
                if (string.IsNullOrWhiteSpace(title)) continue;
                if (ParseTime(record.Str("time")) is not { } ts) continue;

                var channel = record.Arr("subtitles").FirstOrDefault().Str("name");
                var text = channel is null ? title : $"{title} ({channel})";

                items.Add(new EventItem(new Event
                {
                    Id = Ids.ContentHash(subType, title, ts.ToString("O")),
                    Timestamp = ts,
                    Source = Source,
                    SubType = subType,
                    Text = text,
                    Raw = new RawRef(file, $"index={index}")
                }));
            }
        }
        catch (JsonException) { /* skip unparseable file */ }

        return items;
    }

    // ---- Maps saved places: GeoJSON (Saved.json) and/or CSV lists. ----
    private List<ImportItem> ParseMapsSaved(string root)
    {
        var items = new List<ImportItem>();
        var geojson = FindFile(root, "Saved.json", Path.Combine("Maps (your places)", "Saved.json"));
        if (geojson is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(geojson));
                foreach (var feature in doc.RootElement.Arr("features"))
                {
                    var props = feature.Prop("properties") ?? default;
                    var title = props.Str("Title") ?? props.Prop("Location")?.Str("Name");
                    if (string.IsNullOrWhiteSpace(title)) continue;

                    double? lat = null, lon = null;
                    if (feature.Prop("geometry")?.Prop("coordinates") is { ValueKind: JsonValueKind.Array } coords)
                    {
                        var arr = coords.EnumerateArray().ToArray();
                        if (arr.Length >= 2 && arr[0].TryGetDouble(out var x) && arr[1].TryGetDouble(out var y))
                        {
                            lon = x;
                            lat = y;
                        }
                    }

                    var placeId = Ids.ContentHash("google_place", title);
                    items.Add(new EntityItem(new Entity
                    {
                        Id = placeId,
                        Kind = EntityKind.Place,
                        CanonicalName = title
                    }));

                    if (ParseTime(props.Str("Published") ?? props.Str("Date")) is { } ts)
                    {
                        items.Add(new EventItem(new Event
                        {
                            Id = Ids.ContentHash("maps_save", title, ts.ToString("O")),
                            Timestamp = ts,
                            Source = Source,
                            SubType = "maps_save",
                            Text = $"Saved place: {title}",
                            Latitude = lat,
                            Longitude = lon,
                            PlaceId = placeId,
                            Raw = new RawRef(geojson, $"feature={title}")
                        }));
                    }
                }
            }
            catch (JsonException) { /* skip */ }
        }

        // CSV saved lists carry only titles (no dates) → place entities only.
        foreach (var csv in FindAllFiles(root, "*.csv", "Saved"))
        {
            foreach (var title in ReadCsvTitles(csv))
            {
                items.Add(new EntityItem(new Entity
                {
                    Id = Ids.ContentHash("google_place", title),
                    Kind = EntityKind.Place,
                    CanonicalName = title
                }));
            }
        }

        return items;
    }

    // ---- Semantic Location History: actual visits, with timestamps. ----
    private List<ImportItem> ParseSemanticLocation(string root, CancellationToken ct)
    {
        var items = new List<ImportItem>();
        foreach (var file in FindAllFiles(root, "*.json", "Semantic Location History"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                foreach (var obj in doc.RootElement.Arr("timelineObjects"))
                {
                    if (obj.Prop("placeVisit") is not { } visit) continue;
                    var location = visit.Prop("location") ?? default;
                    var name = location.Str("name") ?? location.Str("address");
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var start = visit.Prop("duration")?.Str("startTimestamp");
                    if (ParseTime(start) is not { } ts) continue;

                    double? lat = location.Prop("latitudeE7")?.GetInt64() / 1e7;
                    double? lon = location.Prop("longitudeE7")?.GetInt64() / 1e7;
                    var placeId = Ids.ContentHash("google_place", name);

                    items.Add(new EntityItem(new Entity { Id = placeId, Kind = EntityKind.Place, CanonicalName = name }));
                    items.Add(new EventItem(new Event
                    {
                        Id = Ids.ContentHash("location_visit", name, ts.ToString("O")),
                        Timestamp = ts,
                        Source = Source,
                        SubType = "location_visit",
                        Text = $"Visited {name}",
                        Latitude = lat,
                        Longitude = lon,
                        PlaceId = placeId,
                        Raw = new RawRef(file, $"visit={name};{ts:O}")
                    }));
                }
            }
            catch (JsonException) { /* skip */ }
        }

        return items;
    }

    private static IEnumerable<string> ReadCsvTitles(string csv)
    {
        string[] lines;
        try { lines = File.ReadAllLines(csv); }
        catch { yield break; }
        if (lines.Length < 2) yield break;

        var header = lines[0].Split(',');
        var titleCol = Array.FindIndex(header, h => h.Trim().Equals("Title", StringComparison.OrdinalIgnoreCase));
        if (titleCol < 0) yield break;

        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split(',');
            if (titleCol < cells.Length && !string.IsNullOrWhiteSpace(cells[titleCol]))
                yield return cells[titleCol].Trim().Trim('"');
        }
    }

    private static DateTimeOffset? ParseTime(string? time) =>
        !string.IsNullOrWhiteSpace(time) &&
        DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts)
            ? ts
            : null;

    // ---- File discovery: tolerant of where in the tree the export root points. ----
    private static string? FindFile(string root, string fileName, string preferredSuffix)
    {
        var direct = Path.Combine(root, preferredSuffix);
        if (File.Exists(direct)) return direct;
        return EnumerateSafe(root, fileName).FirstOrDefault(f =>
            f.Replace('\\', '/').EndsWith(preferredSuffix.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            ?? EnumerateSafe(root, fileName).FirstOrDefault();
    }

    private static IEnumerable<string> FindAllFiles(string root, string pattern, string dirSegment) =>
        EnumerateSafe(root, pattern).Where(f => f.Replace('\\', '/').Contains("/" + dirSegment + "/", StringComparison.OrdinalIgnoreCase)
            || f.Contains(dirSegment, StringComparison.OrdinalIgnoreCase));

    private static string? FindDir(string root, string dirName) =>
        SafeEnumerateDirs(root).FirstOrDefault(d => Path.GetFileName(d).Equals(dirName, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> EnumerateSafe(string root, string pattern)
    {
        try { return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories); }
        catch { return []; }
    }

    private static IEnumerable<string> SafeEnumerateDirs(string root)
    {
        try { return Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories); }
        catch { return []; }
    }
}
