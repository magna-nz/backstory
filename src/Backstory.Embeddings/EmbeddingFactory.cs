using Backstory.Core;

namespace Backstory.Embeddings;

/// <summary>
/// Selects the embedding service for a vault: the ONNX semantic model if it has been fetched,
/// otherwise the always-available offline hashing embedder. Both produce 384-dim vectors, so a vault
/// can be upgraded by fetching the model — though re-importing is needed to re-embed existing events
/// with the better model.
/// </summary>
public static class EmbeddingFactory
{
    public static string DefaultModelDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".backstory", "models", "all-MiniLM-L6-v2");
    }

    public static (IEmbeddingService Service, string Name) Create(string? modelDir = null)
    {
        modelDir ??= DefaultModelDir();
        var model = Path.Combine(modelDir, "model.onnx");
        var vocab = Path.Combine(modelDir, "vocab.txt");

        if (File.Exists(model) && File.Exists(vocab))
        {
            try
            {
                return (new OnnxEmbeddingService(model, vocab), "onnx-minilm");
            }
            catch
            {
                // Corrupt/incompatible model — fall back rather than fail the whole command.
            }
        }

        return (new HashingEmbeddingService(), "hashing");
    }
}
