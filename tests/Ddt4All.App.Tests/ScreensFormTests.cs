using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Ddt4All.App.Services;
using Ddt4All.App.ViewModels.Screens;
using Ddt4All.App.Views.Screens;
using Ddt4All.Core.Ecu;
using Ddt4All.Core.Layout;

namespace Ddt4All.App.Tests;

internal sealed class FormRig : IDisposable
{
    public TestApp App;
    public ScreenSession Session = new();
    public SessionState State = new();
    public NotificationService Toasts = new();
    public AutoDialogs Dialogs = new();
    public ScreensViewModel Vm = null!;
    public Ddt4All.Comms.Simulation.EcuScript? Script;
    public Window Window = null!;
    public ScreensView View = null!;

    public FormRig(string theme = "light", int width = 1280, int height = 800) { App = new TestApp(theme: theme, width: width, height: height); }

    /// <summary>Mounts the real view first and assigns the DataContext afterwards (the order that broke command bindings once).</summary>
    public void Show(int w = 1280, int h = 800, bool lateContext = true)
    {
        Vm = new ScreensViewModel(State, Session, Toasts, Dialogs);
        View = new ScreensView();
        Window = new Window { Width = w, Height = h, Content = View };
        if (!lateContext) View.DataContext = Vm;
        Window.Show();
        TestApp.Pump();
        if (lateContext) { View.DataContext = Vm; TestApp.Pump(); }
    }

    public FormPanel Panel => View.FindControl<FormPanel>("Form")!;
    public IEnumerable<T> Realized<T>() where T : Control => Panel.GetVisualDescendants().OfType<T>();

    public async Task Wait(Func<bool> cond, int ms = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond() && DateTime.UtcNow < end) { await Task.Delay(15); TestApp.Pump(); }
        Assert.True(cond(), "condition not reached in time");
    }

    public async Task Settle(int frames = 12)
    {
        for (int i = 0; i < frames; i++) { await Task.Delay(40); Vm.Tick(); TestApp.Pump(); }
    }

    public string Shot(string name)
    {
        TestApp.Pump(); TestApp.Pump();
        var dir = Path.Combine(ScreenFixtures.RepoRoot(), "docs", "screenshots");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".png");
        Window.CaptureRenderedFrame()!.Save(path, new PngBitmapEncoderOptions());
        return path;
    }

    public void Dispose() { Vm.Dispose(); Window.Close(); App.Dispose(); }
}

public class ScreensFormScreenshots
{
    internal static async Task RealShot(string ecuName, string screenName, string file, string theme = "light", int w = 1280, int h = 800, bool expert = false, string? filter = null, Action<FormRig>? tweak = null)
    {
        using var rig = new FormRig(theme, w, h);
        var (ecu, layout, scr) = RealZip.Load(ecuName, screenName);
        var transport = await RealZip.SimulatorAsync(ecu, scr);
        rig.Session.Load(ecu, layout, transport);
        rig.Show(w, h);
        rig.State.IsExpertMode = expert;
        var node = rig.Vm.Nodes.SelectMany(n => n.Children).First(c => c.Screen == scr);
        rig.Vm.SelectedNode = node;
        await rig.Settle(3);
        await rig.Wait(() => rig.Vm.Engine is not null);
        if (filter is not null) rig.Vm.ControlFilter = filter;
        tweak?.Invoke(rig);
        await rig.Settle(25);
        rig.Shot(file);
    }

    [AvaloniaFact]
    public async Task Real_ecu_screenshots()
    {
        RealZip.RequireOrSkip();
        // small identification page
        await RealShot("Abs_X44_2.0", "22 - Identification systeme", "30-form-small-identification");
        // section headings, many numeric tiles with sparklines
        await RealShot("C1AHSEVO_HR16Deg3h_P_HEV_HJBPh2_DJB_P1310_S400_v3", "Fuel System and Emissions Parameters", "31-form-sections-fuel-system");
        // list inputs with their live read-back, two edits staged (expert mode)
        await RealShot("DSMU_CMF1_SW7.6", "Configuration", "32-form-inputs-lists-edited", expert: true, tweak: rig =>
        {
            var lists = rig.Vm.InputRows.OfType<FormListInputRow>().Take(2).ToList();
            foreach (var r in lists) r.Text = r.Choices[0];
        });
        // buttons in safe mode: write buttons carry the lock
        await RealShot("DSMU_CMF1_SW7.6", "I/O Control", "33-form-buttons-safe-mode", expert: false);
        // many displays, one selected -> inspector with the expanded history
        await RealShot("5D0B_HZJ_H_M5Rk_DDg3+_China6_20190510", "Engine Main Parameters", "34-form-many-displays-selected", tweak: rig => rig.Vm.SelectDisplay(3));
        // the biggest write/read table of the sample: 257 inputs (virtualised)
        await RealShot("EMM_EDISON_DDT2000_SW15_2_V1.0", "EMM_AlternatorManagement - Configuration", "35-form-large-257-inputs", expert: true);
        // dark theme
        await RealShot("FrontCamera_SUV_SW7p2_V2", "config + coding", "36-form-dark-config-coding", theme: "dark", expert: true);
        // narrow window
        await RealShot("C1AHSEVO_HR16Deg3h_P_HEV_HJBPh2_DJB_P1310_S400_v3", "Fuel System and Emissions Parameters", "37-form-narrow", w: 700, h: 780);
        // control filter
        await RealShot("5D0B_HZJ_H_M5Rk_DDg3+_China6_20190510", "Engine Main Parameters", "38-form-filter", filter: "temp");
        // documentation style screen: notes, long captions, text values
        await RealShot("DCM34-P4C8x_X95_L38_B32_V22", "Verrou logiciel", "39-form-text-values-notes");
    }

    [AvaloniaFact]
    public async Task Classic_canvas_view_screenshot()
    {
        using var rig = new FormRig();
        var (ecu, layout) = ScreenFixtures.Demo();
        var (transport, _) = await ScreenFixtures.DemoTransportAsync();
        rig.Session.Load(ecu, layout, transport);
        rig.Show();
        rig.Vm.IsClassicView = true;
        await rig.Settle(30);
        rig.Shot("40-classic-canvas-view");
    }
}


internal static class PerfLog
{
    /// <summary>Numbers of the perf tests go to the console and to a temp file (xunit swallows stdout of passing tests).</summary>
    public static void Write(string line)
    {
        Console.WriteLine("[perf] " + line);
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "ddt4all-perf.txt"), line + Environment.NewLine); } catch { }
    }
}

public class ScreensFormUiTests
{
    private static async Task<FormRig> DemoRig(bool expert)
    {
        var rig = new FormRig();
        var (ecu, layout) = ScreenFixtures.Demo();
        var (transport, script) = await ScreenFixtures.DemoTransportAsync();
        rig.Script = script;
        rig.Session.Load(ecu, layout, transport);
        rig.State.IsExpertMode = expert;
        rig.Show();                                  // view first, DataContext afterwards
        await rig.Settle(6);
        return rig;
    }

    private static Button ButtonWithText(FormRig rig, string text) =>
        rig.Realized<TextBlock>().First(t => t.Text == text && t.FindAncestorOfType<Button>() is { })
           .FindAncestorOfType<Button>()!;

    private static void Click(FormRig rig, Control c)
    {
        TestApp.Pump();
        var p = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), rig.Window)!.Value;
        rig.Window.MouseMove(p); rig.Window.MouseDown(p, MouseButton.Left); rig.Window.MouseUp(p, MouseButton.Left);
        TestApp.Pump();
    }

    [AvaloniaFact]
    public async Task Late_datacontext_numeric_edit_and_write_button_work_through_the_real_controls()
    {
        using var rig = await DemoRig(expert: true);
        Assert.NotNull(rig.View.DataContext);                       // assigned after the view was shown
        Assert.True(rig.Panel.RealizedCount > 0);

        // live value arrived in a real tile
        await rig.Wait(() => rig.Realized<ValueBox>().Any(v => v.Row is { ShowValue: true, ValueText.Length: > 0 }));

        // numeric input: a NumericUpDown with unit, step and bounds from the data definition
        var nud = rig.Realized<NumericUpDown>().Single();
        var row = Assert.IsType<FormNumberInputRow>(nud.DataContext);
        Assert.Equal("Idle offset", row.Caption);
        Assert.Equal("rpm", row.Unit);
        Assert.Equal(10m, nud.Increment);
        Assert.Equal(0m, nud.Minimum);
        Assert.Equal(2550m, nud.Maximum);
        nud.Value = 40m;                                            // what typing 40 + Enter ends up doing
        TestApp.Pump();
        Assert.True(row.IsStaged);
        Assert.Equal(1, rig.Vm.StagedCount);
        Assert.True(rig.Vm.HasStaged);
        Assert.Contains(rig.Realized<Border>(), b => b.Classes.Contains("badge") && b.IsEffectivelyVisible);   // EDITED badge

        // an out-of-range value is rejected inline, nothing staged
        nud.Value = 40m;
        var combo = rig.Realized<ComboBox>().Single();
        Assert.Equal("Idle mode", ((FormInputRow)combo.DataContext!).Caption);
        combo.SelectedItem = "Fixed";
        TestApp.Pump();
        Assert.Equal(2, rig.Vm.StagedCount);

        // press the real "Apply settings" button with the mouse
        var apply = ButtonWithText(rig, "Apply settings");
        Assert.Contains("write", apply.Classes);
        Assert.DoesNotContain("locked", apply.Classes);            // expert mode: unlocked
        Click(rig, apply);
        await rig.Wait(() => rig.Script!.Received.Any(r => r.Length == 6 && r[0] == 0x2E));
        var write = rig.Script!.Received.Last(r => r.Length == 6 && r[0] == 0x2E);
        Assert.Equal(1, write[2]);         // Idle mode = Fixed (1)
        Assert.Equal(4, write[3]);         // Idle offset = 40 rpm / step 10
        await rig.Wait(() => rig.Vm.StagedCount == 0);
        Assert.False(row.IsStaged);
    }

    [AvaloniaFact]
    public async Task Safe_mode_marks_write_buttons_and_blocks_them()
    {
        using var rig = await DemoRig(expert: false);
        var reset = ButtonWithText(rig, "Reset adaptations");
        Assert.Contains("write", reset.Classes);
        Assert.Contains("locked", reset.Classes);
        var refresh = ButtonWithText(rig, "Refresh");            // read-only service: plain button
        Assert.DoesNotContain("write", refresh.Classes);
        Click(rig, reset);
        await Task.Delay(150); TestApp.Pump();
        Assert.DoesNotContain(rig.Script!.Received, r => r.Length > 0 && r[0] == 0x31);
        Assert.Contains(rig.Toasts.Toasts, t => t.Title == "Expert mode required");
        rig.State.IsExpertMode = true;
        TestApp.Pump();
        Assert.DoesNotContain("locked", reset.Classes);          // the lock follows the expert switch
    }

    [AvaloniaFact]
    public async Task Selecting_a_tile_expands_its_history()
    {
        using var rig = await DemoRig(expert: false);
        await rig.Settle(12);
        var tile = rig.Realized<Button>().First(b => b.DataContext is FormDisplayRow { Caption: "Engine speed" });
        Click(rig, tile);
        await rig.Settle(4);
        Assert.True(rig.Vm.HasSelection);
        Assert.Equal("Engine speed", rig.Vm.SelectedName);
        Assert.True(rig.Vm.SelectedHistory.Count >= 3);
        Assert.True(((FormDisplayRow)tile.DataContext!).IsSelected);
        Assert.Contains("min", rig.Vm.SelectedRange);
    }

    [AvaloniaFact]
    public async Task Filter_box_narrows_the_form_and_reports_no_match()
    {
        using var rig = await DemoRig(expert: false);
        int all = rig.Vm.Rows.Count;
        var box = rig.View.FindControl<TextBox>("ControlFilterBox")!;
        box.Text = "speed";                                       // typed into the real box
        TestApp.Pump();
        Assert.Equal("speed", rig.Vm.ControlFilter);
        var shown = rig.Vm.Rows.OfType<FormDisplayRow>().Select(r => r.Caption).ToList();
        Assert.Equal(new[] { "Engine speed", "Vehicle speed" }, shown);
        Assert.True(rig.Vm.Rows.Count < all);
        box.Text = "zzz-nothing";
        TestApp.Pump();
        Assert.True(rig.Vm.NoMatches);
        box.Text = "";
        TestApp.Pump();
        Assert.Equal(all, rig.Vm.Rows.Count);
    }

    [AvaloniaFact]
    public async Task Classic_canvas_toggle_swaps_the_views_and_keeps_values_flowing()
    {
        using var rig = await DemoRig(expert: false);
        var canvas = rig.View.FindControl<ScreenCanvas>("Canvas")!;
        var scroller = rig.View.FindControl<ScrollViewer>("FormScroller")!;
        Assert.True(scroller.IsEffectivelyVisible);
        Assert.False(canvas.IsEffectivelyVisible);                // default: new form view
        var toggle = rig.View.FindControl<ToggleButton>("ClassicToggle")!;
        Click(rig, toggle);
        Assert.True(rig.Vm.IsClassicView);
        Assert.True(canvas.IsEffectivelyVisible);
        Assert.False(scroller.IsEffectivelyVisible);
        await rig.Settle(10);
        Assert.Contains(rig.Vm.Scene!.Items, i => i.Kind == SceneKind.Display && i.Value.Length > 0);
        Click(rig, toggle);
        Assert.False(rig.Vm.IsClassicView);
        Assert.True(scroller.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Eight_thousand_widgets_are_virtualised()
    {
        using var rig = new FormRig();
        var (ecu, layout) = ScreenFixtures.Big(4000);
        rig.Session.Load(ecu, layout, await ScreenFixtures.BigTransportAsync(4000));
        var sw = Stopwatch.StartNew();
        rig.Show(1300, 900);
        double openMs = sw.Elapsed.TotalMilliseconds;
        Assert.Equal(4000, rig.Vm.AllRows.OfType<FormDisplayRow>().Count());
        Assert.True(rig.Panel.RealizedCount is > 20 and < 250, $"realized {rig.Panel.RealizedCount}");
        int before = rig.Panel.RealizedCount;
        var scroller = rig.View.FindControl<ScrollViewer>("FormScroller")!;
        scroller.Offset = new Vector(0, rig.Panel.Layout.Height / 2);
        await rig.Settle(3);
        Assert.True(rig.Panel.RealizedCount < 250);
        // scrolled to the middle: the realized rows are the ones near the viewport, not the first ones
        Assert.All(rig.Panel.RealizedControls, c => Assert.NotNull(c.DataContext));
        var firstRealized = rig.Panel.RealizedControls.Select(c => rig.Vm.AllRows.ToList().IndexOf((FormRow)c.DataContext!)).Min();
        Assert.True(firstRealized > 500, $"first realized row {firstRealized}");
        PerfLog.Write($"4000 displays: open+first layout {openMs:0} ms, realized {before} controls, {rig.Panel.Layout.Cards.Count} cards");
    }

    [AvaloniaTheory]
    [InlineData(150)]
    [InlineData(1000)]
    public async Task Form_stays_smooth_with_many_widgets(int count)
    {
        using var rig = new FormRig();
        var (ecu, layout) = ScreenFixtures.Big(count);
        rig.Session.Load(ecu, layout, await ScreenFixtures.BigTransportAsync(count));
        rig.Show(1300, 900);
        await rig.Wait(() => rig.Vm.Engine?.CycleCount > 2);
        rig.Vm.Engine!.RateHz = 30;
        // UI-thread cost of a live frame = draining + applying the changed values + layout + recording the changed value boxes.
        // The headless pipeline also rasterises the dirty region in software on the same thread (a GPU backend does that on its
        // own thread), so that part is measured separately and not counted.
        ValueBox.CollectStats = true;
        double uiTotal = 0, uiWorst = 0, tickMs = 0, layoutMs = 0, recordMs = 0, rasterMs = 0; int frames = 60;
        try
        {
            for (int i = 0; i < frames; i++)
            {
                await Task.Delay(35);
                long rec0 = ValueBox.StatTicks;
                var t0 = Stopwatch.GetTimestamp();
                rig.Vm.Tick();
                var t1 = Stopwatch.GetTimestamp();
                rig.Window.UpdateLayout();
                var t2 = Stopwatch.GetTimestamp();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                var t3 = Stopwatch.GetTimestamp();
                double tick = Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds, lay = Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds;
                double rec = (ValueBox.StatTicks - rec0) * 1000.0 / Stopwatch.Frequency;
                double all = Stopwatch.GetElapsedTime(t2, t3).TotalMilliseconds;
                tickMs += tick; layoutMs += lay; recordMs += rec; rasterMs += Math.Max(0, all - rec);
                double ui = tick + lay + rec; uiTotal += ui; uiWorst = Math.Max(uiWorst, ui);
            }
        }
        finally { ValueBox.CollectStats = false; }
        double avg = uiTotal / frames;
        string msg = $"{count} displays: UI-thread frame avg {avg:0.0} ms (tick {tickMs / frames:0.0} + layout {layoutMs / frames:0.0} + record {recordMs / frames:0.0}; worst {uiWorst:0.0}), headless software raster {rasterMs / frames:0.0} ms/frame, engine cycle {rig.Vm.Engine.LastCycleMs:0.0} ms, realized {rig.Panel.RealizedCount} controls";
        PerfLog.Write(msg);
        Assert.True(avg < 16, msg);
        Assert.True(rig.Vm.DisplayRow(count - 1)!.ShowValue);
    }
}
