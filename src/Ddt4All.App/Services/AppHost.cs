using Ddt4All.App.Localization;
using Ddt4All.App.Services.Fakes;
using Ddt4All.App.Services.Real;
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
    public IScanService? Scan { get; init; }
    public IEcuSession? EcuSession { get; init; }
    /// <summary>Overrides the Requests / Data Editor view of the current ECU (tests).</summary>
    public IEcuContext? EcuContext { get; init; }
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
        s.AddSingleton<Ddt4All.App.ViewModels.Screens.IScreenSession>(sp => sp.GetService<IEcuSession>() is { } es
            ? new Ddt4All.App.ViewModels.Screens.EcuSessionAdapter(es)
            : new Ddt4All.App.ViewModels.Screens.EcuContextAdapter(sp.GetRequiredService<IEcuContext>()));

        // --- seams to Core / Comms: replace these fakes with the real implementations ---
        // real Core/Comms implementations by default; the fakes (Services/Fakes) are for tests and design time (pass them in AppHostOptions)
        s.AddSingleton<IEcuCatalogService>(sp => o.Catalog ?? new EcuCatalogService(settings, sp.GetRequiredService<ILoggerFactory>().CreateLogger<EcuCatalogService>()));
        s.AddSingleton<IConnectionService>(sp => o.Connection ?? new ConnectionService(settings, sp.GetRequiredService<IEcuCatalogService>(), sp.GetRequiredService<ILoggerFactory>().CreateLogger<ConnectionService>()));
        s.AddSingleton<IEcuSession>(sp => o.EcuSession ?? new Real.EcuSession(sp.GetRequiredService<IEcuCatalogService>(), sp.GetRequiredService<IConnectionService>(),
            sp.GetRequiredService<SessionState>(), settings, sp.GetRequiredService<ILoggerFactory>().CreateLogger<Real.EcuSession>()));
        s.AddSingleton<IDiagnosticsService>(sp => o.Diagnostics ?? new DiagnosticsService(sp.GetRequiredService<IEcuSession>(), sp.GetRequiredService<SessionState>(), sp.GetRequiredService<ILoggerFactory>().CreateLogger<DiagnosticsService>()));
        s.AddSingleton<ISnifferService>(sp => o.Sniffer ?? (sp.GetRequiredService<IConnectionService>() is ConnectionService cs
            ? new SnifferService(cs, sp.GetRequiredService<ILoggerFactory>().CreateLogger<SnifferService>()) : new FakeSnifferService()));
        s.AddSingleton<IScanService>(sp => o.Scan ?? new EcuScanService(sp.GetRequiredService<IEcuCatalogService>(), sp.GetRequiredService<IConnectionService>(),
            log: sp.GetRequiredService<ILoggerFactory>().CreateLogger<EcuScanService>()));

        s.AddSingleton<IEcuContext>(sp => o.EcuContext ?? EcuSessionContext.Attach(new EcuContext(sp.GetRequiredService<IConnectionService>(), sp.GetRequiredService<SessionState>()), sp.GetRequiredService<IEcuSession>()));

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
            PageId.Dashboard => new DashboardViewModel(G<IConnectionService>(), G<ISettingsService>(), G<INotificationService>(), nav, Log<DashboardViewModel>(),
                new ScanViewModel(G<IScanService>(), G<IEcuCatalogService>(), G<IConnectionService>(), G<IEcuSession>(), G<IFilePickerService>(), G<INotificationService>(), nav)),
            PageId.EcuBrowser => new EcuBrowserViewModel(G<IEcuCatalogService>(), nav, G<INotificationService>(), G<SessionState>(), G<ISettingsService>(), Log<EcuBrowserViewModel>(), G<IEcuSession>(), G<IFilePickerService>()),
            PageId.Screens => new Ddt4All.App.ViewModels.Screens.ScreensViewModel(G<SessionState>(), G<Ddt4All.App.ViewModels.Screens.IScreenSession>(), G<INotificationService>(), G<IDialogService>(), G<IFilePickerService>(), nav),
            PageId.Requests => new RequestsViewModel(G<IEcuContext>(), G<IConnectionService>(), G<SessionState>(), G<INotificationService>(), G<IDialogService>(), G<IFilePickerService>()),
            PageId.Dtcs => new DtcViewModel(G<IDiagnosticsService>(), G<IConnectionService>(), G<SessionState>(), G<INotificationService>(), G<IDialogService>()),
            PageId.Terminal => new TerminalViewModel(G<IConnectionService>(), G<SessionState>(), G<INotificationService>()),
            PageId.Sniffer => new SnifferViewModel(G<ISnifferService>(), G<INotificationService>()),
            PageId.DataEditor => new DataEditorViewModel(G<IEcuContext>(), G<INotificationService>(), G<IDialogService>(), G<IFilePickerService>(), G<ISettingsService>()),
            PageId.Plugins => Ddt4All.App.ViewModels.Plugins.PluginsViewModel.Create(G<IEcuCatalogService>(), G<ISettingsService>(), G<IConnectionService>(), G<SessionState>(), G<INotificationService>()),
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
