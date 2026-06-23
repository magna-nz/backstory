using Microsoft.Data.Sqlite;

namespace Backstory.Storage;

/// <summary>Owns the connection string and schema for one Backstory vault (a single SQLite file).</summary>
public sealed class SqliteDatabase
{
    public string ConnectionString { get; }

    public SqliteDatabase(string path)
    {
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        return conn;
    }

    public void EnsureCreated()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Schema;
        cmd.ExecuteNonQuery();
    }

    private const string Schema = """
        PRAGMA journal_mode = WAL;

        CREATE TABLE IF NOT EXISTS events (
            id          TEXT PRIMARY KEY,
            ts          TEXT NOT NULL,
            source      TEXT NOT NULL,
            subtype     TEXT NOT NULL,
            text        TEXT NOT NULL,
            lat         REAL,
            lon         REAL,
            place_id    TEXT,
            raw_file    TEXT NOT NULL,
            raw_locator TEXT NOT NULL,
            metadata    TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_events_ts ON events (ts);
        CREATE INDEX IF NOT EXISTS idx_events_source ON events (source);

        CREATE TABLE IF NOT EXISTS event_actors (
            event_id  TEXT NOT NULL,
            entity_id TEXT NOT NULL,
            PRIMARY KEY (event_id, entity_id)
        );

        CREATE TABLE IF NOT EXISTS entities (
            id             TEXT PRIMARY KEY,
            kind           INTEGER NOT NULL,
            canonical_name TEXT NOT NULL,
            metadata       TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS entity_aliases (
            kind      INTEGER NOT NULL,
            alias     TEXT NOT NULL,
            entity_id TEXT NOT NULL,
            PRIMARY KEY (kind, alias)
        );

        CREATE TABLE IF NOT EXISTS embeddings (
            event_id TEXT PRIMARY KEY,
            vector   BLOB NOT NULL
        );

        CREATE VIRTUAL TABLE IF NOT EXISTS events_fts USING fts5 (id UNINDEXED, text);
        """;
}
