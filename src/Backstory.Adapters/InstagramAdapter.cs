using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Backstory.Core;

namespace Backstory.Adapters;

/// <summary>
/// Parses an Instagram "Download your information" export (JSON format). v1 covers direct messages,
/// which live one folder per conversation under inbox/. Instagram stores text as UTF-8 bytes
/// reinterpreted as Latin-1, so message and name text is repaired on the way in.
/// </summary>
public sealed class InstagramAdapter : ISourceAdapter
{
    public string Source => "instagram";

    public bool CanHandle(string path) =>
        Directory.Exists(path) && InboxThreads(path).Any();

    public async IAsyncEnumerable<ImportItem> ParseAsync(string path, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var file in InboxThreads(path))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var item in ParseThread(file))
                yield return item;
        }
        await Task.CompletedTask;
    }

    private List<ImportItem> ParseThread(string file)
    {
        var items = new List<ImportItem>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(file)); }
        catch { return items; }

        using (doc)
        {
            var root = doc.RootElement;
            var title = Fix(root.Str("title") ?? "Instagram chat");

            foreach (var participant in root.Arr("participants"))
            {
                if (Fix(participant.Str("name")) is { Length: > 0 } name)
                    items.Add(new EntityItem(Person(name)));
            }

            var index = 0;
            foreach (var message in root.Arr("messages"))
            {
                index++;
                var content = message.Str("content");
                if (string.IsNullOrWhiteSpace(content)) continue; // skip media-only / reactions
                if (message.Prop("timestamp_ms")?.GetInt64() is not { } ms) continue;

                var ts = DateTimeOffset.FromUnixTimeMilliseconds(ms);
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
                    Timestamp = ts,
                    Source = Source,
                    SubType = "instagram_message",
                    Text = Fix(content!),
                    ActorIds = actorId is null ? [] : [actorId],
                    Raw = new RawRef(file, $"thread={title};message={index}")
                }));
            }
        }

        return items;
    }

    private static Entity Person(string name) => new()
    {
        Id = Ids.ContentHash("instagram_user", name),
        Kind = EntityKind.Person,
        CanonicalName = name
    };

    /// <summary>Instagram double-encodes text: UTF-8 bytes shown as Latin-1. Reverse it when safe.</summary>
    private static string Fix(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        foreach (var ch in text)
            if (ch > 0xFF) return text; // already proper Unicode — leave it
        try { return Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(text)); }
        catch { return text; }
    }

    private static IEnumerable<string> InboxThreads(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "message_*.json", SearchOption.AllDirectories)
                .Where(f => f.Replace('\\', '/').Contains("/inbox/", StringComparison.OrdinalIgnoreCase));
        }
        catch { return []; }
    }
}
