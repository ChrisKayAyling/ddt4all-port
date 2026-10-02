using System.Globalization;

namespace Ddt4All.Comms.Simulation;

/// <summary>
/// Replays recorded request/response pairs. Responses for the same request are served in recorded order and the last
/// one repeats. Accepts any ECU address. Can ingest the Python app's "ecu_*.txt" logs
/// ("TimeStamp;Address;Command;Response;Error").
/// </summary>
public sealed class ReplayTransport : IAddressableTransport
{
    private readonly Dictionary<string, List<byte[]>> _table = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _cursor = new(StringComparer.Ordinal);

    public EcuAddress? CurrentEcu { get; private set; }

    /// <summary>When true (default) unknown requests time out; otherwise they get NRC 0x11.</summary>
    public bool Strict { get; set; } = true;

    /// <summary>All requests received, in order (hex, no spaces).</summary>
    public List<string> Requests { get; } = new();

    public ReplayTransport Add(string requestHex, string responseHex)
    {
        var key = Hex.ToString(Hex.Parse(requestHex));
        if (!_table.TryGetValue(key, out var list)) _table[key] = list = new List<byte[]>();
        list.Add(Hex.Parse(responseHex));
        return this;
    }

    /// <summary>Loads a Python ecu log. Rows with error responses ("NR:..", "WRONG RESPONSE", empty) and '#' comments are skipped.</summary>
    public static ReplayTransport FromEcuLog(IEnumerable<string> lines)
    {
        var t = new ReplayTransport();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split(';');
            if (parts.Length < 4) continue;
            var cmd = parts[2].Replace(" ", "");
            var rsp = parts[3].Trim();
            if (!Hex.IsHexLine(cmd) || !Hex.IsHexLine(rsp)) continue;
            t.Add(cmd, rsp);
        }
        return t;
    }

    public static ReplayTransport FromEcuLogFile(string path) => FromEcuLog(File.ReadLines(path));

    public ValueTask ConnectEcuAsync(EcuAddress ecu, CancellationToken ct = default) { CurrentEcu = ecu; return ValueTask.CompletedTask; }
    public ValueTask CloseProtocolAsync(CancellationToken ct = default) { CurrentEcu = null; return ValueTask.CompletedTask; }

    public ValueTask<byte[]> RequestAsync(ReadOnlyMemory<byte> request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var key = Hex.ToString(request.Span);
        Requests.Add(key);
        if (_table.TryGetValue(key, out var list))
        {
            int i = _cursor.GetValueOrDefault(key);
            _cursor[key] = Math.Min(i + 1, list.Count - 1);
            return ValueTask.FromResult(list[Math.Min(i, list.Count - 1)]);
        }
        if (Strict) throw new EcuTimeoutException($"Replay has no recorded response for {key}.");
        return ValueTask.FromResult(new byte[] { 0x7F, request.Span[0], 0x11 });
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
