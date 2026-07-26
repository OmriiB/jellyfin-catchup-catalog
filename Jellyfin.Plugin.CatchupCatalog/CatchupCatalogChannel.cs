using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CatchupCatalog;

public sealed class CatchupCatalogChannel(
    CatalogService catalogService,
    DispatcharrClient dispatcharrClient,
    ILogger<CatchupCatalogChannel> logger)
    : IChannel, IDisableMediaSourceDisplay
{
    private static readonly string MoviesRootId = CatalogService.StableId("root:movies");
    private static readonly string SeriesRootId = CatalogService.StableId("root:series");
    private static readonly string ProgramsRootId = CatalogService.StableId("root:programs");

    public string? Name => "Catch-up Catalog";

    public string? Description =>
        "Catch-up movies, series and programs organized from Dispatcharr EPG.";

    public string DataVersion => Plugin.Instance.DataVersion;

    public string HomePageUrl => string.Empty;

    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        ContentTypes =
        [
            ChannelMediaContentType.Movie,
            ChannelMediaContentType.Episode,
            ChannelMediaContentType.TvExtra
        ],
        MediaTypes =
        [
            ChannelMediaType.Video
        ]
    };

    public Task<DynamicImageResponse> GetChannelImage(
        ImageType type,
        CancellationToken cancellationToken) =>
        throw new ArgumentException("Unsupported image type: " + type);

    public IEnumerable<ImageType> GetSupportedChannelImages() => [];

    public async Task<ChannelItemResult> GetChannelItems(
        InternalChannelItemQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            CatalogSnapshot catalog = await catalogService.GetAsync(cancellationToken).ConfigureAwait(false);
            string folderId = query.FolderId ?? string.Empty;

            if (string.IsNullOrEmpty(folderId))
            {
                return GetRoot();
            }

            if (folderId == MoviesRootId)
            {
                return Result(catalog.Entries
                    .Where(entry => entry.Kind == CatalogKind.Movie)
                    .Select(CreateMovie));
            }

            if (folderId == ProgramsRootId)
            {
                return Result(catalog.Entries
                    .Where(entry => entry.Kind == CatalogKind.Program)
                    .Select(CreateProgram));
            }

            if (folderId == SeriesRootId)
            {
                return GetSeries(catalog);
            }

            IGrouping<string, CatalogEntry>? series = catalog.Entries
                .Where(entry => entry.Kind == CatalogKind.SeriesEpisode)
                .GroupBy(entry => entry.SeriesTitle, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => SeriesFolderId(group.Key) == folderId);

            if (series is not null)
            {
                return Result(series
                    .GroupBy(entry => entry.SeasonNumber)
                    .OrderBy(group => group.Key)
                    .Select(group => CreateSeason(series.Key, group.Key, group)));
            }

            foreach (IGrouping<string, CatalogEntry> seriesGroup in catalog.Entries
                .Where(entry => entry.Kind == CatalogKind.SeriesEpisode)
                .GroupBy(entry => entry.SeriesTitle, StringComparer.OrdinalIgnoreCase))
            {
                IGrouping<int, CatalogEntry>? season = seriesGroup
                    .GroupBy(entry => entry.SeasonNumber)
                    .FirstOrDefault(group => SeasonFolderId(seriesGroup.Key, group.Key) == folderId);

                if (season is not null)
                {
                    return Result(season
                        .OrderBy(entry => entry.EpisodeNumber)
                        .ThenBy(entry => entry.StartUtc)
                        .Select(CreateEpisode));
                }
            }

            return Result([]);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to generate Catch-up Catalog items");
            throw;
        }
    }

    public bool IsEnabledFor(string userId) => Plugin.Instance.Configuration.Enabled;

    private static ChannelItemResult GetRoot()
    {
        var configuration = Plugin.Instance.Configuration;
        List<ChannelItemInfo> items = [];

        if (configuration.ShowMovies)
        {
            items.Add(new ChannelItemInfo
            {
                Id = MoviesRootId,
                Name = "Catch-up Movies",
                Type = ChannelItemType.Folder
            });
        }

        if (configuration.ShowSeries)
        {
            items.Add(new ChannelItemInfo
            {
                Id = SeriesRootId,
                Name = "Catch-up Series",
                Type = ChannelItemType.Folder
            });
        }

        if (configuration.ShowPrograms)
        {
            items.Add(new ChannelItemInfo
            {
                Id = ProgramsRootId,
                Name = "Catch-up Programs",
                Type = ChannelItemType.Folder
            });
        }

        return new ChannelItemResult
        {
            Items = items,
            TotalRecordCount = items.Count
        };
    }

    private static ChannelItemResult GetSeries(CatalogSnapshot catalog)
    {
        IEnumerable<ChannelItemInfo> items = catalog.Entries
            .Where(entry => entry.Kind == CatalogKind.SeriesEpisode)
            .GroupBy(entry => entry.SeriesTitle, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                CatalogEntry newest = group.OrderByDescending(entry => entry.StartUtc).First();
                return new ChannelItemInfo
                {
                    Id = SeriesFolderId(group.Key),
                    Name = group.Key,
                    SeriesName = group.Key,
                    FolderType = ChannelFolderType.Series,
                    ImageUrl = newest.ImageUrl,
                    Overview = newest.Overview,
                    ProductionYear = newest.ProductionYear,
                    CommunityRating = newest.Rating,
                    Type = ChannelItemType.Folder
                };
            });

        return Result(items);
    }

    private ChannelItemInfo CreateMovie(CatalogEntry entry) => new()
    {
        ContentType = ChannelMediaContentType.Movie,
        DateCreated = entry.StartUtc,
        Id = entry.Id,
        ImageUrl = entry.ImageUrl,
        IsLiveStream = false,
        MediaSources = [CreateMediaSource(entry)],
        MediaType = ChannelMediaType.Video,
        Name = entry.Title,
        Overview = entry.Overview,
        PremiereDate = entry.StartUtc,
        ProductionYear = entry.ProductionYear,
        CommunityRating = entry.Rating,
        RunTimeTicks = entry.DurationMinutes * TimeSpan.TicksPerMinute,
        Tags = [entry.ChannelName, "Catch-up"],
        Type = ChannelItemType.Media
    };

    private ChannelItemInfo CreateProgram(CatalogEntry entry) => new()
    {
        ContentType = ChannelMediaContentType.TvExtra,
        DateCreated = entry.StartUtc,
        Id = entry.Id,
        ImageUrl = entry.ImageUrl,
        IsLiveStream = false,
        MediaSources = [CreateMediaSource(entry)],
        MediaType = ChannelMediaType.Video,
        Name = $"{entry.Title} · {entry.ChannelName}",
        Overview = entry.Overview,
        PremiereDate = entry.StartUtc,
        RunTimeTicks = entry.DurationMinutes * TimeSpan.TicksPerMinute,
        Tags = [entry.ChannelName, "Catch-up"],
        Type = ChannelItemType.Media
    };

    private static ChannelItemInfo CreateSeason(
        string seriesTitle,
        int seasonNumber,
        IEnumerable<CatalogEntry> entries)
    {
        CatalogEntry newest = entries.OrderByDescending(entry => entry.StartUtc).First();
        return new ChannelItemInfo
        {
            Id = SeasonFolderId(seriesTitle, seasonNumber),
            Name = $"Season {seasonNumber}",
            FolderType = ChannelFolderType.Season,
            IndexNumber = seasonNumber,
            ImageUrl = newest.ImageUrl,
            Overview = newest.Overview,
            Type = ChannelItemType.Folder
        };
    }

    private ChannelItemInfo CreateEpisode(CatalogEntry entry) => new()
    {
        ContentType = ChannelMediaContentType.Episode,
        DateCreated = entry.StartUtc,
        Id = entry.Id,
        ImageUrl = entry.ImageUrl,
        IndexNumber = entry.EpisodeNumber,
        IsLiveStream = false,
        MediaSources = [CreateMediaSource(entry)],
        MediaType = ChannelMediaType.Video,
        Name = entry.Title,
        Overview = entry.Overview,
        ParentIndexNumber = entry.SeasonNumber,
        PremiereDate = entry.StartUtc,
        ProductionYear = entry.ProductionYear,
        CommunityRating = entry.Rating,
        RunTimeTicks = entry.DurationMinutes * TimeSpan.TicksPerMinute,
        SeriesName = entry.SeriesTitle,
        Tags = [entry.ChannelName, "Catch-up"],
        Type = ChannelItemType.Media
    };

    private MediaSourceInfo CreateMediaSource(CatalogEntry entry)
    {
        string path = dispatcharrClient.BuildPlaybackUrl(
            Plugin.Instance.Configuration,
            entry);

        return new MediaSourceInfo
        {
            Container = "ts",
            EncoderProtocol = MediaProtocol.Http,
            Id = CatalogService.StableId("media:" + entry.Id),
            IsInfiniteStream = false,
            IsRemote = true,
            Name = "Dispatcharr Catch-up",
            Path = path,
            Protocol = MediaProtocol.Http,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsProbing = true
        };
    }

    private static string SeriesFolderId(string seriesTitle) =>
        CatalogService.StableId("series:" + seriesTitle.ToLowerInvariant());

    private static string SeasonFolderId(string seriesTitle, int season) =>
        CatalogService.StableId($"season:{seriesTitle.ToLowerInvariant()}:{season}");

    private static ChannelItemResult Result(IEnumerable<ChannelItemInfo> source)
    {
        List<ChannelItemInfo> items = source.ToList();
        return new ChannelItemResult
        {
            Items = items,
            TotalRecordCount = items.Count
        };
    }
}
