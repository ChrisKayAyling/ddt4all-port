using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ddt4All.App.Localization;
using Ddt4All.App.Models;
using Ddt4All.App.Services;
using Microsoft.Extensions.Logging;

namespace Ddt4All.App.ViewModels;

public sealed partial class EcuBrowserViewModel : PageViewModel
{
    private const string AllKey = "";
    private readonly IEcuCatalogService _catalog;
    private readonly INavigationService _nav;
    private readonly INotificationService _toasts;
    private readonly SessionState _session;
    private readonly ISettingsService _settings;
    private readonly ILogger<EcuBrowserViewModel>? _log;
    private EcuCatalog _data = EcuCatalog.Empty;
    private CancellationTokenSource? _filterCts;
    private bool _loaded;

    public EcuBrowserViewModel(IEcuCatalogService catalog, INavigationService nav, INotificationService toasts,
        SessionState session, ISettingsService settings, ILogger<EcuBrowserViewModel>? log = null)
    {
        _catalog = catalog; _nav = nav; _toasts = toasts; _session = session; _settings = settings; _log = log;
        GroupModes = [new("none", "No grouping"), new("project", "By project"), new("protocol", "By protocol")];
        _selectedGroupMode = GroupModes[1];
        ProjectFilters = [new ChoiceItem(AllKey, "All projects")];
        ProtocolFilters = [new ChoiceItem(AllKey, "All protocols")];
        _selectedProject = ProjectFilters[0];
        _selectedProtocol = ProtocolFilters[0];
    }

    public override PageId Id => PageId.EcuBrowser;
    public override string Title => Loc.T("ECU Browser");

    public IReadOnlyList<ChoiceItem> GroupModes { get; }
    [ObservableProperty] private IReadOnlyList<ChoiceItem> _projectFilters;
    [ObservableProperty] private IReadOnlyList<ChoiceItem> _protocolFilters;

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private ChoiceItem? _selectedGroupMode;
    [ObservableProperty] private ChoiceItem? _selectedProject;
    [ObservableProperty] private ChoiceItem? _selectedProtocol;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _resultSummary = "";
    [ObservableProperty] private IReadOnlyList<object> _rows = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection)), NotifyPropertyChangedFor(nameof(NoSelection))] private EcuEntry? _selectedEntry;
    [ObservableProperty] private object? _selectedRow;

    public bool HasSelection => SelectedEntry is not null;
    public bool NoSelection => SelectedEntry is null;
    public bool DatabaseMissing => !_catalog.IsAvailable;
    public string? DatabasePath => _catalog.DatabasePath;
    public bool IsEmpty => !IsLoading && Rows.Count == 0;

    partial void OnSelectedRowChanged(object? value) => SelectedEntry = value as EcuEntry;
    partial void OnSearchTextChanged(string value) => ScheduleFilter(120);
    partial void OnSelectedGroupModeChanged(ChoiceItem? value) => ScheduleFilter(0);
    partial void OnSelectedProjectChanged(ChoiceItem? value) => ScheduleFilter(0);
    partial void OnSelectedProtocolChanged(ChoiceItem? value) => ScheduleFilter(0);
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnRowsChanged(IReadOnlyList<object> value) => OnPropertyChanged(nameof(IsEmpty));

    public override void OnNavigatedTo()
    {
        if (!_loaded) { _loaded = true; _ = LoadAsync(); }
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _data = await _catalog.LoadAsync();
            ProjectFilters = [new ChoiceItem(AllKey, "All projects"), .. _data.Projects.Select(p => new ChoiceItem(p, p))];
            ProtocolFilters = [new ChoiceItem(AllKey, "All protocols"), .. _data.Protocols.Select(p => new ChoiceItem(p, p))];
            SelectedProject = ProjectFilters[0];
            SelectedProtocol = ProtocolFilters[0];
            _log?.LogInformation("ECU catalog loaded: {Count} entries in {Ms} ms", _data.Entries.Count, sw.ElapsedMilliseconds);
            await ApplyFilterAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Failed to load ECU catalog");
            _toasts.Error(Loc.T("No database found"), ex.Message);
        }
        finally { IsLoading = false; }
    }

    private async void ScheduleFilter(int delayMs)
    {
        if (!_loaded) return;
        _filterCts?.Cancel();
        var cts = _filterCts = new CancellationTokenSource();
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs, cts.Token);
            await ApplyFilterAsync(cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log?.LogError(ex, "ECU filter failed"); }
    }

    private async Task ApplyFilterAsync(CancellationToken ct)
    {
        var data = _data;
        var text = SearchText;
        var project = SelectedProject?.Id ?? AllKey;
        var protocol = SelectedProtocol?.Id ?? AllKey;
        var mode = SelectedGroupMode?.Id ?? "none";
        var (rows, count) = await Task.Run(() => Filter(data, text, project, protocol, mode, ct), ct);
        ct.ThrowIfCancellationRequested();
        Rows = rows;
        ResultSummary = count == data.Entries.Count
            ? Loc.F("{0:N0} ECUs", count)
            : Loc.F("{0:N0} of {1:N0} ECUs", count, data.Entries.Count);
    }

    public static (IReadOnlyList<object> Rows, int Count) FilterForTests(EcuCatalog d, string t, string p, string pr, string m) => Filter(d, t, p, pr, m, CancellationToken.None);

    internal static (IReadOnlyList<object> Rows, int Count) Filter(EcuCatalog data, string text, string project, string protocol, string mode, CancellationToken ct)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant()).ToArray();
        var matches = new List<EcuEntry>(Math.Min(data.Entries.Count, 1024));
        foreach (var e in data.Entries)
        {
            if (project.Length > 0 && e.Project != project) continue;
            if (protocol.Length > 0 && e.Protocol != protocol) continue;
            var ok = true;
            foreach (var t in tokens)
                if (!e.SearchKey.Contains(t, StringComparison.Ordinal)) { ok = false; break; }
            if (ok) matches.Add(e);
        }
        ct.ThrowIfCancellationRequested();

        if (mode == "none") return (matches.ToArray(), matches.Count);

        Func<EcuEntry, string> key = mode == "protocol" ? e => e.Protocol : e => $"{e.Project} {e.ProjectName}";
        var rows = new List<object>(matches.Count + 64);
        foreach (var g in matches.GroupBy(key).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            rows.Add(new GroupHeaderRow(g.Key, g.Count()));
            rows.AddRange(g.OrderBy(e => e.Name, StringComparer.Ordinal));
        }
        return (rows, matches.Count);
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = "";
        SelectedProject = ProjectFilters[0];
        SelectedProtocol = ProtocolFilters[0];
    }

    [RelayCommand]
    private void OpenEcu(EcuEntry? entry)
    {
        entry ??= SelectedEntry;
        if (entry is null) return;
        _session.CurrentEcu = $"{entry.Name} ({entry.Address})";
        _settings.Update(s => s.LastOpenedEcu = entry.Id);
        _toasts.Info(Loc.F("Opened {0}", entry.Name), Loc.T("Screens will appear here once the ECU engine is connected."));
        _nav.NavigateTo(PageId.Screens);
    }

    [RelayCommand] private void OpenSettings() => _nav.NavigateTo(PageId.Settings);
}
