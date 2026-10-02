using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ddt4All.App.Localization;
using Ddt4All.App.Models;
using Ddt4All.App.Services;

namespace Ddt4All.App.ViewModels;

/// <summary>One line per CAN id, updated in place (<= 20 Hz) so the UI stays smooth at thousands of frames/s.</summary>
public sealed partial class SnifferRow : ObservableObject
{
    public SnifferRow(uint id) { Id = id; IdText = id.ToString("X3"); }
    public uint Id { get; }
    public string IdText { get; }
    [ObservableProperty] private string _data = "";
    [ObservableProperty] private int _dlc;
    [ObservableProperty] private long _count;
    [ObservableProperty] private string _period = "";
    public long LastTicks;
}

public sealed partial class SnifferViewModel : PageViewModel
{
    private readonly ISnifferService _sniffer;
    private readonly INotificationService _toasts;
    private readonly Dictionary<uint, SnifferRow> _rows = new();
    private readonly object _gate = new();
    private readonly Dictionary<uint, CanFrame> _pendingLast = new();
    private readonly Dictionary<uint, int> _pendingCount = new();
    private readonly DispatcherTimer _timer;
    private long _total;

    public SnifferViewModel(ISnifferService sniffer, INotificationService toasts)
    {
        _sniffer = sniffer; _toasts = toasts;
        _sniffer.FramesReceived += OnFrames;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Flush();
    }

    public override PageId Id => PageId.Sniffer;
    public override string Title => Loc.T("CAN Sniffer");

    public ObservableCollection<SnifferRow> Rows { get; } = new();
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _summary = "";

    private void OnFrames(IReadOnlyList<CanFrame> frames)
    {
        lock (_gate)
            foreach (var f in frames)
            {
                _pendingLast[f.Id] = f;
                _pendingCount[f.Id] = _pendingCount.GetValueOrDefault(f.Id) + 1;
            }
    }

    private void Flush()
    {
        KeyValuePair<uint, CanFrame>[] last; Dictionary<uint, int> counts;
        lock (_gate)
        {
            if (_pendingLast.Count == 0) return;
            last = _pendingLast.ToArray(); counts = new(_pendingCount);
            _pendingLast.Clear(); _pendingCount.Clear();
        }
        foreach (var (id, f) in last)
        {
            if (!_rows.TryGetValue(id, out var row))
            {
                _rows[id] = row = new SnifferRow(id);
                var idx = 0;
                while (idx < Rows.Count && Rows[idx].Id < id) idx++;
                Rows.Insert(idx, row);
            }
            var n = counts[id];
            row.Data = HexFormat.Spaced(f.Data);
            row.Dlc = f.Data.Length;
            if (row.LastTicks != 0 && n > 0)
                row.Period = $"{Stopwatch.GetElapsedTime(row.LastTicks, f.Timestamp).TotalMilliseconds / n:0.0} ms";
            row.LastTicks = f.Timestamp;
            row.Count += n; _total += n;
        }
        Summary = Loc.F("{0} ids, {1:N0} frames", Rows.Count, _total);
    }

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (IsRunning) { await _sniffer.StopAsync(); _timer.Stop(); IsRunning = false; return; }
        try { await _sniffer.StartAsync(); _timer.Start(); IsRunning = true; }
        catch (Exception ex) { _toasts.Error(Loc.T("CAN Sniffer"), ex.Message); }
    }

    [RelayCommand]
    private void Clear() { Rows.Clear(); _rows.Clear(); _total = 0; Summary = ""; }
}
