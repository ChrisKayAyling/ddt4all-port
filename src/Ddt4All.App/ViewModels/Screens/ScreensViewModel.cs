using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ddt4All.App.Localization;
using Ddt4All.App.Services;
using Ddt4All.App.Views.Screens;
using Ddt4All.Core.Abstractions;
using Ddt4All.Core.Ecu;
using Ddt4All.Core.Layout;

namespace Ddt4All.App.ViewModels.Screens;

public sealed class ScreenNode
{
    public string Title { get; init; } = "";
    public Screen? Screen { get; init; }
    public bool IsCategory => Screen is null;
    public string Detail { get; init; } = "";
    public ObservableCollection<ScreenNode> Children { get; } = new();
    public override string ToString() => Title;
}

/// <summary>The Screens page: screen tree, live canvas, run controls, recording, sparkline.</summary>
public sealed partial class ScreensViewModel : PageViewModel
{
    private readonly SessionState _state;
    private readonly IScreenSession _session;
    private readonly INotificationService _toasts;
    private readonly IDialogService _dialogs;
    private readonly IFilePickerService? _files;
    private readonly INavigationService? _nav;
    private readonly DispatcherTimer _timer;
    private readonly List<int> _changed = new();
    private readonly List<double> _history = new();
    private readonly Dictionary<string, Dictionary<string, string>> _staged = new();
    private ScreenLiveEngine? _engine;
    private CancellationTokenSource? _openCts;
    private List<ScreenNode> _allNodes = new();
    private bool _suppress;

    public ScreensViewModel(SessionState state, IScreenSession session, INotificationService toasts, IDialogService dialogs,
        IFilePickerService? files = null, INavigationService? nav = null)
    {
        _state = state; _session = session; _toasts = toasts; _dialogs = dialogs; _files = files; _nav = nav;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => Tick());
        session.Changed += () => Dispatcher.UIThread.Post(Reload);
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionState.IsExpertMode)) { OnPropertyChanged(nameof(IsExpert)); OnPropertyChanged(nameof(WriteHint)); }
        };
        Reload();
    }

    public override PageId Id => PageId.Screens;
    public override string Title => Loc.T("Screens");

    public ObservableCollection<ScreenNode> Nodes { get; } = new();
    public ScreenScene? Scene { get; private set; }
    public ScreenLiveEngine? Engine => _engine;
    public IReadOnlyList<double> SelectedHistory => _history;

    /// <summary>Scene replaced (view must assign it to the canvas).</summary>
    public event Action<ScreenScene?>? SceneChanged;
    /// <summary>Live values changed: slots + engine (view pushes them into the canvas and repaints once).</summary>
    public event Action<IReadOnlyList<int>>? ValuesChanged;
    /// <summary>Selected display history changed (sparkline).</summary>
    public event Action? HistoryChanged;
    /// <summary>An input value was staged/unstaged and the canvas item should reflect it.</summary>
    public event Action<SceneItem, string, bool>? InputStaged;

    [ObservableProperty] private ScreenNode? _selectedNode;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private double _zoom = 1.0;
    [ObservableProperty] private double _rateHz = 10;
    [ObservableProperty] private bool _autoStart = true;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private LiveState _liveState = LiveState.Stopped;
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private bool _isLogging;
    [ObservableProperty] private string _recordedText = "";
    [ObservableProperty] private string? _logPath;
    [ObservableProperty] private int _selectedSlot = -1;
    [ObservableProperty] private string _selectedName = "";
    [ObservableProperty] private string _selectedValue = "";
    [ObservableProperty] private string _selectedRange = "";
    [ObservableProperty] private string _ecuTitle = "";
    [ObservableProperty] private string _screenTitle = "";
    [ObservableProperty] private bool _hasEcu;
    [ObservableProperty] private bool _hasScreen;
    [ObservableProperty] private int _widgetCount;
    [ObservableProperty] private int _stagedCount;
    [ObservableProperty] private string _perfText = "";

    public bool IsExpert => _state.IsExpertMode;
    public bool IsOnline => _engine is not null;
    public string WriteHint => IsExpert ? Loc.T("Expert mode: buttons and inputs can write to the ECU (with confirmation).") : Loc.T("Safe mode: live values only. Enable expert mode to use buttons and write inputs.");
    public bool IsRunning => LiveState == LiveState.Running;
    public bool IsPaused => LiveState == LiveState.Paused;
    public bool IsStopped => LiveState == LiveState.Stopped;
    public bool HasSelection => SelectedSlot >= 0;
    public bool HasStaged => StagedCount > 0;
    public string EmptyTitle => HasEcu ? Loc.T("This ECU has no screens") : Loc.T("No ECU opened");
    public string EmptyText => HasEcu ? Loc.T("The definition of this ECU does not contain any screen layout.") : Loc.T("Open an ECU from the ECU Browser to see its screens.");

    partial void OnLiveStateChanged(LiveState value)
    {
        OnPropertyChanged(nameof(IsRunning)); OnPropertyChanged(nameof(IsPaused)); OnPropertyChanged(nameof(IsStopped));
        StartCommand.NotifyCanExecuteChanged(); PauseCommand.NotifyCanExecuteChanged(); StopCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedSlotChanged(int value) { OnPropertyChanged(nameof(HasSelection)); }
    partial void OnStagedCountChanged(int value) { OnPropertyChanged(nameof(HasStaged)); SendStagedCommand.NotifyCanExecuteChanged(); }
    partial void OnHasEcuChanged(bool value) { OnPropertyChanged(nameof(EmptyTitle)); OnPropertyChanged(nameof(EmptyText)); }
    partial void OnRateHzChanged(double value) { if (_engine is not null) _engine.RateHz = Math.Clamp(value, 1, 30); }
    partial void OnFilterTextChanged(string value) => RebuildTree();
    partial void OnSelectedNodeChanged(ScreenNode? value)
    {
        if (_suppress || value?.Screen is null) return;
        _ = OpenScreenAsync(value.Screen);
    }

    public override void OnNavigatedTo()
    {
        if (_engine is not null && AutoStart && LiveState == LiveState.Stopped && Scene is not null) StartLive();
    }

    /// <summary>Called by the view when it is detached: stops polling so a hidden page costs nothing.</summary>
    public void OnHidden()
    {
        if (LiveState == LiveState.Running) { _engine?.Pause(); LiveState = LiveState.Paused; _wasHidden = true; }
    }
    private bool _wasHidden;
    public void OnShown()
    {
        if (_wasHidden && LiveState == LiveState.Paused) { _engine?.Resume(); LiveState = LiveState.Running; }
        _wasHidden = false;
    }

    // ------------------------------------------------------------------ tree / session

    private void Reload()
    {
        var ecu = _session.Ecu; var layout = _session.Layout;
        HasEcu = ecu is not null;
        EcuTitle = ecu is null ? "" : ecu.EcuName;
        _allNodes = new();
        if (layout is not null)
            foreach (var cat in layout.Categories)
            {
                var node = new ScreenNode { Title = cat.Name, Detail = cat.ScreenNames.Count.ToString(CultureInfo.InvariantCulture) };
                foreach (var name in cat.ScreenNames)
                    if (layout.Screens.TryGetValue(name, out var s))
                        node.Children.Add(new ScreenNode { Title = s.Name, Screen = s, Detail = (s.Displays.Count + s.Inputs.Count) > 0 ? $"{s.Displays.Count}" : "" });
                if (node.Children.Count > 0) _allNodes.Add(node);
            }
        RebuildTree();
        CloseScreen();
        StatusText = ecu is null ? Loc.T("No ECU opened") : _session.Transport is null ? Loc.T("Offline: connect an adapter to see live values") : Loc.T("Select a screen");
        // open the first screen automatically
        var first = _allNodes.FirstOrDefault()?.Children.FirstOrDefault();
        if (first is not null) SelectedNode = first;
    }

    private void RebuildTree()
    {
        _suppress = true;
        var keep = SelectedNode?.Screen;
        Nodes.Clear();
        var f = FilterText.Trim();
        foreach (var cat in _allNodes)
        {
            if (f.Length == 0) { Nodes.Add(cat); continue; }
            var hits = cat.Children.Where(c => c.Title.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0 && !cat.Title.Contains(f, StringComparison.OrdinalIgnoreCase)) continue;
            var copy = new ScreenNode { Title = cat.Title, Detail = hits.Count.ToString(CultureInfo.InvariantCulture) };
            foreach (var h in hits.Count > 0 ? hits : cat.Children.ToList()) copy.Children.Add(h);
            Nodes.Add(copy);
        }
        _suppress = false;
        if (keep is not null) SelectedNode = Nodes.SelectMany(n => n.Children).FirstOrDefault(c => c.Screen == keep);
    }

    private void CloseScreen()
    {
        _openCts?.Cancel();
        DisposeEngine();
        Scene = null; _staged.Clear(); StagedCount = 0;
        HasScreen = false; ScreenTitle = ""; WidgetCount = 0; SelectedSlot = -1; _history.Clear();
        SceneChanged?.Invoke(null);
    }

    private void DisposeEngine()
    {
        _timer.Stop();
        var e = _engine; _engine = null;
        if (e is not null) { e.StateChanged -= OnEngineState; _ = Task.Run(() => { try { e.Dispose(); } catch { } }); }
        LiveState = LiveState.Stopped; IsRecording = false; IsLogging = false; RecordedText = "";
        OnPropertyChanged(nameof(IsOnline));
    }

    private void OnEngineState() => Dispatcher.UIThread.Post(() => { if (_engine is not null) LiveState = _engine.State; });

    /// <summary>Opens a screen: builds the scene, sends the screen's pre-send commands (read-only unless expert) and starts polling.</summary>
    public async Task OpenScreenAsync(Screen screen)
    {
        _openCts?.Cancel();
        var cts = _openCts = new CancellationTokenSource();
        DisposeEngine();
        _staged.Clear(); StagedCount = 0; SelectedSlot = -1; _history.Clear();
        var ecu = _session.Ecu;
        Scene = ScreenScene.Build(screen, ecu);
        ScreenTitle = screen.Name; HasScreen = true;
        WidgetCount = Scene.Items.Length;
        SceneChanged?.Invoke(Scene);
        HistoryChanged?.Invoke();

        IEcuTransport? transport = null;
        if (ecu is not null)
        {
            try { transport = await _session.PrepareTransportAsync(cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _toasts.Warning(Loc.T("Cannot reach the ECU"), ex.Message); }
        }
        if (cts.IsCancellationRequested) return;
        if (ecu is null || transport is null)
        {
            StatusText = Loc.T("Offline: connect an adapter to see live values");
            OnPropertyChanged(nameof(IsOnline));
            return;
        }

        var engine = _engine = new ScreenLiveEngine(ecu, screen, transport) { RateHz = Math.Clamp(RateHz, 1, 30) };
        engine.StateChanged += OnEngineState;
        OnPropertyChanged(nameof(IsOnline));
        StatusText = engine.RequestCount == 0 ? Loc.T("This screen has no live values") : Loc.T("Ready");

        if (screen.PreSend.Count > 0)
        {
            int skipped = 0;
            try { skipped = await SendCommandsAsync(engine, screen.PreSend, null, cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { StatusText = Loc.F("Screen start-up request failed: {0}", ex.Message); }
            if (skipped > 0) StatusText = Loc.F("{0} start-up request(s) skipped (expert mode required)", skipped);
        }
        if (cts.IsCancellationRequested) return;
        if (AutoStart && engine.RequestCount > 0) StartLive();
    }

    // ------------------------------------------------------------------ run control

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start() => StartLive();
    private bool CanStart() => _engine is not null && LiveState != LiveState.Running;

    public void StartLive()
    {
        if (_engine is null) return;
        _engine.RateHz = Math.Clamp(RateHz, 1, 30);
        _wasHidden = false;
        _engine.Start();
        LiveState = _engine.State;
        _timer.Start();
        StatusText = Loc.T("Running");
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        if (_engine is null) return;
        if (_engine.State == LiveState.Paused) { _engine.Resume(); StatusText = Loc.T("Running"); }
        else { _engine.Pause(); StatusText = Loc.T("Paused"); }
        LiveState = _engine.State;
    }
    private bool CanPause() => _engine is not null && LiveState != LiveState.Stopped;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        var e = _engine;
        if (e is null) return;
        await e.StopAsync();
        Tick();
        _timer.Stop();
        LiveState = LiveState.Stopped;
        StatusText = Loc.T("Stopped");
    }
    private bool CanStop() => _engine is not null && LiveState != LiveState.Stopped;

    // ------------------------------------------------------------------ UI refresh (<= 30 Hz)

    /// <summary>Drains the engine and notifies the view. Public so tests (and the view) can pump it deterministically.</summary>
    public void Tick()
    {
        var e = _engine;
        if (e is null) return;
        e.DrainChanges(_changed);
        if (_changed.Count > 0) ValuesChanged?.Invoke(_changed);

        if (SelectedSlot >= 0)
        {
            e.CopyHistory(SelectedSlot, _history);
            SelectedValue = e.GetText(SelectedSlot) ?? "-";
            if (_history.Count > 0) SelectedRange = $"min {_history.Min():0.##}   max {_history.Max():0.##}   n={_history.Count}";
            else SelectedRange = "";
            HistoryChanged?.Invoke();
        }

        var err = e.LastError;
        PerfText = $"{e.RequestCount} req | cycle {e.LastCycleMs:0} ms | #{e.CycleCount}" + (e.ErrorCount > 0 ? $" | {e.ErrorCount} err" : "");
        if (LiveState == LiveState.Running) StatusText = err is null ? Loc.T("Running") : Loc.F("Running - last error: {0}", err);
        if (e.IsRecording) RecordedText = Loc.F("{0} rows", e.RecordedRows);
    }

    public void SelectDisplay(int slot)
    {
        SelectedSlot = slot;
        var scr = SelectedNode?.Screen;
        SelectedName = slot >= 0 && scr is not null && slot < scr.Displays.Count ? scr.Displays[slot].DataName : "";
        _history.Clear();
        SelectedValue = ""; SelectedRange = "";
        if (_engine is not null) Tick(); else HistoryChanged?.Invoke();
    }

    // ------------------------------------------------------------------ zoom

    [RelayCommand] private void ZoomIn() => Zoom = Math.Min(4, Math.Round(Zoom * 1.25, 2));
    [RelayCommand] private void ZoomOut() => Zoom = Math.Max(0.25, Math.Round(Zoom / 1.25, 2));
    [RelayCommand] private void ZoomReset() => Zoom = 1;
    public string ZoomText => $"{Zoom * 100:0}%";
    partial void OnZoomChanged(double value) => OnPropertyChanged(nameof(ZoomText));

    /// <summary>Fit the screen into the given viewport (called by the view).</summary>
    public void FitTo(double width, double height)
    {
        if (Scene is null || width <= 0 || height <= 0) return;
        Zoom = Math.Clamp(Math.Round(Math.Min(width / Scene.Width, height / Scene.Height), 2), 0.25, 4);
    }

    // ------------------------------------------------------------------ recording / logging

    [RelayCommand]
    private void ToggleRecord()
    {
        if (_engine is null) return;
        if (_engine.IsRecording) { _engine.StopRecording(); IsRecording = false; RecordedText = Loc.F("{0} rows", _engine.RecordedRows); }
        else { _engine.StartRecording(); IsRecording = true; RecordedText = Loc.F("{0} rows", 0); }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (_engine is null || _engine.RecordedRows == 0) { _toasts.Info(Loc.T("Nothing recorded yet"), Loc.T("Press Record while the screen is running.")); return; }
        string? dir = _files is null ? null : await _files.PickFolderAsync(Loc.T("Export recording"));
        dir ??= DefaultExportDir();
        try
        {
            var path = ExportRecording(dir);
            _toasts.Success(Loc.T("Recording exported"), path);
        }
        catch (Exception ex) { _toasts.Error(Loc.T("Export failed"), ex.Message); }
    }

    private static string DefaultExportDir() => Path.Combine(AppPaths.DataDir, "exports");

    /// <summary>Writes the recording to <paramref name="dir"/>/screen-&lt;name&gt;-&lt;time&gt;.csv and returns the path.</summary>
    public string ExportRecording(string dir)
    {
        if (_engine is null) throw new InvalidOperationException("No live session");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"screen-{Sanitize(ScreenTitle)}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        using var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(false));
        _engine.ExportCsv(w);
        return path;
    }

    [RelayCommand]
    private void ToggleLog()
    {
        if (_engine is null) return;
        if (_engine.IsLogging) { _engine.StopLog(); IsLogging = false; _toasts.Info(Loc.T("CSV log closed"), LogPath); return; }
        try
        {
            LogPath = Path.Combine(AppPaths.LogDir, "screens", $"{Sanitize(ScreenTitle)}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            _engine.StartLog(LogPath);
            IsLogging = true;
        }
        catch (Exception ex) { _toasts.Error(Loc.T("Cannot write the CSV log"), ex.Message); }
    }

    private static string Sanitize(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var chars = s.Select(c => Array.IndexOf(bad, c) >= 0 || c == ' ' ? '_' : c).ToArray();
        return chars.Length == 0 ? "screen" : new string(chars);
    }

    [RelayCommand] private void OpenEcuBrowser() => _nav?.NavigateTo(PageId.EcuBrowser);

    // ------------------------------------------------------------------ writing (inputs, buttons)

    /// <summary>Validates an edited input value and stages it (nothing is sent). Returns false if the codec rejects it.</summary>
    public bool StageInput(SceneItem item, string text)
    {
        var ecu = _session.Ecu;
        var req = ecu?.GetRequest(item.RequestName);
        if (ecu is null || req is null) { _toasts.Warning(Loc.T("Unknown request"), item.RequestName); return false; }
        var probe = new Dictionary<string, string> { [item.DataName] = text };
        if (!req.TryBuildRequest(probe, out _, out var err))
        { _toasts.Warning(Loc.F("Invalid value for {0}", item.DataName), err); return false; }
        if (!_staged.TryGetValue(req.Name, out var d)) _staged[req.Name] = d = new();
        d[item.DataName] = text;
        StagedCount = _staged.Values.Sum(x => x.Count);
        InputStaged?.Invoke(item, text, true);
        StatusText = Loc.F("{0} staged. Press a button or 'Send inputs' (expert mode) to write.", item.DataName);
        return true;
    }

    [RelayCommand]
    private void ClearStaged()
    {
        _staged.Clear(); StagedCount = 0;
        if (Scene is not null) foreach (var it in Scene.Items) if (it.Staged) InputStaged?.Invoke(it, "", false);
    }

    [RelayCommand(CanExecute = nameof(HasStaged))]
    private async Task SendStagedAsync()
    {
        if (_engine is null || _session.Ecu is not { } ecu) return;
        if (!RequireExpert()) return;
        var reqs = _staged.Keys.ToList();
        if (!await _dialogs.ConfirmAsync(Loc.T("Write to ECU"), Loc.F("Send {0} to the ECU with the edited values?", string.Join(", ", reqs)), Loc.T("Send"), Loc.T("Cancel"), danger: true)) return;
        var cmds = reqs.Select(r => new SendCommand(r, 0)).ToList();
        await RunCommandsAsync(cmds, Loc.T("Inputs sent"));
    }

    /// <summary>Button press: expert mode and confirmation required unless every request is a read-only service.</summary>
    public async Task PressButtonAsync(ScreenButton b)
    {
        if (_engine is null || _session.Ecu is not { } ecu) { _toasts.Warning(Loc.T("Offline"), Loc.T("Connect an adapter first.")); return; }
        bool allSafe = b.Send.Count > 0 && b.Send.All(s => ecu.GetRequest(s.RequestName) is { } r && r.GetSentBytesTemplate() is { Length: > 0 } t && _state.IsSafeRequest(t[0]));
        if (!allSafe && !RequireExpert()) return;
        if (b.Send.Count == 0) { _toasts.Info(b.Text, Loc.T("This button has no command.")); return; }
        if (!allSafe || b.Messages.Count > 0)
        {
            var msg = (b.Messages.Count > 0 ? string.Join("\n", b.Messages) + "\n\n" : "")
                      + Loc.F("Requests: {0}", string.Join(", ", b.Send.Select(s => s.RequestName)));
            if (!await _dialogs.ConfirmAsync(b.Text, msg, Loc.T("Send"), Loc.T("Cancel"), danger: !allSafe)) return;
        }
        await RunCommandsAsync(b.Send, b.Text);
    }

    private bool RequireExpert()
    {
        if (IsExpert) return true;
        _toasts.Warning(Loc.T("Expert mode required"), Loc.T("Writing to the ECU is only possible in expert mode."));
        return false;
    }

    private async Task RunCommandsAsync(IReadOnlyList<SendCommand> cmds, string title)
    {
        var engine = _engine!;
        try
        {
            await SendCommandsAsync(engine, cmds, _staged, CancellationToken.None, expert: IsExpert);
            foreach (var c in cmds) _staged.Remove(c.RequestName);
            StagedCount = _staged.Values.Sum(x => x.Count);
            if (Scene is not null) foreach (var it in Scene.Items) if (it.Staged && !_staged.TryGetValue(it.RequestName, out _)) InputStaged?.Invoke(it, "", false);
            _toasts.Success(title, Loc.T("Sent."));
        }
        catch (Exception ex) { _toasts.Error(title, ex.Message); }
    }

    /// <summary>Sends commands in order honouring delays. Non-read-only requests are skipped unless expert. Returns the skipped count.</summary>
    private async Task<int> SendCommandsAsync(ScreenLiveEngine engine, IEnumerable<SendCommand> cmds,
        Dictionary<string, Dictionary<string, string>>? staged, CancellationToken ct, bool expert = false)
    {
        var ecu = _session.Ecu!;
        int skipped = 0;
        foreach (var c in cmds)
        {
            if (c.DelayMs > 0) await Task.Delay(c.DelayMs, ct);
            var req = ecu.GetRequest(c.RequestName);
            if (req is null) throw new InvalidOperationException($"Unknown request '{c.RequestName}'");
            var tpl = req.GetSentBytesTemplate();
            if (!expert && !_state.IsExpertMode && !(tpl.Length > 0 && _state.IsSafeRequest(tpl[0]))) { skipped++; continue; }
            IEnumerable<KeyValuePair<string, string>>? inputs = null;
            if (staged is not null && staged.TryGetValue(req.Name, out var d)) inputs = d;
            var resp = await engine.SendAsync(req, inputs, ct);
            if (resp.IsNegative && !resp.IsResponsePending)
                throw new InvalidOperationException($"{req.Name}: {resp.NegativeResponseText} (0x{resp.NegativeResponseCode:X2})");
        }
        return skipped;
    }

    public void Dispose()
    {
        _timer.Stop();
        _openCts?.Cancel();
        DisposeEngine();
    }
}
