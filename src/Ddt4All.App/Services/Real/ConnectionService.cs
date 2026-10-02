using CommunityToolkit.Mvvm.ComponentModel;
using Ddt4All.App.Localization;
using Ddt4All.App.Models;
using Ddt4All.Comms;
using Ddt4All.Comms.Diagnostics;
using Ddt4All.Comms.DoIp;
using Ddt4All.Comms.Elm;
using Ddt4All.Comms.Serial;
using Ddt4All.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Ddt4All.App.Services.Real;

/// <summary>
/// Adapter lifecycle over Ddt4All.Comms: serial / Bluetooth-serial / WiFi (TCP) ELM327 family adapters via <see cref="ElmTransport"/>,
/// DoIP via <see cref="DoIpClient"/>, and a built-in simulator (<see cref="SimulatedVehicle"/>) for demos without hardware.
/// </summary>
public sealed partial class ConnectionService : ObservableObject, IConnectionService, IAsyncDisposable, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IEcuCatalogService _catalog;
    private readonly ILogger<ConnectionService>? _log;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private SimulatedVehicle? _sim;

    public ConnectionService(ISettingsService settings, IEcuCatalogService catalog, ILogger<ConnectionService>? log = null)
    {
        _settings = settings; _catalog = catalog; _log = log;
        _statusText = Loc.T("Disconnected");
    }

    [ObservableProperty] private ConnectionState _state;
    [ObservableProperty] private string _statusText;
    [ObservableProperty] private AdapterInfo? _adapter;
    public IAddressableTransport? AddressableTransport { get; private set; }
    public IEcuTransport? Transport => AddressableTransport;
    /// <summary>The ELM driver when the connection is an ELM adapter (raw AT commands, sniffer); null for DoIP.</summary>
    public ElmTransport? Elm => AddressableTransport as ElmTransport;
    /// <summary>The simulated car while in simulation mode (tests, diagnostics).</summary>
    public SimulatedVehicle? Simulator => _sim;

    public async Task<IReadOnlyList<SerialPortInfo>> ListPortsAsync(CancellationToken ct = default)
    {
        var ports = await Task.Run(() => PortEnumerator.GetPorts(includeNetworkDefaults: false), ct).ConfigureAwait(false);
        return ports.Select(p => new SerialPortInfo(p.Name, p.Description)).ToList();
    }

    public async Task ConnectAsync(ConnectionRequest r, CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await DisconnectLockedAsync().ConfigureAwait(false);
            State = ConnectionState.Connecting;
            StatusText = Loc.T("Connecting...");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var timeout = _settings.Current.ConnectionTimeoutSec;
            if (timeout > 0 && !r.Simulation) cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(timeout, 3)));
            try
            {
                if (r.Simulation) await ConnectSimulationAsync(cts.Token).ConfigureAwait(false);
                else if (r.Profile.Id == "doip") await ConnectDoIpAsync(r, cts.Token).ConfigureAwait(false);
                else await ConnectElmAsync(r, cts.Token).ConfigureAwait(false);
                State = ConnectionState.Connected;
                StatusText = Loc.T("Connected");
                _log?.LogInformation("Connected to {Endpoint} ({Adapter})", Adapter?.Endpoint, Adapter?.Name);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await DisconnectLockedAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                await DisconnectLockedAsync().ConfigureAwait(false);
                var msg = ex is OperationCanceledException ? Loc.T("The adapter did not answer in time.") : ex.Message;
                State = ConnectionState.Failed; StatusText = msg;
                _log?.LogWarning("Connection failed: {Message}", ex.Message);
                throw new InvalidOperationException(msg, ex);
            }
        }
        finally { _lifecycle.Release(); }
    }

    private async Task ConnectSimulationAsync(CancellationToken ct)
    {
        try { await _catalog.LoadAsync(ct).ConfigureAwait(false); } catch (Exception ex) when (ex is not OperationCanceledException) { _log?.LogDebug(ex, "Simulator without ECU database"); }
        var sim = await Task.Run(() => SimulatedVehicle.Create(_catalog.Database, CanAddressing.Default), ct).ConfigureAwait(false);
        _sim = sim;
        var elm = await ElmTransport.ConnectAsync(sim.Host, BuildOptions(null, ElmProfiles.Elm327, null), ct).ConfigureAwait(false);
        AddressableTransport = elm;
        Adapter = new AdapterInfo(Loc.T("ECU simulator"), elm.Info.Version, await elm.ReadVoltageAsync(ct).ConfigureAwait(false), "simulator");
    }

    private async Task ConnectElmAsync(ConnectionRequest r, CancellationToken ct)
    {
        string endpoint;
        if (r.Transport == TransportKind.Tcp)
        {
            if (string.IsNullOrWhiteSpace(r.Host)) throw new InvalidOperationException(Loc.T("Please enter the adapter address"));
            endpoint = $"{r.Host.Trim()}:{r.TcpPort}";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(r.Port)) throw new InvalidOperationException(Loc.T("Please select a communication port"));
            endpoint = r.Port;
        }
        var profile = MapProfile(r.Profile);
        var opt = BuildOptions(r.Transport == TransportKind.Serial ? r.Baud : null, profile, r.HardwareFlowControl);
        var link = await SerialLinkFactory.OpenAsync(endpoint, new SerialLinkOptions
        {
            BaudRate = r.Baud > 0 ? r.Baud : profile.DefaultBaud,
            RtsCts = r.HardwareFlowControl || profile.RtsCts,
            DsrDtr = profile.DsrDtr,
        }, ct).ConfigureAwait(false);
        ElmTransport elm;
        try { elm = await ElmTransport.ConnectAsync(link, opt, ct).ConfigureAwait(false); }
        catch { await link.DisposeAsync().ConfigureAwait(false); throw; }
        AddressableTransport = elm;
        var name = elm.Profile.DisplayName;
        var fw = elm.Info.StnId is { Length: > 0 } stn ? $"{elm.Info.Version} / {stn}" : elm.Info.Version;
        Adapter = new AdapterInfo(name, fw, await SafeVoltageAsync(elm, ct).ConfigureAwait(false), endpoint);
    }

    private async Task ConnectDoIpAsync(ConnectionRequest r, CancellationToken ct)
    {
        var host = string.IsNullOrWhiteSpace(r.Host) ? "192.168.0.12" : r.Host.Trim();
        var client = new DoIpClient(new DoIpOptions { Host = host, Port = r.TcpPort > 0 ? r.TcpPort : 13400 });
        try { await client.ConnectAsync(ct).ConfigureAwait(false); }
        catch { await client.DisposeAsync().ConfigureAwait(false); throw; }
        AddressableTransport = client;
        Adapter = new AdapterInfo("DoIP gateway", $"entity 0x{client.EntityAddress:X4}", null, $"{host}:{r.TcpPort}");
    }

    private static async Task<double?> SafeVoltageAsync(ElmTransport elm, CancellationToken ct)
    {
        try { return await elm.ReadVoltageAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    private ElmOptions BuildOptions(int? baud, ElmProfile profile, bool? rtsCts)
    {
        var s = _settings.Current;
        var o = new ElmOptions
        {
            Profile = profile,
            InitialBaud = baud is > 0 ? baud : null,
            CanTimeoutMs = s.CanTimeoutMs,
            Trace = line => _log?.LogTrace("{Line}", line),
        };
        if (s.ReadTimeoutSec > 0) o.RequestTimeout = TimeSpan.FromSeconds(s.ReadTimeoutSec);
        return o;
    }

    internal static ElmProfile MapProfile(DeviceProfile p) => p.Id switch
    {
        "vlinker" => ElmProfiles.VLinker,
        "vgate" => ElmProfiles.VGate,
        "obdlink" => ElmProfiles.ObdLinkSx,
        "obdlink_ex" => ElmProfiles.ObdLinkEx,
        "els27" => ElmProfiles.Els27V5,
        "derlek_usb_diag2" => ElmProfiles.DerlekDiag2,
        "derlek_usb_diag3" => ElmProfiles.DerlekDiag3,
        _ => ElmProfiles.Elm327,
    };

    public async Task DisconnectAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await DisconnectLockedAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
        _log?.LogInformation("Disconnected");
    }

    private async Task DisconnectLockedAsync()
    {
        var t = AddressableTransport; var sim = _sim;
        AddressableTransport = null; _sim = null; Adapter = null;
        if (t is not null) { try { await t.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { _log?.LogDebug(ex, "Dispose transport"); } }
        if (sim is not null) { try { await sim.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { _log?.LogDebug(ex, "Dispose simulator"); } }
        State = ConnectionState.Disconnected;
        StatusText = Loc.T("Disconnected");
    }

    public async Task<string> SendRawAsync(string command, CancellationToken ct = default)
    {
        var t = AddressableTransport;
        if (State != ConnectionState.Connected || t is null) throw new InvalidOperationException(Loc.T("Not connected"));
        var c = command.Trim();
        if (c.StartsWith("AT", StringComparison.OrdinalIgnoreCase) || c.StartsWith("ST", StringComparison.OrdinalIgnoreCase))
        {
            if (t is not ElmTransport elm) throw new NotSupportedException(Loc.T("AT commands need an ELM327 adapter."));
            var reply = await elm.SendAtAsync(c, ct).ConfigureAwait(false);
            return reply.TimedOut ? throw new TimeoutException(Loc.T("The adapter did not answer in time.")) : reply.Text;
        }
        if (!Ddt4All.Comms.Hex.TryParse(new string(c.Where(Uri.IsHexDigit).ToArray()), out var payload) || payload.Length == 0)
            throw new FormatException(Loc.T("Invalid hex payload (expected whole bytes, e.g. 22 F1 90)"));
        if (t.CurrentEcu is null)
            throw new InvalidOperationException(Loc.T("No ECU addressed. Open an ECU first (ECU Browser or Scan)."));
        var rsp = await t.RequestAsync(payload, ct).ConfigureAwait(false);
        return FormatResponse(rsp);
    }

    internal static string FormatResponse(byte[] rsp)
    {
        if (rsp.Length == 0) return "NO DATA";
        var hex = Ddt4All.Comms.Hex.ToString(rsp, spaced: true);
        return DiagnosticExtensions.TryGetNegativeResponse(rsp, out _, out var nrc) ? $"{hex}  ({NegativeResponses.Describe(nrc)})" : hex;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);

    /// <summary>Needed because the DI container only disposes services that implement <see cref="IDisposable"/> when disposed synchronously.</summary>
    public void Dispose() => Task.Run(DisposeAsync).GetAwaiter().GetResult();
}
