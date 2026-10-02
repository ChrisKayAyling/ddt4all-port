using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ddt4All.App.Localization;
using Ddt4All.App.Models;
using Ddt4All.App.Services;

namespace Ddt4All.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase, INavigationService
{
    private readonly Func<PageId, PageViewModel> _pageFactory;
    private readonly Dictionary<PageId, PageViewModel> _pages = new();
    private readonly IDialogService _dialogs;
    private readonly ISettingsService _settings;
    private readonly INotificationService _notifications;

    public MainWindowViewModel(Func<PageId, PageViewModel> pageFactory, IConnectionService connection, SessionState session,
        INotificationService notifications, IDialogService dialogs, ISettingsService settings)
    {
        _pageFactory = pageFactory; _dialogs = dialogs; _settings = settings; _notifications = notifications;
        Connection = connection; Session = session; Notifications = notifications;
        _isNavExpanded = settings.Current.NavExpanded;

        MainItems =
        [
            new(PageId.Dashboard, "Connect", Icons.Plug, NavigateTo),
            new(PageId.EcuBrowser, "ECU Browser", Icons.Ecu, NavigateTo),
            new(PageId.Screens, "Screens", Icons.Screens, NavigateTo),
            new(PageId.Requests, "Requests", Icons.List, NavigateTo),
            new(PageId.Dtcs, "DTCs", Icons.Warning, NavigateTo),
            new(PageId.Terminal, "Terminal", Icons.Terminal, NavigateTo),
            new(PageId.Sniffer, "CAN Sniffer", Icons.Pulse, NavigateTo),
            new(PageId.DataEditor, "Data Editor", Icons.Edit, NavigateTo),
            new(PageId.Plugins, "Plugins", Icons.Plugin, NavigateTo),
        ];
        FooterItems =
        [
            new(PageId.Logs, "Logs", Icons.Article, NavigateTo),
            new(PageId.Settings, "Settings", Icons.Settings, NavigateTo),
            new(PageId.About, "About", Icons.Info, NavigateTo),
        ];

        connection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IConnectionService.State) or nameof(IConnectionService.StatusText) or nameof(IConnectionService.Adapter))
                Avalonia.Threading.Dispatcher.UIThread.Post(RaiseConnectionChanged);
        };
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionState.CurrentEcu)) OnPropertyChanged(nameof(EcuText));
            if (e.PropertyName == nameof(SessionState.IsExpertMode)) OnPropertyChanged(nameof(ExpertText));
        };

        NavigateTo(PageId.Dashboard);
    }

    public ObservableCollection<NavItem> MainItems { get; }
    public ObservableCollection<NavItem> FooterItems { get; }
    public IConnectionService Connection { get; }
    public SessionState Session { get; }
    public INotificationService Notifications { get; }

    [ObservableProperty] private PageViewModel? _currentPage;
    [ObservableProperty] private bool _isNavExpanded;

    public string AppTitle => "DDT4All";
    public string WindowTitle => CurrentPage is null ? "DDT4All" : $"{CurrentPage.Title} - DDT4All";

    public bool IsConnected => Connection.State == ConnectionState.Connected;
    public bool IsConnecting => Connection.State == ConnectionState.Connecting;
    public bool IsFailed => Connection.State == ConnectionState.Failed;
    public bool IsDisconnected => Connection.State == ConnectionState.Disconnected;

    public string ConnectionText => Connection.State switch
    {
        ConnectionState.Connected => Connection.Adapter is { } a ? $"{Loc.T("Connected")} · {a.Name} · {a.Endpoint}" : Loc.T("Connected"),
        ConnectionState.Connecting => Loc.T("Connecting..."),
        ConnectionState.Failed => Loc.T("Connection failed"),
        _ => Loc.T("Disconnected"),
    };

    public string EcuText => Session.CurrentEcu is { } e ? $"ECU: {e}" : Loc.T("No ECU selected");
    public string ExpertText => Session.IsExpertMode ? Loc.T("EXPERT") : Loc.T("Safe mode");

    private void RaiseConnectionChanged()
    {
        OnPropertyChanged(nameof(IsConnected)); OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(IsFailed)); OnPropertyChanged(nameof(IsDisconnected));
        OnPropertyChanged(nameof(ConnectionText));
    }

    partial void OnCurrentPageChanged(PageViewModel? value) => OnPropertyChanged(nameof(WindowTitle));
    partial void OnIsNavExpandedChanged(bool value) => _settings.Update(s => s.NavExpanded = value);

    public void NavigateTo(PageId page)
    {
        if (!_pages.TryGetValue(page, out var vm)) _pages[page] = vm = _pageFactory(page);
        foreach (var i in MainItems) i.IsActive = i.Id == page;
        foreach (var i in FooterItems) i.IsActive = i.Id == page;
        CurrentPage = vm;
        vm.OnNavigatedTo();
    }

    [RelayCommand] private void ToggleNav() => IsNavExpanded = !IsNavExpanded;
    [RelayCommand] private void GoToConnect() => NavigateTo(PageId.Dashboard);

    [RelayCommand]
    private async Task ToggleExpertModeAsync()
    {
        if (Session.IsExpertMode)
        {
            Session.IsExpertMode = false;
            _notifications.Info(Loc.T("Expert mode disabled"), Loc.T("Write operations are blocked again."));
        }
        else if (await _dialogs.ConfirmExpertModeAsync())
        {
            Session.IsExpertMode = true;
            _notifications.Warning(Loc.T("Expert mode enabled"), Loc.T("Write operations are allowed. You are responsible."));
        }
        OnPropertyChanged(nameof(ExpertText));
    }
}
