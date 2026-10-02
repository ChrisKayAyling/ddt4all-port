using Ddt4All.Core.Ecu;

namespace Ddt4All.Core.Database;

/// <summary>
/// Lightweight handle to one ECU definition of an <see cref="EcuDatabase"/> (no allocation until a property is read).
/// </summary>
public readonly struct EcuEntry : IEquatable<EcuEntry>
{
    private readonly EcuIndex? _ix;

    internal EcuEntry(EcuIndex ix, int index) { _ix = ix; Index = index; }

    /// <summary>Position inside the database (stable for a given index).</summary>
    public int Index { get; }

    /// <summary>False for <c>default</c>.</summary>
    public bool IsValid => _ix != null;

    /// <summary>File name inside ecu.zip (e.g. "UCH_X95.json"); also used as the key to load the ECU.</summary>
    public string Href => _ix!.Str(_ix.Href[Index]);
    /// <summary>ECU name ("ecuname" of the definition).</summary>
    public string EcuName => _ix!.Str(_ix.Name[Index]);
    /// <summary>Functional group ("Injection", "UCH"...).</summary>
    public string Group => _ix!.Str(_ix.Group[Index]);
    /// <summary>Functional address as hex text ("26").</summary>
    public string Address => _ix!.Str(_ix.Addr[Index]);
    /// <summary>Protocol, classified by substring (CAN, KWP2000, ISO8, DoIP, Unknown).</summary>
    public EcuProtocol Protocol => (EcuProtocol)_ix!.Protocol[Index];

    /// <summary>Project codes (upper-case) this ECU belongs to.</summary>
    public IReadOnlyList<string> Projects
    {
        get
        {
            var ix = _ix!;
            int s = ix.ProjStart[Index], e = ix.ProjStart[Index + 1];
            if (s == e) return Array.Empty<string>();
            var a = new string[e - s];
            for (int i = s; i < e; i++) a[i - s] = ix.Str(ix.ProjectStr[ix.ProjIds[i]]);
            return a;
        }
    }

    /// <summary>Number of autoident records.</summary>
    public int AutoIdentCount => _ix!.AiStart[Index + 1] - _ix.AiStart[Index];

    /// <summary>Autoident record <paramref name="i"/> (0-based).</summary>
    public AutoIdent GetAutoIdent(int i)
    {
        var ix = _ix!;
        int k = ix.AiStart[Index] + i;
        if ((uint)i >= (uint)AutoIdentCount) throw new ArgumentOutOfRangeException(nameof(i));
        return new AutoIdent(ix.Str(ix.AiDiag[k]), ix.Str(ix.AiSupplier[k]), ix.Str(ix.AiSoft[k]), ix.Str(ix.AiVersion[k]));
    }

    /// <summary>All autoident records.</summary>
    public IEnumerable<AutoIdent> AutoIdents
    {
        get
        {
            int n = AutoIdentCount;
            for (int i = 0; i < n; i++) yield return GetAutoIdent(i);
        }
    }

    /// <inheritdoc />
    public bool Equals(EcuEntry other) => ReferenceEquals(_ix, other._ix) && Index == other.Index;
    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is EcuEntry e && Equals(e);
    /// <inheritdoc />
    public override int GetHashCode() => Index;
    /// <inheritdoc />
    public override string ToString() => _ix == null ? "<none>" : $"[{Group}] {EcuName} ({Href})";
}

/// <summary>Filter for <see cref="EcuDatabase.Search(in EcuSearch)"/>. All set criteria must match.</summary>
public readonly record struct EcuSearch
{
    /// <summary>Case-insensitive substring of the ECU name, group or href (null = any).</summary>
    public string? Text { get; init; }
    /// <summary>Project code (case-insensitive exact).</summary>
    public string? Project { get; init; }
    /// <summary>Functional address (hex, case-insensitive exact, e.g. "26").</summary>
    public string? Address { get; init; }
    /// <summary>Protocol filter.</summary>
    public EcuProtocol? Protocol { get; init; }
    /// <summary>Functional group (case-insensitive exact).</summary>
    public string? Group { get; init; }
    /// <summary>Stop after this many results (0 = unlimited).</summary>
    public int MaxResults { get; init; }
}

/// <summary>
/// Identification data read from a live ECU (service 21 80 / 22 F1xx) used to find the matching definition.
/// Diag versions are compared as hexadecimal numbers (like the Python reference), the other fields as trimmed text.
/// </summary>
/// <param name="DiagVersion">Diagnostic version (hex text; the Python scanner passes the decimal string of the byte).</param>
/// <param name="Supplier">Supplier code.</param>
/// <param name="Soft">Software number.</param>
/// <param name="Version">Software version.</param>
/// <param name="Protocol">Bus the ECU was found on: <see cref="EcuProtocol.Can"/> or <see cref="EcuProtocol.Kwp2000"/>.</param>
/// <param name="Address">Address the ECU answered at (informational; copied to the result).</param>
public readonly record struct AutoIdentQuery(string DiagVersion, string Supplier, string Soft, string Version,
    EcuProtocol Protocol = EcuProtocol.Can, string? Address = null);

/// <summary>Quality of an auto-identification match.</summary>
public enum MatchKind : byte
{
    /// <summary>No definition found.</summary>
    None = 0,
    /// <summary>Diag version, supplier, soft and version all match.</summary>
    Exact,
    /// <summary>Supplier and soft match; the closest version was chosen ("not perfect match").</summary>
    Approximate,
}

/// <summary>Result of <see cref="EcuDatabase.Match"/>.</summary>
public readonly record struct EcuMatch(MatchKind Kind, EcuEntry Entry, int AutoIdentIndex, AutoIdentQuery Query)
{
    /// <summary>True unless <see cref="Kind"/> is <see cref="MatchKind.None"/>.</summary>
    public bool IsMatch => Kind != MatchKind.None;
    /// <summary>The autoident record that matched.</summary>
    public AutoIdent Ident => Entry.GetAutoIdent(AutoIdentIndex);
}

/// <summary>A (protocol, address) pair used by a vehicle project.</summary>
public readonly record struct ProjectAddress(EcuProtocol Protocol, string Address);
