namespace Backstory.Embeddings;

/// <summary>
/// Downloads the all-MiniLM-L6-v2 ONNX model + vocab to the local model directory. This is the only
/// network access anywhere in Backstory, and it is explicit (a `model fetch` command), never silent.
/// </summary>
public static class ModelDownloader
{
    private const string ModelUrl = "https://huggingface.co/Xenova/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx";
    private const string VocabUrl = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt";

    public static async Task FetchAsync(string targetDir, Action<string>? log = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(targetDir);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        await DownloadAsync(http, ModelUrl, Path.Combine(targetDir, "model.onnx"), log, ct);
        await DownloadAsync(http, VocabUrl, Path.Combine(targetDir, "vocab.txt"), log, ct);
        log?.Invoke($"Model ready in {targetDir}");
    }

    private static async Task DownloadAsync(HttpClient http, string url, string dest, Action<string>? log, CancellationToken ct)
    {
        log?.Invoke($"Downloading {Path.GetFileName(dest)}…");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var tmp = dest + ".part";
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = File.Create(tmp))
            await source.CopyToAsync(target, ct);

        File.Move(tmp, dest, overwrite: true);
        log?.Invoke($"Saved {Path.GetFileName(dest)} ({new FileInfo(dest).Length / 1024 / 1024} MB)");
    }
}
