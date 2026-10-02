using System.ComponentModel;
using Ddt4All.Comms;
using Ddt4All.Core.Ecu;
using Ddt4All.Core.Layout;

namespace Ddt4All.App.Services;

/// <summary>
/// The shared "current ECU session": the loaded ECU definition (<see cref="Ecu"/> + <see cref="Layout"/>) together with
/// the live adapter transport. Screens / Requests / Data editor pages consume this instead of talking to the catalog or
/// the connection directly. Registered as a singleton in <c>AppHost</c>; <c>SessionState.CurrentEcu</c> mirrors <see cref="DisplayName"/>.
/// <para>Raises <see cref="INotifyPropertyChanged.PropertyChanged"/> (any thread; UI code must marshal) for
/// <see cref="IsLoaded"/>, <see cref="Ecu"/>, <see cref="Layout"/>, <see cref="Transport"/>, <see cref="IsConnected"/>,
/// <see cref="Address"/>, <see cref="DisplayName"/>; <see cref="Changed"/> fires once after every such change.</para>
/// </summary>
public interface IEcuSession : INotifyPropertyChanged
{
    bool IsLoaded { get; }
    /// <summary>Catalog key (file name inside ecu.zip, or the full path for loose files).</summary>
    string? Href { get; }
    /// <summary>Loaded ECU definition (requests, data, devices); null when no ECU is open.</summary>
    EcuFile? Ecu { get; }
    /// <summary>Screens of the ECU; null when the ECU has no layout (or none is open).</summary>
    EcuLayout? Layout { get; }
    /// <summary>The connected adapter/transport (null while disconnected). Not necessarily addressed to this ECU yet: call <see cref="EnsureAddressedAsync"/>.</summary>
    IAddressableTransport? Transport { get; }
    bool IsConnected { get; }
    /// <summary>Bus address used to talk to this ECU (derived from the definition, or the scan result).</summary>
    EcuAddress? Address { get; }
    /// <summary>"NAME (addr)" or null.</summary>
    string? DisplayName { get; }
    /// <summary>File the definition came from / was last saved to (null for catalog ECUs and in-memory ones).</summary>
    string? SourcePath { get; }

    event Action? Changed;

    /// <summary>Loads an ECU of the catalog by its href (<c>EcuEntry.Id</c>). <paramref name="addressOverride"/> pins the bus address (scan results).</summary>
    Task OpenAsync(string href, EcuAddress? addressOverride = null, CancellationToken ct = default);
    /// <summary>Loads a loose .xml / .json ECU definition from disk (and the layout of an .xml file).</summary>
    Task OpenFileAsync(string path, CancellationToken ct = default);
    /// <summary>Makes an already parsed definition (e.g. just created or edited in the Data Editor) the current ECU.</summary>
    void SetLoaded(EcuFile ecu, EcuLayout? layout, string? sourcePath);
    void Close();

    /// <summary>
    /// Points the adapter at this ECU (idempotent) and returns the transport, ready for <c>EcuRequest.SendAsync</c>.
    /// Throws <see cref="InvalidOperationException"/> if no ECU is open or there is no connection.
    /// </summary>
    ValueTask<IAddressableTransport> EnsureAddressedAsync(CancellationToken ct = default);

    /// <summary>True when sending <paramref name="request"/> is allowed now: always for read-only services, writes only in expert mode.</summary>
    bool IsRequestAllowed(EcuRequest request);
}
