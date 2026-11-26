namespace Nivo.Services;

public static class JsonPayload
{
    public static JsonElement Object(string text, string requiredProperty) => FindObject(text, [requiredProperty], StringComparison.Ordinal);
    public static JsonElement ObjectIgnoreCase(string text, params string[] requiredProperties) => FindObject(text, requiredProperties, StringComparison.OrdinalIgnoreCase);
    private static JsonElement FindObject(string text, string[] requiredProperties, StringComparison comparison)
    {
        if (text.Length > 200_000) throw new InvalidDataException("The model JSON exceeds the response limit.");
        int position = -1;
        for (int attempt = 0; attempt < 32; attempt++)
        {
            position = text.IndexOf('{', position + 1); if (position < 0) break;
            try
            {
                var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text[position..]), new JsonReaderOptions { MaxDepth = 32 });
                using var doc = JsonDocument.ParseValue(ref reader);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.EnumerateObject().Any(p => requiredProperties.Any(name => p.Name.Equals(name, comparison)))) return doc.RootElement.Clone();
            }
            catch (JsonException) { continue; }
        }
        throw new InvalidDataException("The model did not return the required JSON object. Nothing was executed.");
    }
}
