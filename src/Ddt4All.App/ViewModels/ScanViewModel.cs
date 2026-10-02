using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ddt4All.App.Localization;
using Ddt4All.App.Models;
using Ddt4All.App.Services;
using Ddt4All.Comms.Scanning;

namespace Ddt4All.App.ViewModels;

/// <summary>One line of the scan result list.</summary>
public sealed class ScanRow
{
    public ScanRow(FoundEcu found, Func<ScanRow, Task> open) { Found = found; OpenCommand = new AsyncRelayCommand(() => open(this)); }
    public IAsyncRelayCommand OpenCommand { get; }
    public FoundEcu Found { get; }
    public string Address => Found.FunctionalAddress;
    public string AddressName => Found.AddressName;
    public string Protocol => Found.Protocol;
    public string EcuName => Found.HasDefinition ? (Found.Group is { Length: > 0 } g ? $"[{g}] {Found.EcuName}" : Found.EcuName ?? "") : Loc.T("No matching ECU file");
    public string MatchText => Found.Match switch
    {
        FoundEcuMatch.Exact => Loc.T("Identified"),
        FoundEcuMatch.Approximate => Loc.T("Closest version (not a perfect match)"),
        _ => Loc.T("Not in database"),
    };
    public bool IsExact => Found.Match == FoundEcuMatch.Exact;
    public bool IsApproximate => Found.Match == FoundEcuMatch.Approximate;
    public bool IsUnknown => Found.Match == FoundEcuMatch.None;
    public bool CanOpen => Found.HasDefinition;
    public string Ident => $"diag {Found.DiagVersion} · {Found.Supplier} · {Found.Soft} · {Found.Version}";
}

/// <summary>"Scan" section of the Connect page: probes every ECU address through the connected adapter and lets the user open what was found.</summary>
public sealed partial class ScanViewModel : ViewModelBase
{
    private static string VehicleTitle(string code) =>
        Ddt4All.App.Services.VehicleCatalog.Resolve(code) is { } v ? $"{v.Name}  ({code})" : code;

    private const string AllKey = "";
    private readonly IScanService _scan;
    private readonly IEcuCatalogService _catalog;
    private readonly IConnectionService _connection;
    private readonly IEcuSession _session;
    private readonly IFilePickerService _picker;
    private readonly INotificationService _toasts;
    private readonly INavigationService _nav;
    private CancellationTokenSource? _cts;

    public ScanViewModel(IScanService scan, IEcuCatalogService catalog, IConnectionService connection, IEcuSession session,
        IFilePickerService picker, INotificationService toasts, INavigationService nav)
    {
        _scan = scan; _catalog = catalog; _connection = connection; _session = session; _picker = picker; _toasts = toasts; _nav = nav;
        ProjectFilters = [new ChoiceItem(AllKey, "All projects")];
        _selectedProject = ProjectFilters[0];
        connection.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(RaiseState);
        catalog.Changed += () => Dispatcher.UIThread.Post(RaiseState);
    }

    public ObservableCollection<ScanRow> Results { get; } = new();
    [ObservableProperty] private IReadOnlyList<ChoiceItem> _projectFilters;
    [ObservableProperty] private ChoiceItem? _selectedProject;
    [ObservableProperty] private bool _includeKLine;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanStart)), NotifyPropertyChangedFor(nameof(ShowHint))] private bool _isScanning;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NoResults))] private bool _hasScanned;

    public bool IsConnected => _connection.State == ConnectionState.Connected;
    public bool DatabaseMissing => !_catalog.IsAvailable;
    public string? DatabaseError => _catalog.Error;
    public bool HasResults => Results.Count > 0;
    public bool NoResults => HasScanned && !IsScanning && Results.Count == 0;
    public bool CanStart => IsConnected && !DatabaseMissing && !IsScanning;
    public bool ShowHint => !IsScanning && !HasScanned;
    public string Hint => DatabaseMissing ? Loc.T("The ECU database (ecu.zip) was not found. Locate it to enable scanning.")
        : !IsConnected ? Loc.T("Connect to an adapter first, then scan the vehicle for ECUs.")
        : Loc.T("Probes every ECU address of the database and identifies what answers. With a vehicle project selected it is much faster.");

    private void RaiseState()
    {
        OnPropertyChanged(nameof(IsConnected)); OnPropertyChanged(nameof(DatabaseMissing)); OnPropertyChanged(nameof(DatabaseError));
        OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(Hint));
        StartScanCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Loads the database (cached index) so the project filter is populated.</summary>
    public async Task RefreshAsync()
    {
        try { await _catalog.LoadAsync(); } catch (Exception) { /* surfaced through DatabaseError */ }
        var db = _catalog.Database;
        if (db is not null && ProjectFilters.Count <= 1)
        {
            ProjectFilters = [new ChoiceItem(AllKey, "All projects"), .. db.Projects.Where(p => p.Length > 0).Select(p => new ChoiceItem(p, VehicleTitle(p))).OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase)];
            SelectedProject = ProjectFilters[0];
        }
        RaiseState();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartScanAsync()
    {
        Results.Clear();
        OnPropertyChanged(nameof(HasResults));
        HasScanned = true; IsScanning = true; Progress = 0;
        StatusText = Loc.T("Starting scan...");
        _cts = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(p =>
        {
            Progress = p.Total == 0 ? 0 : 100.0 * p.Completed / p.Total;
            StatusText = p.Completed >= p.Total && p.Total > 0
                ? Loc.F("Scan finished: {0} ECU(s) found", p.Found)
                : Loc.F("Scanning {0} ({1}/{2}) - {3} found", p.CurrentName, p.Completed, p.Total, p.Found);
        });
        try
        {
            var req = new ScanRequest { Project = SelectedProject?.Id is { Length: > 0 } pr ? pr : null, IncludeKLine = IncludeKLine };
            var found = await Task.Run(() => _scan.ScanAsync(req, progress, f => Dispatcher.UIThread.Post(() => { Results.Add(new ScanRow(f, OpenAsync)); OnPropertyChanged(nameof(HasResults)); }), _cts.Token));
            StatusText = Loc.F("Scan finished: {0} ECU(s) found", found.Count);
            Progress = 100;
        }
        catch (OperationCanceledException) { StatusText = Loc.T("Scan cancelled."); }
        catch (Exception ex) { StatusText = ex.Message; _toasts.Error(Loc.T("Scan failed"), ex.Message); }
        finally
        {
            IsScanning = false;
            OnPropertyChanged(nameof(NoResults)); OnPropertyChanged(nameof(HasResults));
            _cts?.Dispose(); _cts = null;
            CancelScanCommand.NotifyCanExecuteChanged();
            StartScanCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnIsScanningChanged(bool value) { CancelScanCommand.NotifyCanExecuteChanged(); StartScanCommand.NotifyCanExecuteChanged(); }

    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void CancelScan() => _cts?.Cancel();

    private async Task OpenAsync(ScanRow? row)
    {
        if (row is null) return;
        if (!row.CanOpen) { _toasts.Warning(Loc.T("No matching ECU file"), row.Ident); return; }
        try
        {
            await _session.OpenAsync(row.Found.Href!, row.Found.BusAddress);
            _toasts.Success(Loc.F("Opened {0}", row.Found.EcuName ?? ""), row.Found.Match == FoundEcuMatch.Approximate ? Loc.T("Closest version (not a perfect match)") : null);
            _nav.NavigateTo(PageId.Screens);
        }
        catch (Exception ex) { _toasts.Error(Loc.T("Could not open ECU"), ex.Message); }
    }

    [RelayCommand]
    private async Task LocateDatabaseAsync()
    {
        var path = await _picker.PickFileAsync(Loc.T("Locate ecu.zip"), (Loc.T("ECU database"), ["*.zip"]));
        if (path is null) return;
        if (await _catalog.SetDatabasePathAsync(path)) { ProjectFilters = [new ChoiceItem(AllKey, "All projects")]; await RefreshAsync(); }
        else _toasts.Error(Loc.T("No database found"), _catalog.Error);
        RaiseState();
    }
}
