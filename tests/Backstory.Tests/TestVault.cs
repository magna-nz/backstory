using Backstory.Embeddings;
using Backstory.Storage;
using Microsoft.Data.Sqlite;

namespace Backstory.Tests;

/// <summary>A throwaway on-disk vault (SQLite db + stores) for integration tests.</summary>
internal sealed class TestVault : IDisposable
{
    public string Dir { get; }
    public SqliteDatabase Db { get; }
    public SqliteEventStore Events { get; }
    public SqliteEntityStore Entities { get; }
    public BruteForceVectorStore Vectors { get; }
    public HashingEmbeddingService Embeddings { get; }

    public TestVault()
    {
        Dir = Path.Combine(Path.GetTempPath(), "backstory-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Db = new SqliteDatabase(Path.Combine(Dir, "vault.db"));
        Db.EnsureCreated();
        Events = new SqliteEventStore(Db);
        Entities = new SqliteEntityStore(Db);
        Vectors = new BruteForceVectorStore(Db);
        Embeddings = new HashingEmbeddingService();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); }
        catch { /* best effort */ }
    }
}
