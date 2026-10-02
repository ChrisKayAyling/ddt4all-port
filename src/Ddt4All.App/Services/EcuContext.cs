using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ddt4All.Core.Abstractions;
using Ddt4All.Core.Ecu;
using Ddt4All.Core.Layout;
using Ddt4All.Core.Loading;

namespace Ddt4All.App.Services;

/// <summary>
/// The "current ECU session" as seen by the Requests and Data Editor pages: the loaded definition, its layout and the live
/// transport. Adapt/replace with the shared session service when it exists (see <see cref="EcuContext"/>).
/// </summary>
public interface IEcuContext : INotifyPropertyChanged
{
    EcuFile? Ecu { get; }
    EcuLayout? Layout { get; }
    /// <summary>File the ECU was loaded from / last saved to (null for in-memory ECUs).</summary>
    string? SourcePath { get; }
    /// <summary>Connected transport or null.</summary>
    IEcuTransport? Transport { get; }
    /// <summary>Makes <paramref name="ecu"/> the current ECU.</summary>
    void SetEcu(EcuFile ecu, EcuLayout? layout, string? path);
}

/// <summary>Optional capability of an <see cref="IEcuContext"/>: points the adapter at the current ECU before sending.</summary>
public interface IEcuTransportProvider
{
    ValueTask<IEcuTransport> EnsureAddressedAsync(CancellationToken ct = default);
}

/// <summary>Stand-alone implementation backed by <see cref="IConnectionService"/> for the transport.</summary>
public sealed partial class EcuContext : ObservableObject, IEcuContext, IEcuTransportProvider
{
    private readonly IConnectionService? _connection;
    private readonly SessionState? _session;

    public EcuContext(IConnectionService? connection = null, SessionState? session = null)
    {
        _connection = connection; _session = session;
        if (connection != null)
            connection.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(IConnectionService.State) or null) OnPropertyChanged(nameof(Transport));
            };
    }

    [ObservableProperty] private EcuFile? _ecu;
    [ObservableProperty] private EcuLayout? _layout;
    [ObservableProperty] private string? _sourcePath;
    private IEcuTransport? _transportOverride;
    /// <summary>Forces a transport regardless of the connection service (tests, demos).</summary>
    public IEcuTransport? TransportOverride { get => _transportOverride; set { _transportOverride = value; OnPropertyChanged(nameof(Transport)); } }
    public IEcuTransport? Transport => _transportOverride ?? _connection?.Transport;
    /// <summary>Points the adapter at the current ECU (set by the composition root from the shared ECU session).</summary>
    public Func<CancellationToken, ValueTask<IEcuTransport>>? Addresser { get; set; }

    public async ValueTask<IEcuTransport> EnsureAddressedAsync(CancellationToken ct = default)
    {
        if (_transportOverride != null) return _transportOverride;
        if (Addresser != null) return await Addresser(ct);
        return Transport ?? throw new InvalidOperationException("Not connected");
    }

    public void SetEcu(EcuFile ecu, EcuLayout? layout, string? path)
    {
        Ecu = ecu; Layout = layout; SourcePath = path;
        if (_session != null) _session.CurrentEcu = ecu.EcuName;
    }

    /// <summary>Loads an ECU definition (+ screens of the same XML) from a .xml / .json file.</summary>
    public static (EcuFile Ecu, EcuLayout? Layout) LoadFile(string path)
    {
        var ecu = EcuFile.Load(path);
        EcuLayout? layout = null;
        try { if (path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) layout = EcuLayoutXmlLoader.Load(path); }
        catch (Exception) { /* screens are optional */ }
        return (ecu, layout);
    }
}
