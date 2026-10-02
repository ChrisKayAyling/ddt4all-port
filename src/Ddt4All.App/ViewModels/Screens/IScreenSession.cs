using Ddt4All.App.Services;
using Ddt4All.Core.Abstractions;
using Ddt4All.Core.Ecu;
using Ddt4All.Core.Layout;

namespace Ddt4All.App.ViewModels.Screens;

/// <summary>
/// What the Screens page needs from the app: the loaded ECU definition, its layout and the transport already
/// addressed at that ECU. A shared "current ECU session" service can implement this directly.
/// </summary>
public interface IScreenSession
{
    EcuFile? Ecu { get; }
    EcuLayout? Layout { get; }
    /// <summary>Transport connected to <see cref="Ecu"/> (null when offline).</summary>
    IEcuTransport? Transport { get; }
    /// <summary>Raised (any thread) when Ecu/Layout/Transport changed.</summary>
    event Action? Changed;
    /// <summary>Makes sure the transport is addressed at <see cref="Ecu"/> and returns it (null when offline).</summary>
    ValueTask<IEcuTransport?> PrepareTransportAsync(CancellationToken ct = default);
}

/// <summary>Adapts the shared <see cref="IEcuSession"/> service.</summary>
public sealed class EcuSessionAdapter : IScreenSession
{
    private readonly IEcuSession _s;
    public EcuSessionAdapter(IEcuSession session) { _s = session; _s.Changed += () => Changed?.Invoke(); }
    public EcuFile? Ecu => _s.Ecu;
    public EcuLayout? Layout => _s.Layout;
    public IEcuTransport? Transport => _s.Transport;
    public event Action? Changed;
    public async ValueTask<IEcuTransport?> PrepareTransportAsync(CancellationToken ct = default)
    {
        if (!_s.IsLoaded || !_s.IsConnected) return null;
        return await _s.EnsureAddressedAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Default session: ECU + layout set explicitly, transport follows the connection service.</summary>
public sealed class ScreenSession : IScreenSession
{
    private readonly IConnectionService? _connection;
    private IEcuTransport? _override;

    public ScreenSession(IConnectionService? connection = null)
    {
        _connection = connection;
        if (connection is not null) connection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IConnectionService.State)) Changed?.Invoke();
        };
    }

    public EcuFile? Ecu { get; private set; }
    public EcuLayout? Layout { get; private set; }
    public IEcuTransport? Transport => _override ?? _connection?.Transport;
    public event Action? Changed;

    public void Load(EcuFile ecu, EcuLayout layout, IEcuTransport? transport = null)
    {
        Ecu = ecu; Layout = layout; _override = transport;
        Changed?.Invoke();
    }

    public ValueTask<IEcuTransport?> PrepareTransportAsync(CancellationToken ct = default) => new(Transport);

    public void Clear() { Ecu = null; Layout = null; _override = null; Changed?.Invoke(); }
}

/// <summary>Adapts the stand-alone <see cref="IEcuContext"/> (used until the shared session is registered).</summary>
public sealed class EcuContextAdapter : IScreenSession
{
    private readonly IEcuContext _c;
    public EcuContextAdapter(IEcuContext c)
    {
        _c = c;
        c.PropertyChanged += (_, _) => Changed?.Invoke();
    }
    public EcuFile? Ecu => _c.Ecu;
    public EcuLayout? Layout => _c.Layout;
    public IEcuTransport? Transport => _c.Transport;
    public event Action? Changed;
    public ValueTask<IEcuTransport?> PrepareTransportAsync(CancellationToken ct = default) => new(_c.Transport);
}
