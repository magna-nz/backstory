using System.Globalization;
using Backstory.Adapters;
using Backstory.Core;
using Backstory.Embeddings;
using Backstory.Eval;
using Backstory.Mcp;
using Backstory.Query;
using Backstory.Storage;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var command = args[0];
var rest = args[1..];
var (positional, options) = ParseArgs(rest);

// "serve" and "eval" don't need a positional; everything else shares vault wiring.
var dbPath = ResolveDbPath();
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
var db = new SqliteDatabase(dbPath);
db.EnsureCreated();
var events = new SqliteEventStore(db);
var entities = new SqliteEntityStore(db);
var vectors = new BruteForceVectorStore(db);
var (embeddings, embedderName) = EmbeddingFactory.Create();

switch (command)
{
    case "import":
        return await Import();
    case "search":
        return await Search();
    case "timeline":
        return await Timeline();
    case "entity":
        return await LookupEntity();
    case "stats":
        return await Stats();
    case "serve":
        await BackstoryMcpServer.RunAsync(dbPath);
        return 0;
    case "eval":
        return await RunEval();
    case "model":
        return await Model();
    default:
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintUsage();
        return 1;
}

async Task<int> Import()
{
    if (positional.Count == 0)
    {
        Console.Error.WriteLine("usage: backstory import <path> [--source auto|telegram|google_takeout]");
        return 1;
    }

    var path = positional[0];
    ISourceAdapter[] adapters = [new GoogleTakeoutAdapter(), new TelegramAdapter()];
    var requested = options.GetValueOrDefault("source", "auto");

    var adapter = requested == "auto"
        ? adapters.FirstOrDefault(a => a.CanHandle(path))
        : adapters.FirstOrDefault(a => a.Source == requested);

    if (adapter is null)
    {
        Console.Error.WriteLine($"No adapter could handle '{path}' (source={requested}).");
        return 1;
    }

    Console.WriteLine($"Importing with '{adapter.Source}' adapter (embedder: {embedderName})…");
    var pipeline = new IngestionPipeline(events, entities, embeddings, vectors);
    var stats = await pipeline.ImportAsync(adapter, path);

    Console.WriteLine($"Imported {stats.Events} events, {stats.Entities} entities.");
    foreach (var (subType, count) in stats.BySubType.OrderByDescending(kv => kv.Value))
        Console.WriteLine($"  {subType,-18} {count}");
    return 0;
}

async Task<int> Search()
{
    if (positional.Count == 0)
    {
        Console.Error.WriteLine("usage: backstory search \"<query>\" [--limit N] [--from ISO] [--to ISO] [--source S]");
        return 1;
    }

    var query = string.Join(' ', positional);
    var limit = ParseInt(options.GetValueOrDefault("limit"), 10);
    var search = new HybridSearch(events, embeddings, vectors);
    var results = await search.SearchAsync(query, Filter(), limit);

    if (results.Count == 0)
    {
        Console.WriteLine("No matches.");
        return 0;
    }

    foreach (var r in results)
        Console.WriteLine($"{r.Event.Timestamp:yyyy-MM-dd}  [{r.Event.Source}/{r.Event.SubType}]  ({r.MatchKind})  {r.Event.Text}");
    return 0;
}

async Task<int> Timeline()
{
    var limit = ParseInt(options.GetValueOrDefault("limit"), 20);
    var any = false;
    await foreach (var e in events.QueryAsync(Filter(), limit))
    {
        any = true;
        Console.WriteLine($"{e.Timestamp:yyyy-MM-dd HH:mm}  [{e.Source}/{e.SubType}]  {e.Text}");
    }
    if (!any) Console.WriteLine("No events.");
    return 0;
}

async Task<int> LookupEntity()
{
    if (positional.Count == 0)
    {
        Console.Error.WriteLine("usage: backstory entity \"<name>\"");
        return 1;
    }

    var name = string.Join(' ', positional);
    var match = await entities.FindByAliasAsync(EntityKind.Person, name)
                ?? await entities.FindByAliasAsync(EntityKind.Place, name);
    if (match is null)
    {
        Console.WriteLine($"No entity named '{name}'.");
        return 0;
    }

    Console.WriteLine($"{match.CanonicalName} [{match.Kind}]");
    if (match.Aliases.Count > 0)
        Console.WriteLine($"  aliases: {string.Join(", ", match.Aliases)}");
    return 0;
}

async Task<int> Stats()
{
    var bySource = new Dictionary<string, int>();
    var bySubType = new Dictionary<string, int>();
    await foreach (var e in events.QueryAsync(new EventFilter(), int.MaxValue))
    {
        bySource[e.Source] = bySource.GetValueOrDefault(e.Source) + 1;
        bySubType[e.SubType] = bySubType.GetValueOrDefault(e.SubType) + 1;
    }

    Console.WriteLine($"Vault: {dbPath}");
    Console.WriteLine($"Embedder: {embedderName}");
    Console.WriteLine($"Total events: {bySource.Values.Sum()}");
    foreach (var (source, count) in bySource.OrderByDescending(kv => kv.Value))
        Console.WriteLine($"  {source,-18} {count}");
    Console.WriteLine("By type:");
    foreach (var (subType, count) in bySubType.OrderByDescending(kv => kv.Value))
        Console.WriteLine($"  {subType,-18} {count}");
    return 0;
}

async Task<int> Model()
{
    var sub = positional.Count > 0 ? positional[0] : "";
    if (sub != "fetch")
    {
        Console.Error.WriteLine("usage: backstory model fetch   # download the semantic embedding model");
        return 1;
    }

    var dir = EmbeddingFactory.DefaultModelDir();
    Console.WriteLine("Fetching all-MiniLM-L6-v2 (this is the only network access Backstory makes)…");
    try
    {
        await ModelDownloader.FetchAsync(dir, Console.WriteLine);
        Console.WriteLine("Done. Re-import your exports to re-embed them with the semantic model.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Download failed: {ex.Message}");
        return 1;
    }
}

async Task<int> RunEval()
{
    var report = await new EvalRunner().RunAsync();
    Console.WriteLine($"Embedder           : {report.Embedder}");
    Console.WriteLine($"Ingestion coverage : {report.IngestionCoverage:P1}  ({report.EventsEmitted}/{report.EventsExpected} events)");
    Console.WriteLine($"Retrieval Recall@5 : {report.RecallAt5:P1}  ({report.Hits}/{report.Questions} questions)");
    return 0;
}

EventFilter Filter() => new()
{
    From = ParseDate(options.GetValueOrDefault("from")),
    To = ParseDate(options.GetValueOrDefault("to")),
    Source = options.GetValueOrDefault("source") is { } s && s != "auto" ? s : null
};

static string ResolveDbPath()
{
    var env = Environment.GetEnvironmentVariable("BACKSTORY_DB");
    if (!string.IsNullOrWhiteSpace(env)) return env;
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    return Path.Combine(home, ".backstory", "backstory.db");
}

static (List<string> Positional, Dictionary<string, string> Options) ParseArgs(string[] args)
{
    var positional = new List<string>();
    var options = new Dictionary<string, string>();
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith("--"))
        {
            var key = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
            options[key] = value;
        }
        else
        {
            positional.Add(args[i]);
        }
    }
    return (positional, options);
}

static int ParseInt(string? value, int fallback) =>
    int.TryParse(value, out var n) ? n : fallback;

static DateTimeOffset? ParseDate(string? value) =>
    !string.IsNullOrWhiteSpace(value) &&
    DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts)
        ? ts
        : null;

static void PrintUsage()
{
    Console.WriteLine("""
        backstory — your data exports, searchable and local.

        Usage:
          backstory import <path> [--source auto|telegram|google_takeout]
          backstory search "<query>" [--limit N] [--from ISO] [--to ISO] [--source S]
          backstory timeline [--from ISO] [--to ISO] [--source S] [--limit N]
          backstory entity "<name>"
          backstory stats
          backstory serve            # run the MCP server (stdio)
          backstory eval             # run the benchmark
          backstory model fetch      # download the semantic embedding model (one-time, opt-in)

        Vault location: $BACKSTORY_DB or ~/.backstory/backstory.db
        """);
}
