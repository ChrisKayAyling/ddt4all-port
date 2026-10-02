using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Ddt4All.App.Services;
using Ddt4All.App.ViewModels;
using Ddt4All.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ddt4All.App;

public partial class App : Application
{
    public static ServiceProvider? Services { get; set; }
    public static SingleInstance? Instance { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            StartupTrace.Mark("framework initialized");
            Services ??= AppHost.Create();
            StartupTrace.Mark("services built");
            var settings = Services.GetRequiredService<ISettingsService>();
            Services.GetRequiredService<IThemeService>().Apply(settings.Current.Theme, settings.Current.Accent);

            var window = new MainWindow(settings) { DataContext = Services.GetRequiredService<MainWindowViewModel>() };
            desktop.MainWindow = window;
            StartupTrace.Mark("window created");

            if (Instance is { } inst)
                inst.ActivationRequested += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (window.WindowState == Avalonia.Controls.WindowState.Minimized) window.WindowState = Avalonia.Controls.WindowState.Normal;
                    window.Activate();
                });

            var log = Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
            log.LogInformation("DDT4All started ({Report})", StartupTrace.Report().Replace(Environment.NewLine, "; "));
            desktop.Exit += (_, _) =>
            {
                settings.SaveNow();
                Instance?.Dispose();
                Services.Dispose();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
