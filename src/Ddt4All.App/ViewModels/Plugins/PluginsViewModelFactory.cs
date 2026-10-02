using Ddt4All.App.Services;
using Ddt4All.Plugins;

namespace Ddt4All.App.ViewModels.Plugins;

public sealed partial class PluginsViewModel
{
    /// <summary>Production wiring: all built-in plugins, ECU definitions from the opened ecu.zip, then the ECU directory of the settings.</summary>
    public static PluginsViewModel Create(IEcuCatalogService catalog, ISettingsService settings, IConnectionService connection, SessionState session, INotificationService toasts)
    {
        var definitions = new CompositeEcuDefinitionProvider(
            new EcuDatabaseDefinitionProvider(() => catalog.Database),
            new DirectoryEcuDefinitionProvider(() => settings.Current.EcuDirectory));
        return new PluginsViewModel(PluginRegistry.CreateDefault(), definitions, connection, session, toasts);
    }
}
