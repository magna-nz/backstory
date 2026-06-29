using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Backstory.Core;

namespace Backstory.Adapters;

/// <summary>
/// Parses a Telegram Desktop JSON export (`result.json`). Handles both the full account export
/// (with a `chats.list` array) and a single-chat export (top-level `messages`).
/// </summary>
public sealed class TelegramAdapter : ISourceAdapter
{
    public string Source => "telegram";

    public bool CanHandle(string path)
    {
        var file = ResolveFile(path);
        if (file is null) return false;
        try
        {
            // Sniff only the start of the file so a multi-GB export is not read into memory here.
            using var reader = new StreamReader(file);
            var head = new char[256 * 1024];
            var n = reader.Read(head, 0, head.Length);
            var text = new string(head, 0, n);
            return text.Contains("\"chats\"") || (text.Contains("\"messages\"") && text.Contains("\"type\""));
        }
        catch
        {
            return false;
        }
    }

    public async IAsyncEnumerable<ImportItem> ParseAsync(string path, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var file = ResolveFile(path) ?? throw new FileNotFoundException("No Telegram result.json found.", path);
        var seenSenders = new HashSet<string>();

        await foreach (var (kind, chat, element) in TelegramStream.ReadAsync(file, ct))
        {
            if (kind == TelegramStream.ItemKind.Contact)
            {
                if (BuildContact(element) is { } entity)
                    yield return new EntityItem(entity);
            }
            else
            {
                if (element.Str("type") != "message") continue; // skip service messages
                foreach (var item in BuildMessage(element, chat, file, seenSenders))
                    yield return item;
            }
        }
    }

    private static IEnumerable<ImportItem> BuildMessage(JsonElement message, string chatName, string file, HashSet<string> seenSenders)
    {
        var text = message.Prop("text") is { } textValue ? JsonX.FlattenText(textValue) : "";
        if (string.IsNullOrWhiteSpace(text))
        {
            yield return new Notice("message without text (e.g. media or sticker)");
            yield break;
        }

        if (ParseTimestamp(message) is not { } ts)
        {
            yield return new Notice("record without a usable timestamp");
            yield break;
        }

        var fromId = message.Str("from_id");
        var fromName = message.Str("from");
        string? actorId = null;
        if (fromId is not null)
        {
            actorId = Ids.ContentHash("telegram_user", fromId);
            if (seenSenders.Add(fromId))
            {
                yield return new EntityItem(new Entity
                {
                    Id = actorId,
                    Kind = EntityKind.Person,
                    CanonicalName = fromName ?? fromId,
                    Aliases = fromName is null ? [fromId] : [fromName, fromId]
                });
            }
        }

        var msgId = message.Prop("id")?.ToString() ?? "";
        yield return new EventItem(new Event
        {
            Id = Ids.ContentHash("telegram", chatName, msgId),
            Timestamp = ts,
            Source = "telegram",
            SubType = "telegram_message",
            Text = text,
            ActorIds = actorId is null ? [] : [actorId],
            Raw = new RawRef(file, $"chat={chatName};message={msgId}")
        });
    }

    private static Entity? BuildContact(JsonElement contact)
    {
        var first = contact.Str("first_name") ?? "";
        var last = contact.Str("last_name") ?? "";
        var name = $"{first} {last}".Trim();
        var phone = contact.Str("phone_number");
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(phone)) return null;

        var key = !string.IsNullOrWhiteSpace(phone) ? phone! : name;
        var aliases = new List<string>();
        if (!string.IsNullOrWhiteSpace(name)) aliases.Add(name);
        if (!string.IsNullOrWhiteSpace(phone)) aliases.Add(phone!);

        return new Entity
        {
            Id = Ids.ContentHash("telegram_contact", key),
            Kind = EntityKind.Person,
            CanonicalName = string.IsNullOrWhiteSpace(name) ? phone! : name,
            Aliases = aliases
        };
    }

    private static DateTimeOffset? ParseTimestamp(JsonElement message)
    {
        if (message.Str("date_unixtime") is { } unix && long.TryParse(unix, out var seconds))
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (message.Str("date") is { } date &&
            DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
            return new DateTimeOffset(dt, TimeSpan.Zero);
        return null;
    }

    private static string? ResolveFile(string path)
    {
        if (File.Exists(path)) return path;
        if (Directory.Exists(path))
        {
            var candidate = Path.Combine(path, "result.json");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
