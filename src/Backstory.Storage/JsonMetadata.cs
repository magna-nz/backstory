using System.Text.Json;

namespace Backstory.Storage;

/// <summary>Serialises/parses the untyped metadata bag carried on events and entities.</summary>
internal static class JsonMetadata
{
    public static string ToText(JsonElement element) =>
        element.ValueKind == JsonValueKind.Undefined ? "null" : element.GetRawText();

    public static JsonElement FromText(string text)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "null" : text);
        return doc.RootElement.Clone();
    }
}
