using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ddt4All.App.Localization;
using Ddt4All.App.Models;
using Ddt4All.App.Services;
using Microsoft.Extensions.Logging;

namespace Ddt4All.App.ViewModels;

/// <summary>"Dashboard / Connect" page: adapter selection, connect/disconnect, quick actions.</summary>
public sealed partial class DashboardViewModel : PageViewModel
{
    private readonly IConnectionService _connection;
    private readonly ISettingsService _settings;
    private readonly INotificationService _toasts;
    private readonly INavigationService _nav;
    private readonly ILogger<DashboardViewModel>? _log;
    private CancellationTokenSource? _cts;
    private bool _loading = true;

    public DashboardViewModel(IConnectionService connection, ISettingsService settings, INotificationService toasts,
        INavigationService nav, ILogger<DashboardViewModel>? log = null, ScanViewModel? scan = null)
    {
        Scan = scan;
        _connection = connection; _settings = settings; _toasts = toasts; _nav = nav; _log = log;
        var c = settings.Current.Connection;
        _transportIndex = c.Transport == "tcp" ? 1 : 0;
        _selectedProfile = DeviceProfile.ById(c.ProfileId);
        _baud = c.Baud;
        _host = c.Host; _tcpPort = c.TcpPort; _simulation = c.Simulation;
        _hardwareFlowControl = settings.Current.Serial.HardwareFlowControl;
        if (c.Port is { } p) { Ports.Add(new SerialPortInfo(p, "")); _selectedPort = Ports[0]; }
        connection.PropertyChanged += (_, e) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                OnPropertyChanged(nameof(IsConnected)); OnPropertyChanged(nameof(IsDisconnected));
                OnPropertyChanged(nameof(IsConnecting)); OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(Adapter)); OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(ConnectButtonText));
                ConnectCommand.NotifyCanExecuteChanged();
            });
        };
        _loading = false;
    }

    /// <summary>ECU auto-scan section (null in tests that do not need it).</summary>
    public ScanViewModel? Scan { get; }

    public override PageId Id => PageId.Dashboard;
    public override string Title => Loc.T("Connect");

    public IReadOnlyList<DeviceProfile> Profiles { get; } = DeviceProfile.All;
    public IReadOnlyList<int> BaudRates { get; } = [9600, 19200, 38400, 57600, 115200, 230400, 500000, 1000000];
    public ObservableCollection<SerialPortInfo> Ports { get; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsSerial)), NotifyPropertyChangedFor(nameof(IsTcp))] private int _transportIndex;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ProfileHint))] private DeviceProfile _selectedProfile;
    [ObservableProperty] private SerialPortInfo? _selectedPort;
    [ObservableProperty] private int _baud;
    [ObservableProperty] private string _host;
    [ObservableProperty] private int _tcpPort;
    [ObservableProperty] private bool _simulation;
    [ObservableProperty] private bool _hardwareFlowControl;
    [ObservableProperty] private bool _isScanningPorts;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEdit))] private bool _isBusy;

    public bool IsSerial => TransportIndex == 0;
    public bool IsTcp => TransportIndex == 1;
    public string? ProfileHint => SelectedProfile.Hint;

    public bool IsConnected => _connection.State == ConnectionState.Connected;
    public bool IsConnecting => _connection.State == ConnectionState.Connecting;
    public bool IsDisconnected => !IsConnected && !IsConnecting;
    public bool CanEdit => IsDisconnected && !IsBusy;
    public AdapterInfo? Adapter => _connection.Adapter;
    public string StateText => _connection.State == ConnectionState.Failed ? _connection.StatusText : _connection.State switch
    {
        ConnectionState.Connected => Loc.T("Connected"),
        ConnectionState.Connecting => Loc.T("Connecting..."),
        _ => Loc.T("Not connected"),
    };
    public string ConnectButtonText => IsConnected ? Loc.T("Disconnect") : IsConnecting ? Loc.T("Cancel") : Loc.T("Connect");

    partial void OnSelectedProfileChanged(DeviceProfile value)
    {
        if (_loading) return;
        if (value.DefaultBaud > 0) Baud = value.DefaultBaud;
        HardwareFlowControl = value.RtsCts;
        if (!value.SupportsSerial) TransportIndex = 1;
        else if (!value.SupportsTcp) TransportIndex = 0;
        if (value.Id == "doip") { Host = "192.168.0.12"; TcpPort = 13400; }
    }

    public override void OnNavigatedTo()
    {
        if (Ports.Count <= 1 && !IsScanningPorts) _ = RefreshPortsAsync();
        if (Scan is not null) _ = Scan.RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshPortsAsync()
    {
        IsScanningPorts = true;
        try
        {
            var keep = SelectedPort?.Name;
            var found = await _connection.ListPortsAsync();
            Ports.Clear();
            foreach (var p in found) Ports.Add(p);
            SelectedPort = Ports.FirstOrDefault(p => p.Name == keep) ?? Ports.FirstOrDefault();
            _log?.LogDebug("Found {Count} serial ports", Ports.Count);
        }
        catch (Exception ex) { _toasts.Error(Loc.T("Could not list ports"), ex.Message); }
        finally { IsScanningPorts = false; }
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsConnecting) { _cts?.Cancel(); return; }
        if (IsConnected)
        {
            await _connection.DisconnectAsync();
            _toasts.Info(Loc.T("Disconnected"));
            return;
        }
        SaveSettings();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            var req = new ConnectionRequest(IsTcp ? TransportKind.Tcp : TransportKind.Serial, SelectedPort?.Name, Baud, Host, TcpPort,
                SelectedProfile, Simulation, HardwareFlowControl);
            await _connection.ConnectAsync(req, _cts.Token);
            _toasts.Success(Loc.T("Connection established successfully"), _connection.Adapter?.Name);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _toasts.Error(Loc.T("Connection failed"), ex.Message); }
        finally { IsBusy = false; }
    }

    private void SaveSettings() => _settings.Update(s =>
    {
        s.Connection.Transport = IsTcp ? "tcp" : "serial";
        s.Connection.ProfileId = SelectedProfile.Id;
        s.Connection.Port = SelectedPort?.Name;
        s.Connection.Baud = Baud; s.Connection.Host = Host; s.Connection.TcpPort = TcpPort;
        s.Connection.Simulation = Simulation;
        s.Serial.HardwareFlowControl = HardwareFlowControl;
    });

    [RelayCommand] private void OpenBrowser() => _nav.NavigateTo(PageId.EcuBrowser);
    [RelayCommand] private void OpenTerminal() => _nav.NavigateTo(PageId.Terminal);
    [RelayCommand] private void OpenDtcs() => _nav.NavigateTo(PageId.Dtcs);
}
