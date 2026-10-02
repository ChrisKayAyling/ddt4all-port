using System.Text.Json.Serialization;

namespace Ddt4All.App.Services;

/// <summary>Persisted user settings (JSON, source-generated serializer, no reflection).</summary>
public sealed class AppSettings
{
    public string Language { get; set; } = "system";   // "system" or locale code (fr, cs-CZ, ...)
    public string Theme { get; set; } = "system";      // system | light | dark
    public string Accent { get; set; } = "blue";
    public string LogLevel { get; set; } = "Information";

    public string? EcuZipPath { get; set; }
    public string? EcuDirectory { get; set; }
    public string? GraphicsDirectory { get; set; }

    public SerialDefaults Serial { get; set; } = new();
    public ConnectionSettings Connection { get; set; } = new();

    public int RefreshRateMs { get; set; } = 5;
    public int CanTimeoutMs { get; set; } = 0;           // 0 = auto
    public int ConnectionTimeoutSec { get; set; } = 10;
    public int ReadTimeoutSec { get; set; } = 5;
    public int MaxReconnectAttempts { get; set; } = 3;

    public string CarlistSortMode { get; set; } = "code"; // code | name
    public string? LastSelectedVehicle { get; set; }
    public string? LastOpenedEcu { get; set; }

    public bool NavExpanded { get; set; } = true;
    public WindowSettings Window { get; set; } = new();
}

public sealed class SerialDefaults
{
    public int BaudRate { get; set; } = 38400;
    public bool HardwareFlowControl { get; set; }
    public bool AutoDetectDevices { get; set; } = true;
}

public sealed class ConnectionSettings
{
    public string Transport { get; set; } = "serial";   // serial | tcp
    public string? Port { get; set; }
    public string ProfileId { get; set; } = "elm327";
    public int Baud { get; set; } = 38400;
    public string Host { get; set; } = "192.168.0.10";
    public int TcpPort { get; set; } = 35000;
    public bool Simulation { get; set; }
}

public sealed class WindowSettings
{
    public double Width { get; set; } = 1280;
    public double Height { get; set; } = 780;
    public int? X { get; set; }
    public int? Y { get; set; }
    public bool Maximized { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class LocaleJsonContext : JsonSerializerContext;
