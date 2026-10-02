using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ddt4All.App.Localization;
using Ddt4All.App.Models;
using Ddt4All.App.Services;

namespace Ddt4All.App.ViewModels;

public enum TerminalLineKind { Info, Request, Response, Error }

public sealed record TerminalLine(DateTime Time, TerminalLineKind Kind, string Text, double? Millis = null)
{
    public string TimeText => Time.ToString("HH:mm:ss.fff");
    public string Prefix => Kind switch { TerminalLineKind.Request => ">", TerminalLineKind.Response => "<", TerminalLineKind.Error => "!", _ => "#" };
    public bool IsRequest => Kind == TerminalLineKind.Request;
    public bool IsResponse => Kind == TerminalLineKind.Response;
    public bool IsError => Kind == TerminalLineKind.Error;
    public bool IsInfo => Kind == TerminalLineKind.Info;
    public string Duration => Millis is { } m ? $"{m:0} ms" : "";
}

/// <summary>Manual request console: raw AT/ST adapter commands or hex diagnostic payloads, with history.</summary>
public sealed partial class TerminalViewModel : PageViewModel
{
    private readonly IConnectionService _connection;
    private readonly SessionState _session;
    private readonly INotificationService _toasts;
    private readonly List<string> _history = new();
    private int _historyPos;

    public TerminalViewModel(IConnectionService connection, SessionState session, INotificationService toasts)
    {
        _connection = connection; _session = session; _toasts = toasts;
        Lines.Add(new TerminalLine(DateTime.Now, TerminalLineKind.Info, Loc.T("Manual request console. Type an AT command (ATZ) or hex bytes (22 F1 90).")));
        connection.PropertyChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        { OnPropertyChanged(nameof(IsConnected)); SendCommand.NotifyCanExecuteChanged(); });
        session.PropertyChanged += (_, _) => OnPropertyChanged(nameof(IsExpert));
    }

    public override PageId Id => PageId.Terminal;
    public override string Title => Loc.T("Terminal");

    public ObservableCollection<TerminalLine> Lines { get; } = new();
    public IReadOnlyList<string> QuickCommands { get; } = ["ATZ", "ATI", "ATRV", "10 03", "22 F1 90", "19 02 AF", "3E 00"];

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SendCommand))] private string _input = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private bool _showTimestamps = true;

    public bool IsConnected => _connection.State == ConnectionState.Connected;
    public bool IsExpert => _session.IsExpertMode;

    /// <summary>Raised after a line is appended so the view can scroll.</summary>
    public event Action? LineAdded;

    private bool CanSend() => !IsBusy && !string.IsNullOrWhiteSpace(Input);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var cmd = Input.Trim();
        if (cmd.Length == 0) return;
        Input = "";
        _history.Remove(cmd); _history.Add(cmd); _historyPos = _history.Count;

        var isAt = cmd.StartsWith("AT", StringComparison.OrdinalIgnoreCase) || cmd.StartsWith("ST", StringComparison.OrdinalIgnoreCase);
        Add(TerminalLineKind.Request, cmd.ToUpperInvariant());

        if (!isAt)
        {
            var hex = new string(cmd.Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (hex.Length == 0 || hex.Length % 2 != 0 || !hex.All(Uri.IsHexDigit))
            { Add(TerminalLineKind.Error, Loc.T("Invalid hex payload (expected whole bytes, e.g. 22 F1 90)")); return; }
            var sid = Convert.ToByte(hex[..2], 16);
            if (!_session.IsExpertMode && !_session.IsSafeRequest(sid))
            {
                Add(TerminalLineKind.Error, Loc.F("Blocked: service {0:X2} can modify the ECU. Enable expert mode to send it.", sid));
                _toasts.Warning(Loc.T("Request blocked"), Loc.T("Enable expert mode to send write requests."));
                return;
            }
        }

        if (!IsConnected) { Add(TerminalLineKind.Error, Loc.T("Not connected")); return; }

        IsBusy = true;
        var sw = Stopwatch.StartNew();
        try
        {
            var resp = await _connection.SendRawAsync(cmd);
            Add(TerminalLineKind.Response, resp, sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) { Add(TerminalLineKind.Error, ex.Message); }
        finally { IsBusy = false; SendCommand.NotifyCanExecuteChanged(); }
    }

    [RelayCommand] private void UseQuick(string cmd) => Input = cmd;
    [RelayCommand] private void Clear() => Lines.Clear();

    [RelayCommand]
    private void HistoryPrevious()
    {
        if (_history.Count == 0) return;
        _historyPos = Math.Max(0, _historyPos - 1);
        Input = _history[_historyPos];
    }

    [RelayCommand]
    private void HistoryNext()
    {
        if (_history.Count == 0) return;
        _historyPos = Math.Min(_history.Count, _historyPos + 1);
        Input = _historyPos >= _history.Count ? "" : _history[_historyPos];
    }

    public string ExportText() => string.Join(Environment.NewLine, Lines.Select(l => $"{l.TimeText} {l.Prefix} {l.Text}"));

    private void Add(TerminalLineKind kind, string text, double? ms = null)
    {
        Lines.Add(new TerminalLine(DateTime.Now, kind, text, ms));
        if (Lines.Count > 5000) Lines.RemoveAt(0);
        LineAdded?.Invoke();
    }
}
