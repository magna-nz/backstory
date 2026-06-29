using Backstory.Adapters;
using Backstory.Core;
using Backstory.Query;

namespace Backstory.Tests;

public class ErrorReportingTests
{
    private static async Task<List<ImportItem>> Collect(ISourceAdapter adapter, string path)
    {
        var items = new List<ImportItem>();
        await foreach (var item in adapter.ParseAsync(path))
            items.Add(item);
        return items;
    }

    [Fact]
    public async Task Telegram_emits_notices_for_skipped_messages()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("result.json", """
            { "chats": { "list": [ { "name": "C", "type": "personal_chat", "id": 1, "messages": [
              { "id": 1, "type": "message", "date_unixtime": "1710444720", "from": "A", "from_id": "u1", "text": "hello" },
              { "id": 2, "type": "message", "date_unixtime": "1710444800", "from": "A", "from_id": "u1", "text": "" },
              { "id": 3, "type": "message", "from": "A", "from_id": "u1", "text": "no timestamp here" }
            ] } ] } }
            """);

        var items = await Collect(new TelegramAdapter(), file);
        var events = items.OfType<EventItem>().Count();
        var notices = items.OfType<Notice>().Select(n => n.Category).ToList();

        Assert.Equal(1, events); // only the valid message
        Assert.Contains(notices, c => c.Contains("without text"));
        Assert.Contains(notices, c => c.Contains("timestamp"));
    }

    [Fact]
    public async Task Pipeline_aggregates_skips_by_reason()
    {
        using var vault = new TestVault();
        using var tmp = new TempDir();
        var file = tmp.Write("result.json", """
            { "chats": { "list": [ { "name": "C", "type": "personal_chat", "id": 1, "messages": [
              { "id": 1, "type": "message", "date_unixtime": "1710444720", "from": "A", "from_id": "u1", "text": "hi" },
              { "id": 2, "type": "message", "date_unixtime": "1710444800", "from": "A", "from_id": "u1", "text": "" },
              { "id": 3, "type": "message", "date_unixtime": "1710444900", "from": "A", "from_id": "u1", "text": "" }
            ] } ] } }
            """);

        var pipeline = new IngestionPipeline(vault.Events, vault.Entities, vault.Embeddings, vault.Vectors);
        var stats = await pipeline.ImportAsync(new TelegramAdapter(), file);

        Assert.Equal(1, stats.Events);
        Assert.Equal(2, stats.Skipped);
        Assert.Equal(2, stats.SkippedByReason.Values.Sum());
        Assert.Contains(stats.SkippedByReason.Keys, k => k.Contains("without text"));
    }

    [Fact]
    public async Task Malformed_file_is_reported_not_silently_dropped()
    {
        using var tmp = new TempDir();
        tmp.Write(Path.Combine("inbox", "alex", "message_1.json"), "{ this is not valid json");

        var items = await Collect(new InstagramAdapter(), tmp.Path);
        var notices = items.OfType<Notice>().Select(n => n.Category).ToList();

        Assert.Contains(notices, c => c.Contains("unreadable file") && c.Contains("message_1.json"));
    }
}
