namespace Backstory.Embeddings;

/// <summary>Pooling and normalisation used to turn token embeddings into one sentence vector.</summary>
public static class Pooling
{
    /// <summary>
    /// Attention-masked mean pooling: averages the per-token vectors, counting only real tokens.
    /// <paramref name="tokenEmbeddings"/> is row-major [seq × hidden]; <paramref name="mask"/> is 1 for
    /// real tokens, 0 for padding.
    /// </summary>
    public static float[] MeanPool(ReadOnlySpan<float> tokenEmbeddings, int seq, int hidden, ReadOnlySpan<long> mask)
    {
        var pooled = new float[hidden];
        long counted = 0;
        for (var t = 0; t < seq; t++)
        {
            if (mask[t] == 0) continue;
            counted++;
            var offset = t * hidden;
            for (var h = 0; h < hidden; h++)
                pooled[h] += tokenEmbeddings[offset + h];
        }

        if (counted > 0)
            for (var h = 0; h < hidden; h++)
                pooled[h] /= counted;

        return pooled;
    }

    /// <summary>L2-normalises a vector in place and returns it.</summary>
    public static float[] L2Normalize(float[] vector)
    {
        double sum = 0;
        foreach (var x in vector) sum += x * x;
        var norm = Math.Sqrt(sum);
        if (norm > 0)
            for (var i = 0; i < vector.Length; i++)
                vector[i] = (float)(vector[i] / norm);
        return vector;
    }
}
