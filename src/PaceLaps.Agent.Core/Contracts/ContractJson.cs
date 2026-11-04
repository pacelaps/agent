using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaceLaps.Agent.Core.Contracts;

/// <summary>
/// JSON settings for every message the agent sends or receives (schemas in <c>contracts/</c>, copied from the
/// web service): camelCase properties, snake_case enum values, null properties left out, UTC timestamps with
/// millisecond precision.
/// </summary>
public static class ContractJson
{
    /// <summary>Protocol version <c>v</c> the agent speaks.</summary>
    public const int Version = 1;

    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower), new TimestampConverter() },
    };

    public static string Serialize<T>(T message) => JsonSerializer.Serialize(message, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>"2026-11-07T19:03:11.250Z": the contract's timestamp format (UTC, ISO 8601, milliseconds).</summary>
    public static string FormatTimestamp(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private sealed class TimestampConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(FormatTimestamp(value));
    }
}
