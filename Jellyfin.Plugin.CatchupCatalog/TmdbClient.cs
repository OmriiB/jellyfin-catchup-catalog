using System.Net.Http.Headers;
using System.Text.Json;
using Jellyfin.Plugin.CatchupCatalog.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CatchupCatalog;

public sealed class TmdbClient : IDisposable
{
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(25)
    };

    private readonly ILogger<TmdbClient> _logger;
    private readonly Dictionary<string, TmdbMatch?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TmdbClient(ILogger<TmdbClient> logger)
    {
        _logger = logger;
    }

    internal async Task<TmdbMatch?> SearchAsync(
        PluginConfiguration configuration,
        string title,
        bool searchTv,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.TmdbBearerToken)
            || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        string cacheKey = $"{(searchTv ? "tv" : "movie")}:{configuration.MetadataLanguage}:{title}";
        lock (_cache)
        {
            if (_cache.TryGetValue(cacheKey, out TmdbMatch? cached))
            {
                return cached;
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_cache)
            {
                if (_cache.TryGetValue(cacheKey, out TmdbMatch? cached))
                {
                    return cached;
                }
            }

            string endpoint = searchTv ? "tv" : "movie";
            string language = string.IsNullOrWhiteSpace(configuration.MetadataLanguage)
                ? "he-IL"
                : configuration.MetadataLanguage;
            string uri = "https://api.themoviedb.org/3/search/"
                + endpoint
                + "?query=" + Uri.EscapeDataString(title)
                + "&include_adult=false&language=" + Uri.EscapeDataString(language)
                + "&page=1";

            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                configuration.TmdbBearerToken.Trim());

            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "TMDb lookup failed for {Title}: HTTP {StatusCode}",
                    title,
                    (int)response.StatusCode);
                lock (_cache)
                {
                    _cache[cacheKey] = null;
                }
                return null;
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            JsonElement results = document.RootElement.GetProperty("results");
            if (results.GetArrayLength() == 0)
            {
                lock (_cache)
                {
                    _cache[cacheKey] = null;
                }
                return null;
            }

            JsonElement first = results[0];
            string matchedTitle = GetString(first, searchTv ? "name" : "title");
            string overview = GetString(first, "overview");
            string posterPath = GetString(first, "poster_path");
            string backdropPath = GetString(first, "backdrop_path");
            string date = GetString(first, searchTv ? "first_air_date" : "release_date");
            int? year = DateTime.TryParse(date, out DateTime parsedDate) ? parsedDate.Year : null;
            float? rating = first.TryGetProperty("vote_average", out JsonElement vote)
                && vote.TryGetSingle(out float value)
                    ? value
                    : null;

            TmdbMatch match = new()
            {
                Title = string.IsNullOrWhiteSpace(matchedTitle) ? title : matchedTitle,
                Overview = overview,
                PosterUrl = string.IsNullOrWhiteSpace(posterPath)
                    ? string.Empty
                    : "https://image.tmdb.org/t/p/w500" + posterPath,
                BackdropUrl = string.IsNullOrWhiteSpace(backdropPath)
                    ? string.Empty
                    : "https://image.tmdb.org/t/p/w780" + backdropPath,
                Year = year,
                Rating = rating
            };

            lock (_cache)
            {
                _cache[cacheKey] = match;
            }

            return match;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "TMDb lookup failed for {Title}", title);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    public void Dispose()
    {
        _httpClient.Dispose();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }
}
