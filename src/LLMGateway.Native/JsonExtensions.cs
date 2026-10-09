using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LLMGateway.Native;

internal static class JsonExtensions
{
    public static JsonElement? Prop(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    public static JsonElement? Prop(this JsonElement? element, string name) => element is { } value ? value.Prop(name) : null;

    public static string? Str(this JsonElement element, string name) => element.Prop(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    public static string? Str(this JsonElement? element, string name) => element is { } value ? value.Str(name) : null;

    public static double? Num(this JsonElement element, string name) => element.Prop(name) switch
    {
        { ValueKind: JsonValueKind.Number } value => value.GetDouble(),
        { ValueKind: JsonValueKind.String } value when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
        _ => null
    };

    public static double? Num(this JsonElement? element, string name) => element is { } value ? value.Num(name) : null;

    public static int Int(this JsonElement? element, params string[] names)
    {
        foreach (var name in names)
            if (element.Num(name) is { } value) return (int)Math.Min(int.MaxValue, Math.Max(0, value));
        return 0;
    }

    public static int Int(this JsonElement element, params string[] names) => ((JsonElement?)element).Int(names);

    public static DateTimeOffset? Date(this JsonElement element, string name) => ((JsonElement?)element).Date(name);

    public static IEnumerable<JsonElement> Items(this JsonElement element) => ((JsonElement?)element).Items();

    public static bool Bool(this JsonElement element, string name) => element.Prop(name) is { ValueKind: JsonValueKind.True };

    public static DateTimeOffset? Date(this JsonElement? element, string name)
    {
        var value = element.Prop(name);
        return value switch
        {
            { ValueKind: JsonValueKind.Number } number when number.TryGetInt64(out var unix) =>
                unix > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(unix) : DateTimeOffset.FromUnixTimeSeconds(unix),
            { ValueKind: JsonValueKind.String } text when DateTimeOffset.TryParse(text.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) => date,
            _ => null
        };
    }

    public static IEnumerable<JsonElement> Items(this JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    /// <summary>Concatenates text blocks of an Anthropic/Cursor style <c>content</c> array.</summary>
    public static string ContentText(this JsonElement? message, string type = "text")
    {
        var content = message.Prop("content");
        if (content is { ValueKind: JsonValueKind.String } text) return text.GetString() ?? string.Empty;
        var builder = new StringBuilder();
        foreach (var block in content.Items())
            if (block.Str("type") == type && block.Str("text") is { } value) builder.Append(value);
        return builder.ToString();
    }
}
