using System.Diagnostics;
using Avalonia.Controls;
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

public class ScreenEngineTests
{
    [Fact]
    public async Task Engine_polls_and_decodes_in_background()
    {
        var (ecu, layout) = ScreenFixtures.Rich();
        var (transport, script) = await ScreenFixtures.RichTransportAsync();
        var screen = layout.Screens["Live data"];
        using var engine = new ScreenLiveEngine(ecu, screen, transport) { RateHz = 50 };
        Assert.Equal(1, engine.RequestCount);
        engine.Start();
        await WaitFor(() => engine.CycleCount >= 3);
        var changed = new List<int>();
        engine.DrainChanges(changed);
        Assert.Contains(0, changed); // Coolant is display 0
        Assert.Equal("-39 C", engine.GetText(0)); // 0xA0 - 40
        Assert.Equal(-39, engine.GetValue(0));
        engine.DrainChanges(changed);
        Assert.Empty(changed);        // unchanged values are not reported again
        var hist = new List<double>();
        engine.CopyHistory(0, hist);
        Assert.True(hist.Count >= 3 && hist.All(v => v == -39));
        await engine.StopAsync();
        Assert.Equal(LiveState.Stopped, engine.State);
        Assert.True(script.RequestCount >= 3);
    }

    [Fact]
    public async Task Engine_pause_resume_and_rate()
    {
        var (ecu, layout) = ScreenFixtures.Rich();
        var (transport, script) = await ScreenFixtures.RichTransportAsync();
        using var engine = new ScreenLiveEngine(ecu, layout.Screens["Live data"], transport) { RateHz = 20 };
        engine.Start();
        await WaitFor(() => engine.CycleCount >= 2);
        engine.Pause();
        await Task.Delay(120);
        long frozen = engine.CycleCount;
        await Task.Delay(250);
        Assert.True(engine.CycleCount - frozen <= 1);
        engine.Resume();
        await WaitFor(() => engine.CycleCount > frozen + 2);
        // 20 Hz => roughly 50 ms per cycle; 12 cycles must not take much less than 400 ms
        long c0 = engine.CycleCount; var sw = Stopwatch.StartNew();
        await WaitFor(() => engine.CycleCount >= c0 + 10, 5000);
        Assert.True(sw.ElapsedMilliseconds >= 300, $"rate limiter ineffective: {sw.ElapsedMilliseconds} ms for 10 cycles");
        await engine.StopAsync();
    }

    [Fact]
    public async Task Engine_reports_negative_response_without_crashing()
    {
        var (ecu, layout) = ScreenFixtures.Rich();
        var script = new Ddt4All.Comms.Simulation.EcuScript().Negative("2101", 0x31);
        var t = new Ddt4All.Comms.Simulation.SimulatedTransport().AddCan("X", 0x700, script);
        await t.ConnectEcuAsync(new Ddt4All.Comms.EcuAddress { Name = "X", TxId = 0x700 });
        using var engine = new ScreenLiveEngine(ecu, layout.Screens["Live data"], t) { RateHz = 50 };
        engine.Start();
        await WaitFor(() => engine.ErrorCount >= 2);
        Assert.Contains("0x31", engine.LastError);
        Assert.Null(engine.GetText(0));
        await engine.StopAsync();
    }

    [Fact]
    public async Task Recording_and_csv_export()
    {
        var (ecu, layout) = ScreenFixtures.Rich();
        var (transport, _) = await ScreenFixtures.RichTransportAsync();
        using var engine = new ScreenLiveEngine(ecu, layout.Screens["Live data"], transport) { RateHz = 50 };
        var logFile = Path.Combine(Path.GetTempPath(), "ddt4all-screen-" + Guid.NewGuid().ToString("N") + ".csv");
        engine.StartRecording();
        engine.StartLog(logFile);
        engine.Start();
        await WaitFor(() => engine.RecordedRows >= 5);
        await engine.StopAsync();
        engine.StopRecording(); engine.StopLog();
        var sw = new StringWriter();
        int rows = engine.ExportCsv(sw);
        var lines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("time_ms,Coolant", lines[0].Trim());
        Assert.Equal(rows + 1, lines.Length);
        Assert.EndsWith(",-39", lines[1].Trim());
        var fileLines = File.ReadAllLines(logFile);
        Assert.True(fileLines.Length >= 6); Assert.Equal("time_ms,Coolant", fileLines[0]);
        File.Delete(logFile);
        Assert.Equal("\"a,\"\"b\"\"\"", ScreenLiveEngine.Csv("a,\"b\""));
    }

    private static async Task WaitFor(Func<bool> cond, int ms = 3000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond() && DateTime.UtcNow < end) await Task.Delay(10);
        Assert.True(cond(), "condition not reached in time");
    }
}

public class ScreensPageTests
{
    private sealed class Rig : IDisposable
    {
        public TestApp App = new(width: 1280, height: 800);
        public ScreenSession Session = new();
        public SessionState State = new();
        public NotificationService Toasts = new();
        public AutoDialogs Dialogs = new();
        public ScreensViewModel Vm = null!;
        public Window Window = null!;
        public ScreensView View = null!;
        public ScreenCanvas Canvas = null!;

        public void Show(int w = 1280, int h = 800)
        {
            Vm = new ScreensViewModel(State, Session, Toasts, Dialogs);
            View = new ScreensView { DataContext = Vm };
            Window = new Window { Width = w, Height = h, Content = View };
            Window.Show();
            Canvas = View.FindControl<ScreenCanvas>("Canvas")!;
            TestApp.Pump();
        }

        public async Task WaitValues(Func<bool> cond, int ms = 4000)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (!cond() && DateTime.UtcNow < end) { await Task.Delay(15); TestApp.Pump(); }
            Assert.True(cond(), "values did not arrive");
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

    private static SceneItem Item(Rig r, SceneKind k, string? caption = null) =>
        r.Vm.Scene!.Items.First(i => i.Kind == k && (caption is null || i.Caption == caption));

    [AvaloniaFact]
    public async Task Live_values_flow_into_canvas_end_to_end()
    {
        using var rig = new Rig();
        var (ecu, layout) = ScreenFixtures.Rich();
        var (transport, script) = await ScreenFixtures.RichTransportAsync();
        rig.Session.Load(ecu, layout, transport);
        rig.Show();
        Assert.Equal("Live data", rig.Vm.SelectedNode?.Title);
        var coolant = Item(rig, SceneKind.Display, "Coolant");
        await rig.WaitValues(() => coolant.Value == "-39 C");
        Assert.Contains(script.Received, r => r.AsSpan().SequenceEqual(new byte[] { 0x10, 0x03 })); // PreSend (read-only service) ran
        Assert.Equal(LiveState.Running, rig.Vm.LiveState);
        rig.Vm.Engine!.RateHz = 30;
        await rig.Vm.StopCommand.ExecuteAsync(null);
        Assert.Equal(LiveState.Stopped, rig.Vm.LiveState);
        rig.Vm.StartCommand.Execute(null);
        await rig.WaitValues(() => rig.Vm.LiveState == LiveState.Running);
    }

    [AvaloniaFact]
    public async Task Safe_mode_blocks_buttons_and_writes_expert_mode_allows_them()
    {
        using var rig = new Rig();
        var (ecu, layout) = ScreenFixtures.Rich();
        var (transport, script) = await ScreenFixtures.RichTransportAsync();
        rig.Session.Load(ecu, layout, transport);
        rig.Show();
        await rig.WaitValues(() => rig.Vm.Engine?.CycleCount > 1);

        var reset = Item(rig, SceneKind.Button, "Reset");
        await rig.Vm.PressButtonAsync(reset.Button!);
        Assert.DoesNotContain(script.Received, r => r.Length > 0 && r[0] == 0x31); // 31 01 FF 00 never sent
        Assert.Contains(rig.Toasts.Toasts, t => t.Title == "Expert mode required");

        // staging an input is allowed, sending it is not
        var mode = Item(rig, SceneKind.Input, "Mode");
        Assert.True(rig.Vm.StageInput(mode, "ON"));
        Assert.True(rig.Vm.StagedCount == 1);
        Assert.False(rig.Vm.StageInput(mode, "NOPE_NOT_IN_LIST_999999"));
        await rig.Vm.SendStagedCommand.ExecuteAsync(null);
        Assert.DoesNotContain(script.Received, r => r.Length > 0 && r[0] == 0x2E);

        rig.State.IsExpertMode = true;
        rig.Dialogs.Answer = false;
        await rig.Vm.PressButtonAsync(reset.Button!);
        Assert.DoesNotContain(script.Received, r => r.Length > 0 && r[0] == 0x31); // user said no
        rig.Dialogs.Answer = true;
        await rig.Vm.PressButtonAsync(reset.Button!);
        Assert.Contains(script.Received, r => r.AsSpan().SequenceEqual(new byte[] { 0x31, 0x01, 0xFF, 0x00 }));
        await rig.Vm.SendStagedCommand.ExecuteAsync(null);
        // WriteConfig template 2E 01 00 00 00 00 with Mode=ON (1) at byte 3
        Assert.Contains(script.Received, r => r.Length == 6 && r[0] == 0x2E && r[2] == 0x01);
        Assert.Equal(0, rig.Vm.StagedCount);
    }

    [AvaloniaFact]
    public async Task Input_editor_and_sparkline_selection()
    {
        using var rig = new Rig();
        var (ecu, layout) = ScreenFixtures.Rich();
        var (transport, _) = await ScreenFixtures.RichTransportAsync();
        rig.Session.Load(ecu, layout, transport);
        rig.Show();
        await rig.WaitValues(() => rig.Vm.Engine?.CycleCount > 3);
        var disp = Item(rig, SceneKind.Display, "Coolant");
        rig.Vm.SelectDisplay(disp.Slot);
        rig.Vm.Tick();
        TestApp.Pump();
        Assert.True(rig.Vm.HasSelection);
        Assert.True(rig.Vm.SelectedHistory.Count >= 3);
        Assert.Equal("-39 C", rig.Vm.SelectedValue);
        Assert.Equal(disp.Slot, rig.Canvas.SelectedSlot);

        var input = Item(rig, SceneKind.Input, "Mode");
        rig.Canvas.BeginEdit(input);
        TestApp.Pump();
        Assert.Contains(rig.Canvas.GetVisualChildren(), c => c is ComboBox); // list data -> combo box
        rig.Canvas.CancelEdit();
        Assert.DoesNotContain(rig.Canvas.GetVisualChildren(), c => c is ComboBox);
    }

    [AvaloniaFact]
    public async Task Hundred_fifty_widgets_stay_smooth()
    {
        using var rig = new Rig();
        var (ecu, layout) = ScreenFixtures.Big(150);
        rig.Session.Load(ecu, layout, await ScreenFixtures.BigTransportAsync(150));
        rig.Show(1300, 900);
        Assert.Equal(150, rig.Vm.WidgetCount);
        await rig.WaitValues(() => rig.Vm.Engine?.CycleCount > 2);
        rig.Vm.Engine!.RateHz = 30;
        // UI side per frame: drain the engine + apply the changed values + render the canvas (software Skia, 1200x900).
        // The whole-window headless pipeline (full-frame readback) is not representative, so the canvas is rendered directly.
        var rtb = new RenderTargetBitmap(new Avalonia.PixelSize(1200, 900));
        rtb.Render(rig.Canvas); // warm-up
        double total = 0, worst = 0; int frames = 60, changedFrames = 0;
        for (int i = 0; i < frames; i++)
        {
            await Task.Delay(35); // new engine data is available on most frames (30 Hz poll)
            var t0 = Stopwatch.GetTimestamp();
            rig.Vm.Tick();
            rtb.Render(rig.Canvas);
            double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            total += ms; worst = Math.Max(worst, ms);
            if (rig.Vm.Engine!.CycleCount > 0) changedFrames++;
        }
        double avg = total / frames;
        Assert.True(avg < 33, $"avg UI frame {avg:0.0} ms (worst {worst:0.0} ms)");
        Assert.All(rig.Vm.Scene!.Items, it => Assert.NotEqual("", it.Value));
        Assert.True(rig.Vm.Engine.LastCycleMs < 50, $"engine cycle {rig.Vm.Engine.LastCycleMs} ms");
    }

    [AvaloniaFact]
    public async Task Screenshots()
    {
        using var rig = new Rig();
        var (ecu, layout) = ScreenFixtures.Demo();
        var (transport, _) = await ScreenFixtures.DemoTransportAsync();
        rig.Vm = null!;
        // empty state first
        rig.Show();
        rig.Shot("16-screens-empty");
        rig.Window.Close(); rig.Vm.Dispose();

        rig.Session.Load(ecu, layout, transport);
        rig.Show();
        await rig.WaitValues(() => Item(rig, SceneKind.Display, "Engine speed").Value.Length > 0);
        var disp = Item(rig, SceneKind.Display, "Engine speed");
        rig.Vm.SelectDisplay(disp.Slot);
        rig.Vm.ToggleRecordCommand.Execute(null);
        for (int i = 0; i < 40; i++) { await Task.Delay(40); rig.Vm.Tick(); TestApp.Pump(); }
        rig.Shot("17-screens-live-safe");

        rig.State.IsExpertMode = true;
        var mode = Item(rig, SceneKind.Input, "Idle mode");
        rig.Vm.StageInput(mode, "Fixed");
        rig.Vm.ZoomInCommand.Execute(null);
        rig.Vm.Tick(); TestApp.Pump();
        rig.Shot("18-screens-expert-staged-zoom");
        rig.Vm.ZoomResetCommand.Execute(null);

        var exportDir = Path.Combine(Path.GetTempPath(), "ddt4all-export-" + Guid.NewGuid().ToString("N"));
        var csv = rig.Vm.ExportRecording(exportDir);
        Assert.True(File.ReadAllLines(csv).Length > 5);
        Directory.Delete(exportDir, true);
    }

    [AvaloniaFact]
    public async Task Screenshot_150_widgets()
    {
        using var rig = new Rig();
        var (ecu, layout) = ScreenFixtures.Big(150);
        rig.Session.Load(ecu, layout, await ScreenFixtures.BigTransportAsync(150));
        rig.Show(1300, 820);
        await rig.WaitValues(() => rig.Vm.Scene!.Items[149].Value.Length > 0);
        rig.Vm.FitTo(1000, 640);
        for (int i = 0; i < 10; i++) { await Task.Delay(40); rig.Vm.Tick(); TestApp.Pump(); }
        rig.Shot("19-screens-150-widgets");
    }
}
