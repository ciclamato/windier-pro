using System.Text.Json;
using System.Text.Json.Serialization;
using PalmierPro.Core.Models;

namespace PalmierPro.Core.Serialization;

public static class PalmierJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new SwiftReferenceDateConverter());
        options.Converters.Add(new MediaSourceJsonConverter());
        return options;
    }
}

internal sealed class SwiftReferenceDateConverter : JsonConverter<DateTimeOffset>
{
    private static readonly DateTimeOffset SwiftReferenceDate = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var seconds)) return SwiftReferenceDate.AddSeconds(seconds);
        if (reader.TokenType == JsonTokenType.String && DateTimeOffset.TryParse(reader.GetString(), out var value)) return value;
        throw new JsonException("Expected a Swift Date value (seconds from 2001-01-01 or ISO-8601 text).");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteNumberValue((value.ToUniversalTime() - SwiftReferenceDate).TotalSeconds);
}

public sealed class MediaSourceJsonConverter : JsonConverter<MediaSource>
{
    public override MediaSource Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.TryGetProperty("external", out var external))
            return new MediaSource.External(external.GetProperty("absolutePath").GetString() ?? string.Empty);
        if (root.TryGetProperty("project", out var project))
            return new MediaSource.Project(project.GetProperty("relativePath").GetString() ?? string.Empty);
        throw new JsonException("Unsupported media source.");
    }

    public override void Write(Utf8JsonWriter writer, MediaSource value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case MediaSource.External external:
                writer.WriteStartObject("external");
                writer.WriteString("absolutePath", external.AbsolutePath);
                writer.WriteEndObject();
                break;
            case MediaSource.Project project:
                writer.WriteStartObject("project");
                writer.WriteString("relativePath", project.RelativePath);
                writer.WriteEndObject();
                break;
            default:
                throw new JsonException("Unsupported media source.");
        }
        writer.WriteEndObject();
    }
}
