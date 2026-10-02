using System.Text.Json;

namespace Ddt4All.App.Services;

/// <summary>A vehicle (platform) an ECU definition applies to, e.g. code "FB" = Renault / "Megane IV".</summary>
public sealed record Vehicle(string Code, string Manufacturer, string Model, string Name);

/// <summary>
/// Maps the project codes found in ECU definitions ("xFB", "HFEPh2", ...) to a vehicle manufacturer and model.
/// Names come from the DDT4ALL projects list ("[FB] - Renault Megane IV"); the manufacturer is the first word.
/// </summary>
public static class VehicleCatalog
{
    public const string OtherManufacturer = "Other";
    private static readonly Lazy<Dictionary<string, Vehicle>> Map = new(Load);

    public static IReadOnlyCollection<Vehicle> All => Map.Value.Values;

    /// <summary>Resolves a project code as stored in ecu.zip; null for empty/unknown codes.</summary>
    public static Vehicle? Resolve(string? projectCode)
    {
        if (string.IsNullOrWhiteSpace(projectCode)) return null;
        var map = Map.Value;
        var c = projectCode.Trim().ToLowerInvariant();
        if (map.TryGetValue(c, out var v)) return v;
        // codes differ in an 'x' platform prefix and/or a phase suffix between the list and the definitions: xFB/FB, HFEPh2, xZHPh2
        if (c.Length > 1 && c[0] == 'x' && map.TryGetValue(c[1..], out v)) return v;
        if (map.TryGetValue('x' + c, out v)) return v;          // list uses the 'x' form, definition the bare one
        foreach (var suffix in new[] { "ph1", "ph2", "ph3" })
            if (c.EndsWith(suffix, StringComparison.Ordinal))
            {
                var b = c[..^suffix.Length];
                if (map.TryGetValue(b, out v) || (b.Length > 1 && b[0] == 'x' && map.TryGetValue(b[1..], out v))) return v;
            }
        return null;
    }

    /// <summary>A vehicle for any non-empty code: the known one, or a placeholder under "Other" so no ECU is hidden.</summary>
    public static Vehicle ResolveOrOther(string projectCode) =>
        Resolve(projectCode) ?? new Vehicle(projectCode, OtherManufacturer, projectCode, projectCode);

    internal static Vehicle Parse(string code, string name)
    {
        name = name.Trim();
        var space = name.IndexOf(' ');
        var first = space > 0 ? name[..space] : name;
        var rest = space > 0 ? name[(space + 1)..].Trim() : "";
        return first switch
        {
            "Rsm" => new Vehicle(code, "Renault Samsung", rest.Length > 0 ? rest : name, name),
            "Contrôle" or "Controle" or "Ddt_Training" or "Partners" => new Vehicle(code, OtherManufacturer, name, name),
            _ => new Vehicle(code, first, rest.Length > 0 ? rest : name, name),
        };
    }

    private static Dictionary<string, Vehicle> Load()
    {
        var map = new Dictionary<string, Vehicle>(StringComparer.Ordinal);
        try
        {
            using var s = typeof(VehicleCatalog).Assembly.GetManifestResourceStream("Ddt4All.App.Resources.vehicles.json");
            if (s is null) return map;
            using var doc = JsonDocument.Parse(s);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var code = e.GetProperty("code").GetString() ?? "";
                var name = e.GetProperty("name").GetString() ?? code;
                if (code.Length > 0) map[code.ToLowerInvariant()] = Parse(code, name);
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { /* no vehicle names: filters just stay empty */ }
        return map;
    }
}
