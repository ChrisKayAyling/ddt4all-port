using System.Text;

namespace Ddt4All.Plugins.Vehicles;

/// <summary>CRC-16/X-25 helper shared by the VIN calculator and the Clio III EPS VIN writer.</summary>
public static class VinCrc
{
    /// <summary>CRC-16/X-25 (poly 0x1021 reflected, init/xorout 0xFFFF; crcmod "x-25").</summary>
    public static ushort Crc16X25(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0x8408) : (ushort)(crc >> 1);
        }
        return (ushort)(crc ^ 0xFFFF);
    }

    /// <summary>Python <c>calc_crc</c>: CRC of the VIN bytes as 4 upper-case hex digits with the two bytes swapped.</summary>
    public static string Calc(string vin)
    {
        ushort crc = Crc16X25(Encoding.UTF8.GetBytes(vin));
        string hex = crc.ToString("X4");
        return hex[2..4] + hex[0..2];
    }
}

/// <summary>"CRC calculator" (category VIN): no ECU, no hardware.</summary>
public sealed class VinCrcPlugin : IPlugin
{
    public PluginInfo Info { get; } = new("vin-crc", "CRC calculator", "VIN", "Renault (VIN based immobiliser data)")
    {
        Description = "Computes the CRC-16 (X-25, bytes swapped) of a VIN, as used when writing the VIN to some Renault ECUs.",
        RequiresHardware = false,
    };

    public IReadOnlyList<PluginAction> Actions { get; } =
    [
        new PluginAction("calculate", "Calculate CRC")
        {
            Description = "Pure calculation; nothing is sent to a vehicle.",
            StepCount = 1,
            Parameters = [new PluginParameter("vin", "VIN") { Description = "Vehicle identification number (upper-cased).", MaxLength = 32 }],
        },
    ];

    public ValueTask<PluginResult> RunAsync(PluginAction action, PluginContext ctx)
    {
        string vin = ctx.Param("vin").ToUpperInvariant();
        ctx.Step("Computing CRC");
        string crc = VinCrc.Calc(vin);
        ctx.Info($"VIN {vin} -> CRC {crc}");
        return new(PluginResult.Ok($"CRC {crc}", "crc", new KeyValuePair<string, string>("CRC", crc)));
    }
}
