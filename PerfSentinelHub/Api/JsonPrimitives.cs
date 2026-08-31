using System.Text.Json;

namespace PerfSentinelHub.Api;

/// <summary>
///     The one copy of the typed-property readers this codebase kept growing:
///     a missing property, a wrong kind, and an unreadable number all answer null,
///     which every caller treats as "the daemon did not say".
/// </summary>
internal static class JsonRead
{
    public static string? ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    public static long? ReadLong(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt64(out var number)
            ? number
            : null;
    }
}

/// <summary>Null-tolerant writers shared by the hand-rolled JSON writers.</summary>
internal static class JsonWrite
{
    public static void StringOrNull(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }

    public static void NumberOrNull(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is { } number)
            writer.WriteNumber(name, number);
        else
            writer.WriteNull(name);
    }
}
