using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.CatchupCatalog.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;

    public string BaseUrl { get; set; } = "http://192.168.1.100:9191";

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public int ArchiveDays { get; set; } = 7;

    public int CacheMinutes { get; set; } = 30;

    public string MetadataLanguage { get; set; } = "he-IL";

    public string TmdbBearerToken { get; set; } = string.Empty;

    public bool ShowMovies { get; set; } = true;

    public bool ShowSeries { get; set; } = true;

    public bool ShowPrograms { get; set; } = true;

    public int MaxConcurrentEpgRequests { get; set; } = 8;
}
