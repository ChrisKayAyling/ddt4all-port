using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Ddt4All.App.Localization;
using Ddt4All.App.Models;
using Ddt4All.App.Services;
using Ddt4All.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ddt4All.App.Tests;

/// <summary>Renders each page through Avalonia.Headless + Skia into docs/screenshots (used for visual review).</summary>
public class ScreenshotTests
{
    private static async Task Connect(TestApp app)
    {
        var d = app.Page<DashboardViewModel>(PageId.Dashboard);
        d.Simulation = true;
        await d.ConnectCommand.ExecuteAsync(null);
        await app.PumpAsync(100);
    }

    [AvaloniaFact]
    public async Task Dashboard_light_and_dark()
    {
        using var app = new TestApp(theme: "light");
        await app.PumpAsync(500);
        app.Screenshot("01-connect-light");
        await Connect(app);
        app.Screenshot("02-connect-connected-light");
        app.Services.GetRequiredService<IThemeService>().Apply("dark", "blue");
        app.Screenshot("03-connect-connected-dark");
    }

    [AvaloniaFact]
    public async Task Ecu_browser()
    {
        using var app = new TestApp(theme: "light");
        var b = app.Page<EcuBrowserViewModel>(PageId.EcuBrowser);
        await app.PumpAsync(700);
        app.Screenshot("04-ecu-browser-grouped");
        b.SearchText = "abs siemens";
        await app.PumpAsync(500);
        b.SelectedRow = b.Rows.OfType<EcuEntry>().First();
        await app.PumpAsync(200);
        app.Screenshot("05-ecu-browser-search-details");
        app.Services.GetRequiredService<IThemeService>().Apply("dark", "teal");
        app.Screenshot("06-ecu-browser-dark-teal");
    }

    [AvaloniaFact]
    public async Task Terminal_page()
    {
        using var app = new TestApp(theme: "dark");
        await Connect(app);
        var t = app.Page<TerminalViewModel>(PageId.Terminal);
        foreach (var c in new[] { "ATZ", "ATRV", "22 F1 90", "2E F1 90 01", "19 02 AF" })
        { t.Input = c; await t.SendCommand.ExecuteAsync(null); }
        await app.PumpAsync(200);
        app.Screenshot("07-terminal-dark");
    }

    [AvaloniaFact]
    public async Task Expert_mode_banner_and_toast()
    {
        using var app = new TestApp(theme: "light");
        await Connect(app);
        app.Main.Session.IsExpertMode = true;
        app.Services.GetRequiredService<INotificationService>().Warning("Expert mode enabled", "Write operations are allowed. You are responsible.");
        app.Services.GetRequiredService<INotificationService>().Success("Connection established successfully", "ECU simulator");
        var dtc = app.Page<DtcViewModel>(PageId.Dtcs);
        await dtc.ReadCommand.ExecuteAsync(null);
        await app.PumpAsync(200);
        app.Screenshot("08-dtcs-expert-toasts");
    }

    [AvaloniaFact]
    public async Task Other_pages()
    {
        using var app = new TestApp(theme: "light");
        app.Page<SettingsViewModel>(PageId.Settings); await app.PumpAsync(100);
        app.Screenshot("09-settings");
        app.Page<AboutViewModel>(PageId.About); await app.PumpAsync(100);
        app.Screenshot("10-about");
        app.Page<Ddt4All.App.ViewModels.Screens.ScreensViewModel>(PageId.Screens); await app.PumpAsync(100);
        app.Screenshot("11-screens-no-ecu");
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Demo");
        log.LogInformation("Catalog loaded: 5000 entries in 12 ms");
        log.LogWarning("Adapter reported low voltage (9.8 V)");
        log.LogError("Read timeout after 5 s");
        app.Page<LogViewModel>(PageId.Logs); await app.PumpAsync(400);
        app.Screenshot("12-logs");
        var sn = app.Page<SnifferViewModel>(PageId.Sniffer);
        await sn.ToggleCommand.ExecuteAsync(null);
        await app.PumpAsync(700);
        app.Screenshot("13-sniffer");
        await sn.ToggleCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task Collapsed_nav_french_and_dialog()
    {
        using var app = new TestApp(language: "fr", theme: "light");
        app.Main.IsNavExpanded = false;
        await app.PumpAsync(400);
        app.Page<DashboardViewModel>(PageId.Dashboard);
        app.Screenshot("14-collapsed-nav-french");
    }

    [AvaloniaFact]
    public async Task Expert_dialog()
    {
        using var app = new TestApp(theme: "light");
        var vm = new ConfirmDialogViewModel
        {
            Title = Loc.T("Enable expert mode?"),
            Message = Loc.T("Expert mode enables writing to ECUs. Wrong requests can cause serious damage on a live vehicle."),
            Bullets = ["This application is work in progress. Use Expert mode only if you know what you are doing.",
                "Do not use this software if you do not understand how a CAN network (or an ECU) works.",
                "Maintenance, testing and research only. The author declines responsibility for any use. You are responsible."],
            RequireAcknowledgement = true, AcknowledgementText = "I understand the risks and want to enable expert mode",
            ConfirmText = "Enable expert mode", IsDanger = true,
        };
        var dlg = new Ddt4All.App.Views.ConfirmDialog { DataContext = vm };
        dlg.Show(app.Window);
        await app.PumpAsync(200);
        var frame = dlg.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var dir = Path.Combine(Path.GetDirectoryName(app.Screenshot("tmp-dialog-host"))!);
        frame!.Save(Path.Combine(dir, "15-expert-dialog.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        File.Delete(Path.Combine(dir, "tmp-dialog-host.png"));
        dlg.Close();
    }
}
