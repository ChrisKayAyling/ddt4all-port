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
    public string Supplier { get; init; } = "";
    public string Version { get; init; } = "";
    public string File { get; init; } = "";
    public int VariantCount { get; init; } = 1;
    /// <summary>Pre-lowercased haystack so searching is one IndexOf per token.</summary>
    public string SearchKey { get; init; } = "";
}

public sealed record GroupHeaderRow(string Title, int Count);

public sealed class EcuCatalog
{
    public static EcuCatalog Empty { get; } = new([], [], []);
    public EcuCatalog(IReadOnlyList<EcuEntry> entries, IReadOnlyList<string> projects, IReadOnlyList<string> protocols)
    { Entries = entries; Projects = projects; Protocols = protocols; }
    public IReadOnlyList<EcuEntry> Entries { get; }
    public IReadOnlyList<string> Projects { get; }
    public IReadOnlyList<string> Protocols { get; }
}

public sealed record DtcItem(string Code, string Description, string Status, bool IsActive);

public readonly record struct CanFrame(uint Id, byte[] Data, long Timestamp);
