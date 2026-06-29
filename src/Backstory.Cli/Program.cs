using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
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
    case "fetch":
        return Fetch();
    case "watch":
        return await Watch();
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

    var stats = await ImportPath(positional[0], options.GetValueOrDefault("source", "auto"));
    return stats is null ? 1 : 0;
}

// Shared by `import` and `watch`. Unzips a Takeout zip, picks an adapter, and ingests.
async Task<ImportStats?> ImportPath(string path, string requested)
{
    var importPath = path;
    if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
    {
        var parts = ExportFiles.ZipGroup(path);
        Console.WriteLine(parts.Count > 1
            ? $"Merging {parts.Count} Takeout parts…"
            : $"Extracting {Path.GetFileName(path)}…");
        importPath = ExportFiles.ExtractZips(parts, Path.Combine(Path.GetDirectoryName(dbPath)!, "imports"));
    }

    ISourceAdapter[] adapters =
        [new GoogleTakeoutAdapter(), new TelegramAdapter(), new SpotifyAdapter(), new InstagramAdapter()];
    var adapter = requested == "auto"
        ? adapters.FirstOrDefault(a => a.CanHandle(importPath))
        : adapters.FirstOrDefault(a => a.Source == requested);

    if (adapter is null)
    {
        Console.Error.WriteLine($"No adapter could handle '{path}' (source={requested}).");
        return null;
    }

    Console.WriteLine($"Importing with '{adapter.Source}' adapter (embedder: {embedderName})…");
    var pipeline = new IngestionPipeline(events, entities, embeddings, vectors);
    var stats = await pipeline.ImportAsync(adapter, importPath);

    Console.WriteLine($"Imported {stats.Events} events, {stats.Entities} entities.");
    foreach (var (subType, count) in stats.BySubType.OrderByDescending(kv => kv.Value))
        Console.WriteLine($"  {subType,-22} {count}");

    if (stats.Skipped > 0)
    {
        Console.WriteLine($"Skipped {stats.Skipped}:");
        foreach (var (reason, count) in stats.SkippedByReason.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"  {count,-6} {reason}");
    }
    return stats;
}

int Fetch()
{
    var src = positional.Count > 0 ? positional[0].ToLowerInvariant() : "";
    switch (src)
    {
        case "google":
            Console.WriteLine("""
                Export your Google data via Takeout:

                  1. Opening https://takeout.google.com in your browser…
                  2. Click "Deselect all", then tick only what Backstory uses:
                       • My Activity                 (search history)
                       • Maps (your places)          (saved places)
                       • Location History / Timeline (if available)
                       • YouTube and YouTube Music   (watch history)
                  3. Next → "Export once", file type .zip, a large size (e.g. 50 GB).
                  4. Google emails a link when it's ready (minutes to hours).
                  5. Download the .zip, then:  backstory watch   (auto-imports it)
                       or:  backstory import ~/Downloads/takeout-*.zip
                """);
            TryOpenUrl("https://takeout.google.com");
            return 0;

        case "telegram":
            Console.WriteLine("""
                Export your Telegram data (Telegram Desktop — not the phone app):

                  1. Settings → Advanced → Export Telegram data
                  2. Set the format to "Machine-readable JSON"
                  3. Tick what you want (Personal chats, etc.); set a date range if you like
                  4. Export — it writes a folder containing result.json
                  5. Then:  backstory watch   (auto-imports it)
                       or:  backstory import <export-folder>/result.json
                """);
            return 0;

        case "spotify":
            Console.WriteLine("""
                Export your Spotify data:

                  1. Opening https://www.spotify.com/account/privacy/ …
                  2. Scroll to "Download your data".
                  3. Tick "Extended streaming history" for everything you've played
                     (or "Account data" for the last year plus playlists and searches).
                  4. Confirm using the email Spotify sends you.
                  5. It can take up to 30 days. When the zip arrives:  backstory watch
                       or:  backstory import ~/Downloads/my_spotify_data.zip
                """);
            TryOpenUrl("https://www.spotify.com/account/privacy/");
            return 0;

        case "instagram":
            Console.WriteLine("""
                Export your Instagram data:

                  1. Opening https://accountscenter.instagram.com/info_and_permissions/ …
                     (in the app: Accounts Center → Your information and permissions → Download your information)
                  2. Choose "Some of your information" and pick Messages (and anything else you want).
                  3. Set the format to JSON.
                  4. Request it. Instagram emails a link, which can take a few hours, sometimes up to 30 days.
                  5. When the zip arrives:  backstory watch
                       or:  backstory import ~/Downloads/instagram-export.zip
                """);
            TryOpenUrl("https://accountscenter.instagram.com/info_and_permissions/");
            return 0;

        default:
            Console.Error.WriteLine("usage: backstory fetch google|telegram|spotify|instagram");
            return 1;
    }
}

async Task<int> Watch()
{
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var dir = options.GetValueOrDefault("dir") ?? Path.Combine(home, "Downloads");
    if (!Directory.Exists(dir))
    {
        Console.Error.WriteLine($"No such directory: {dir}");
        return 1;
    }

    Console.WriteLine($"Watching {dir} for Google Takeout / Telegram exports… (Ctrl+C to stop)");

    var channel = Channel.CreateUnbounded<string>();
    void Enqueue(string p)
    {
        if (ExportFiles.Detect(p) is not null) channel.Writer.TryWrite(p);
    }

    using var watcher = new FileSystemWatcher(dir)
    {
        IncludeSubdirectories = true,
        NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
        EnableRaisingEvents = true
    };
    watcher.Created += (_, e) => Enqueue(e.FullPath);
    watcher.Renamed += (_, e) => Enqueue(e.FullPath);
    watcher.Changed += (_, e) => Enqueue(e.FullPath);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); channel.Writer.TryComplete(); };

    var processed = new HashSet<string>();
    try
    {
        await foreach (var path in channel.Reader.ReadAllAsync(cts.Token))
        {
            if (!processed.Add(path)) continue;
            if (!await WaitUntilStable(path, cts.Token)) { processed.Remove(path); continue; }

            Console.WriteLine($"\nDetected export: {Path.GetFileName(path)}");
            try { await ImportPath(path, "auto"); }
            catch (Exception ex) { Console.Error.WriteLine($"  import failed: {ex.Message}"); }
        }
    }
    catch (OperationCanceledException) { /* Ctrl+C */ }

    Console.WriteLine("\nStopped watching.");
    return 0;
}

// Wait for a download to finish: size stable across two polls and the file is openable.
static async Task<bool> WaitUntilStable(string path, CancellationToken ct)
{
    if (ExportFiles.IsPartialDownload(path)) return false;
    long last = -1;
    var stableCount = 0;
    for (var i = 0; i < 240 && !ct.IsCancellationRequested; i++)
    {
        long length;
        try
        {
            if (Directory.Exists(path)) return true;
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            length = info.Length;
        }
        catch { await Task.Delay(500, ct); continue; }

        if (length == last)
        {
            if (++stableCount >= 2) return CanOpen(path);
        }
        else { stableCount = 0; last = length; }

        await Task.Delay(500, ct);
    }
    return false;
}

static bool CanOpen(string path)
{
    try
    {
        using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return true;
    }
    catch { return false; }
}

static void TryOpenUrl(string url)
{
    try
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS())
            Process.Start("open", url);
        else
            Process.Start("xdg-open", url);
    }
    catch { /* best effort — the URL is printed above */ }
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
          backstory fetch google|telegram|spotify|instagram   # how to export your data (opens the page)
          backstory watch [--dir <path>]       # auto-import exports as they land in ~/Downloads
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
