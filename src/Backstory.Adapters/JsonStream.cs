using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Backstory.Adapters;

/// <summary>Streams the elements of a top-level JSON array file without loading it all into memory.</summary>
internal static class JsonStream
{
    public static async IAsyncEnumerable<JsonElement> ArrayAsync(
        string file, [EnumeratorCancellation] CancellationToken ct = default)
    {
        FileStream stream;
        try { stream = File.OpenRead(file); }
        catch { yield break; }

        await using (stream)
        {
            var source = JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(stream, options: null, ct);
            await using var e = source.GetAsyncEnumerator(ct);
            while (true)
            {
                JsonElement current;
                try
                {
                    if (!await e.MoveNextAsync()) break;
                    current = e.Current;
                }
                catch (JsonException)
                {
                    break; // stop at the first malformed element rather than failing the whole import
                }
                yield return current;
            }
        }
    }
}
