using Backstory.Core;

namespace Backstory.Storage;

/// <summary>
/// v1 vector store: persists embeddings in SQLite and searches with brute-force cosine over
/// in-memory float32 vectors. At personal scale (10k–1M vectors) this is milliseconds and
/// avoids a vector-DB dependency. Swap behind <see cref="IVectorStore"/> if scale demands it.
/// </summary>
public sealed class BruteForceVectorStore(SqliteDatabase db) : IVectorStore
{
    public async ValueTask AddAsync(string eventId, float[] vector, CancellationToken ct = default) =>
        await AddBatchAsync([(eventId, vector)], ct);

    public async ValueTask AddBatchAsync(IReadOnlyList<(string EventId, float[] Vector)> items, CancellationToken ct = default)
    {
        if (items.Count == 0) return;
        await using var conn = db.Open();
        await using var tx = conn.BeginTransaction();
        foreach (var (eventId, vector) in items)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO embeddings (event_id, vector) VALUES ($id, $vec)
                ON CONFLICT(event_id) DO UPDATE SET vector=$vec;
                """;
            cmd.Parameters.AddWithValue("$id", eventId);
            cmd.Parameters.AddWithValue("$vec", ToBytes(vector));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async ValueTask<IReadOnlyList<VectorHit>> SearchAsync(float[] query, int limit, CancellationToken ct = default)
    {
        var queryNorm = Norm(query);
        if (queryNorm == 0) return [];

        var hits = new List<VectorHit>();
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT event_id, vector FROM embeddings;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetString(0);
            var vec = FromBytes((byte[])reader[1]);
            if (vec.Length != query.Length) continue;
            var score = Cosine(query, queryNorm, vec);
            hits.Add(new VectorHit(id, score));
        }

        return hits.OrderByDescending(h => h.Score).Take(limit).ToList();
    }

    private static double Cosine(float[] a, double aNorm, float[] b)
    {
        double dot = 0, bNorm = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            bNorm += b[i] * b[i];
        }
        bNorm = Math.Sqrt(bNorm);
        return bNorm == 0 ? 0 : dot / (aNorm * bNorm);
    }

    private static double Norm(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += x * x;
        return Math.Sqrt(sum);
    }

    private static byte[] ToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] FromBytes(byte[] bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }
}
