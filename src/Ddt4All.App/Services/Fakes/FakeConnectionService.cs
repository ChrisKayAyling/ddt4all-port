using CommunityToolkit.Mvvm.ComponentModel;
using Ddt4All.App.Localization;
using Ddt4All.App.Models;
using Ddt4All.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Ddt4All.App.Services.Fakes;

/// <summary>Simulated ELM adapter so the UI is fully exercisable without hardware or Comms.</summary>
public sealed partial class FakeConnectionService : ObservableObject, IConnectionService
{
    private readonly ILogger<FakeConnectionService>? _log;
    public FakeConnectionService(ILogger<FakeConnectionService>? log = null) { _log = log; }

    [ObservableProperty] private ConnectionState _state;
    [ObservableProperty] private string _statusText = Loc.T("Disconnected");
    [ObservableProperty] private AdapterInfo? _adapter;
    public IEcuTransport? Transport { get; private set; }
    public Ddt4All.Comms.IAddressableTransport? AddressableTransport => null;

    public async Task<IReadOnlyList<SerialPortInfo>> ListPortsAsync(CancellationToken ct = default)
    {
        await Task.Delay(150, ct);
        var list = new List<SerialPortInfo>();
        if (OperatingSystem.IsWindows())
        { list.Add(new("COM3", "USB Serial Port")); list.Add(new("COM5", "Standard Serial over Bluetooth link")); }
        else
        {
            try
            {
                if (Directory.Exists("/dev"))
                    foreach (var f in Directory.EnumerateFiles("/dev").Where(f => Path.GetFileName(f).StartsWith("ttyUSB") || Path.GetFileName(f).StartsWith("ttyACM") || Path.GetFileName(f).StartsWith("rfcomm") || Path.GetFileName(f).StartsWith("cu.usb")).Order())
                        list.Add(new(f, "Serial device"));
            }
            catch { /* ignore */ }
            if (list.Count == 0) { list.Add(new("/dev/ttyUSB0", "FTDI USB Serial (simulated)")); list.Add(new("/dev/rfcomm0", "Bluetooth ELM327 (simulated)")); }
        }
        return list;
    }

    public async Task ConnectAsync(ConnectionRequest r, CancellationToken ct = default)
    {
        State = ConnectionState.Connecting;
        StatusText = Loc.T("Connecting...");
        try
        {
            await Task.Delay(700, ct);
            if (!r.Simulation && r.Transport == TransportKind.Serial && string.IsNullOrEmpty(r.Port))
                throw new InvalidOperationException(Loc.T("Please select a communication port"));
            var endpoint = r.Transport == TransportKind.Tcp ? $"{r.Host}:{r.TcpPort}" : r.Port ?? "simulator";
            Adapter = new AdapterInfo(r.Simulation ? "ECU simulator" : r.Profile.Name, "ELM327 v1.5", 12.4, endpoint);
            Transport = new FakeTransport();
            State = ConnectionState.Connected;
            StatusText = Loc.T("Connected");
            _log?.LogInformation("Connected (fake) to {Endpoint} using {Profile}", endpoint, r.Profile.Name);
        }
        catch (OperationCanceledException) { State = ConnectionState.Disconnected; StatusText = Loc.T("Disconnected"); throw; }
        catch (Exception ex)
        {
            State = ConnectionState.Failed; StatusText = ex.Message; Adapter = null; Transport = null;
            _log?.LogWarning("Connection failed: {Message}", ex.Message);
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        await Task.Delay(80);
        Transport = null; Adapter = null;
        State = ConnectionState.Disconnected; StatusText = Loc.T("Disconnected");
        _log?.LogInformation("Disconnected");
    }

    public async Task<string> SendRawAsync(string command, CancellationToken ct = default)
    {
        if (State != ConnectionState.Connected) throw new InvalidOperationException(Loc.T("Not connected"));
        await Task.Delay(30 + Math.Abs(command.GetHashCode() % 40), ct);
        var c = command.Trim().ToUpperInvariant();
        if (c.StartsWith("AT") || c.StartsWith("ST"))
            return c switch
            {
                "ATZ" or "ATWS" => "ELM327 v1.5",
                "ATI" => "ELM327 v1.5",
                "ATRV" => "12.4V",
                "AT@1" => "OBDII to RS232 Interpreter",
                _ => "OK",
            };
        var bytes = FakeTransport.ParseHex(c);
        var resp = FakeTransport.Respond(bytes);
        return resp is null ? "NO DATA" : string.Join(' ', resp.Select(b => b.ToString("X2")));
    }

    private sealed class FakeTransport : IEcuTransport
    {
        public static byte[] ParseHex(string s)
        {
            var clean = new string(s.Where(Uri.IsHexDigit).ToArray());
            if (clean.Length % 2 == 1) clean = "0" + clean;
            return Convert.FromHexString(clean);
        }

        public static byte[]? Respond(byte[] req)
        {
            if (req.Length == 0) return null;
            if (req[0] == 0x3E) return [0x7E, 0x00];
            if (req[0] == 0x22 && req.Length >= 3) return [0x62, req[1], req[2], 0x56, 0x46, 0x31, 0x52, 0x4C, 0x30, 0x30];
            if (req[0] == 0x19) return [0x59, 0x02, 0xFF, 0x03, 0x01, 0x28, 0x2F];
            if (req[0] == 0x10) return [0x50, req.Length > 1 ? req[1] : (byte)1, 0x00, 0x32, 0x01, 0xF4];
            return [0x7F, req[0], 0x11];
        }

        public ValueTask<byte[]> RequestAsync(ReadOnlyMemory<byte> request, CancellationToken ct = default)
            => ValueTask.FromResult(Respond(request.ToArray()) ?? []);
    }
}
