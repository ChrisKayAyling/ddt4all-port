using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ddt4All.App.Localization;
using Ddt4All.App.Services;
using Microsoft.Extensions.Logging;

namespace Ddt4All.App.ViewModels;

public sealed partial class LogViewModel : PageViewModel
{
    private readonly ILogStore _store;
    private readonly System.Collections.Concurrent.ConcurrentQueue<LogEntry> _pending = new();
    private readonly DispatcherTimer _timer;
    private const int Max = 3000;

    public LogViewModel(ILogStore store)
    {
        _store = store;
        Levels = [LogLevel.Trace, LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Error];
        _minLevel = LogLevel.Information;
        foreach (var e in store.Snapshot().TakeLast(Max)) _all.Add(e);
        Rebuild();
        store.EntryAdded += e => _pending.Enqueue(e);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) => Drain();
        _timer.Start();
    }

    public override PageId Id => PageId.Logs;
    public override string Title => Loc.T("Logs");

    private readonly List<LogEntry> _all = new();
    public ObservableCollection<LogEntry> Entries { get; } = new();
    public IReadOnlyList<LogLevel> Levels { get; }

    [ObservableProperty] private LogLevel _minLevel;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private string _summary = "";

    public event Action? Appended;
    public string LogDir => AppPaths.LogDir;

    partial void OnMinLevelChanged(LogLevel value) => Rebuild();
    partial void OnFilterChanged(string value) => Rebuild();

    private bool Matches(LogEntry e) =>
        e.Level >= MinLevel && (Filter.Length == 0 || e.Message.Contains(Filter, StringComparison.OrdinalIgnoreCase) || e.Category.Contains(Filter, StringComparison.OrdinalIgnoreCase));

    private void Drain()
    {
        var any = false;
        while (_pending.TryDequeue(out var e))
        {
            _all.Add(e);
            if (_all.Count > Max) _all.RemoveAt(0);
            if (Matches(e)) { Entries.Add(e); any = true; if (Entries.Count > Max) Entries.RemoveAt(0); }
        }
        if (any) { UpdateSummary(); Appended?.Invoke(); }
    }

    private void Rebuild()
    {
        Entries.Clear();
        foreach (var e in _all) if (Matches(e)) Entries.Add(e);
        UpdateSummary();
        Appended?.Invoke();
    }

    private void UpdateSummary() => Summary = Loc.F("{0:N0} of {1:N0} entries", Entries.Count, _all.Count);

    [RelayCommand] private void Clear() { _store.Clear(); _all.Clear(); Entries.Clear(); UpdateSummary(); }

    public string ExportText() => string.Join(Environment.NewLine, Entries.Select(e => e.ToString()));
}
