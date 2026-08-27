using System.Text.Json;
using System.Text.Json.Serialization;
using BytemountsAiStudio.Core.Assets;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Time;

namespace BytemountsAiStudio.Media.Timeline;

/// Timeline belgesinin JSON gidiş-gelişi.
///
/// §11: timeline bir BELGE. Bellekte bir nesne olarak taşımak yerine
/// serileştirilebilir olması üç şeyi mümkün kılıyor: varlık deposunda
/// saklanabilmesi, node'lar arasında geçebilmesi ve içerik hash'inin
/// render önbelleği anahtarı olabilmesi.
public static class TimelineJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static string Serialize(TimelineDocument timeline)
        => JsonSerializer.Serialize(timeline, Options);

    public static TimelineDocument? Deserialize(string json)
        => JsonSerializer.Deserialize<TimelineDocument>(json, Options);

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        options.Converters.Add(new MsConverter());
        options.Converters.Add(new TimeRangeConverter());
        options.Converters.Add(new AssetRefConverter());
        options.Converters.Add(new LanguageTagConverter());
        options.Converters.Add(new CanvasConverter());

        return options;
    }
}

/// Süreler JSON'da düz sayı: `4820`. Nesne olarak yazmak (`{"value":4820}`)
/// belgeyi hem şişirir hem okunmaz yapardı.
internal sealed class MsConverter : JsonConverter<Ms>
{
    public override Ms Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetInt32());

    public override void Write(Utf8JsonWriter writer, Ms value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value.Value);
}

/// Aralık iki elemanlı dizi: `[0, 4820]`. Yarı açık olduğu şema
/// dokümantasyonunda; burada kısa tutuluyor.
internal sealed class TimeRangeConverter : JsonConverter<TimeRange>
{
    public override TimeRange Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        reader.Read();
        var start = reader.GetInt32();
        reader.Read();
        var end = reader.GetInt32();
        reader.Read();   // ]

        return new TimeRange(new Ms(start), new Ms(end));
    }

    public override void Write(Utf8JsonWriter writer, TimeRange value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.Start.Value);
        writer.WriteNumberValue(value.End.Value);
        writer.WriteEndArray();
    }
}

/// `"sha256:9f8e..."` — önek okunabilirlik için, ayrıştırma ikisini de kabul eder.
internal sealed class AssetRefConverter : JsonConverter<AssetRef>
{
    public override AssetRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => AssetRef.Create(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, AssetRef value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}

internal sealed class LanguageTagConverter : JsonConverter<LanguageTag>
{
    public override LanguageTag Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => LanguageTag.Create(reader.GetString() ?? "en-US");

    public override void Write(Utf8JsonWriter writer, LanguageTag value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}

internal sealed class CanvasConverter : JsonConverter<Canvas>
{
    public override Canvas Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        int width = 0, height = 0, fps = 30;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            var name = reader.GetString();
            reader.Read();

            switch (name)
            {
                case "width": width = reader.GetInt32(); break;
                case "height": height = reader.GetInt32(); break;
                case "fps": fps = reader.GetInt32(); break;
                default: reader.Skip(); break;
            }
        }

        return new Canvas(width, height, fps);
    }

    public override void Write(Utf8JsonWriter writer, Canvas value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("width", value.Width);
        writer.WriteNumber("height", value.Height);
        writer.WriteNumber("fps", value.Fps);
        writer.WriteEndObject();
    }
}
