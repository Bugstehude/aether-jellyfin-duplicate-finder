using Jellyfin.Plugin.DuplicateFinder.Application;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DuplicateFinder;

/// <summary>Registers plugin services in Jellyfin's container.</summary>
public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton(_ => new DuplicateResultStore(
            Plugin.Instance?.DataFolderPath
                ?? throw new InvalidOperationException("Duplicate Finder plugin data path is not initialized.")));
        serviceCollection.AddSingleton<DuplicateScanner>();
    }
}
