using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ddt4All.App.Localization;
using Ddt4All.App.Services;
using Microsoft.Extensions.Logging;

namespace Ddt4All.App.ViewModels;

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly ISettingsService _settings;
    private readonly IThemeService _theme;
    private readonly IFilePickerService _picker;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _toasts;
    private readonly bool _init;

    public SettingsViewModel(ISettingsService settings, IThemeService theme, IFilePickerService picker, IDialogService dialogs, INotificationService toasts)
    {
        _settings = settings; _theme = theme; _picker = picker; _dialogs = dialogs; _toasts = toasts;
        var s = settings.Current;

        Languages = [new LanguageChoice(Loc.SystemCode, "System default", null), .. Loc.Languages.Select(l => new LanguageChoice(l.Code, l.NativeName, l))];
        Themes = [new("system", "System"), new("light", "Light"), new("dark", "Dark")];
        Accents = theme.Accents;
        LogLevels = ["Trace", "Debug", "Information", "Warning", "Error"];
        CarlistSortModes = [new("code", "By project code"), new("name", "By car name")];

        _selectedLanguage = Languages.FirstOrDefault(l => l.Code == s.Language) ?? Languages[0];
        _selectedTheme = Themes.FirstOrDefault(t => t.Id == s.Theme) ?? Themes[0];
        _selectedAccent = Accents.FirstOrDefault(a => a.Id == s.Accent) ?? Accents[0];
        _selectedLogLevel = LogLevels.Contains(s.LogLevel) ? s.LogLevel : "Information";
        _selectedCarlistSort = CarlistSortModes.FirstOrDefault(c => c.Id == s.CarlistSortMode) ?? CarlistSortModes[0];
        _serialBaud = s.Serial.BaudRate; _hardwareFlowControl = s.Serial.HardwareFlowControl; _autoDetect = s.Serial.AutoDetectDevices;
        _connectionTimeout = s.ConnectionTimeoutSec; _readTimeout = s.ReadTimeoutSec; _maxReconnect = s.MaxReconnectAttempts;
        _refreshRate = s.RefreshRateMs; _canTimeout = s.CanTimeoutMs;
        _ecuZipPath = s.EcuZipPath ?? ""; _ecuDirectory = s.EcuDirectory ?? ""; _graphicsDirectory = s.GraphicsDirectory ?? "";
        _init = true;
    }

    public override PageId Id => PageId.Settings;
    public override string Title => Loc.T("Settings");

    public sealed record LanguageChoice(string Code, string Name, LanguageInfo? Info)
    {
        public string Display => Code == Loc.SystemCode ? $"{Loc.T("System default")}" : Name;
        public override string ToString() => Display;
    }

    public IReadOnlyList<LanguageChoice> Languages { get; }
    public IReadOnlyList<ChoiceItem> Themes { get; }
    public IReadOnlyList<AccentOption> Accents { get; }
    public IReadOnlyList<string> LogLevels { get; }
    public IReadOnlyList<ChoiceItem> CarlistSortModes { get; }
    public IReadOnlyList<int> BaudRates { get; } = [9600, 19200, 38400, 57600, 115200, 230400, 500000, 1000000];

    [ObservableProperty] private LanguageChoice _selectedLanguage;
    [ObservableProperty] private ChoiceItem _selectedTheme;
    [ObservableProperty] private AccentOption _selectedAccent;
    [ObservableProperty] private string _selectedLogLevel;
    [ObservableProperty] private ChoiceItem _selectedCarlistSort;
    [ObservableProperty] private int _serialBaud;
    [ObservableProperty] private bool _hardwareFlowControl;
    [ObservableProperty] private bool _autoDetect;
    [ObservableProperty] private int _connectionTimeout;
    [ObservableProperty] private int _readTimeout;
    [ObservableProperty] private int _maxReconnect;
    [ObservableProperty] private int _refreshRate;
    [ObservableProperty] private int _canTimeout;
    [ObservableProperty] private string _ecuZipPath;
    [ObservableProperty] private string _ecuDirectory;
    [ObservableProperty] private string _graphicsDirectory;

    public string ConfigDir => AppPaths.ConfigDir;
    public string LogDir => AppPaths.LogDir;

    partial void OnSelectedLanguageChanged(LanguageChoice value)
    {
        if (!_init) return;
        _settings.Update(s => s.Language = value.Code);
        Loc.Instance.SetLanguage(value.Code);
    }
    partial void OnSelectedThemeChanged(ChoiceItem value) { if (_init) { _settings.Update(s => s.Theme = value.Id); _theme.Apply(value.Id, SelectedAccent.Id); } }
    partial void OnSelectedAccentChanged(AccentOption value) { if (_init) { _settings.Update(s => s.Accent = value.Id); _theme.Apply(SelectedTheme.Id, value.Id); } }
    partial void OnSelectedLogLevelChanged(string value)
    {
        if (!_init) return;
        _settings.Update(s => s.LogLevel = value);
        if (LogLevelSwitch.TryParse(value, out var lvl)) LogLevelSwitch.Minimum = lvl;
    }
    partial void OnSelectedCarlistSortChanged(ChoiceItem value) { if (_init) _settings.Update(s => s.CarlistSortMode = value.Id); }
    partial void OnSerialBaudChanged(int value) { if (_init) _settings.Update(s => s.Serial.BaudRate = value); }
    partial void OnHardwareFlowControlChanged(bool value) { if (_init) _settings.Update(s => s.Serial.HardwareFlowControl = value); }
    partial void OnAutoDetectChanged(bool value) { if (_init) _settings.Update(s => s.Serial.AutoDetectDevices = value); }
    partial void OnConnectionTimeoutChanged(int value) { if (_init) _settings.Update(s => s.ConnectionTimeoutSec = Math.Clamp(value, 1, 120)); }
    partial void OnReadTimeoutChanged(int value) { if (_init) _settings.Update(s => s.ReadTimeoutSec = Math.Clamp(value, 1, 120)); }
    partial void OnMaxReconnectChanged(int value) { if (_init) _settings.Update(s => s.MaxReconnectAttempts = Math.Clamp(value, 0, 20)); }
    partial void OnRefreshRateChanged(int value) { if (_init) _settings.Update(s => s.RefreshRateMs = Math.Clamp(value, 1, 5000)); }
    partial void OnCanTimeoutChanged(int value) { if (_init) _settings.Update(s => s.CanTimeoutMs = Math.Clamp(value, 0, 10000)); }
    partial void OnEcuZipPathChanged(string value) { if (_init) _settings.Update(s => s.EcuZipPath = value.Length == 0 ? null : value); }
    partial void OnEcuDirectoryChanged(string value) { if (_init) _settings.Update(s => s.EcuDirectory = value.Length == 0 ? null : value); }
    partial void OnGraphicsDirectoryChanged(string value) { if (_init) _settings.Update(s => s.GraphicsDirectory = value.Length == 0 ? null : value); }

    [RelayCommand]
    private async Task BrowseEcuZipAsync()
    {
        var p = await _picker.PickFileAsync(Loc.T("Select ecu.zip"), (Loc.T("ECU database"), ["*.zip"]), (Loc.T("All files"), ["*"]));
        if (p is not null) EcuZipPath = p;
    }

    [RelayCommand]
    private async Task BrowseEcuDirAsync()
    {
        var p = await _picker.PickFolderAsync(Loc.T("Select ECU directory"));
        if (p is not null) EcuDirectory = p;
    }

    [RelayCommand]
    private async Task BrowseGraphicsDirAsync()
    {
        var p = await _picker.PickFolderAsync(Loc.T("Select graphics directory"));
        if (p is not null) GraphicsDirectory = p;
    }

    [RelayCommand] private void ClearEcuZip() => EcuZipPath = "";

    [RelayCommand]
    private void OpenFolder(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex) { _toasts.Error(Loc.T("Could not open folder"), ex.Message); }
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        if (!await _dialogs.ConfirmAsync(Loc.T("Reset DDT4ALL Settings"), Loc.T("All settings return to their defaults."), Loc.T("Reset"), danger: true)) return;
        _settings.Update(s =>
        {
            var d = new AppSettings();
            s.Language = d.Language; s.Theme = d.Theme; s.Accent = d.Accent; s.LogLevel = d.LogLevel;
            s.EcuZipPath = null; s.EcuDirectory = null; s.GraphicsDirectory = null;
            s.Serial = d.Serial; s.Connection = d.Connection; s.CarlistSortMode = d.CarlistSortMode;
            s.RefreshRateMs = d.RefreshRateMs; s.CanTimeoutMs = d.CanTimeoutMs; s.ConnectionTimeoutSec = d.ConnectionTimeoutSec;
            s.ReadTimeoutSec = d.ReadTimeoutSec; s.MaxReconnectAttempts = d.MaxReconnectAttempts;
        });
        _toasts.Success(Loc.T("The configuration has been reset."), Loc.T("Restart the app to refresh every page."));
    }
}
