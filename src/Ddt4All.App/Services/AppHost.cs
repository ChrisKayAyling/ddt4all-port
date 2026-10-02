using Ddt4All.App.Localization;
using Ddt4All.App.Services.Fakes;
using Ddt4All.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ddt4All.App.Services;

public sealed class AppHostOptions
{
    public string? SettingsPath { get; init; }
    public bool FileLogging { get; init; } = true;
    public IEcuCatalogService? Catalog { get; init; }
    public IConnectionService? Connection { get; init; }
    public IDiagnosticsService? Diagnostics { get; init; }
    public ISnifferService? Sniffer { get; init; }
    public IDialogService? Dialogs { get; init; }
    public IFilePickerService? FilePicker { get; init; }
}

/// <summary>
/// Composition root. Everything is registered with explicit factory lambdas, so the container never
/// reflects over constructors (fast startup, trimming friendly). To wire the real Core/Comms services,
/// pass them in <see cref="AppHostOptions"/> (or swap the fallbacks below).
/// </summary>
public static class AppHost
{
    public static ServiceProvider Create(AppHostOptions? o = null)
    {
        o ??= new AppHostOptions();
        var settings = new SettingsService(o.SettingsPath);
        if (LogLevelSwitch.TryParse(settings.Current.LogLevel, out var lvl)) LogLevelSwitch.Minimum = lvl;
        Loc.Instance.SetLanguage(settings.Current.Language);

        var logStore = new InMemoryLogStore();
        var loggers = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace); // real filtering happens in LogLevelSwitch (runtime changeable)
            b.AddProvider(new InMemoryLoggerProvider(logStore));
            if (o.FileLogging) b.AddProvider(new FileLoggerProvider(AppPaths.LogDir));
        });

        var s = new ServiceCollection();
        s.AddSingleton<ISettingsService>(settings);
        s.AddSingleton(loggers);
        s.AddSingleton<ILogStore>(logStore);
        s.AddSingleton<IThemeService>(_ => new ThemeService());
        s.AddSingleton<INotificationService>(_ => new NotificationService());
        s.AddSingleton<IDialogService>(_ => o.Dialogs ?? new DialogService());
        s.AddSingleton<IFilePickerService>(_ => o.FilePicker ?? new FilePickerService());
        s.AddSingleton(_ => new SessionState());

        // --- seams to Core / Comms: replace these fakes with the real implementations ---
        s.AddSingleton<IEcuCatalogService>(_ => o.Catalog ?? new FakeEcuCatalogService());
        s.AddSingleton<IConnectionService>(sp => o.Connection ?? new FakeConnectionService(sp.GetRequiredService<ILoggerFactory>().CreateLogger<FakeConnectionService>()));
        s.AddSingleton<IDiagnosticsService>(_ => o.Diagnostics ?? new FakeDiagnosticsService());
        s.AddSingleton<ISnifferService>(_ => o.Sniffer ?? new FakeSnifferService());

        // --- shell + pages ---
        s.AddSingleton<Func<PageId, PageViewModel>>(sp => id => CreatePage(sp, id));
        s.AddSingleton(sp => new MainWindowViewModel(sp.GetRequiredService<Func<PageId, PageViewModel>>(), sp.GetRequiredService<IConnectionService>(),
            sp.GetRequiredService<SessionState>(), sp.GetRequiredService<INotificationService>(), sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<ISettingsService>()));
        s.AddSingleton<INavigationService>(sp => sp.GetRequiredService<MainWindowViewModel>());
        return s.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = false, ValidateOnBuild = false });
    }

    private static PageViewModel CreatePage(IServiceProvider sp, PageId id)
    {
        T G<T>() where T : notnull => sp.GetRequiredService<T>();
        ILogger<T> Log<T>() => G<ILoggerFactory>().CreateLogger<T>();
        // NavigationService is resolved lazily (MainWindowViewModel is still being constructed when the first page is created)
        var nav = new LazyNavigation(sp);
        return id switch
        {
            PageId.Dashboard => new DashboardViewModel(G<IConnectionService>(), G<ISettingsService>(), G<INotificationService>(), nav, Log<DashboardViewModel>()),
            PageId.EcuBrowser => new EcuBrowserViewModel(G<IEcuCatalogService>(), nav, G<INotificationService>(), G<SessionState>(), G<ISettingsService>(), Log<EcuBrowserViewModel>()),
            PageId.Screens => new ScreensViewModel(),
            PageId.Requests => new RequestsViewModel(),
            PageId.Dtcs => new DtcViewModel(G<IDiagnosticsService>(), G<IConnectionService>(), G<SessionState>(), G<INotificationService>(), G<IDialogService>()),
            PageId.Terminal => new TerminalViewModel(G<IConnectionService>(), G<SessionState>(), G<INotificationService>()),
            PageId.Sniffer => new SnifferViewModel(G<ISnifferService>(), G<INotificationService>()),
            PageId.DataEditor => new DataEditorViewModel(),
            PageId.Plugins => new PluginsViewModel(),
            PageId.Logs => new LogViewModel(G<ILogStore>()),
            PageId.Settings => new SettingsViewModel(G<ISettingsService>(), G<IThemeService>(), G<IFilePickerService>(), G<IDialogService>(), G<INotificationService>()),
            PageId.About => new AboutViewModel(),
            _ => throw new ArgumentOutOfRangeException(nameof(id)),
        };
    }

    private sealed class LazyNavigation(IServiceProvider sp) : INavigationService
    {
        public void NavigateTo(PageId page) => sp.GetRequiredService<INavigationService>().NavigateTo(page);
    }
}
