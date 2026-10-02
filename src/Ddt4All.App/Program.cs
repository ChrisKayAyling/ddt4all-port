using Avalonia;
using Ddt4All.App.Services;

namespace Ddt4All.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        StartupTrace.Mark("Main");

        if (args.Contains("--headless-check"))
            return HeadlessCheck.Run(args);

        if (!args.Contains("--allow-multiple"))
        {
            var instance = new SingleInstance();
            if (!instance.TryBecomePrimary())
            {
                SingleInstance.SignalPrimary();
                return 0;
            }
            App.Instance = instance;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
