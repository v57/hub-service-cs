using System.Text.Json;

namespace HubServiceCs;

// JsonBody decodes raw JSON values (System.Text.Json produces JsonElement for
// incoming JSON) into plain C# objects, mirroring what the TS/Go references
// hand to handlers: string, long/double, bool, null, Dictionary<string, object?>
// for objects, List<object?> for arrays.
internal static class JsonBody
{
    public static object? Decode(object? value)
    {
        return value switch
        {
            null => null,
            JsonElement element => DecodeElement(element),
            _ => value,
        };
    }

    private static object? DecodeElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            // Box each branch: a bare long/double ternary would promote every integer to double.
            JsonValueKind.Number => element.TryGetInt64(out var integer) ? (object)integer : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => DecodeElement(property.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(DecodeElement).ToList(),
            _ => null,
        };
    }
}
