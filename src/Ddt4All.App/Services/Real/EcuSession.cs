using CommunityToolkit.Mvvm.ComponentModel;
using Ddt4All.App.Localization;
using Ddt4All.App.Models;
using Ddt4All.Comms;
using Ddt4All.Core.Ecu;
using Ddt4All.Core.Layout;
using Microsoft.Extensions.Logging;
using CoreProtocol = Ddt4All.Core.Ecu.EcuProtocol;
using CommsProtocol = Ddt4All.Comms.EcuProtocol;

namespace Ddt4All.App.Services.Real;

/// <summary>Default <see cref="IEcuSession"/>: catalog (definitions) + connection (transport) + expert-mode policy.</summary>
public sealed class EcuSession : ObservableObject, IEcuSession
{
    private readonly IEcuCatalogService _catalog;
    private readonly IConnectionService _connection;
    private readonly SessionState _state;
    private readonly ISettingsService _settings;
    private readonly ILogger<EcuSession>? _log;
    private EcuFile? _ecu;
    private EcuLayout? _layout;
    private string? _href;
    private string? _sourcePath;
    private EcuAddress? _addressOverride;
    private IAddressableTransport? _lastTransport;

    public EcuSession(IEcuCatalogService catalog, IConnectionService connection, SessionState state, ISettingsService settings, ILogger<EcuSession>? log = null)
    {
        _catalog = catalog; _connection = connection; _state = state; _settings = settings; _log = log;
        connection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IConnectionService.State) or nameof(IConnectionService.Adapter))
            {
                if (!ReferenceEquals(_lastTransport, connection.AddressableTransport)) { _lastTransport = connection.AddressableTransport; RaiseTransport(); AddressInBackground(); }
            }
        };
    }

    public event Action? Changed;

    public bool IsLoaded => _ecu is not null;
    public string? Href => _href;
    public EcuFile? Ecu => _ecu;
    public EcuLayout? Layout => _layout;
    public IAddressableTransport? Transport => _connection.State == ConnectionState.Connected ? _connection.AddressableTransport : null;
    public bool IsConnected => Transport is not null;
    public EcuAddress? Address => _addressOverride ?? (_ecu is null ? null : DeriveAddress(_ecu));
    public string? SourcePath => _sourcePath;
    public string? DisplayName => _ecu is null ? null : $"{_ecu.EcuName} ({_ecu.FuncAddr})";

    public async Task OpenAsync(string href, EcuAddress? addressOverride = null, CancellationToken ct = default)
    {
        var ecuTask = _catalog.LoadEcuAsync(href, ct);
        var layoutTask = _catalog.LoadLayoutAsync(href, ct);
        var ecu = await ecuTask;
        EcuLayout? layout = null;
        try { layout = await layoutTask; } catch (Exception ex) when (ex is not OperationCanceledException) { _log?.LogWarning(ex, "Layout of {Href} could not be loaded", href); }
        Apply(href, ecu, layout, addressOverride);
        _settings.Update(s => s.LastOpenedEcu = href);
        _log?.LogInformation("Opened ECU {Name} ({Href})", ecu.EcuName, href);
    }

    public async Task OpenFileAsync(string path, CancellationToken ct = default)
    {
        var (ecu, layout) = await Task.Run(() =>
        {
            var e = EcuFile.Load(path);
            EcuLayout? l = null;
            if (string.Equals(Path.GetExtension(path), ".xml", StringComparison.OrdinalIgnoreCase))
            {
                try { l = EcuLayoutXmlLoader.Load(path); } catch (Exception ex) when (ex is not OperationCanceledException) { _log?.LogWarning(ex, "No layout in {Path}", path); }
            }
            return (e, l);
        }, ct);
        Apply(path, ecu, layout, null, path);
    }

    public void SetLoaded(EcuFile ecu, EcuLayout? layout, string? sourcePath)
    {
        Apply(sourcePath ?? ecu.EcuName, ecu, layout, null, sourcePath);
    }

    private void Apply(string href, EcuFile ecu, EcuLayout? layout, EcuAddress? addr, string? sourcePath = null)
    {
        _href = href; _ecu = ecu; _layout = layout; _addressOverride = addr; _sourcePath = sourcePath;
        _state.CurrentEcu = DisplayName;
        RaiseAll();
        AddressInBackground();
    }

    public void Close()
    {
        if (_ecu is null) return;
        _href = null; _ecu = null; _layout = null; _addressOverride = null; _sourcePath = null;
        _state.CurrentEcu = null;
        RaiseAll();
    }

    public async ValueTask<IAddressableTransport> EnsureAddressedAsync(CancellationToken ct = default)
    {
        var ecu = _ecu ?? throw new InvalidOperationException(Loc.T("No ECU selected"));
        var t = Transport ?? throw new InvalidOperationException(Loc.T("Not connected"));
        var addr = Address ?? throw new InvalidOperationException(Loc.F("No bus address known for {0}.", ecu.EcuName));
        if (!(t.CurrentEcu is { } cur && cur.Equals(addr)))
            await t.ConnectEcuAsync(addr, ct).ConfigureAwait(false);
        return t;
    }

    /// <summary>Best effort: point the adapter at the ECU as soon as both are there, so pages that only look at <see cref="Transport"/> can send right away.</summary>
    private void AddressInBackground()
    {
        if (_ecu is null || Transport is null) return;
        _ = Task.Run(async () =>
        {
            try { await EnsureAddressedAsync().ConfigureAwait(false); }
            catch (Exception ex) { _log?.LogDebug("Could not address {Ecu}: {Message}", DisplayName, ex.Message); }
        });
    }

    public bool IsRequestAllowed(EcuRequest request)
    {
        if (_state.IsExpertMode) return true;
        var t = request.GetSentBytesTemplate();
        return t.Length > 0 && _state.IsSafeRequest(t[0]);
    }

    /// <summary>Bus address from the definition: its own CAN ids when it has them, else the DDT address tables; K-line from the functional address.</summary>
    internal static EcuAddress? DeriveAddress(EcuFile ecu)
    {
        var name = ecu.EcuName;
        switch (ecu.Protocol)
        {
            case CoreProtocol.Can:
            {
                CanSpeed speed = ecu.BaudRate == 250000 ? CanSpeed.Kbps250 : CanSpeed.Default;
                byte? func = byte.TryParse(ecu.FuncAddr, System.Globalization.NumberStyles.HexNumber, null, out var f) ? f : null;
                if (IsId(ecu.SendId) && IsId(ecu.RecvId))
                    return EcuAddress.Can(name, ecu.SendId, ecu.RecvId, speed) with { FuncAddress = func };
                var a = CanAddressing.Default.ToCanAddress(name, ecu.FuncAddr);
                return a is null ? null : a with { Speed = speed };
            }
            case CoreProtocol.Kwp2000:
                return byte.TryParse(ecu.FuncAddr, System.Globalization.NumberStyles.HexNumber, null, out _)
                    ? EcuAddress.KLine(name, ecu.FastInit ? CommsProtocol.Kwp2000Fast : CommsProtocol.Kwp2000Slow, ecu.FuncAddr) : null;
            case CoreProtocol.Iso8 or CoreProtocol.Iso:
                return byte.TryParse(ecu.FuncAddr, System.Globalization.NumberStyles.HexNumber, null, out _)
                    ? EcuAddress.KLine(name, CommsProtocol.Iso8, ecu.FuncAddr) : null;
            case CoreProtocol.DoIp:
                return uint.TryParse(ecu.FuncAddr, System.Globalization.NumberStyles.HexNumber, null, out var logical)
                    ? new EcuAddress { Name = name, Protocol = CommsProtocol.DoIp, TxId = logical } : null;
            default: return null;
        }
    }

    private static bool IsId(string s) => !string.IsNullOrWhiteSpace(s) && s.All(Uri.IsHexDigit) && s.Trim('0').Length > 0 && (s.Length <= 3 || s.Length == 8);

    private void RaiseTransport()
    {
        OnPropertyChanged(nameof(Transport)); OnPropertyChanged(nameof(IsConnected));
        Changed?.Invoke();
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(IsLoaded)); OnPropertyChanged(nameof(Href)); OnPropertyChanged(nameof(Ecu)); OnPropertyChanged(nameof(Layout));
        OnPropertyChanged(nameof(Address)); OnPropertyChanged(nameof(DisplayName)); OnPropertyChanged(nameof(Transport)); OnPropertyChanged(nameof(IsConnected));
        Changed?.Invoke();
    }
}
