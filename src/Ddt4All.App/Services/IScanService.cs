using Ddt4All.Comms;
using Ddt4All.Comms.Scanning;

namespace Ddt4All.App.Services;

public enum FoundEcuMatch { None, Exact, Approximate }

/// <summary>One ECU that answered during an auto-scan, with the database entry it was matched to (if any).</summary>
public sealed record FoundEcu(
    string FunctionalAddress, string AddressName, string Protocol,
    string DiagVersion, string Supplier, string Soft, string Version,
    FoundEcuMatch Match, string? Href, string? EcuName, string? Group,
    EcuAddress BusAddress)
{
    public bool HasDefinition => Match != FoundEcuMatch.None && Href is not null;
}

public sealed class ScanRequest
{
    /// <summary>Restrict to the addresses used by one vehicle project (null = every address of the database).</summary>
    public string? Project { get; init; }
    /// <summary>Also probe K-line (KWP2000 fast init) addresses; slow, off by default.</summary>
    public bool IncludeKLine { get; init; }
}

public interface IScanService
{
    /// <summary>True when a database and a connected adapter are available.</summary>
    bool CanScan { get; }
    /// <summary>Probes every candidate address; <paramref name="onFound"/> is called (any thread) as ECUs answer.</summary>
    Task<IReadOnlyList<FoundEcu>> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, Action<FoundEcu>? onFound, CancellationToken ct);
}
