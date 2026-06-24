using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Backstory.Core;

namespace Backstory.Adapters;

/// <summary>
/// Parses an Instagram "Download your information" export (JSON format): direct messages, posts,
/// comments, and search history. Instagram stores text as UTF-8 bytes reinterpreted as Latin-1, so
/// all text is repaired on the way in. Activity files use Meta's string_map_data shape, which is
/// handled generically since the wrapping key and file names vary between exports.
/// </summary>
public sealed class InstagramAdapter : ISourceAdapter
{
    public string Source => "instagram";

    public bool CanHandle(string path) =>
        Directory.Exists(path) &&
        (InboxThreads(path).Any() || Find(path, "posts_*.json").Any() || Find(path, "*searches*.json").Any());

    public async IAsyncEnumerable<ImportItem> ParseAsync(string path, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var file in InboxThreads(path))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var item in ParseThread(file)) yield return item;
        }

        foreach (var file in Find(path, "posts_*.json"))
            foreach (var item in ParsePosts(file)) yield return item;

        foreach (var file in Find(path, "*comments*.json"))
            foreach (var item in ParseStringMap(file, "instagram_comment", ["Comment"], "Commented: {0}"))
                yield return item;

        foreach (var file in Find(path, "*searches*.json"))
            foreach (var item in ParseStringMap(file, "instagram_search", ["Search"], "Searched Instagram for {0}"))
                yield return item;

        await Task.CompletedTask;
    }

    // ---- Direct messages (one file per conversation under inbox/). ----
    private static List<ImportItem> ParseThread(string file)
    {
        var items = new List<ImportItem>();
        if (Load(file) is not { } doc) return items;
        using (doc)
        {
            var root = doc.RootElement;
            var title = Fix(root.Str("title") ?? "Instagram chat");

            foreach (var participant in root.Arr("participants"))
                if (Fix(participant.Str("name")) is { Length: > 0 } name)
                    items.Add(new EntityItem(Person(name)));

            var index = 0;
            foreach (var message in root.Arr("messages"))
            {
                index++;
                var content = message.Str("content");
                if (string.IsNullOrWhiteSpace(content)) continue;
                if (Seconds(message.Prop("timestamp_ms")) is not { } ms) continue;

                var sender = Fix(message.Str("sender_name") ?? "");
                string? actorId = null;
                if (sender.Length > 0)
                {
                    var person = Person(sender);
                    actorId = person.Id;
                    items.Add(new EntityItem(person));
                }

                items.Add(new EventItem(new Event
                {
                    Id = Ids.ContentHash("instagram", title, index.ToString(), ms.ToString()),
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(ms),
                    Source = "instagram",
                    SubType = "instagram_message",
                    Text = Fix(content!),
                    ActorIds = actorId is null ? [] : [actorId],
                    Raw = new RawRef(file, $"thread={title};message={index}")
                }));
            }
        }
        return items;
    }

    // ---- Posts: caption + creation time live on the post or its first media item. ----
    private static List<ImportItem> ParsePosts(string file)
    {
        var items = new List<ImportItem>();
        if (Load(file) is not { } doc) return items;
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return items;
            var index = 0;
            foreach (var post in doc.RootElement.EnumerateArray())
            {
                index++;
                var firstMedia = post.Arr("media").FirstOrDefault();
                var caption = Fix(post.Str("title") ?? firstMedia.Str("title") ?? "");
                var seconds = Seconds(post.Prop("creation_timestamp")) ?? Seconds(firstMedia.Prop("creation_timestamp"));
                if (seconds is not { } ts) continue;

                items.Add(new EventItem(new Event
                {
                    Id = Ids.ContentHash("instagram_post", caption, ts.ToString()),
                    Timestamp = DateTimeOffset.FromUnixTimeSeconds(ts),
                    Source = "instagram",
                    SubType = "instagram_post",
                    Text = caption.Length > 0 ? caption : "Shared an Instagram post",
                    Raw = new RawRef(file, $"index={index}")
                }));
            }
        }
        return items;
    }

    // ---- Generic Meta "string_map_data" activity (comments, searches). ----
    private static List<ImportItem> ParseStringMap(string file, string subType, string[] valueKeys, string textFormat)
    {
        var items = new List<ImportItem>();
        if (Load(file) is not { } doc) return items;
        using (doc)
        {
            var index = 0;
            foreach (var entry in Entries(doc.RootElement))
            {
                index++;
                var map = entry.Prop("string_map_data");
                if (map is not { ValueKind: JsonValueKind.Object }) continue;

                var (value, seconds) = ReadMap(map.Value, valueKeys);
                if (string.IsNullOrWhiteSpace(value) || seconds is not { } ts) continue;

                items.Add(new EventItem(new Event
                {
                    Id = Ids.ContentHash(subType, value!, ts.ToString()),
                    Timestamp = DateTimeOffset.FromUnixTimeSeconds(ts),
                    Source = "instagram",
                    SubType = subType,
                    Text = string.Format(textFormat, Fix(value)),
                    Raw = new RawRef(file, $"index={index}")
                }));
            }
        }
        return items;
    }

    // Pulls a value + timestamp from string_map_data, trying the named keys then falling back.
    private static (string? Value, long? Seconds) ReadMap(JsonElement map, string[] valueKeys)
    {
        string? value = null;
        long? seconds = null;
        foreach (var key in valueKeys)
            if (map.Prop(key) is { } field)
            {
                value = field.Str("value");
                seconds = Seconds(field.Prop("timestamp"));
                break;
            }

        // Timestamp can live under a separate "Time" entry.
        seconds ??= Seconds((map.Prop("Time") ?? default).Prop("timestamp"));
        return (value, seconds);
    }

    // Meta activity files are either an array, or an object whose first array property holds entries.
    private static IEnumerable<JsonElement> Entries(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in root.EnumerateArray()) yield return e;
            yield break;
        }
        if (root.ValueKind == JsonValueKind.Object)
            foreach (var prop in root.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in prop.Value.EnumerateArray()) yield return e;
                    yield break;
                }
    }

    private static Entity Person(string name) => new()
    {
        Id = Ids.ContentHash("instagram_user", name),
        Kind = EntityKind.Person,
        CanonicalName = name
    };

    private static long? Seconds(JsonElement? field) =>
        field is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out var v) ? v : null;

    /// <summary>Instagram double-encodes text: UTF-8 bytes shown as Latin-1. Reverse it when safe.</summary>
    private static string Fix(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        foreach (var ch in text)
            if (ch > 0xFF) return text;
        try { return Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(text)); }
        catch { return text; }
    }

    private static JsonDocument? Load(string file)
    {
        try { return JsonDocument.Parse(File.ReadAllText(file)); }
        catch { return null; }
    }

    private static IEnumerable<string> InboxThreads(string root) =>
        Find(root, "message_*.json").Where(f => f.Replace('\\', '/').Contains("/inbox/", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> Find(string root, string pattern)
    {
        try { return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories); }
        catch { return []; }
    }
}
