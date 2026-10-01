using Jellyfin.Plugin.ComingSoon.Clients;
using Jellyfin.Plugin.ComingSoon.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
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
        serviceCollection.AddHostedService<ComingSoonHostedService>();
    }
}
