using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.CatchupCatalog;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<DispatcharrClient>();
        serviceCollection.AddSingleton<TmdbClient>();
        serviceCollection.AddSingleton<CatalogService>();
        serviceCollection.AddSingleton<IChannel, CatchupCatalogChannel>();
    }
}
