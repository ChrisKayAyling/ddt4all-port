using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Ddt4All.App.Services;
using Ddt4All.App.ViewModels;
using Ddt4All.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Ddt4All.App;

/// <summary>
/// `Ddt4All.App --headless-check [--shot file.png]`: boots the real app (DI, theme, main window, all pages) on the
/// Avalonia headless platform with Skia rendering, prints a startup timeline and exits 0. No display server needed.
/// </summary>
internal static class HeadlessCheck
{
    public static int Run(string[] args)
    {
        var shot = args.SkipWhile(a => a != "--shot").Skip(1).FirstOrDefault();
        var home = Path.Combine(Path.GetTempPath(), "ddt4all-headless-" + Environment.ProcessId);
        Environment.SetEnvironmentVariable("DDT4ALL_HOME", home);
        var code = 1;
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();
        StartupTrace.Mark("avalonia setup");
        try
        {
            var services = AppHost.Create(new AppHostOptions { SettingsPath = Path.Combine(home, "settings.json"), FileLogging = false });
            App.Services = services;
            StartupTrace.Mark("services built");
            var settings = services.GetRequiredService<ISettingsService>();
            services.GetRequiredService<IThemeService>().Apply(settings.Current.Theme, settings.Current.Accent);
            var vm = services.GetRequiredService<MainWindowViewModel>();
            var w = new MainWindow(settings) { DataContext = vm };
            w.Show();
            StartupTrace.Mark("window shown");
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var frame = w.CaptureRenderedFrame();
            StartupTrace.Mark("first frame rendered");
            foreach (var id in Enum.GetValues<PageId>()) { vm.NavigateTo(id); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); w.CaptureRenderedFrame(); }
            StartupTrace.Mark("all pages created");
            if (shot is not null) { vm.NavigateTo(PageId.Dashboard); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); w.CaptureRenderedFrame()?.Save(shot, new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
            Console.WriteLine(StartupTrace.Report());
            Console.WriteLine($"frame: {frame?.PixelSize.Width}x{frame?.PixelSize.Height}");
            code = 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
        return code;
    }
}
