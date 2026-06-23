using Backstory.Adapters;
using Backstory.Core;
using Backstory.Embeddings;
using Backstory.Query;
using Backstory.Storage;

namespace Backstory.Eval;

public sealed record EvalReport(double IngestionCoverage, int EventsEmitted, int EventsExpected, double RecallAt5, int Questions, int Hits);

/// <summary>
/// Self-contained benchmark: ingests bundled fixture exports and measures (1) ingestion coverage
/// — emitted vs. expected events, surfacing silent data loss — and (2) Recall@5 of the hybrid
/// search over a hand-built question→gold-event set. Reproducible, no network, no external files.
/// </summary>
public sealed class EvalRunner
{
    // Each tuple: a unique marker substring identifying the gold event, and the message text.
    private static readonly (string Marker, string Text)[] TelegramMessages =
    [
        ("Dishoom", "lets meet at Dishoom for dinner on friday"),
        ("tax return", "did you file the tax return before the deadline"),
        ("Tokyo", "the flight to Tokyo is booked for may"),
        ("oat milk", "remember to buy oat milk and coffee beans"),
        ("dentist", "the dentist appointment is rescheduled to 3pm"),
        ("Kind of Blue", "loved that jazz record you sent, Kind of Blue"),
        ("rent", "the landlord is raising the rent next quarter"),
        ("Berlin", "my sister is visiting from Berlin next week"),
        ("gym membership", "the gym membership renews automatically in june"),
        ("bridge", "watch out the bridge on highway 5 is closed"),
    ];

    private static readonly (string Marker, string Text)[] TakeoutSearches =
    [
        ("ramen", "Searched for best ramen in shibuya"),
        ("leaking tap", "Searched for how to fix a leaking tap"),
    ];

    private static readonly (string Question, string Marker)[] Questions =
    [
        ("where did we plan to have dinner", "Dishoom"),
        ("what did I need to do about my taxes", "tax return"),
        ("when is my flight to japan", "Tokyo"),
        ("what groceries should I buy", "oat milk"),
        ("what jazz album was recommended to me", "Kind of Blue"),
        ("who is coming to visit me", "Berlin"),
        ("how do I fix the leaking tap", "leaking tap"),
        ("best ramen restaurant search", "ramen"),
    ];

    public async Task<EvalReport> RunAsync(CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "backstory-eval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var db = new SqliteDatabase(Path.Combine(dir, "eval.db"));
            db.EnsureCreated();
            var events = new SqliteEventStore(db);
            var entities = new SqliteEntityStore(db);
            var vectors = new BruteForceVectorStore(db);
            var embeddings = new HashingEmbeddingService();
            var pipeline = new IngestionPipeline(events, entities, embeddings, vectors);

            WriteFixtures(dir);

            var tg = await pipeline.ImportAsync(new TelegramAdapter(), Path.Combine(dir, "telegram", "result.json"), ct);
            var gt = await pipeline.ImportAsync(new GoogleTakeoutAdapter(), Path.Combine(dir, "takeout"), ct);

            var emitted = tg.Events + gt.Events;
            var expected = TelegramMessages.Length + TakeoutSearches.Length + 1; // +1 youtube watch
            var coverage = (double)emitted / expected;

            var search = new HybridSearch(events, embeddings, vectors);
            var hits = 0;
            foreach (var (question, marker) in Questions)
            {
                var goldId = await FindGoldId(events, marker, ct);
                var results = await search.SearchAsync(question, new EventFilter(), 5, ct);
                if (results.Any(r => r.Event.Id == goldId)) hits++;
            }

            return new EvalReport(coverage, emitted, expected, (double)hits / Questions.Length, Questions.Length, hits);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static async Task<string?> FindGoldId(IEventStore events, string marker, CancellationToken ct)
    {
        await foreach (var e in events.QueryAsync(new EventFilter(), int.MaxValue, ct))
            if (e.Text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return e.Id;
        return null;
    }

    private static void WriteFixtures(string dir)
    {
        var messages = string.Join(",\n", TelegramMessages.Select((m, i) =>
            $$"""{ "id": {{i + 1}}, "type": "message", "date_unixtime": "{{1700000000 + i * 86400}}", "from": "Alex", "from_id": "u1", "text": "{{m.Text}}" }"""));
        var telegram = $$"""
            { "chats": { "list": [ { "name": "Alex", "type": "personal_chat", "id": 1, "messages": [
            {{messages}}
            ] } ] } }
            """;
        Directory.CreateDirectory(Path.Combine(dir, "telegram"));
        File.WriteAllText(Path.Combine(dir, "telegram", "result.json"), telegram);

        var searches = string.Join(",\n", TakeoutSearches.Select((s, i) =>
            $$"""{ "header": "Search", "title": "{{s.Text}}", "time": "2023-1{{i}}-01T12:00:00.000Z" }"""));
        var searchDir = Path.Combine(dir, "takeout", "My Activity", "Search");
        Directory.CreateDirectory(searchDir);
        File.WriteAllText(Path.Combine(searchDir, "MyActivity.json"), $"[\n{searches}\n]");

        var ytDir = Path.Combine(dir, "takeout", "YouTube and YouTube Music", "history");
        Directory.CreateDirectory(ytDir);
        File.WriteAllText(Path.Combine(ytDir, "watch-history.json"),
            """[ { "header": "YouTube", "title": "Watched Tokyo travel vlog", "time": "2023-05-01T12:00:00Z", "subtitles": [ { "name": "WanderChannel" } ] } ]""");
    }
}
