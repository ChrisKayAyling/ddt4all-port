using System.Diagnostics;
using Ddt4All.App.Localization;
using Ddt4All.App.Models;
using Ddt4All.Comms.Sniffer;
using Microsoft.Extensions.Logging;

namespace Ddt4All.App.Services.Real;

/// <summary>Passive CAN monitor (ATMA / STMA) over the connected ELM adapter. The adapter is busy while sniffing.</summary>
public sealed class SnifferService : ISnifferService
{
    private readonly ConnectionService _connection;
    private readonly ILogger<SnifferService>? _log;
    private CancellationTokenSource? _cts;
    private Task? _pump;

    public SnifferService(ConnectionService connection, ILogger<SnifferService>? log = null) { _connection = connection; _log = log; }

    public bool IsRunning => _cts is not null;
    public event Action<IReadOnlyList<CanFrame>>? FramesReceived;
    /// <summary>Raised when the monitor stopped on its own (adapter lost, error).</summary>
    public event Action<string>? Faulted;

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_cts is not null) return Task.CompletedTask;
        var elm = _connection.Elm ?? throw new InvalidOperationException(_connection.State == ConnectionState.Connected
            ? Loc.T("The CAN sniffer needs an ELM327 / STN adapter.") : Loc.T("Not connected"));
        var cts = _cts = new CancellationTokenSource();
        var sniffer = new CanSniffer(elm, new SnifferOptions());
        _pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var batch in sniffer.BatchesAsync(null, cts.Token).ConfigureAwait(false))
                {
                    var frames = new List<CanFrame>(batch.Count);
                    foreach (var f in batch)
                        if (f.Id is { } id) frames.Add(new CanFrame(id, f.Data, f.Timestamp));
                    if (frames.Count > 0) FramesReceived?.Invoke(frames);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "CAN sniffer stopped");
                Faulted?.Invoke(ex.Message);
            }
            finally { if (ReferenceEquals(_cts, cts)) _cts = null; }
        });
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var cts = _cts; var pump = _pump;
        _cts = null; _pump = null;
        if (cts is null) return;
        cts.Cancel();
        if (pump is not null) { try { await pump.ConfigureAwait(false); } catch { /* reported via Faulted */ } }
        cts.Dispose();
    }
}
