using Backstory.Core;

namespace Backstory.Storage;

public sealed class SqliteEntityStore(SqliteDatabase db) : IEntityStore
{
    public async ValueTask UpsertAsync(Entity entity, CancellationToken ct = default)
    {
        await using var conn = db.Open();
        await using var tx = conn.BeginTransaction();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO entities (id, kind, canonical_name, metadata)
                VALUES ($id, $kind, $name, $meta)
                ON CONFLICT(id) DO UPDATE SET kind=$kind, canonical_name=$name, metadata=$meta;
                """;
            cmd.Parameters.AddWithValue("$id", entity.Id);
            cmd.Parameters.AddWithValue("$kind", (int)entity.Kind);
            cmd.Parameters.AddWithValue("$name", entity.CanonicalName);
            cmd.Parameters.AddWithValue("$meta", JsonMetadata.ToText(entity.Metadata));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Canonical name doubles as an alias so lookups by name work.
        foreach (var alias in entity.Aliases.Append(entity.CanonicalName).Select(Normalize).Distinct())
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO entity_aliases (kind, alias, entity_id)
                VALUES ($kind, $alias, $id)
                ON CONFLICT(kind, alias) DO UPDATE SET entity_id=$id;
                """;
            cmd.Parameters.AddWithValue("$kind", (int)entity.Kind);
            cmd.Parameters.AddWithValue("$alias", alias);
            cmd.Parameters.AddWithValue("$id", entity.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async ValueTask<Entity?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, kind, canonical_name, metadata FROM entities WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var entityId = reader.GetString(0);
        var kind = (EntityKind)reader.GetInt32(1);
        var name = reader.GetString(2);
        var meta = JsonMetadata.FromText(reader.GetString(3));
        var aliases = await LoadAliasesAsync(conn, kind, entityId, ct);
        return new Entity { Id = entityId, Kind = kind, CanonicalName = name, Aliases = aliases, Metadata = meta };
    }

    public async ValueTask<Entity?> FindByAliasAsync(EntityKind kind, string alias, CancellationToken ct = default)
    {
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT entity_id FROM entity_aliases WHERE kind = $kind AND alias = $alias;";
        cmd.Parameters.AddWithValue("$kind", (int)kind);
        cmd.Parameters.AddWithValue("$alias", Normalize(alias));
        var id = await cmd.ExecuteScalarAsync(ct) as string;
        return id is null ? null : await GetAsync(id, ct);
    }

    private static async Task<IReadOnlyList<string>> LoadAliasesAsync(Microsoft.Data.Sqlite.SqliteConnection conn, EntityKind kind, string id, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT alias FROM entity_aliases WHERE kind = $kind AND entity_id = $id ORDER BY alias;";
        cmd.Parameters.AddWithValue("$kind", (int)kind);
        cmd.Parameters.AddWithValue("$id", id);
        var aliases = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            aliases.Add(reader.GetString(0));
        return aliases;
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
}
