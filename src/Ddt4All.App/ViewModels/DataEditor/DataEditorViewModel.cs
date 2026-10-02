using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ddt4All.App.Localization;
using Ddt4All.App.Services;
using Ddt4All.Core.Ecu;
using Ddt4All.Core.Editing;
using Ddt4All.Core.Layout;

namespace Ddt4All.App.ViewModels;

public sealed partial class EditorRow : ObservableObject
{
    public EditorRow(string name, string subtitle, object model) { _name = name; _subtitle = subtitle; Model = model; }
    public object Model { get; }
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _subtitle;
    [ObservableProperty] private bool _flag;
    [ObservableProperty] private string _badge = "";
    public string Key => Name.ToLowerInvariant();
}

public sealed class IssueRow
{
    public IssueRow(EcuIssue i) { Issue = i; }
    public EcuIssue Issue { get; }
    public string Severity => Issue.Severity.ToString();
    public bool IsError => Issue.Severity == IssueSeverity.Error;
    public bool IsWarning => Issue.Severity == IssueSeverity.Warning;
    public bool IsInfo => Issue.Severity == IssueSeverity.Info;
    public string Target => Issue.Target;
    public string Kind => Issue.Kind;
    public string Message => Issue.Message;
}

/// <summary>Data Editor page: edit the ECU definition (requests, data, ECU properties, screens) with undo/redo, validation and saving.</summary>
public sealed partial class DataEditorViewModel : PageViewModel, IEditHost
{
    private sealed record Snapshot(EcuFile Ecu, string? LayoutJson, string? Request, string? Data);

    private readonly IEcuContext _ctx;
    private readonly INotificationService _toasts;
    private readonly IDialogService _dialogs;
    private readonly IFilePickerService _picker;
    private readonly ISettingsService? _settings;
    private readonly Stack<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();
    private readonly DispatcherTimer _validateTimer;
    private EcuFile? _sessionEcu;
    private string? _lastKey;
    private long _lastEditTicks;
    private string? _layoutJson;
    private EditorRow[] _allRequests = Array.Empty<EditorRow>();
    private EditorRow[] _allData = Array.Empty<EditorRow>();

    public DataEditorViewModel(IEcuContext ctx, INotificationService toasts, IDialogService dialogs, IFilePickerService picker, ISettingsService? settings = null)
    {
        _ctx = ctx; _toasts = toasts; _dialogs = dialogs; _picker = picker; _settings = settings;
        _validateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _validateTimer.Tick += (_, _) => { _validateTimer.Stop(); Revalidate(); };
        ctx.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IEcuContext.Ecu) or null) Dispatcher.UIThread.Post(SyncFromSession);
        };
        SyncFromSession();
    }

    public override PageId Id => PageId.DataEditor;
    public override string Title => Loc.T("Data Editor");
    public override void OnNavigatedTo() => SyncFromSession();

    /// <summary>Folder used by Save / Export (defaults to the user ecus folder). Settable for tests.</summary>
    public string? SaveDirectoryOverride { get; set; }
    public string SaveDirectory => SaveDirectoryOverride ?? (_settings?.Current.EcuDirectory is { Length: > 0 } d ? d : AppPaths.DefaultEcuDir);

    // ------------------------------------------------------------------ state
    public EcuFile? Working { get; private set; }
    public EcuLayout? Layout { get; private set; }
    EcuFile IEditHost.Ecu => Working ?? throw new InvalidOperationException("No ECU");
    public IReadOnlyList<string> DataNames { get; private set; } = Array.Empty<string>();
    public bool HasEcu => Working != null;
    public bool NoEcu => Working == null;

    [ObservableProperty] private string? _filePath;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TitleText))] private bool _isDirty;
    [ObservableProperty] private bool _sessionChanged;
    [ObservableProperty] private string _statusLine = "";
    public string TitleText => Working == null ? "" : Working.EcuName + (IsDirty ? " •" : "");

    // tabs
    [ObservableProperty] private int _tabIndex;
    public bool IsRequestsTab => TabIndex == 0;
    public bool IsDataTab => TabIndex == 1;
    public bool IsEcuTab => TabIndex == 2;
    public bool IsScreensTab => TabIndex == 3;
    public bool IsValidationTab => TabIndex == 4;
    partial void OnTabIndexChanged(int value)
    {
        foreach (var n in new[] { nameof(IsRequestsTab), nameof(IsDataTab), nameof(IsEcuTab), nameof(IsScreensTab), nameof(IsValidationTab) }) OnPropertyChanged(n);
        if (value == 0) SelectedRequest?.Refresh();
    }

    // ------------------------------------------------------------------ loading
    private void SyncFromSession()
    {
        var e = _ctx.Ecu;
        if (ReferenceEquals(e, _sessionEcu)) { SessionChanged = false; return; }
        if (e == null) { if (Working == null) RaiseHas(); return; }
        if (Working == null || !IsDirty) LoadWorking(e, _ctx.Layout, _ctx.SourcePath);
        else SessionChanged = true;
    }

    private void LoadWorking(EcuFile ecu, EcuLayout? layout, string? path, bool fromSession = true)
    {
        _validateTimer.Stop();
        Working = ecu.Clone();
        Layout = CloneLayout(layout);
        _layoutJson = null;
        if (fromSession) _sessionEcu = ecu;
        FilePath = path;
        _undo.Clear(); _redo.Clear(); _lastKey = null;
        IsDirty = false; SessionChanged = false;
        EcuProps = new EcuPropsVM(this);
        RebuildLists(null, null);
        BuildScreens();
        Revalidate();
        RaiseHas();
        OnPropertyChanged(nameof(EcuProps)); OnPropertyChanged(nameof(TitleText));
        RaiseUndo();
    }

    private void RaiseHas() { OnPropertyChanged(nameof(HasEcu)); OnPropertyChanged(nameof(NoEcu)); OnPropertyChanged(nameof(Working)); OnPropertyChanged(nameof(HasLayout)); OnPropertyChanged(nameof(NoLayout)); }

    private static EcuLayout? CloneLayout(EcuLayout? l)
    {
        if (l == null) return null;
        try { return EcuLayoutJson.LoadFromString(EcuLayoutJson.Dump(l)); } catch (Exception) { return l; }
    }

    [RelayCommand]
    private async Task ReloadFromSessionAsync()
    {
        if (_ctx.Ecu == null) return;
        if (IsDirty && !await _dialogs.ConfirmAsync(Loc.T("Discard changes?"), Loc.T("The editor has unsaved changes. Load the current session ECU and discard them?"), Loc.T("Discard"), danger: true)) return;
        LoadWorking(_ctx.Ecu, _ctx.Layout, _ctx.SourcePath);
    }

    [RelayCommand]
    private async Task NewEcuAsync()
    {
        if (IsDirty && !await _dialogs.ConfirmAsync(Loc.T("Discard changes?"), Loc.T("The editor has unsaved changes. Create a new ECU and discard them?"), Loc.T("Discard"), danger: true)) return;
        var e = EcuEditing.NewEcu(Loc.T("NEW_ECU"));
        LoadWorking(e, null, null, fromSession: false);
        IsDirty = true;
        TabIndex = 2;
        StatusLine = Loc.T("New ECU created. Set its name and protocol, then save it.");
    }

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        var path = await _picker.PickFileAsync(Loc.T("Open ECU definition"), (Loc.T("ECU definitions"), ["*.xml", "*.json"]));
        if (path == null) return;
        if (IsDirty && !await _dialogs.ConfirmAsync(Loc.T("Discard changes?"), Loc.T("The editor has unsaved changes. Open another file and discard them?"), Loc.T("Discard"), danger: true)) return;
        try
        {
            var (ecu, layout) = await Task.Run(() => EcuContext.LoadFile(path));
            LoadWorking(ecu, layout, path, fromSession: false);
        }
        catch (Exception ex) { _toasts.Error(Loc.T("Cannot open ECU file"), ex.Message); }
    }

    /// <summary>Loads an ECU into the editor directly (tests, deep links).</summary>
    public void Load(EcuFile ecu, EcuLayout? layout = null, string? path = null) => LoadWorking(ecu, layout, path, fromSession: false);

    // ------------------------------------------------------------------ lists
    public IReadOnlyList<EditorRow> RequestRows { get; private set; } = Array.Empty<EditorRow>();
    public IReadOnlyList<EditorRow> DataRows { get; private set; } = Array.Empty<EditorRow>();
    [ObservableProperty] private string _requestSearch = "";
    [ObservableProperty] private string _dataSearch = "";
    [ObservableProperty] private EditorRow? _selectedRequestRow;
    [ObservableProperty] private EditorRow? _selectedDataRow;
    [ObservableProperty] private RequestEditVM? _selectedRequest;
    [ObservableProperty] private DataEditVM? _selectedData;
    public EcuPropsVM? EcuProps { get; private set; }
    public string RequestCountText => Working == null ? "" : Loc.F("{0} requests", Working.Requests.Count);
    public string DataCountText => Working == null ? "" : Loc.F("{0} data definitions", Working.Data.Count);

    partial void OnRequestSearchChanged(string value) => FilterRows();
    partial void OnDataSearchChanged(string value) => FilterRows();
    partial void OnSelectedRequestRowChanged(EditorRow? value)
    {
        SelectedRequest = value?.Model is EcuRequest r ? new RequestEditVM(this, r) : null;
        OnPropertyChanged(nameof(HasSelectedRequest));
    }
    partial void OnSelectedDataRowChanged(EditorRow? value)
    {
        SelectedData = value?.Model is EcuData d ? new DataEditVM(this, d) : null;
        OnPropertyChanged(nameof(HasSelectedData));
    }
    public bool HasSelectedRequest => SelectedRequest != null;
    public bool HasSelectedData => SelectedData != null;

    private static string DataSubtitle(EcuData d)
    {
        var parts = new List<string> { d.BitsCount + " bit" };
        if (d.BytesAscii) parts.Add("ASCII"); else if (d.Scaled) parts.Add(d.Unit.Length > 0 ? "scaled · " + d.Unit : "scaled");
        if (d.HasList) parts.Add(d.Lists.Count + " values");
        if (d.Signed) parts.Add("signed");
        return string.Join(" · ", parts);
    }

    private static string RequestSubtitle(EcuRequest r) => (r.SentBytes.Length > 22 ? r.SentBytes[..22] + "…" : r.SentBytes) + "  ↑" + r.SendItems.Count + " ↓" + r.ReceiveItems.Count;

    private void RebuildLists(string? selectRequest, string? selectData)
    {
        var w = Working;
        string? keepReq = selectRequest ?? SelectedRequestRow?.Name, keepData = selectData ?? SelectedDataRow?.Name;
        if (w == null) { _allRequests = _allData = Array.Empty<EditorRow>(); DataNames = Array.Empty<string>(); }
        else
        {
            var usage = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var r in w.Requests)
            {
                foreach (var i in r.SendItems) usage[i.Name] = usage.GetValueOrDefault(i.Name) + 1;
                foreach (var i in r.ReceiveItems) usage[i.Name] = usage.GetValueOrDefault(i.Name) + 1;
            }
            _allRequests = w.Requests.Select(r => new EditorRow(r.Name, RequestSubtitle(r), r)).ToArray();
            _allData = w.Data.Select(d =>
            {
                int u = usage.GetValueOrDefault(d.Name);
                return new EditorRow(d.Name, DataSubtitle(d), d) { Flag = u == 0, Badge = u == 0 ? Loc.T("unused") : u.ToString(CultureInfo.InvariantCulture) };
            }).ToArray();
            DataNames = w.Data.Select(d => d.Name).ToArray();
        }

        FilterRows();
        OnPropertyChanged(nameof(DataNames)); OnPropertyChanged(nameof(RequestCountText)); OnPropertyChanged(nameof(DataCountText));
        SelectedRequestRow = RequestRows.FirstOrDefault(r => r.Name == keepReq) ?? _allRequests.FirstOrDefault(r => r.Name == keepReq) ?? RequestRows.FirstOrDefault();
        SelectedDataRow = DataRows.FirstOrDefault(r => r.Name == keepData) ?? _allData.FirstOrDefault(r => r.Name == keepData) ?? DataRows.FirstOrDefault();
    }

    private void FilterRows()
    {
        RequestRows = Filter(_allRequests, RequestSearch);
        DataRows = Filter(_allData, DataSearch);
        OnPropertyChanged(nameof(RequestRows)); OnPropertyChanged(nameof(DataRows));
    }

    private static EditorRow[] Filter(EditorRow[] all, string q)
    {
        q = q.Trim().ToLowerInvariant();
        if (q.Length == 0) return all;
        var parts = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return all.Where(r => parts.All(p => r.Key.Contains(p, StringComparison.Ordinal) || r.Subtitle.Contains(p, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    // ------------------------------------------------------------------ IEditHost
    public void BeginEdit(string key)
    {
        long now = Stopwatch.GetTimestamp();
        double ms = (now - _lastEditTicks) * 1000.0 / Stopwatch.Frequency;
        if (key == _lastKey && ms < 1500 && _undo.Count > 0) { _lastEditTicks = now; return; }
        _undo.Push(TakeSnapshot());
        if (_undo.Count > 100) { var keep = _undo.Take(100).Reverse().ToArray(); _undo.Clear(); foreach (var s in keep) _undo.Push(s); }
        _redo.Clear();
        _lastKey = key; _lastEditTicks = now;
        RaiseUndo();
    }

    public void Modified()
    {
        IsDirty = true;
        if (TabIndex == 3) _layoutJson = null;
        SelectedRequest?.Refresh();
        if (SelectedDataRow?.Model is EcuData d) SelectedDataRow.Subtitle = DataSubtitle(d);
        if (SelectedRequestRow?.Model is EcuRequest r) SelectedRequestRow.Subtitle = RequestSubtitle(r);
        OnPropertyChanged(nameof(TitleText));
        RaiseUndo();
        _validateTimer.Stop(); _validateTimer.Start();
    }

    public void Structural(string? selectRequest = null, string? selectData = null)
    {
        IsDirty = true;
        _lastKey = null;
        if (TabIndex == 3) _layoutJson = null;
        Working?.ResolveReferences();
        RebuildLists(selectRequest, selectData);
        BuildScreens();
        OnPropertyChanged(nameof(TitleText));
        RaiseUndo();
        _validateTimer.Stop(); _validateTimer.Start();
    }

    public void Warn(string message) => _toasts.Warning(Loc.T("Data Editor"), message);

    // ------------------------------------------------------------------ undo / redo
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string UndoCountText => _undo.Count.ToString(CultureInfo.InvariantCulture);
    private void RaiseUndo() { OnPropertyChanged(nameof(CanUndo)); OnPropertyChanged(nameof(CanRedo)); UndoCommand.NotifyCanExecuteChanged(); RedoCommand.NotifyCanExecuteChanged(); }

    private Snapshot TakeSnapshot() => new(Working!.Clone(), Layout == null ? null : (_layoutJson ??= SafeDump(Layout)), SelectedRequestRow?.Name, SelectedDataRow?.Name);
    private static string? SafeDump(EcuLayout l) { try { return EcuLayoutJson.Dump(l); } catch (Exception) { return null; } }

    private void Restore(Snapshot s)
    {
        Working = s.Ecu;
        Layout = s.LayoutJson == null ? Layout : EcuLayoutJson.LoadFromString(s.LayoutJson);
        _layoutJson = s.LayoutJson;
        _lastKey = null;
        EcuProps = new EcuPropsVM(this);
        OnPropertyChanged(nameof(EcuProps));
        RebuildLists(s.Request, s.Data);
        BuildScreens();
        IsDirty = true;
        OnPropertyChanged(nameof(TitleText));
        Revalidate();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_undo.Count == 0 || Working == null) return;
        _redo.Push(TakeSnapshot());
        Restore(_undo.Pop());
        RaiseUndo();
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (_redo.Count == 0 || Working == null) return;
        _undo.Push(TakeSnapshot());
        Restore(_redo.Pop());
        RaiseUndo();
    }

    // ------------------------------------------------------------------ add / duplicate / delete
    [RelayCommand]
    private void AddRequest()
    {
        if (Working == null) return;
        var name = EcuEditing.UniqueName(Working.Requests.Contains, Loc.T("NewRequest"));
        BeginEdit("add-request:" + Guid.NewGuid());
        Working.Requests.Add(new EcuRequest { Name = name, SentBytes = "22", MinBytes = 2 });
        Structural(selectRequest: name);
        TabIndex = 0;
    }

    [RelayCommand]
    private void DuplicateRequest()
    {
        if (Working == null || SelectedRequest == null) return;
        var src = SelectedRequest.Request;
        var copy = src.Clone();
        copy.Name = EcuEditing.UniqueName(Working.Requests.Contains, src.Name + "_copy");
        BeginEdit("dup-request:" + Guid.NewGuid());
        Working.Requests.Insert(Working.Requests.IndexOf(src.Name) + 1, copy);
        Structural(selectRequest: copy.Name);
    }

    [RelayCommand]
    private async Task DeleteRequestAsync()
    {
        if (Working == null || SelectedRequest == null) return;
        var name = SelectedRequest.Request.Name;
        if (!await _dialogs.ConfirmAsync(Loc.T("Delete request"), Loc.F("Delete the request '{0}'? You can undo this.", name), Loc.T("Delete"), danger: true)) return;
        BeginEdit("del-request:" + Guid.NewGuid());
        int idx = Working.Requests.IndexOf(name);
        Working.Requests.Remove(name);
        SelectedRequestRow = null;
        Structural(selectRequest: Working.Requests.Count == 0 ? null : Working.Requests[Math.Min(idx, Working.Requests.Count - 1)].Name);
    }

    [RelayCommand]
    private void AddData()
    {
        if (Working == null) return;
        var name = EcuEditing.UniqueName(Working.Data.Contains, Loc.T("NewData"));
        BeginEdit("add-data:" + Guid.NewGuid());
        Working.Data.Add(new EcuData { Name = name });
        Structural(selectData: name);
        TabIndex = 1;
    }

    [RelayCommand]
    private void DuplicateData()
    {
        if (Working == null || SelectedData == null) return;
        var src = SelectedData.Data;
        var copy = src.Clone();
        copy.Name = EcuEditing.UniqueName(Working.Data.Contains, src.Name + "_copy");
        BeginEdit("dup-data:" + Guid.NewGuid());
        Working.Data.Insert(Working.Data.IndexOf(src.Name) + 1, copy);
        Structural(selectData: copy.Name);
    }

    [RelayCommand]
    private async Task DeleteDataAsync()
    {
        if (Working == null || SelectedData == null) return;
        var name = SelectedData.Data.Name;
        var used = Working.DataUsage(name);
        var msg = used.Count == 0 ? Loc.F("Delete the data definition '{0}'?", name)
            : Loc.F("'{0}' is used by {1} request(s). Deleting it also removes it from them. Continue?", name, used.Count);
        if (!await _dialogs.ConfirmAsync(Loc.T("Delete data definition"), msg, Loc.T("Delete"), danger: true)) return;
        BeginEdit("del-data:" + Guid.NewGuid());
        int idx = Working.Data.IndexOf(name);
        Working.RemoveData(name);
        SelectedDataRow = null;
        Structural(selectData: Working.Data.Count == 0 ? null : Working.Data[Math.Min(idx, Working.Data.Count - 1)].Name);
    }

    // ------------------------------------------------------------------ screens
    public ObservableCollection<ScreenEditVM> Screens { get; } = new();
    [ObservableProperty] private ScreenEditVM? _selectedScreen;
    public bool HasLayout => Layout != null && Layout.Screens.Count > 0;
    public bool NoLayout => !HasLayout;

    private void BuildScreens()
    {
        var keep = SelectedScreen?.Screen.Name;
        Screens.Clear();
        if (Layout != null)
            foreach (var c in Layout.Categories)
                foreach (var n in c.ScreenNames)
                    if (Layout.Screens.TryGetValue(n, out var s)) Screens.Add(new ScreenEditVM(this, s, c.Name, DeleteScreen));
        SelectedScreen = Screens.FirstOrDefault(s => s.Screen.Name == keep) ?? Screens.FirstOrDefault();
        OnPropertyChanged(nameof(HasLayout)); OnPropertyChanged(nameof(NoLayout));
    }

    [RelayCommand]
    private void AddScreen()
    {
        if (Working == null) return;
        BeginEdit("add-screen:" + Guid.NewGuid());
        Layout ??= new EcuLayout();
        _layoutJson = null;
        var cat = Layout.Categories.FirstOrDefault();
        if (cat == null) { cat = new ScreenCategory { Name = Loc.T("Screens") }; Layout.Categories.Add(cat); }
        var name = EcuEditing.UniqueName(Layout.Screens.Contains, Loc.T("NewScreen"));
        Layout.Screens.Add(new Screen { Name = name, Width = 800, Height = 480, Color = new LayoutColor(0xE0, 0xE0, 0xE0) });
        cat.ScreenNames.Add(name);
        Structural();
        SelectedScreen = Screens.FirstOrDefault(s => s.Screen.Name == name);
        TabIndex = 3;
    }

    private void DeleteScreen(ScreenEditVM vm)
    {
        if (Layout == null) return;
        BeginEdit("del-screen:" + Guid.NewGuid());
        _layoutJson = null;
        foreach (var c in Layout.Categories) c.ScreenNames.RemoveAll(n => n == vm.Screen.Name);
        Layout.Screens.Remove(vm.Screen.Name);
        Structural();
    }

    // ------------------------------------------------------------------ validation
    public ObservableCollection<IssueRow> Issues { get; } = new();
    [ObservableProperty] private string _issueSummary = "";
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private int _warningCount;
    [ObservableProperty] private IssueRow? _selectedIssue;
    public string ValidationTabText => ErrorCount + WarningCount == 0 ? Loc.T("Validation") : Loc.F("Validation ({0})", ErrorCount + WarningCount);

    /// <summary>Runs all checks now (also scheduled automatically 400 ms after the last edit).</summary>
    public void Revalidate()
    {
        _validateTimer.Stop();
        Issues.Clear();
        if (Working == null) { IssueSummary = ""; ErrorCount = WarningCount = 0; return; }
        var list = EcuValidator.Validate(Working);
        if (Layout != null)
            foreach (var s in Layout.Screens)
            {
                var vm = new ScreenEditVM(this, s, "", _ => { });
                foreach (var m in vm.MissingRequests) list.Add(new EcuIssue(IssueSeverity.Warning, "screen", s.Name, Loc.F("Screen refers to the missing request '{0}'", m)));
            }

        foreach (var i in list.OrderByDescending(i => i.Severity)) Issues.Add(new IssueRow(i));
        ErrorCount = list.Count(i => i.Severity == IssueSeverity.Error);
        WarningCount = list.Count(i => i.Severity == IssueSeverity.Warning);
        int info = list.Count - ErrorCount - WarningCount;
        IssueSummary = list.Count == 0 ? Loc.T("No problems found.") : Loc.F("{0} errors, {1} warnings, {2} notes", ErrorCount, WarningCount, info);
        OnPropertyChanged(nameof(ValidationTabText)); OnPropertyChanged(nameof(HasIssues)); OnPropertyChanged(nameof(NoIssues));
    }

    public bool HasIssues => Issues.Count > 0;
    public bool NoIssues => Issues.Count == 0;

    partial void OnSelectedIssueChanged(IssueRow? value)
    {
        if (value == null) return;
        switch (value.Kind)
        {
            case "request": TabIndex = 0; SelectedRequestRow = _allRequests.FirstOrDefault(r => r.Name == value.Target) ?? SelectedRequestRow; if (RequestRows.All(r => r.Name != value.Target)) RequestSearch = ""; break;
            case "data": TabIndex = 1; if (DataRows.All(r => r.Name != value.Target)) DataSearch = ""; SelectedDataRow = _allData.FirstOrDefault(r => r.Name == value.Target) ?? SelectedDataRow; break;
            case "ecu": TabIndex = 2; break;
            case "screen": TabIndex = 3; SelectedScreen = Screens.FirstOrDefault(s => s.Screen.Name == value.Target) ?? SelectedScreen; break;
        }
    }

    // ------------------------------------------------------------------ saving
    private static string SafeFileName(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in name.Trim()) sb.Append(Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c);
        return sb.Length == 0 ? "ecu" : sb.ToString();
    }

    private static void WriteAtomic(string path, Action<string> write)
    {
        var tmp = path + ".tmp";
        write(tmp);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Saves the JSON definition (and the screens as .layout) into <paramref name="dir"/>. Returns the JSON path.</summary>
    public string SaveJson(string dir)
    {
        var w = Working ?? throw new InvalidOperationException("No ECU");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, SafeFileName(w.EcuName) + ".json");
        WriteAtomic(path, t => File.WriteAllText(t, w.DumpJson(), new System.Text.UTF8Encoding(false)));
        var verify = EcuFile.Load(path);   // fail loudly if what we wrote cannot be read back
        if (verify.Requests.Count != w.Requests.Count || verify.Data.Count != w.Data.Count) throw new IOException("Saved file does not match the edited ECU.");
        if (Layout != null && Layout.Screens.Count > 0)
            WriteAtomic(Path.Combine(dir, SafeFileName(w.EcuName) + ".layout"), t => File.WriteAllText(t, EcuLayoutJson.Dump(Layout), new System.Text.UTF8Encoding(false)));
        FilePath = path;
        IsDirty = false;
        OnPropertyChanged(nameof(TitleText));
        StatusLine = Loc.F("Saved {0}", path);
        return path;
    }

    /// <summary>Exports DDT-style XML into <paramref name="dir"/>. Returns the path.</summary>
    public string ExportXml(string dir)
    {
        var w = Working ?? throw new InvalidOperationException("No ECU");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, SafeFileName(w.EcuName) + ".xml");
        WriteAtomic(path, t => EcuXmlWriter.Save(w, t));
        EcuFile.Load(path);
        StatusLine = Loc.F("Exported {0}", path);
        return path;
    }

    [RelayCommand]
    private void Save()
    {
        if (Working == null) return;
        try
        {
            Revalidate();
            var dir = SaveDirectory;
            // keep saving next to the original when it is a loose JSON file
            if (FilePath != null && string.Equals(Path.GetExtension(FilePath), ".json", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.GetDirectoryName(FilePath))) dir = Path.GetDirectoryName(FilePath)!;
            var p = SaveJson(dir);
            if (ErrorCount > 0) _toasts.Warning(Loc.T("Saved with problems"), Loc.F("{0} validation errors remain. {1}", ErrorCount, p));
            else _toasts.Success(Loc.T("ECU saved"), p);
        }
        catch (Exception ex) { _toasts.Error(Loc.T("Save failed"), ex.Message); }
    }

    [RelayCommand]
    private void ExportXmlFile()
    {
        if (Working == null) return;
        try { _toasts.Success(Loc.T("XML exported"), ExportXml(SaveDirectory)); }
        catch (Exception ex) { _toasts.Error(Loc.T("Export failed"), ex.Message); }
    }

    [RelayCommand]
    private async Task SaveToFolderAsync()
    {
        if (Working == null) return;
        var dir = await _picker.PickFolderAsync(Loc.T("Choose a folder"));
        if (dir == null) return;
        try { _toasts.Success(Loc.T("ECU saved"), SaveJson(dir)); }
        catch (Exception ex) { _toasts.Error(Loc.T("Save failed"), ex.Message); }
    }

    [RelayCommand]
    private void UseInSession()
    {
        if (Working == null) return;
        var copy = Working.Clone();
        _sessionEcu = copy;
        _ctx.SetEcu(copy, CloneLayout(Layout), FilePath);
        SessionChanged = false;
        _toasts.Success(Loc.T("Session updated"), Loc.T("The Requests and Screens pages now use the edited definition."));
    }

    [RelayCommand] private void RevalidateNow() => Revalidate();
}
