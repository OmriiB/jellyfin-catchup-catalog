using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.CatchupCatalog.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CatchupCatalog;

public sealed class DispatcharrClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DispatcharrClient> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public DispatcharrClient(ILogger<DispatcharrClient> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Jellyfin-Catchup-Catalog", "0.1.0"));
    }

    internal async Task<List<LiveStreamDto>> GetLiveStreamsAsync(
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        Uri uri = BuildApiUri(configuration, "get_live_streams");
        using HttpResponseMessage response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<List<LiveStreamDto>>(
            content,
            _jsonOptions,
            cancellationToken).ConfigureAwait(false) ?? [];
    }

    internal async Task<EpgListingsDto> GetEpgAsync(
        PluginConfiguration configuration,
        int streamId,
        CancellationToken cancellationToken)
    {
        UriBuilder builder = new(BuildApiUri(configuration, "get_simple_data_table"));
        string query = builder.Query.TrimStart('&', '?');
        builder.Query = $"{query}&stream_id={streamId}";
        using HttpResponseMessage response = await _httpClient.GetAsync(builder.Uri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<EpgListingsDto>(
            content,
            _jsonOptions,
            cancellationToken).ConfigureAwait(false) ?? new EpgListingsDto();
    }

    internal string BuildPlaybackUrl(PluginConfiguration configuration, CatalogEntry entry)
    {
        string baseUrl = configuration.BaseUrl.TrimEnd('/');
        string start = entry.StartLocal.ToString(
            "yyyy-MM-dd:HH-mm",
            System.Globalization.CultureInfo.InvariantCulture);

        return $"{baseUrl}/streaming/timeshift.php"
            + $"?username={Uri.EscapeDataString(configuration.Username)}"
            + $"&password={Uri.EscapeDataString(configuration.Password)}"
            + $"&stream={entry.StreamId}"
            + $"&start={Uri.EscapeDataString(start)}"
            + $"&duration={entry.DurationMinutes}";
    }

    private static Uri BuildApiUri(PluginConfiguration configuration, string action)
    {
        string baseUrl = configuration.BaseUrl.TrimEnd('/');
        return new Uri(
            $"{baseUrl}/player_api.php"
            + $"?username={Uri.EscapeDataString(configuration.Username)}"
            + $"&password={Uri.EscapeDataString(configuration.Password)}"
            + $"&action={Uri.EscapeDataString(action)}");
    }

    public static string DecodePossiblyBase64(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(value);
            string decoded = Encoding.UTF8.GetString(bytes);
            if (decoded.Any(ch => !char.IsControl(ch) || ch is '\r' or '\n' or '\t'))
            {
                return decoded.Trim();
            }
        }
        catch (FormatException)
        {
            // Not Base64; use the original value.
        }

        return value.Trim();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
