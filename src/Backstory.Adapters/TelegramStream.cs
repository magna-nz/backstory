using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Backstory.Adapters;

/// <summary>
/// Streams a Telegram <c>result.json</c> without loading it into memory. A full account export is a
/// single deeply nested file whose <c>messages</c> arrays can be many gigabytes, so this walks the
/// JSON token by token over a <see cref="PipeReader"/> and materialises only one message (or contact)
/// at a time. Memory stays bounded to roughly one object plus the pipe's buffer.
/// </summary>
internal static class TelegramStream
{
    public enum ItemKind { Message, Contact }

    public static async IAsyncEnumerable<(ItemKind Kind, string Chat, JsonElement Element)> ReadAsync(
        string file, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(file);
        var pipe = PipeReader.Create(stream);
        var ctx = new Context();
        var state = new JsonReaderState();

        while (true)
        {
            var result = await pipe.ReadAsync(ct);
            var buffer = result.Buffer;
            var items = new List<(ItemKind, string, JsonDocument)>();

            var consumed = Process(buffer, result.IsCompleted, ref state, ctx, items);

            pipe.AdvanceTo(consumed, buffer.End);

            foreach (var (kind, chat, doc) in items)
            {
                using (doc)
                    yield return (kind, chat, doc.RootElement.Clone());
            }

            if (result.IsCompleted) break;
        }

        await pipe.CompleteAsync();
    }

    private sealed class Context
    {
        public string Chat = "Saved Messages";
        public string? Property;
        public readonly Stack<string?> Containers = new();
        public bool InTargetArray;
        public ItemKind CurrentKind;
    }

    // Walks as many complete tokens as the buffer holds, materialising target objects. Returns the
    // position consumed; anything after it is a partial token to be retried once more data arrives.
    private static SequencePosition Process(
        ReadOnlySequence<byte> buffer, bool isFinalBlock, ref JsonReaderState state,
        Context ctx, List<(ItemKind, string, JsonDocument)> items)
    {
        var reader = new Utf8JsonReader(buffer, isFinalBlock, state);

        while (true)
        {
            if (ctx.InTargetArray)
            {
                // We are inside a messages/contacts array. Advance onto the next token: either the
                // closing ']' (done), or an element's '{' which we then parse as a single object.
                var save = reader; // rewind point if the element spans past this buffer
                if (!reader.Read())
                {
                    reader = save;
                    break;
                }

                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    if (ctx.Containers.Count > 0) ctx.Containers.Pop();
                    ctx.InTargetArray = false;
                    continue;
                }

                // reader is now positioned at the element's StartObject; parse that one object.
                if (!JsonDocument.TryParseValue(ref reader, out var doc))
                {
                    reader = save;
                    break;
                }
                items.Add((ctx.CurrentKind, ctx.Chat, doc!));
                continue;
            }

            var checkpoint = reader;
            if (!reader.Read())
            {
                reader = checkpoint;
                break;
            }

            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    ctx.Property = reader.GetString();
                    continue;

                case JsonTokenType.StartObject:
                    ctx.Containers.Push(ctx.Property);
                    ctx.Property = null;
                    continue;

                case JsonTokenType.EndObject:
                    if (ctx.Containers.Count > 0) ctx.Containers.Pop();
                    ctx.Property = null;
                    continue;

                case JsonTokenType.StartArray:
                    ctx.Containers.Push(ctx.Property);
                    ctx.Property = null;
                    if (ArrayTargetKind(ctx) is { } kind)
                    {
                        ctx.InTargetArray = true;
                        ctx.CurrentKind = kind;
                    }
                    continue;

                case JsonTokenType.EndArray:
                    if (ctx.Containers.Count > 0) ctx.Containers.Pop();
                    ctx.Property = null;
                    continue;

                case JsonTokenType.String:
                    if (ctx.Property == "name" && reader.GetString() is { Length: > 0 } name)
                        ctx.Chat = name;
                    ctx.Property = null;
                    continue;

                default:
                    ctx.Property = null;
                    continue;
            }
        }

        state = reader.CurrentState;
        return buffer.GetPosition(reader.BytesConsumed);
    }

    // True for an array we drain element by element: a "messages" array, or "contacts" → "list".
    // Chat objects ("chats" → "list") are descended into normally, not drained.
    private static ItemKind? ArrayTargetKind(Context ctx)
    {
        if (ctx.Containers.Count == 0) return null;
        var top = ctx.Containers.Peek();
        if (top == "messages") return ItemKind.Message;
        if (top == "list" && ctx.Containers.Count >= 2 && ctx.Containers.ElementAt(1) == "contacts")
            return ItemKind.Contact;
        return null;
    }
}
