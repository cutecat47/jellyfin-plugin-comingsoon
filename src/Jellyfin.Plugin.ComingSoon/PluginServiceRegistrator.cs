using Jellyfin.Plugin.ComingSoon.Clients;
using Jellyfin.Plugin.ComingSoon.Library;
using Jellyfin.Plugin.ComingSoon.Playback;
using Jellyfin.Plugin.ComingSoon.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ComingSoon;

/// <summary>
/// Registers the plugin's services with Jellyfin's DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // IHttpClientFactory is already registered by the Jellyfin host.
        serviceCollection.AddSingleton<IServiceClientFactory, ServiceClientFactory>();
        serviceCollection.AddSingleton<ConnectionTester>();

        serviceCollection.AddSingleton<StubRegistry>();
        serviceCollection.AddSingleton<IStubLibrary, ComingSoonLibrary>();
        serviceCollection.AddSingleton<PlaybackBlocker>();
        serviceCollection.AddSingleton<PlaybackInfoFilter>();

        // Refuse PlaybackInfo for stubs before clients start playing; PlaybackGuard is the fallback.
        serviceCollection.Configure<MvcOptions>(options => options.Filters.AddService<PlaybackInfoFilter>());

        serviceCollection.AddHostedService<ComingSoonHostedService>();
        serviceCollection.AddHostedService<PlaybackGuard>();
    }
}
