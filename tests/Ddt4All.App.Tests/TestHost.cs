using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Ddt4All.App;
using Ddt4All.App.Localization;
using Ddt4All.App.Services;
using Ddt4All.App.ViewModels;
using Ddt4All.App.Views;
using Microsoft.Extensions.DependencyInjection;

[assembly: AvaloniaTestApplication(typeof(Ddt4All.App.Tests.TestAppBuilder))]

namespace Ddt4All.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Ddt4All.App.App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

internal sealed class AutoDialogs : IDialogService
{
    public bool Answer { get; set; } = true;
    public int ExpertPrompts { get; private set; }
    public Task<bool> ConfirmAsync(string title, string message, string confirmText, string? cancelText = null, bool danger = false) => Task.FromResult(Answer);
    public Task<bool> ConfirmExpertModeAsync() { ExpertPrompts++; return Task.FromResult(Answer); }
}

internal sealed class TestApp : IDisposable
{
    public ServiceProvider Services { get; }
    public MainWindow Window { get; }
    public MainWindowViewModel Main { get; }
    public AutoDialogs Dialogs { get; } = new();
    public string Home { get; } = Path.Combine(Path.GetTempPath(), "ddt4all-tests-" + Guid.NewGuid().ToString("N"));

    public TestApp(string language = "en", string theme = "light", int width = 1280, int height = 780, IEcuContext? ecuContext = null)
    {
        var settingsPath = Path.Combine(Home, "settings.json");
        Directory.CreateDirectory(Home);
        File.WriteAllText(settingsPath, $"{{\"language\":\"{language}\",\"theme\":\"{theme}\"}}");
        Services = AppHost.Create(new AppHostOptions
        {
            SettingsPath = settingsPath, FileLogging = false, Dialogs = Dialogs, EcuContext = ecuContext,
            // deterministic fakes: the real services are covered by RealServicesTests
            Catalog = new Ddt4All.App.Services.Fakes.FakeEcuCatalogService(), Connection = new Ddt4All.App.Services.Fakes.FakeConnectionService(),
            Diagnostics = new Ddt4All.App.Services.Fakes.FakeDiagnosticsService(), Sniffer = new Ddt4All.App.Services.Fakes.FakeSnifferService(),
        });
        var settings = Services.GetRequiredService<ISettingsService>();
        Services.GetRequiredService<IThemeService>().Apply(settings.Current.Theme, settings.Current.Accent);
        Main = Services.GetRequiredService<MainWindowViewModel>();
        Window = new MainWindow(settings) { DataContext = Main, Width = width, Height = height };
        Window.Show();
        Pump();
    }

    public static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    public async Task PumpAsync(int ms = 300)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { await Task.Delay(15); Pump(); }
    }

    public T Page<T>(PageId id) where T : PageViewModel { Main.NavigateTo(id); Pump(); return (T)Main.CurrentPage!; }

    public string Screenshot(string name)
    {
        Pump(); Pump();
        var dir = FindScreenshotDir();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".png");
        Window.CaptureRenderedFrame()!.Save(path, new PngBitmapEncoderOptions());
        return path;
    }

    private static string FindScreenshotDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Ddt4All.sln"))) d = d.Parent;
        return Path.Combine(d?.FullName ?? Path.GetTempPath(), "docs", "screenshots");
    }

    public void Dispose()
    {
        Window.Close();
        Services.Dispose();
        Loc.Instance.SetLanguage("en");
        try { Directory.Delete(Home, true); } catch { }
    }
}
