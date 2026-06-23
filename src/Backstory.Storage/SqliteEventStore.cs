using System.Globalization;
using System.Runtime.CompilerServices;
using Backstory.Core;
using Microsoft.Data.Sqlite;

namespace Backstory.Storage;

public sealed class SqliteEventStore(SqliteDatabase db) : IEventStore
{
    public async ValueTask UpsertAsync(Event ev, CancellationToken ct = default)
    {
        await using var conn = db.Open();
        await using var tx = conn.BeginTransaction();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO events (id, ts, source, subtype, text, lat, lon, place_id, raw_file, raw_locator, metadata)
                VALUES ($id, $ts, $source, $subtype, $text, $lat, $lon, $place, $rawFile, $rawLoc, $meta)
                ON CONFLICT(id) DO UPDATE SET
                    ts=$ts, source=$source, subtype=$subtype, text=$text, lat=$lat, lon=$lon,
                    place_id=$place, raw_file=$rawFile, raw_locator=$rawLoc, metadata=$meta;
                """;
            cmd.Parameters.AddWithValue("$id", ev.Id);
            cmd.Parameters.AddWithValue("$ts", ev.Timestamp.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$source", ev.Source);
            cmd.Parameters.AddWithValue("$subtype", ev.SubType);
            cmd.Parameters.AddWithValue("$text", ev.Text);
            cmd.Parameters.AddWithValue("$lat", (object?)ev.Latitude ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lon", (object?)ev.Longitude ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$place", (object?)ev.PlaceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rawFile", ev.Raw.FilePath);
            cmd.Parameters.AddWithValue("$rawLoc", ev.Raw.Locator);
            cmd.Parameters.AddWithValue("$meta", JsonMetadata.ToText(ev.Metadata));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await ReplaceFtsAsync(conn, tx, ev.Id, ev.Text, ct);
        await ReplaceActorsAsync(conn, tx, ev.Id, ev.ActorIds, ct);

        await tx.CommitAsync(ct);
    }

    private static async Task ReplaceFtsAsync(SqliteConnection conn, SqliteTransaction tx, string id, string text, CancellationToken ct)
    {
        await using var del = conn.CreateCommand();
        del.Transaction = tx;
        del.CommandText = "DELETE FROM events_fts WHERE id = $id;";
        del.Parameters.AddWithValue("$id", id);
        await del.ExecuteNonQueryAsync(ct);

        await using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = "INSERT INTO events_fts (id, text) VALUES ($id, $text);";
        ins.Parameters.AddWithValue("$id", id);
        ins.Parameters.AddWithValue("$text", text);
        await ins.ExecuteNonQueryAsync(ct);
    }

    private static async Task ReplaceActorsAsync(SqliteConnection conn, SqliteTransaction tx, string id, IReadOnlyList<string> actors, CancellationToken ct)
    {
        await using var del = conn.CreateCommand();
        del.Transaction = tx;
        del.CommandText = "DELETE FROM event_actors WHERE event_id = $id;";
        del.Parameters.AddWithValue("$id", id);
        await del.ExecuteNonQueryAsync(ct);

        foreach (var actor in actors)
        {
            await using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO event_actors (event_id, entity_id) VALUES ($id, $actor);";
            ins.Parameters.AddWithValue("$id", id);
            ins.Parameters.AddWithValue("$actor", actor);
            await ins.ExecuteNonQueryAsync(ct);
        }
    }

    public async ValueTask<Event?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM events WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return await ReadEventAsync(conn, reader, ct);
    }

    public async ValueTask<IReadOnlyList<Event>> GetManyAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0) return [];

        await using var conn = db.Open();
        var results = new List<Event>(idList.Count);
        foreach (var id in idList)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM events WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                results.Add(await ReadEventAsync(conn, reader, ct));
        }
        return results;
    }

    public async IAsyncEnumerable<Event> QueryAsync(EventFilter filter, int limit, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        var where = BuildFilter(filter, cmd);
        cmd.CommandText = $"SELECT {Columns} FROM events {where} ORDER BY ts DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return await ReadEventAsync(conn, reader, ct);
    }

    public async ValueTask<IReadOnlyList<string>> SearchTextAsync(string query, EventFilter filter, int limit, CancellationToken ct = default)
    {
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        var where = BuildFilter(filter, cmd, "e.");
        var and = where.Length == 0 ? "WHERE" : $"{where} AND";
        cmd.CommandText = $"""
            SELECT e.id
            FROM events_fts f
            JOIN events e ON e.id = f.id
            {and} f.text MATCH $q
            ORDER BY f.rank
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$q", FtsEscape(query));
        cmd.Parameters.AddWithValue("$limit", limit);

        var ids = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetString(0));
        return ids;
    }

    private const string Columns = "id, ts, source, subtype, text, lat, lon, place_id, raw_file, raw_locator, metadata";

    private static async Task<Event> ReadEventAsync(SqliteConnection conn, SqliteDataReader reader, CancellationToken ct)
    {
        var id = reader.GetString(0);
        var actors = await LoadActorsAsync(conn, id, ct);
        return new Event
        {
            Id = id,
            Timestamp = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            Source = reader.GetString(2),
            SubType = reader.GetString(3),
            Text = reader.GetString(4),
            Latitude = reader.IsDBNull(5) ? null : reader.GetDouble(5),
            Longitude = reader.IsDBNull(6) ? null : reader.GetDouble(6),
            PlaceId = reader.IsDBNull(7) ? null : reader.GetString(7),
            Raw = new RawRef(reader.GetString(8), reader.GetString(9)),
            ActorIds = actors,
            Metadata = JsonMetadata.FromText(reader.GetString(10))
        };
    }

    private static async Task<IReadOnlyList<string>> LoadActorsAsync(SqliteConnection conn, string eventId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT entity_id FROM event_actors WHERE event_id = $id ORDER BY entity_id;";
        cmd.Parameters.AddWithValue("$id", eventId);
        var actors = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            actors.Add(reader.GetString(0));
        return actors;
    }

    private static string BuildFilter(EventFilter filter, SqliteCommand cmd, string prefix = "")
    {
        var clauses = new List<string>();
        if (filter.From is { } from)
        {
            clauses.Add($"{prefix}ts >= $from");
            cmd.Parameters.AddWithValue("$from", from.ToString("O", CultureInfo.InvariantCulture));
        }
        if (filter.To is { } to)
        {
            clauses.Add($"{prefix}ts <= $to");
            cmd.Parameters.AddWithValue("$to", to.ToString("O", CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrWhiteSpace(filter.Source))
        {
            clauses.Add($"{prefix}source = $source");
            cmd.Parameters.AddWithValue("$source", filter.Source);
        }
        if (!string.IsNullOrWhiteSpace(filter.SubType))
        {
            clauses.Add($"{prefix}subtype = $subtype");
            cmd.Parameters.AddWithValue("$subtype", filter.SubType);
        }
        return clauses.Count == 0 ? "" : "WHERE " + string.Join(" AND ", clauses);
    }

    /// <summary>
    /// Turn a free-text query into an FTS5 MATCH expression: each alphanumeric term is quoted
    /// (neutralising FTS operators) and OR-ed together, so multi-word queries match on any term
    /// rather than requiring the whole string as an exact phrase.
    /// </summary>
    private static string FtsEscape(string query)
    {
        var terms = query
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => new string(t.Where(char.IsLetterOrDigit).ToArray()))
            .Where(t => t.Length > 0)
            .Select(t => $"\"{t}\"");

        var expr = string.Join(" OR ", terms);
        return expr.Length == 0 ? "\"\"" : expr;
    }
}
