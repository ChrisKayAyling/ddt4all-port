using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ddt4All.App.Localization;
using Ddt4All.App.Models;
using Ddt4All.App.Services;

namespace Ddt4All.App.ViewModels;

public sealed partial class DtcViewModel : PageViewModel
{
    private readonly IDiagnosticsService _diag;
    private readonly IConnectionService _connection;
    private readonly SessionState _session;
    private readonly INotificationService _toasts;
    private readonly IDialogService _dialogs;

    public DtcViewModel(IDiagnosticsService diag, IConnectionService connection, SessionState session, INotificationService toasts, IDialogService dialogs)
    {
        _diag = diag; _connection = connection; _session = session; _toasts = toasts; _dialogs = dialogs;
        connection.PropertyChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => { OnPropertyChanged(nameof(IsConnected)); ReadCommand.NotifyCanExecuteChanged(); });
        session.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CanClear));
    }

    public override PageId Id => PageId.Dtcs;
    public override string Title => Loc.T("DTCs");
    public ObservableCollection<DtcItem> Items { get; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEmpty)), NotifyPropertyChangedFor(nameof(ShowHint))] private bool _hasRead;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowHint))] private bool _isBusy;
    public bool ShowHint => !HasRead && !IsBusy;
    public bool IsConnected => _connection.State == ConnectionState.Connected;
    public bool IsEmpty => HasRead && Items.Count == 0;
    public bool CanClear => _session.IsExpertMode;
    public string Subtitle => _session.CurrentEcu ?? Loc.T("All ECUs");

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task ReadAsync()
    {
        IsBusy = true;
        try
        {
            var res = await _diag.ReadDtcsAsync(_session.CurrentEcu);
            Items.Clear();
            foreach (var d in res) Items.Add(d);
            HasRead = true;
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (Exception ex) { _toasts.Error(Loc.T("Read DTC"), ex.Message); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (!_session.IsExpertMode) { _toasts.Warning(Loc.T("Expert mode required"), Loc.T("Clearing DTCs writes to the ECU.")); return; }
        if (!await _dialogs.ConfirmAsync(Loc.T("Clear DTCs"), Loc.T("Erase all stored fault codes from the selected ECU?"), Loc.T("Clear DTCs"), danger: true)) return;
        IsBusy = true;
        try { await _diag.ClearDtcsAsync(_session.CurrentEcu); Items.Clear(); HasRead = true; OnPropertyChanged(nameof(IsEmpty)); _toasts.Success(Loc.T("DTCs cleared")); }
        catch (Exception ex) { _toasts.Error(Loc.T("Clear DTCs"), ex.Message); }
        finally { IsBusy = false; }
    }
}
