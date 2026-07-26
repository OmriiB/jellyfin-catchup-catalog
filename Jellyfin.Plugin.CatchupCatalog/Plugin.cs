using System.Globalization;
using System.Reflection;
using Jellyfin.Plugin.CatchupCatalog.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.CatchupCatalog;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private static Plugin? _instance;

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        _instance = this;
    }

    public static Plugin Instance =>
        _instance ?? throw new InvalidOperationException("Plugin instance is unavailable.");

    public override string Name => "Catch-up Catalog";

    public override Guid Id => Guid.Parse("6fb961ad-51d1-42c2-a1c3-49e5a9458c68");

    public string DataVersion =>
        $"{Assembly.GetExecutingAssembly().GetName().Version}-{Configuration.GetHashCode()}-{CatalogService.VersionToken}";

    private static PluginPageInfo CreatePage(string name) => new()
    {
        Name = name,
        EmbeddedResourcePath = string.Format(
            CultureInfo.InvariantCulture,
            "{0}.Configuration.Web.{1}",
            typeof(Plugin).Namespace,
            name)
    };

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        CreatePage("CatchupCatalog.html"),
        CreatePage("CatchupCatalog.js")
    ];

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        base.UpdateConfiguration(configuration);
        CatalogService.InvalidateGlobal();
    }
}
