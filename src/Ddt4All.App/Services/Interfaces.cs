using System.ComponentModel;
using Ddt4All.App.Models;
using Ddt4All.Comms;
using Ddt4All.Core.Abstractions;
using Ddt4All.Core.Database;
using Ddt4All.Core.Ecu;
using Ddt4All.Core.Layout;

namespace Ddt4All.App.Services;

/// <summary>ECU database access. Real impl wraps Ddt4All.Core's ecu.zip index; must be cheap to call repeatedly.</summary>
public interface IEcuCatalogService
{
    /// <summary>Path of the ecu.zip / ecus dir in use, or null if not found.</summary>
    string? DatabasePath { get; }
    bool IsAvailable { get; }
    /// <summary>Why the database could not be opened (null when fine / not tried yet).</summary>
    string? Error { get; }
    /// <summary>The open Core database (null when unavailable or for fakes). Used by the scanner and the simulator.</summary>
    EcuDatabase? Database { get; }
    /// <summary>Raised (any thread) after the database path changed or the database was (re)opened.</summary>
    event Action? Changed;
    /// <summary>Index names/ids only (cached by Core). Called off the UI thread.</summary>
    Task<EcuCatalog> LoadAsync(CancellationToken ct = default);
    /// <summary>Points the app at another ecu.zip (persisted in the settings) and reopens it. Returns false (and sets <see cref="Error"/>) on failure.</summary>
    Task<bool> SetDatabasePathAsync(string path, CancellationToken ct = default);
    /// <summary>Parses one ECU definition of the database (lazy, off the UI thread). <paramref name="href"/> = <c>EcuEntry.Id</c>.</summary>
    Task<EcuFile> LoadEcuAsync(string href, CancellationToken ct = default);
    /// <summary>Screens of an ECU definition, or null if it has none.</summary>
    Task<EcuLayout?> LoadLayoutAsync(string href, CancellationToken ct = default);
}

/// <summary>Adapter connection lifecycle + raw terminal I/O. Real impl lives on Ddt4All.Comms.</summary>
public interface IConnectionService : INotifyPropertyChanged
{
    ConnectionState State { get; }
    string StatusText { get; }
    AdapterInfo? Adapter { get; }
    /// <summary>The Core seam once connected (null otherwise).</summary>
    IEcuTransport? Transport { get; }
    /// <summary>Same object as <see cref="Transport"/> when it can be pointed at ECUs (ELM, DoIP); null otherwise.</summary>
    IAddressableTransport? AddressableTransport { get; }

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
