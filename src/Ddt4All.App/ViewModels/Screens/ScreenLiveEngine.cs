using System.Diagnostics;
using System.Globalization;
using System.Text;
using Ddt4All.Core.Abstractions;
using Ddt4All.Core.Ecu;
using Ddt4All.Core.Layout;

namespace Ddt4All.App.ViewModels.Screens;

public enum LiveState { Stopped, Running, Paused }

/// <summary>
/// Polls the requests behind a screen's displays on a background task and keeps the latest decoded values,
/// a short history per display (sparkline), an in-memory recording and an optional streaming CSV log.
/// The UI never blocks on it: it drains <see cref="DrainChanges"/> from a timer and repaints only what changed.
/// Slot index == index into <c>screen.Displays</c>.
/// </summary>
public sealed class ScreenLiveEngine : IDisposable
{
    public const int HistoryLength = 240;
    public const int MaxRecordedRows = 200_000;

    private sealed class Binding
    {
        public int Slot;
        public DataItem Item = null!;
        public EcuData Data = null!;
    }

    private sealed class Group
    {
        public EcuRequest Request = null!;
        public byte[] Payload = Array.Empty<byte>();
        public List<Binding> Bindings = new();
    }

    private readonly EcuFile _ecu;
    private readonly Screen _screen;
    private readonly IEcuTransport _transport;
    private readonly List<Group> _groups = new();
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly object _lock = new();

    private readonly string?[] _text;
    private readonly double[] _value;
    private readonly double[][] _hist;
    private readonly int[] _histCount;
    private readonly int[] _histHead;
    private readonly bool[] _isDirty;
    private readonly byte[] _status;          // 0 waiting, 1 ok, 2 error
    private readonly double[] _min, _max;     // running min/max of the numeric value since the engine was created
    private List<int> _dirty = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _paused;
    private readonly ManualResetEventSlim _gate = new(true);

    private readonly List<(long Ms, double[] Values, string?[] Text)> _rec = new();
    private bool _recording;
    private StreamWriter? _log;
    private long _recStartTicks;

    public ScreenLiveEngine(EcuFile ecu, Screen screen, IEcuTransport transport)
    {
        _ecu = ecu; _screen = screen; _transport = transport;
        int n = screen.Displays.Count;
        _text = new string?[n];
        _value = new double[n]; Array.Fill(_value, double.NaN);
        _hist = new double[n][];
        _histCount = new int[n]; _histHead = new int[n];
        _isDirty = new bool[n];
        _status = new byte[n];
        _min = new double[n]; _max = new double[n]; Array.Fill(_min, double.NaN); Array.Fill(_max, double.NaN);
        BuildGroups();
    }

    /// <summary>Number of display slots (screen.Displays.Count).</summary>
    public int SlotCount => _text.Length;
    public int RequestCount => _groups.Count;
    public LiveState State { get; private set; }
    public event Action? StateChanged;

    /// <summary>Poll cycles per second (1..50). Takes effect on the next cycle.</summary>
    public double RateHz { get; set; } = 10;
    public string? LastError { get; private set; }
    public long CycleCount { get; private set; }
    public double LastCycleMs { get; private set; }
    public long ErrorCount { get; private set; }

    public bool IsRecording { get { lock (_lock) return _recording; } }
    public int RecordedRows { get { lock (_lock) return _rec.Count; } }
    public bool IsLogging => _log is not null;

    private void BuildGroups()
    {
        var byReq = new Dictionary<string, Group>(StringComparer.Ordinal);
        for (int i = 0; i < _screen.Displays.Count; i++)
        {
            var d = _screen.Displays[i];
            var req = _ecu.GetRequest(d.RequestName);
            if (req is null) continue;
            var item = FindItem(req, d.DataName);
            if (item is null) continue;
            var data = item.Data ?? _ecu.GetData(item.Name);
            if (data is null) continue;
            if (!byReq.TryGetValue(req.Name, out var g))
            {
                if (!req.TryBuildRequest(null, out var payload, out _) || payload is null || payload.Length == 0) continue;
                g = new Group { Request = req, Payload = payload };
                byReq[req.Name] = g; _groups.Add(g);
            }
            g.Bindings.Add(new Binding { Slot = i, Item = item, Data = data });
        }
    }

    internal static DataItem? FindItem(EcuRequest req, string name)
    {
        if (req.ReceiveItems.TryGetValue(name, out var it)) return it;
        foreach (var x in req.ReceiveItems) if (string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) return x;
        return null;
    }

    // ------------------------------------------------------------------ lifecycle

    public void Start()
    {
        if (State == LiveState.Running) return;
        if (State == LiveState.Paused) { Resume(); return; }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _paused = false; _gate.Set();
        SetState(LiveState.Running);
        _loop = Task.Factory.StartNew(() => RunAsync(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    public void Pause()
    {
        if (State != LiveState.Running) return;
        _paused = true; _gate.Reset(); SetState(LiveState.Paused);
    }

    public void Resume()
    {
        if (State != LiveState.Paused) return;
        _paused = false; _gate.Set(); SetState(LiveState.Running);
    }

    public async Task StopAsync()
    {
        var cts = _cts; var loop = _loop;
        if (cts is null) { SetState(LiveState.Stopped); return; }
        cts.Cancel(); _gate.Set();
        try { if (loop is not null) await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _cts = null; _loop = null;
        SetState(LiveState.Stopped);
    }

    public void Dispose()
    {
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
        StopLog();
        _io.Dispose();
    }

    private void SetState(LiveState s) { State = s; StateChanged?.Invoke(); }

    private async Task RunAsync(CancellationToken ct)
    {
        var sw = new Stopwatch();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                _gate.Wait(ct);
                sw.Restart();
                bool anyError = false;
                foreach (var g in _groups)
                {
                    if (ct.IsCancellationRequested || _paused) break;
                    if (!await PollGroupAsync(g, ct).ConfigureAwait(false)) anyError = true;
                }

                CycleCount++;
                LastCycleMs = sw.Elapsed.TotalMilliseconds;
                Sample();
                double hz = Math.Clamp(RateHz, 0.5, 50);
                int wait = (int)Math.Max(anyError ? 200 : 0, 1000.0 / hz - sw.Elapsed.TotalMilliseconds);
                if (wait > 0) await Task.Delay(wait, ct).ConfigureAwait(false);
                else await Task.Yield();
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<bool> PollGroupAsync(Group g, CancellationToken ct)
    {
        byte[] reply;
        await _io.WaitAsync(ct).ConfigureAwait(false);
        try { reply = await _transport.RequestAsync(g.Payload, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LastError = $"{g.Request.Name}: {ex.Message}"; ErrorCount++;
            foreach (var b in g.Bindings) Set(b.Slot, null, double.NaN);
            return false;
        }
        finally { _io.Release(); }

        if (reply.Length >= 3 && reply[0] == 0x7F)
        {
            LastError = $"{g.Request.Name}: negative response 0x{reply[2]:X2} ({Core.Codec.NegativeResponses.Describe(reply[2])})"; ErrorCount++;
            foreach (var b in g.Bindings) Set(b.Slot, null, double.NaN);
            return false;
        }

        var order = _ecu.Endianness;
        foreach (var b in g.Bindings)
        {
            string? text = b.Data.GetDisplayValue(reply, b.Item, order);
            double num = double.NaN;
            if (text is not null)
            {
                if (!b.Data.BytesAscii && !b.Data.HasList && b.Data.TryGetNumeric(reply, b.Item, order, out var v)) num = v;
                if (b.Data.Scaled && b.Data.Unit.Length > 0) text = text + " " + b.Data.Unit;
            }
            Set(b.Slot, text, num);
        }
        LastError = null;
        return true;
    }

    private void Set(int slot, string? text, double num)
    {
        lock (_lock)
        {
            if (!double.IsNaN(num))
            {
                var h = _hist[slot] ??= new double[HistoryLength];
                h[_histHead[slot]] = num;
                _histHead[slot] = (_histHead[slot] + 1) % HistoryLength;
                if (_histCount[slot] < HistoryLength) _histCount[slot]++;
            }
            _value[slot] = num;
            if (!double.IsNaN(num))
            {
                if (double.IsNaN(_min[slot]) || num < _min[slot]) _min[slot] = num;
                if (double.IsNaN(_max[slot]) || num > _max[slot]) _max[slot] = num;
            }
            byte status = text is null ? (byte)2 : (byte)1;
            if (_text[slot] == text && _status[slot] == status) return;
            _text[slot] = text; _status[slot] = status;
            if (!_isDirty[slot]) { _isDirty[slot] = true; _dirty.Add(slot); }
        }
    }

    // ------------------------------------------------------------------ UI side

    /// <summary>Moves the slots changed since the last call into <paramref name="into"/> (cleared first).</summary>
    public void DrainChanges(List<int> into)
    {
        into.Clear();
        lock (_lock)
        {
            if (_dirty.Count == 0) return;
            into.AddRange(_dirty);
            foreach (var i in _dirty) _isDirty[i] = false;
            _dirty.Clear();
        }
    }

    public string? GetText(int slot) { lock (_lock) return _text[slot]; }
    public double GetValue(int slot) { lock (_lock) return _value[slot]; }
    /// <summary>0 = nothing received yet, 1 = decoded value, 2 = the last poll failed / no value.</summary>
    public int GetStatus(int slot) { lock (_lock) return _status[slot]; }
    /// <summary>Running min/max of a numeric display (NaN when it never had a numeric value).</summary>
    public (double Min, double Max) GetMinMax(int slot) { lock (_lock) return (_min[slot], _max[slot]); }
    /// <summary>The newest <paramref name="max"/> history samples of a slot (oldest first), without allocating.</summary>
    public int CopyRecent(int slot, Span<double> dest)
    {
        lock (_lock)
        {
            var h = _hist[slot]; int n = Math.Min(_histCount[slot], dest.Length);
            if (h is null || n == 0) return 0;
            int start = (_histHead[slot] - n + HistoryLength) % HistoryLength;
            for (int i = 0; i < n; i++) dest[i] = h[(start + i) % HistoryLength];
            return n;
        }
    }

    /// <summary>Oldest-to-newest history of numeric values of a slot.</summary>
    public void CopyHistory(int slot, List<double> into)
    {
        into.Clear();
        lock (_lock)
        {
            var h = _hist[slot]; int n = _histCount[slot];
            if (h is null || n == 0) return;
            int start = (_histHead[slot] - n + HistoryLength) % HistoryLength;
            for (int i = 0; i < n; i++) into.Add(h[(start + i) % HistoryLength]);
        }
    }

    // ------------------------------------------------------------------ writes (serialised with polling)

    /// <summary>Builds and sends one request now (shares the transport lock with the poll loop).</summary>
    public async Task<EcuResponse> SendAsync(EcuRequest req, IEnumerable<KeyValuePair<string, string>>? inputs, CancellationToken ct = default)
    {
        var payload = req.BuildRequest(inputs);
        await _io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var reply = await _transport.RequestAsync(payload, ct).ConfigureAwait(false);
            return req.DecodeResponse(reply);
        }
        finally { _io.Release(); }
    }

    // ------------------------------------------------------------------ recording / logging

    private void Sample()
    {
        bool rec; StreamWriter? log;
        lock (_lock) { rec = _recording; log = _log; }
        if (!rec && log is null) return;
        long ms = (long)Stopwatch.GetElapsedTime(_recStartTicks).TotalMilliseconds;
        double[] vals; string?[] txt;
        lock (_lock) { vals = (double[])_value.Clone(); txt = (string?[])_text.Clone(); }
        if (rec)
            lock (_lock)
            {
                if (_rec.Count >= MaxRecordedRows) _rec.RemoveRange(0, MaxRecordedRows / 10);
                _rec.Add((ms, vals, txt));
            }
        if (log is not null)
        {
            try { lock (log) WriteRow(log, ms, vals, txt); }
            catch (Exception ex) { LastError = "CSV log: " + ex.Message; StopLog(); }
        }
    }

    public void StartRecording()
    {
        lock (_lock) { _rec.Clear(); _recording = true; }
        _recStartTicks = Stopwatch.GetTimestamp();
    }

    public void StopRecording() { lock (_lock) _recording = false; }
    public void ClearRecording() { lock (_lock) _rec.Clear(); }

    public string[] ColumnNames()
    {
        var c = new string[_screen.Displays.Count];
        for (int i = 0; i < c.Length; i++) c[i] = _screen.Displays[i].DataName;
        return c;
    }

    /// <summary>Streams every poll cycle to a CSV file until <see cref="StopLog"/>.</summary>
    public void StartLog(string path)
    {
        StopLog();
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var w = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = false };
        WriteHeader(w);
        _recStartTicks = Stopwatch.GetTimestamp();
        lock (_lock) _log = w;
    }

    public void StopLog()
    {
        StreamWriter? w;
        lock (_lock) { w = _log; _log = null; }
        if (w is null) return;
        try { lock (w) { w.Flush(); w.Dispose(); } } catch { }
    }

    /// <summary>Writes the in-memory recording as CSV (numeric where decodable, else the text).</summary>
    public int ExportCsv(TextWriter w)
    {
        WriteHeader(w);
        (long, double[], string?[])[] rows;
        lock (_lock) rows = _rec.ToArray();
        foreach (var (ms, v, t) in rows) WriteRow(w, ms, v, t);
        w.Flush();
        return rows.Length;
    }

    private void WriteHeader(TextWriter w)
    {
        w.Write("time_ms");
        foreach (var n in ColumnNames()) { w.Write(','); w.Write(Csv(n)); }
        w.WriteLine();
    }

    private static void WriteRow(TextWriter w, long ms, double[] v, string?[] t)
    {
        w.Write(ms.ToString(CultureInfo.InvariantCulture));
        for (int i = 0; i < v.Length; i++)
        {
            w.Write(',');
            if (!double.IsNaN(v[i])) w.Write(v[i].ToString("R", CultureInfo.InvariantCulture));
            else if (t[i] is { } s) w.Write(Csv(s));
        }
        w.WriteLine();
    }

    internal static string Csv(string s) =>
        s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
