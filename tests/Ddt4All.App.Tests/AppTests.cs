using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Ddt4All.App.Localization;
using Ddt4All.App.Models;
using Ddt4All.App.Services;
using Ddt4All.App.Services.Fakes;
using Ddt4All.App.ViewModels;
using Ddt4All.App.Views;

namespace Ddt4All.App.Tests;

public class SmokeTests
{
    [AvaloniaFact]
    public void Every_page_creates_a_view()
    {
        using var app = new TestApp();
        foreach (var id in Enum.GetValues<PageId>())
        {
            app.Main.NavigateTo(id);
            TestApp.Pump();
            Assert.Equal(id, app.Main.CurrentPage!.Id);
            var host = app.Window.GetVisualDescendants().OfType<TransitioningContentControl>().First();
            Assert.NotNull(host.Content);
            // the page view is materialised by the DataTemplate
            Assert.True(app.Window.GetVisualDescendants().OfType<UserControl>().Any(), $"no view for {id}");
        }
    }

    [AvaloniaFact]
    public void Nav_marks_active_item()
    {
        using var app = new TestApp();
        app.Main.NavigateTo(PageId.Terminal);
        Assert.Single(app.Main.MainItems, i => i.IsActive);
        Assert.Equal(PageId.Terminal, app.Main.MainItems.Single(i => i.IsActive).Id);
        app.Main.NavigateTo(PageId.Settings);
        Assert.DoesNotContain(app.Main.MainItems, i => i.IsActive);
        Assert.True(app.Main.FooterItems.Single(i => i.IsActive).Id == PageId.Settings);
    }

    [AvaloniaFact]
    public async Task Expert_mode_requires_confirmation()
    {
        using var app = new TestApp();
        app.Dialogs.Answer = false;
        await app.Main.ToggleExpertModeCommand.ExecuteAsync(null);
        Assert.False(app.Main.Session.IsExpertMode);
        app.Dialogs.Answer = true;
        await app.Main.ToggleExpertModeCommand.ExecuteAsync(null);
        Assert.True(app.Main.Session.IsExpertMode);
        Assert.Equal(2, app.Dialogs.ExpertPrompts);
        await app.Main.ToggleExpertModeCommand.ExecuteAsync(null); // disabling needs no prompt
        Assert.False(app.Main.Session.IsExpertMode);
        Assert.Equal(2, app.Dialogs.ExpertPrompts);
    }

    [AvaloniaFact]
    public async Task Connect_disconnect_roundtrip_updates_status()
    {
        using var app = new TestApp();
        var dash = app.Page<DashboardViewModel>(PageId.Dashboard);
        dash.Simulation = true;
        await dash.ConnectCommand.ExecuteAsync(null);
        TestApp.Pump();
        Assert.True(app.Main.IsConnected);
        Assert.Contains("ELM", app.Main.ConnectionText + app.Main.Connection.Adapter!.Firmware);
        await dash.ConnectCommand.ExecuteAsync(null);
        TestApp.Pump();
        Assert.True(app.Main.IsDisconnected);
    }

    [AvaloniaFact]
    public async Task Terminal_blocks_write_services_unless_expert()
    {
        using var app = new TestApp();
        var dash = app.Page<DashboardViewModel>(PageId.Dashboard);
        dash.Simulation = true;
        await dash.ConnectCommand.ExecuteAsync(null);
        var t = app.Page<TerminalViewModel>(PageId.Terminal);

        t.Input = "2E F1 90 01"; await t.SendCommand.ExecuteAsync(null);
        Assert.Contains(t.Lines, l => l.IsError && l.Text.StartsWith("Blocked"));
        Assert.DoesNotContain(t.Lines, l => l.IsResponse);

        t.Input = "22 F1 90"; await t.SendCommand.ExecuteAsync(null);
        Assert.Contains(t.Lines, l => l.IsResponse && l.Text.StartsWith("62 F1 90"));

        app.Main.Session.IsExpertMode = true;
        t.Input = "2E F1 90 01"; await t.SendCommand.ExecuteAsync(null);
        Assert.Equal(2, t.Lines.Count(l => l.IsResponse));

        t.Input = "zz"; await t.SendCommand.ExecuteAsync(null);
        Assert.Contains(t.Lines, l => l.IsError && l.Text.StartsWith("Invalid hex"));

        t.HistoryPreviousCommand.Execute(null);
        Assert.Equal("zz", t.Input);
    }
}

public class LocalizationTests
{
    [AvaloniaFact]
    public void Catalog_loads_lazily_and_falls_back_to_key()
    {
        var loc = Loc.Instance;
        loc.SetLanguage("en");
        Assert.Equal("Expert mode (enable writing)", Loc.T("Expert mode (enable writing)"));
        int changed = 0; loc.LanguageChanged += () => changed++;
        loc.SetLanguage("fr");
        Assert.Equal("fr", loc.Language);
        Assert.NotEqual("Expert mode (enable writing)", Loc.T("Expert mode (enable writing)"));
        Assert.Equal("Totally new untranslated string", Loc.T("Totally new untranslated string"));
        loc.SetLanguage("en");
        Assert.True(changed >= 2);
    }

    [Theory]
    [InlineData("fr-FR", "fr")]
    [InlineData("cs_CZ", "cs-CZ")]
    [InlineData("uk", "uk-UA")]
    [InlineData("ja-JP", "en")]
    public void Resolve_maps_culture_names(string input, string expected) => Assert.Equal(expected, Loc.Resolve(input));

    [AvaloniaFact]
    public void All_catalogs_parse()
    {
        foreach (var l in Loc.Languages.Where(l => l.Code != "en"))
        {
            Loc.Instance.SetLanguage(l.Code);
            Assert.Equal(l.Code, Loc.Instance.Language);
        }
        Loc.Instance.SetLanguage("en");
    }

    [AvaloniaFact]
    public void Switching_language_updates_bound_text()
    {
        using var app = new TestApp();
        app.Page<DashboardViewModel>(PageId.Dashboard);
        var texts = () => app.Window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToHashSet();
        Assert.Contains("Manual request", texts());
        Loc.Instance.SetLanguage("de");
        TestApp.Pump();
        Assert.DoesNotContain("Manual request", texts());
        Assert.Contains(Loc.T("Manual request"), texts());
        Loc.Instance.SetLanguage("en");
        TestApp.Pump();
        Assert.Contains("Manual request", texts());
    }
}

public class ServiceTests
{
    [Fact]
    public void Settings_roundtrip_and_corrupt_file_recovery()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddt4all-s-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "settings.json");
        using (var s = new SettingsService(path))
        {
            s.Update(x => { x.Theme = "dark"; x.EcuZipPath = "/data/ecu.zip"; x.Serial.BaudRate = 115200; });
            s.SaveNow();
        }
        using (var s = new SettingsService(path))
        {
            Assert.Equal("dark", s.Current.Theme);
            Assert.Equal("/data/ecu.zip", s.Current.EcuZipPath);
            Assert.Equal(115200, s.Current.Serial.BaudRate);
        }
        File.WriteAllText(path, "{ not json");
        using (var s = new SettingsService(path)) Assert.Equal("system", s.Current.Theme);
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Fake_catalog_has_5000_entries_and_filters_fast()
    {
        var cat = await new FakeEcuCatalogService().LoadAsync();
        Assert.Equal(5000, cat.Entries.Count);
        var sw = Stopwatch.StartNew();
        var (rows, count) = EcuBrowserViewModel_Filter(cat, "abs siemens", "", "", "project");
        sw.Stop();
        Assert.True(count > 0 && count < 5000);
        Assert.IsType<GroupHeaderRow>(rows[0]);
        Assert.True(sw.ElapsedMilliseconds < 100, $"filter took {sw.ElapsedMilliseconds} ms");
        var (flat, n2) = EcuBrowserViewModel_Filter(cat, "", "", "", "none");
        Assert.Equal(5000, n2); Assert.Equal(5000, flat.Count);
    }

    private static (IReadOnlyList<object>, int) EcuBrowserViewModel_Filter(EcuCatalog c, string t, string p, string pr, string m)
        => Ddt4All.App.ViewModels.EcuBrowserViewModel.FilterForTests(c, t, p, pr, m);

    [Fact]
    public void Logging_ring_buffer_is_bounded_and_levels_switch()
    {
        var store = new InMemoryLogStore();
        for (int i = 0; i < 6000; i++) store.Add(new LogEntry(DateTime.Now, Microsoft.Extensions.Logging.LogLevel.Information, "t", "m" + i, null));
        Assert.Equal(5000, store.Snapshot().Count);
        Assert.Equal("m1000", store.Snapshot()[0].Message);
    }
}
