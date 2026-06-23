using Backstory.Core;
using Backstory.Embeddings;
using Backstory.Query;
using Backstory.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backstory.Mcp;

/// <summary>Hosts the Backstory MCP server over stdio, backed by a single vault.</summary>
public static class BackstoryMcpServer
{
    public static async Task RunAsync(string dbPath, CancellationToken ct = default)
    {
        var builder = Host.CreateApplicationBuilder();

        // stdout is the MCP transport — all logging must go to stderr.
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        var db = new SqliteDatabase(dbPath);
        db.EnsureCreated();

        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton<IEventStore>(sp => new SqliteEventStore(sp.GetRequiredService<SqliteDatabase>()));
        builder.Services.AddSingleton<IEntityStore>(sp => new SqliteEntityStore(sp.GetRequiredService<SqliteDatabase>()));
        builder.Services.AddSingleton<IVectorStore>(sp => new BruteForceVectorStore(sp.GetRequiredService<SqliteDatabase>()));
        builder.Services.AddSingleton<IEmbeddingService>(new HashingEmbeddingService());
        builder.Services.AddSingleton<HybridSearch>();

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync(ct);
    }
}
