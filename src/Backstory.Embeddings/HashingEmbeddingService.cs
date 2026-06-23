using System.Globalization;
using System.Text;
using Backstory.Core;

namespace Backstory.Embeddings;

/// <summary>
/// Dependency-free, fully offline embedding via signed feature hashing of word and character
/// n-grams into a fixed-dimension vector, L2-normalised. Quality is lexical rather than deeply
/// semantic, but it runs everywhere with zero model assets and is deterministic — which makes it
/// the right default and test embedder. The MiniLM/ONNX upgrade implements the same
/// <see cref="IEmbeddingService"/> at the same dimension, so it is a drop-in replacement.
/// </summary>
public sealed class HashingEmbeddingService : IEmbeddingService
{
    public int Dimension { get; }

    public HashingEmbeddingService(int dimension = 384) => Dimension = dimension;

    public ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default) =>
        new(Embed(text));

    public ValueTask<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var vectors = new float[texts.Count][];
        for (var i = 0; i < texts.Count; i++)
            vectors[i] = Embed(texts[i]);
        return new ValueTask<IReadOnlyList<float[]>>(vectors);
    }

    private float[] Embed(string text)
    {
        var vector = new float[Dimension];
        foreach (var token in Tokenize(text))
        {
            AddFeature(vector, token, weight: 1f);
            // Character trigrams give partial-match / typo robustness.
            foreach (var trigram in CharTrigrams(token))
                AddFeature(vector, "#" + trigram, weight: 0.5f);
        }

        Normalize(vector);
        return vector;
    }

    private void AddFeature(float[] vector, string feature, float weight)
    {
        var hash = Hash(feature);
        var index = (int)(hash % (uint)Dimension);
        var sign = (hash & 1) == 0 ? 1f : -1f;
        vector[index] += sign * weight;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            else if (sb.Length > 0)
            {
                yield return sb.ToString();
                sb.Clear();
            }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static IEnumerable<string> CharTrigrams(string token)
    {
        if (token.Length < 3) yield break;
        for (var i = 0; i <= token.Length - 3; i++)
            yield return token.Substring(i, 3);
    }

    private static void Normalize(float[] vector)
    {
        double sum = 0;
        foreach (var x in vector) sum += x * x;
        var norm = Math.Sqrt(sum);
        if (norm == 0) return;
        for (var i = 0; i < vector.Length; i++)
            vector[i] = (float)(vector[i] / norm);
    }

    // FNV-1a 32-bit — stable across runs and platforms (unlike string.GetHashCode).
    private static uint Hash(string value)
    {
        const uint prime = 16777619;
        uint hash = 2166136261;
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= prime;
        }
        return hash;
    }
}
