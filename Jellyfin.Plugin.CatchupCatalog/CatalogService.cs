using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.CatchupCatalog.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CatchupCatalog;

public sealed partial class CatalogService
{
    private static long _globalVersion;
    private readonly DispatcharrClient _dispatcharr;
    private readonly TmdbClient _tmdb;
    private readonly ILogger<CatalogService> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private CatalogSnapshot? _snapshot;
    private int _configurationHash;

    public CatalogService(
        DispatcharrClient dispatcharr,
        TmdbClient tmdb,
        ILogger<CatalogService> logger)
    {
        _dispatcharr = dispatcharr;
        _tmdb = tmdb;
        _logger = logger;
    }

    public static string VersionToken => Interlocked.Read(ref _globalVersion).ToString(CultureInfo.InvariantCulture);

    public static void InvalidateGlobal() => Interlocked.Increment(ref _globalVersion);

    internal async Task<CatalogSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        PluginConfiguration configuration = Plugin.Instance.Configuration;
        int configurationHash = HashCode.Combine(
            configuration.BaseUrl,
            configuration.Username,
            configuration.Password,
            configuration.ArchiveDays,
            configuration.CacheMinutes,
            configuration.TmdbBearerToken,
            configuration.MetadataLanguage);

        int cacheMinutes = Math.Clamp(configuration.CacheMinutes, 5, 720);
        if (_snapshot is not null
            && _configurationHash == configurationHash
            && DateTime.UtcNow - _snapshot.GeneratedUtc < TimeSpan.FromMinutes(cacheMinutes))
        {
            return _snapshot;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_snapshot is not null
                && _configurationHash == configurationHash
                && DateTime.UtcNow - _snapshot.GeneratedUtc < TimeSpan.FromMinutes(cacheMinutes))
            {
                return _snapshot;
            }

            _snapshot = await BuildSnapshotAsync(configuration, cancellationToken).ConfigureAwait(false);
            _configurationHash = configurationHash;
            Interlocked.Increment(ref _globalVersion);
            return _snapshot;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<CatalogSnapshot> BuildSnapshotAsync(
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!configuration.Enabled
            || string.IsNullOrWhiteSpace(configuration.BaseUrl)
            || string.IsNullOrWhiteSpace(configuration.Username)
            || string.IsNullOrWhiteSpace(configuration.Password))
        {
            return new CatalogSnapshot();
        }

        List<LiveStreamDto> streams = await _dispatcharr.GetLiveStreamsAsync(
            configuration,
            cancellationToken).ConfigureAwait(false);

        List<LiveStreamDto> catchupStreams = streams
            .Where(stream => stream.TvArchive.AsBool() || stream.TvArchiveDuration.AsInt() > 0)
            .ToList();

        ConcurrentBag<CatalogEntry> entries = [];
        int concurrency = Math.Clamp(configuration.MaxConcurrentEpgRequests, 1, 16);
        using SemaphoreSlim semaphore = new(concurrency, concurrency);
        List<Task> tasks = [];

        foreach (LiveStreamDto stream in catchupStreams)
        {
            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await AddStreamEntriesAsync(
                        configuration,
                        stream,
                        entries,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
                {
                    _logger.LogWarning(
                        ex,
                        "Could not load catch-up EPG for stream {StreamId} ({StreamName})",
                        stream.StreamId,
                        stream.Name);
                }
                finally
                {
                    semaphore.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        List<CatalogEntry> list = entries
            .GroupBy(entry => entry.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(entry => entry.StartUtc)
            .ToList();

        ApplyRepeatedTitleClassification(list);
        ApplyEpisodeNumbers(list);
        await ApplyMetadataAsync(configuration, list, cancellationToken).ConfigureAwait(false);

        return new CatalogSnapshot
        {
            Entries = list
        };
    }

    private async Task AddStreamEntriesAsync(
        PluginConfiguration configuration,
        LiveStreamDto stream,
        ConcurrentBag<CatalogEntry> entries,
        CancellationToken cancellationToken)
    {
        EpgListingsDto epg = await _dispatcharr.GetEpgAsync(
            configuration,
            stream.StreamId,
            cancellationToken).ConfigureAwait(false);

        int archiveDays = Math.Clamp(
            Math.Min(
                configuration.ArchiveDays,
                stream.TvArchiveDuration.AsInt(configuration.ArchiveDays)),
            1,
            30);
        DateTime cutoff = DateTime.UtcNow.AddDays(-archiveDays);
        DateTime now = DateTime.UtcNow.AddMinutes(5);

        foreach (EpgItemDto item in epg.Listings)
        {
            DateTime startUtc = UnixToUtc(item.StartTimestamp.AsLong());
            DateTime endUtc = UnixToUtc(item.StopTimestamp.AsLong());
            if (startUtc == default || endUtc <= startUtc || startUtc < cutoff || startUtc > now)
            {
                continue;
            }

            string title = DispatcharrClient.DecodePossiblyBase64(item.TitleRaw);
            string description = DispatcharrClient.DecodePossiblyBase64(item.DescriptionRaw);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            DateTime startLocal = ParseLocalStart(item.StartLocalRaw, startUtc);
            int durationMinutes = Math.Max(
                1,
                (int)Math.Ceiling((endUtc - startUtc).TotalMinutes));

            (CatalogKind kind, string seriesTitle, int season, int episode) =
                Classify(title, description, durationMinutes);

            entries.Add(new CatalogEntry
            {
                Id = StableId($"{stream.StreamId}:{item.Id.Value}:{startUtc.Ticks}:{title}"),
                Kind = kind,
                StreamId = stream.StreamId,
                ChannelName = stream.Name,
                Title = title,
                SeriesTitle = seriesTitle,
                Overview = description,
                ImageUrl = stream.StreamIcon,
                StartUtc = startUtc,
                StartLocal = startLocal,
                EndUtc = endUtc,
                DurationMinutes = durationMinutes,
                SeasonNumber = season,
                EpisodeNumber = episode
            });
        }
    }

    private async Task ApplyMetadataAsync(
        PluginConfiguration configuration,
        List<CatalogEntry> entries,
        CancellationToken cancellationToken)
    {
        Dictionary<string, List<CatalogEntry>> groups = entries
            .Where(entry => entry.Kind is CatalogKind.Movie or CatalogKind.SeriesEpisode)
            .GroupBy(
                entry => entry.Kind == CatalogKind.Movie ? entry.Title : entry.SeriesTitle,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        using SemaphoreSlim semaphore = new(5, 5);
        List<Task> tasks = [];
        foreach ((string title, List<CatalogEntry> matchingEntries) in groups)
        {
            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    bool searchTv = matchingEntries[0].Kind == CatalogKind.SeriesEpisode;
                    TmdbMatch? match = await _tmdb.SearchAsync(
                        configuration,
                        title,
                        searchTv,
                        cancellationToken).ConfigureAwait(false);

                    if (match is null)
                    {
                        return;
                    }

                    foreach (CatalogEntry entry in matchingEntries)
                    {
                        if (!string.IsNullOrWhiteSpace(match.PosterUrl))
                        {
                            entry.ImageUrl = match.PosterUrl;
                        }

                        entry.BackdropUrl = match.BackdropUrl;
                        entry.ProductionYear = match.Year;
                        entry.Rating = match.Rating;
                        if (string.IsNullOrWhiteSpace(entry.Overview))
                        {
                            entry.Overview = match.Overview;
                        }

                        if (entry.Kind == CatalogKind.Movie && !string.IsNullOrWhiteSpace(match.Title))
                        {
                            entry.Title = match.Title;
                        }
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static void ApplyRepeatedTitleClassification(List<CatalogEntry> entries)
    {
        foreach (IGrouping<string, CatalogEntry> group in entries
            .Where(entry => entry.Kind == CatalogKind.Program)
            .GroupBy(entry => NormalizeTitle(entry.Title), StringComparer.OrdinalIgnoreCase))
        {
            List<CatalogEntry> values = group.OrderBy(entry => entry.StartUtc).ToList();
            if (values.Count < 2)
            {
                continue;
            }

            foreach (CatalogEntry entry in values)
            {
                entry.Kind = CatalogKind.SeriesEpisode;
                entry.SeriesTitle = CleanSeriesTitle(entry.Title);
                entry.SeasonNumber = 1;
            }
        }
    }

    private static void ApplyEpisodeNumbers(List<CatalogEntry> entries)
    {
        foreach (IGrouping<string, CatalogEntry> group in entries
            .Where(entry => entry.Kind == CatalogKind.SeriesEpisode)
            .GroupBy(entry => entry.SeriesTitle, StringComparer.OrdinalIgnoreCase))
        {
            int generatedEpisode = 1;
            foreach (CatalogEntry entry in group.OrderBy(entry => entry.StartUtc))
            {
                if (entry.EpisodeNumber <= 0)
                {
                    entry.EpisodeNumber = generatedEpisode;
                }

                generatedEpisode = Math.Max(generatedEpisode + 1, entry.EpisodeNumber + 1);
            }
        }
    }

    private static (CatalogKind Kind, string SeriesTitle, int Season, int Episode) Classify(
        string title,
        string description,
        int durationMinutes)
    {
        string combined = $"{title} {description}";

        Match match = SeasonEpisodeRegex().Match(combined);
        if (match.Success)
        {
            return (
                CatalogKind.SeriesEpisode,
                CleanSeriesTitle(title),
                ParseGroup(match, "season", 1),
                ParseGroup(match, "episode", 0));
        }

        match = HebrewSeasonEpisodeRegex().Match(combined);
        if (match.Success)
        {
            return (
                CatalogKind.SeriesEpisode,
                CleanSeriesTitle(title),
                ParseGroup(match, "season", 1),
                ParseGroup(match, "episode", 0));
        }

        if (ContainsAny(combined, "סדרה", "עונה", "פרק", "episode", "season"))
        {
            return (CatalogKind.SeriesEpisode, CleanSeriesTitle(title), 1, 0);
        }

        if (ContainsAny(combined, "סרט", "movie", "film", "cinema")
            || durationMinutes >= 70)
        {
            return (CatalogKind.Movie, string.Empty, 0, 0);
        }

        return (CatalogKind.Program, string.Empty, 0, 0);
    }

    private static int ParseGroup(Match match, string group, int fallback) =>
        int.TryParse(match.Groups[group].Value, out int value) ? value : fallback;

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string CleanSeriesTitle(string title)
    {
        string cleaned = SeasonEpisodeRegex().Replace(title, string.Empty);
        cleaned = HebrewSeasonEpisodeRegex().Replace(cleaned, string.Empty);
        return cleaned.Trim(' ', '-', ':', '|');
    }

    private static string NormalizeTitle(string title) =>
        Regex.Replace(CleanSeriesTitle(title).ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();

    private static DateTime UnixToUtc(long value)
    {
        if (value <= 0)
        {
            return default;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(value).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return default;
        }
    }

    private static DateTime ParseLocalStart(string raw, DateTime startUtc)
    {
        if (DateTime.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out DateTime result))
        {
            return DateTime.SpecifyKind(result, DateTimeKind.Unspecified);
        }

        return startUtc.ToLocalTime();
    }

    public static string StableId(string value)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash).ToString();
    }

    [GeneratedRegex(
        @"\bS(?<season>\d{1,2})\s*E(?<episode>\d{1,3})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonEpisodeRegex();

    [GeneratedRegex(
        @"עונה\s*(?<season>\d{1,2}).{0,12}?פרק\s*(?<episode>\d{1,3})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HebrewSeasonEpisodeRegex();
}
