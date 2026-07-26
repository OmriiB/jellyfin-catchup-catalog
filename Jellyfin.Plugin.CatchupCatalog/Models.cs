using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.CatchupCatalog;

internal sealed class LiveStreamDto
{
    [JsonPropertyName("stream_id")]
    public int StreamId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("stream_icon")]
    public string StreamIcon { get; set; } = string.Empty;

    [JsonPropertyName("tv_archive")]
    public JsonElementValue TvArchive { get; set; }

    [JsonPropertyName("tv_archive_duration")]
    public JsonElementValue TvArchiveDuration { get; set; }
}

internal sealed class EpgListingsDto
{
    [JsonPropertyName("epg_listings")]
    public List<EpgItemDto> Listings { get; set; } = [];
}

internal sealed class EpgItemDto
{
    [JsonPropertyName("id")]
    public JsonElementValue Id { get; set; }

    [JsonPropertyName("title")]
    public string TitleRaw { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string DescriptionRaw { get; set; } = string.Empty;

    [JsonPropertyName("start_timestamp")]
    public JsonElementValue StartTimestamp { get; set; }

    [JsonPropertyName("stop_timestamp")]
    public JsonElementValue StopTimestamp { get; set; }

    [JsonPropertyName("start")]
    public string StartLocalRaw { get; set; } = string.Empty;

    [JsonPropertyName("has_archive")]
    public JsonElementValue HasArchive { get; set; }
}

[JsonConverter(typeof(JsonElementValueConverter))]
internal readonly record struct JsonElementValue(string Value)
{
    public int AsInt(int fallback = 0) =>
        int.TryParse(Value, out int result) ? result : fallback;

    public long AsLong(long fallback = 0) =>
        long.TryParse(Value, out long result) ? result : fallback;

    public bool AsBool()
    {
        string value = Value.Trim();
        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class JsonElementValueConverter : System.Text.Json.Serialization.JsonConverter<JsonElementValue>
{
    public override JsonElementValue Read(
        ref System.Text.Json.Utf8JsonReader reader,
        Type typeToConvert,
        System.Text.Json.JsonSerializerOptions options)
    {
        string value = reader.TokenType switch
        {
            System.Text.Json.JsonTokenType.String => reader.GetString() ?? string.Empty,
            System.Text.Json.JsonTokenType.Number => reader.TryGetInt64(out long number)
                ? number.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            System.Text.Json.JsonTokenType.True => "true",
            System.Text.Json.JsonTokenType.False => "false",
            System.Text.Json.JsonTokenType.Null => string.Empty,
            _ => string.Empty
        };
        return new JsonElementValue(value);
    }

    public override void Write(
        System.Text.Json.Utf8JsonWriter writer,
        JsonElementValue value,
        System.Text.Json.JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

internal enum CatalogKind
{
    Movie,
    SeriesEpisode,
    Program
}

internal sealed class CatalogEntry
{
    public required string Id { get; init; }

    public required CatalogKind Kind { get; set; }

    public required int StreamId { get; init; }

    public required string ChannelName { get; init; }

    public required string Title { get; set; }

    public string SeriesTitle { get; set; } = string.Empty;

    public string Overview { get; set; } = string.Empty;

    public string ImageUrl { get; set; } = string.Empty;

    public string BackdropUrl { get; set; } = string.Empty;

    public DateTime StartUtc { get; init; }

    public DateTime StartLocal { get; init; }

    public DateTime EndUtc { get; init; }

    public int DurationMinutes { get; init; }

    public int? ProductionYear { get; set; }

    public float? Rating { get; set; }

    public int SeasonNumber { get; set; } = 1;

    public int EpisodeNumber { get; set; }

    public List<string> Genres { get; set; } = [];
}

internal sealed class CatalogSnapshot
{
    public DateTime GeneratedUtc { get; init; } = DateTime.UtcNow;

    public List<CatalogEntry> Entries { get; init; } = [];
}

internal sealed class TmdbMatch
{
    public string Title { get; init; } = string.Empty;

    public string Overview { get; init; } = string.Empty;

    public string PosterUrl { get; init; } = string.Empty;

    public string BackdropUrl { get; init; } = string.Empty;

    public int? Year { get; init; }

    public float? Rating { get; init; }
}
