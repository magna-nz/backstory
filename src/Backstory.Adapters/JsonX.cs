using System.Text;
using System.Text.Json;

namespace Backstory.Adapters;

/// <summary>Small, null-tolerant helpers over <see cref="JsonElement"/> for messy exports.</summary>
internal static class JsonX
{
    public static JsonElement? Prop(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value
            : null;

    public static string? Str(this JsonElement element, string name)
    {
        var prop = element.Prop(name);
        return prop is { ValueKind: JsonValueKind.String } p ? p.GetString() : null;
    }

    public static IEnumerable<JsonElement> Arr(this JsonElement element, string name)
    {
        var prop = element.Prop(name);
        if (prop is { ValueKind: JsonValueKind.Array } p)
            foreach (var item in p.EnumerateArray())
                yield return item;
    }

    /// <summary>Telegram message text is either a string or an array of string/{type,text} parts.</summary>
    public static string FlattenText(JsonElement textValue)
    {
        switch (textValue.ValueKind)
        {
            case JsonValueKind.String:
                return textValue.GetString() ?? "";
            case JsonValueKind.Array:
                var sb = new StringBuilder();
                foreach (var part in textValue.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.String)
                        sb.Append(part.GetString());
                    else if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var t))
                        sb.Append(t.GetString());
                }
                return sb.ToString();
            default:
                return "";
        }
    }
}
