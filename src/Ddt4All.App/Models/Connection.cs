namespace Ddt4All.App.Models;

public enum ConnectionState { Disconnected, Connecting, Connected, Failed }
public enum TransportKind { Serial, Tcp }

public sealed record SerialPortInfo(string Name, string Description)
{
    public override string ToString() => string.IsNullOrEmpty(Description) ? Name : $"{Name} - {Description}";
}

/// <summary>Adapter family (from the Python options.get_device_settings defaults + main_window_options buttons).</summary>
public sealed record DeviceProfile(string Id, string Name, int DefaultBaud, int TimeoutSec, bool RtsCts, bool SupportsSerial = true, bool SupportsTcp = true, string? Hint = null)
{
    public override string ToString() => Name;

    public static IReadOnlyList<DeviceProfile> All { get; } =
    [
        new("elm327", "Generic ELM327", 38400, 5, false, Hint: "Bluetooth/USB clones. 38400 baud is the safest default."),
        new("vlinker", "vLinker (FS / MC / BM)", 38400, 3, false, Hint: "Wi-Fi: 192.168.0.10:35000."),
        new("vgate", "vGate iCar Pro", 115200, 2, false),
        new("obdlink", "OBDLink SX / MX", 115200, 2, true, Hint: "Uses STN firmware; full-speed UART switch is negotiated automatically."),
        new("obdlink_ex", "OBDLink EX", 115200, 2, true),
        new("els27", "ELS27 V5", 38400, 4, false, Hint: "CAN on pins 12-13."),
        new("derlek_usb_diag2", "Derlek USB-Diag2", 115200, 3, false, SupportsTcp: false),
        new("derlek_usb_diag3", "Derlek USB-Diag3", 115200, 3, false, SupportsTcp: false),
        new("doip", "DoIP (Ethernet)", 0, 5, false, SupportsSerial: false, Hint: "Default target 192.168.0.12:13400."),
    ];

    public static DeviceProfile ById(string? id) => All.FirstOrDefault(p => p.Id == id) ?? All[0];
}

public sealed record ConnectionRequest(
    TransportKind Transport, string? Port, int Baud, string? Host, int TcpPort,
    DeviceProfile Profile, bool Simulation, bool HardwareFlowControl);

public sealed record AdapterInfo(string Name, string Firmware, double? Voltage, string Endpoint);
