using Backstory.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Backstory.Embeddings;

/// <summary>
/// Semantic embeddings via a local ONNX sentence-transformer (default all-MiniLM-L6-v2). Tokenises
/// with BERT WordPiece, runs inference with ONNX Runtime, mean-pools the token vectors, and
/// L2-normalises. Fully offline once the model is on disk. Implements <see cref="IEmbeddingService"/>
/// at 384 dimensions, so it is a drop-in replacement for the hashing embedder.
/// </summary>
public sealed class OnnxEmbeddingService : IEmbeddingService, IDisposable
{
    private const int MaxTokens = 256;

    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly string[] _inputNames;

    public int Dimension { get; }

    public OnnxEmbeddingService(string modelPath, string vocabPath, int dimension = 384)
    {
        _session = new InferenceSession(modelPath);
        _tokenizer = BertTokenizer.Create(vocabPath, new BertOptions { LowerCaseBeforeTokenization = true });
        _inputNames = _session.InputMetadata.Keys.ToArray();
        Dimension = dimension;
    }

    public ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default) => new(Embed(text));

    public ValueTask<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var vectors = new float[texts.Count][];
        for (var i = 0; i < texts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            vectors[i] = Embed(texts[i]);
        }
        return new ValueTask<IReadOnlyList<float[]>>(vectors);
    }

    private float[] Embed(string text)
    {
        var ids = _tokenizer.EncodeToIds(string.IsNullOrWhiteSpace(text) ? " " : text, true, true);
        var n = Math.Min(ids.Count, MaxTokens);
        if (n == 0) return new float[Dimension];

        var inputIds = new DenseTensor<long>([1, n]);
        var attentionMask = new DenseTensor<long>([1, n]);
        var tokenTypeIds = new DenseTensor<long>([1, n]);
        for (var i = 0; i < n; i++)
        {
            inputIds[0, i] = ids[i];
            attentionMask[0, i] = 1;
            tokenTypeIds[0, i] = 0;
        }

        // Only feed inputs this particular model actually declares.
        var inputs = new List<NamedOnnxValue>();
        if (_inputNames.Contains("input_ids")) inputs.Add(NamedOnnxValue.CreateFromTensor("input_ids", inputIds));
        if (_inputNames.Contains("attention_mask")) inputs.Add(NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask));
        if (_inputNames.Contains("token_type_ids")) inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds));

        using var results = _session.Run(inputs);
        var output = results.First().AsTensor<float>();
        var hidden = output.Dimensions[^1];

        var flat = output.ToArray(); // [1 * seq * hidden] row-major
        var maskArray = new long[n];
        for (var i = 0; i < n; i++) maskArray[i] = 1;

        var pooled = Pooling.MeanPool(flat, n, hidden, maskArray);
        return Pooling.L2Normalize(pooled);
    }

    public void Dispose() => _session.Dispose();
}
