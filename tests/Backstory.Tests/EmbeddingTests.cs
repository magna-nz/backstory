using Backstory.Embeddings;

namespace Backstory.Tests;

public class EmbeddingTests
{
    [Fact]
    public void MeanPool_averages_only_masked_tokens()
    {
        // 2 tokens, hidden=2: [[1,2],[3,4]]
        float[] embeddings = [1, 2, 3, 4];

        var both = Pooling.MeanPool(embeddings, seq: 2, hidden: 2, mask: [1, 1]);
        Assert.Equal([2f, 3f], both);

        var firstOnly = Pooling.MeanPool(embeddings, seq: 2, hidden: 2, mask: [1, 0]);
        Assert.Equal([1f, 2f], firstOnly);
    }

    [Fact]
    public void L2Normalize_gives_unit_length()
    {
        var v = Pooling.L2Normalize([3f, 4f]);
        Assert.Equal(0.6f, v[0], 5);
        Assert.Equal(0.8f, v[1], 5);
    }

    [Fact]
    public void Factory_falls_back_to_hashing_when_model_absent()
    {
        using var tmp = new TempDir();
        var (service, name) = EmbeddingFactory.Create(tmp.Path);

        Assert.Equal("hashing", name);
        Assert.IsType<HashingEmbeddingService>(service);
        Assert.Equal(384, service.Dimension);
    }

    [Fact]
    public async Task Hashing_embedder_is_deterministic_and_normalised()
    {
        var svc = new HashingEmbeddingService();
        var a = await svc.EmbedAsync("ramen in tokyo");
        var b = await svc.EmbedAsync("ramen in tokyo");

        Assert.Equal(a, b);
        var norm = Math.Sqrt(a.Sum(x => (double)x * x));
        Assert.Equal(1.0, norm, 3);
    }
}
