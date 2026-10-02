using System.ComponentModel;
using Ddt4All.App.Models;
using Ddt4All.Core.Abstractions;

namespace Ddt4All.App.Services;

/// <summary>ECU database access. Real impl wraps Ddt4All.Core's ecu.zip index; must be cheap to call repeatedly.</summary>
public interface IEcuCatalogService
{
    /// <summary>Path of the ecu.zip / ecus dir in use, or null if not found.</summary>
    string? DatabasePath { get; }
    bool IsAvailable { get; }
    /// <summary>Index names/ids only (cached by Core). Called off the UI thread.</summary>
    Task<EcuCatalog> LoadAsync(CancellationToken ct = default);
}

/// <summary>Adapter connection lifecycle + raw terminal I/O. Real impl lives on Ddt4All.Comms.</summary>
public interface IConnectionService : INotifyPropertyChanged
{
    ConnectionState State { get; }
    string StatusText { get; }
    AdapterInfo? Adapter { get; }
    /// <summary>The Core seam once connected (null otherwise).</summary>
    IEcuTransport? Transport { get; }

    Task<IReadOnlyList<SerialPortInfo>> ListPortsAsync(CancellationToken ct = default);
    Task ConnectAsync(ConnectionRequest request, CancellationToken ct = default);
    Task DisconnectAsync();

    /// <summary>Manual console: "ATZ" style adapter commands or hex payloads ("22 F1 90"). Returns the raw response text.</summary>
    Task<string> SendRawAsync(string command, CancellationToken ct = default);
}

public interface IDiagnosticsService
{
    Task<IReadOnlyList<DtcItem>> ReadDtcsAsync(string? ecu, CancellationToken ct = default);
    Task ClearDtcsAsync(string? ecu, CancellationToken ct = default);
}

public interface ISnifferService
{
    bool IsRunning { get; }
    /// <summary>Frames are delivered in batches from any thread.</summary>
    event Action<IReadOnlyList<CanFrame>>? FramesReceived;
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
}

public interface INavigationService
{
    void NavigateTo(PageId page);
}

public enum PageId { Dashboard, EcuBrowser, Screens, Requests, Dtcs, Terminal, Sniffer, DataEditor, Plugins, Logs, Settings, About }
