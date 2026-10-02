using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ddt4All.Comms;

namespace Ddt4All.App.Services.Real;

/// <summary>
/// DDT functional address -> CAN id tables (the "All" project of the Python projects.json: dnat/snat and their 29-bit
/// variants). Tester sends to <c>dnat[addr]</c>, the ECU answers on <c>snat[addr]</c>.
/// </summary>
public sealed class CanAddressing
{
    public Dictionary<string, string> Dnat { get; init; } = new();
    public Dictionary<string, string> Snat { get; init; } = new();
    public Dictionary<string, string> DnatExt { get; init; } = new();
    public Dictionary<string, string> SnatExt { get; init; } = new();
    public Dictionary<string, string> Names { get; init; } = new();

    private static CanAddressing? _default;
    public static CanAddressing Default => _default ??= Load();

    private static CanAddressing Load()
    {
        using var s = typeof(CanAddressing).Assembly.GetManifestResourceStream("Ddt4All.App.Resources.can-addressing.json");
        if (s is null) return new CanAddressing();
        var raw = JsonSerializer.Deserialize(s, CanAddressingJsonContext.Default.CanAddressing) ?? new CanAddressing();
        return new CanAddressing
        {
            Dnat = new(raw.Dnat, StringComparer.OrdinalIgnoreCase), Snat = new(raw.Snat, StringComparer.OrdinalIgnoreCase),
            DnatExt = new(raw.DnatExt, StringComparer.OrdinalIgnoreCase), SnatExt = new(raw.SnatExt, StringComparer.OrdinalIgnoreCase),
            Names = new(raw.Names, StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>Python <c>addr_exist</c>.</summary>
    public bool Exists(string addr) => Dnat.ContainsKey(addr) || DnatExt.ContainsKey(addr);

    public string NameOf(string addr) => Names.TryGetValue(addr, out var n) ? n : "";

    /// <summary>(tx, rx) CAN ids as hex text for a functional address (11-bit table first, then the 29-bit one), or null.</summary>
    public (string Tx, string Rx)? Lookup(string addr)
    {
        if (Dnat.TryGetValue(addr, out var tx) && Snat.TryGetValue(addr, out var rx)) return (tx, rx);
        if (DnatExt.TryGetValue(addr, out tx) && SnatExt.TryGetValue(addr, out rx)) return (tx, rx);
        return null;
    }

    /// <summary>CAN bus address for a functional address, or null when it is not mapped.</summary>
    public EcuAddress? ToCanAddress(string name, string addr)
    {
        if (Lookup(addr) is not { } ids) return null;
        var a = EcuAddress.Can(name, ids.Tx, ids.Rx);
        return byte.TryParse(addr, System.Globalization.NumberStyles.HexNumber, null, out var f) ? a with { FuncAddress = f } : a;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CanAddressing))]
internal sealed partial class CanAddressingJsonContext : JsonSerializerContext;
