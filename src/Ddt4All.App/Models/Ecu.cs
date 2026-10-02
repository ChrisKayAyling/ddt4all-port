namespace Ddt4All.App.Models;

/// <summary>Lightweight catalog row. The real Core index maps onto this (see docs/APP_ARCHITECTURE.md).</summary>
public sealed class EcuEntry
{
    public required string Id { get; init; }          // stable key (file name / hash)
    public required string Name { get; init; }
    public required string Address { get; init; }      // hex, e.g. "7A"
    public required string Protocol { get; init; }     // CAN, KWP2000, ISO8, DoIP ...
    public required string Project { get; init; }      // vehicle project code, e.g. "X98"
    public string ProjectName { get; init; } = "";
    /// <summary>Canonical vehicle code the project maps to ("FB" for project "xFB"); empty when the ECU has no project.</summary>
    public string VehicleCode { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    /// <summary>Vehicle name for display, falling back to the raw project code.</summary>
    public string VehicleLabel => ProjectName.Length > 0 ? ProjectName : Project;
    /// <summary>Functional group ("Injection", "UCH"...), empty when unknown.</summary>
    public string Group { get; init; } = "";
    public string Supplier { get; init; } = "";
    public string Version { get; init; } = "";
    public string File { get; init; } = "";
    public int VariantCount { get; init; } = 1;
    /// <summary>Pre-lowercased haystack so searching is one IndexOf per token.</summary>
    public string SearchKey { get; init; } = "";
}

public sealed record VehicleInfo(string Code, string Manufacturer, string Model, int EcuCount);

public sealed record GroupHeaderRow(string Title, int Count);

public sealed class EcuCatalog
{
    public static EcuCatalog Empty { get; } = new([], [], []);
    public EcuCatalog(IReadOnlyList<EcuEntry> entries, IReadOnlyList<string> projects, IReadOnlyList<string> protocols)
    {
        Entries = entries; Projects = projects; Protocols = protocols;
        Vehicles = entries.Where(e => e.VehicleCode.Length > 0)
            .GroupBy(e => e.VehicleCode)
            .Select(g => { var f = g.First(); return new VehicleInfo(g.Key, f.Manufacturer, f.Model, g.Select(x => x.Id).Distinct().Count()); })
            .OrderBy(v => v.Manufacturer, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Model, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        // "Other" (training / tool / unknown platforms) last
        Manufacturers = Vehicles.Select(v => v.Manufacturer).Distinct()
            .OrderBy(m => m == Services.VehicleCatalog.OtherManufacturer).ThenBy(m => m, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    /// <summary>Distinct ECU definitions (the entry list has one row per ECU per vehicle).</summary>
    public int UniqueEcuCount => _unique ??= Entries.Select(e => e.Id).Distinct().Count();
    private int? _unique;
    public IReadOnlyList<VehicleInfo> Vehicles { get; }
    public IReadOnlyList<string> Manufacturers { get; }
    public IReadOnlyList<EcuEntry> Entries { get; }
    public IReadOnlyList<string> Projects { get; }
    public IReadOnlyList<string> Protocols { get; }
}

public sealed record DtcItem(string Code, string Description, string Status, bool IsActive);

public readonly record struct CanFrame(uint Id, byte[] Data, long Timestamp);
