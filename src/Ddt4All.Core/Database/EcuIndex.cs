using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Ddt4All.Core.Ecu;

namespace Ddt4All.Core.Database;

/// <summary>
/// Compact, immutable, persistable index of all ECU definitions (structure-of-arrays with a shared UTF-8 string table).
/// Strings are materialised lazily, so loading a cached index is dominated by a few bulk reads.
/// </summary>
public sealed class EcuIndex
{
    private const long Magic = 0x3158444934544444; // "DDT4IDX1" (little endian)
    private const int FormatVersion = 1;

    // string table
    internal byte[] Blob = Array.Empty<byte>();
    internal int[] StrOff = new int[1];
    internal string?[] StrCache = Array.Empty<string?>();

    // projects (unique, upper-case): string ids
    internal int[] ProjectStr = Array.Empty<int>();

    // entries
    internal int[] Href = Array.Empty<int>();
    internal int[] Name = Array.Empty<int>();
    internal int[] Group = Array.Empty<int>();
    internal int[] Addr = Array.Empty<int>();
    internal byte[] Protocol = Array.Empty<byte>();
    internal int[] ProjStart = new int[1];   // Count+1
    internal int[] ProjIds = Array.Empty<int>();
    internal int[] AiStart = new int[1];     // Count+1

    // autoidents (flat)
    internal int[] AiDiag = Array.Empty<int>();
    internal int[] AiSupplier = Array.Empty<int>();
    internal int[] AiSoft = Array.Empty<int>();
    internal int[] AiVersion = Array.Empty<int>();
    internal int[] AiDiagValue = Array.Empty<int>(); // hex value of diag version, -1 if empty/invalid

    /// <summary>Number of ECU entries.</summary>
    public int Count => Href.Length;

    /// <summary>Total number of autoident records.</summary>
    public int AutoIdentCount => AiDiag.Length;

    /// <summary>Number of distinct projects.</summary>
    public int ProjectCount => ProjectStr.Length;

    internal string Str(int id)
    {
        var c = StrCache[id];
        if (c != null) return c;
        int off = StrOff[id];
        c = id == 0 ? "" : Encoding.UTF8.GetString(Blob, off, StrOff[id + 1] - off);
        StrCache[id] = c;
        return c;
    }

    internal ReadOnlySpan<byte> StrBytes(int id) => Blob.AsSpan(StrOff[id], StrOff[id + 1] - StrOff[id]);

    internal static int ParseHex(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return -1;
        return int.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int v) ? v : -1;
    }

    // ------------------------------------------------------------------ persistence

    /// <summary>Writes the index (binary, little endian) tagged with the source fingerprint.</summary>
    public void Save(Stream s, long sourceLength, long sourceTicks)
    {
        using var w = new BinaryWriter(s, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(FormatVersion);
        w.Write(sourceLength);
        w.Write(sourceTicks);
        w.Write(Count);
        w.Write(StrOff.Length - 1);
        w.Write(Blob.Length);
        w.Write(ProjectStr.Length);
        w.Write(ProjIds.Length);
        w.Write(AiDiag.Length);
        w.Flush();
        WriteArr(s, Blob);
        WriteArr(s, StrOff);
        WriteArr(s, ProjectStr);
        WriteArr(s, Href); WriteArr(s, Name); WriteArr(s, Group); WriteArr(s, Addr);
        WriteArr(s, Protocol);
        WriteArr(s, ProjStart); WriteArr(s, ProjIds); WriteArr(s, AiStart);
        WriteArr(s, AiDiag); WriteArr(s, AiSupplier); WriteArr(s, AiSoft); WriteArr(s, AiVersion); WriteArr(s, AiDiagValue);
    }

    private static void WriteArr<T>(Stream s, T[] a) where T : unmanaged => s.Write(MemoryMarshal.AsBytes(a.AsSpan()));

    private static void ReadArr<T>(Stream s, T[] a) where T : unmanaged => s.ReadExactly(MemoryMarshal.AsBytes(a.AsSpan()));

    /// <summary>
    /// Reads an index written by <see cref="Save"/>. Returns null if the file is of another format version or
    /// was built from a different source (length/timestamp mismatch) or is corrupt.
    /// </summary>
    public static EcuIndex? TryLoad(Stream s, long sourceLength, long sourceTicks)
    {
        try
        {
            using var r = new BinaryReader(s, Encoding.UTF8, leaveOpen: true);
            if (r.ReadInt64() != Magic || r.ReadInt32() != FormatVersion) return null;
            if (r.ReadInt64() != sourceLength || r.ReadInt64() != sourceTicks) return null;
            int count = r.ReadInt32(), strCount = r.ReadInt32(), blobLen = r.ReadInt32();
            int projCount = r.ReadInt32(), projIdsLen = r.ReadInt32(), aiCount = r.ReadInt32();
            if (count < 0 || strCount < 1 || blobLen < 0 || projCount < 0 || projIdsLen < 0 || aiCount < 0) return null;

            var ix = new EcuIndex
            {
                Blob = new byte[blobLen],
                StrOff = new int[strCount + 1],
                ProjectStr = new int[projCount],
                Href = new int[count], Name = new int[count], Group = new int[count], Addr = new int[count],
                Protocol = new byte[count],
                ProjStart = new int[count + 1], ProjIds = new int[projIdsLen], AiStart = new int[count + 1],
                AiDiag = new int[aiCount], AiSupplier = new int[aiCount], AiSoft = new int[aiCount],
                AiVersion = new int[aiCount], AiDiagValue = new int[aiCount],
                StrCache = new string?[strCount],
            };
            ReadArr(s, ix.Blob);
            ReadArr(s, ix.StrOff);
            ReadArr(s, ix.ProjectStr);
            ReadArr(s, ix.Href); ReadArr(s, ix.Name); ReadArr(s, ix.Group); ReadArr(s, ix.Addr);
            ReadArr(s, ix.Protocol);
            ReadArr(s, ix.ProjStart); ReadArr(s, ix.ProjIds); ReadArr(s, ix.AiStart);
            ReadArr(s, ix.AiDiag); ReadArr(s, ix.AiSupplier); ReadArr(s, ix.AiSoft); ReadArr(s, ix.AiVersion); ReadArr(s, ix.AiDiagValue);
            // sanity
            if (ix.StrOff[strCount] != blobLen || ix.ProjStart[count] != projIdsLen || ix.AiStart[count] != aiCount) return null;
            return ix;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentException or OutOfMemoryException)
        {
            return null;
        }
    }
}

/// <summary>Builds an <see cref="EcuIndex"/>. Strings are de-duplicated; projects are stored upper-case.</summary>
public sealed class EcuIndexBuilder
{
    private readonly Dictionary<string, int> _strings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    private readonly List<string> _table = new() { "" };
    private readonly Dictionary<int, int> _projectOfStr = new();
    private readonly List<int> _projectStr = new();

    private readonly List<int> _href = new(), _name = new(), _group = new(), _addr = new();
    private readonly List<byte> _protocol = new();
    private readonly List<int> _projStart = new() { 0 }, _projIds = new(), _aiStart = new() { 0 };
    private readonly List<int> _aiDiag = new(), _aiSupplier = new(), _aiSoft = new(), _aiVersion = new(), _aiDiagValue = new();

    /// <summary>Creates an empty builder.</summary>
    public EcuIndexBuilder()
    {
        _strings[""] = 0;
        _lookup = _strings.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>Number of entries added so far.</summary>
    public int Count => _href.Count;

    internal int Intern(ReadOnlySpan<char> s)
    {
        if (_lookup.TryGetValue(s, out int id)) return id;
        string str = s.ToString();
        id = _table.Count;
        _table.Add(str);
        _strings[str] = id;
        return id;
    }

    internal int Intern(string s) => Intern(s.AsSpan());

    internal int InternProject(ReadOnlySpan<char> project)
    {
        Span<char> up = project.Length <= 128 ? stackalloc char[project.Length] : new char[project.Length];
        project.ToUpperInvariant(up);
        int sid = Intern(up);
        if (!_projectOfStr.TryGetValue(sid, out int pid))
        {
            pid = _projectStr.Count;
            _projectStr.Add(sid);
            _projectOfStr[sid] = pid;
        }

        return pid;
    }

    internal static int HexOrMinus1(ReadOnlySpan<char> diag) =>
        diag.IsEmpty ? -1 : int.TryParse(diag.Trim(), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int v) ? v : -1;

    /// <summary>Commits an entry whose strings were already interned. <paramref name="ai"/> holds 5 ints per autoident.</summary>
    internal void CommitEntry(int href, int name, int group, int addr, EcuProtocol protocol,
        ReadOnlySpan<int> projectIds, ReadOnlySpan<int> ai)
    {
        _href.Add(href); _name.Add(name); _group.Add(group); _addr.Add(addr); _protocol.Add((byte)protocol);
        int start = _projIds.Count;
        foreach (int pid in projectIds)
        {
            bool dup = false;
            for (int i = start; i < _projIds.Count; i++) if (_projIds[i] == pid) { dup = true; break; }
            if (!dup) _projIds.Add(pid);
        }

        _projStart.Add(_projIds.Count);
        for (int i = 0; i + 4 < ai.Length; i += 5)
        {
            _aiDiag.Add(ai[i]); _aiSupplier.Add(ai[i + 1]); _aiSoft.Add(ai[i + 2]); _aiVersion.Add(ai[i + 3]); _aiDiagValue.Add(ai[i + 4]);
        }

        _aiStart.Add(_aiDiag.Count);
    }

    /// <summary>Adds one ECU definition.</summary>
    /// <param name="href">Entry name inside ecu.zip (e.g. "UCH_X95.json").</param>
    /// <param name="ecuName">ECU name.</param>
    /// <param name="group">Functional group.</param>
    /// <param name="protocol">Protocol text (classified by substring: CAN, KWP, ISO8, DOIP).</param>
    /// <param name="address">Functional address (hex text).</param>
    /// <param name="projects">Project codes.</param>
    /// <param name="autoIdents">Auto identification records.</param>
    public void Add(string href, string ecuName, string group, string protocol, string address,
        IEnumerable<string> projects, IEnumerable<AutoIdent> autoIdents)
    {
        var pids = new List<int>();
        foreach (var p in projects) if (p.Length > 0) pids.Add(InternProject(p));
        var ai = new List<int>();
        foreach (var a in autoIdents)
        {
            ai.Add(Intern(a.DiagVersion)); ai.Add(Intern(a.Supplier)); ai.Add(Intern(a.Soft)); ai.Add(Intern(a.Version));
            ai.Add(HexOrMinus1(a.DiagVersion));
        }

        CommitEntry(Intern(href), Intern(ecuName), Intern(group), Intern(address), EcuProtocolNames.Classify(protocol),
            pids.ToArray(), ai.ToArray());
    }

    /// <summary>Finishes the index.</summary>
    public EcuIndex Build()
    {
        int n = _table.Count;
        var off = new int[n + 1];
        int total = 0;
        for (int i = 0; i < n; i++)
        {
            off[i] = total;
            total += Encoding.UTF8.GetByteCount(_table[i]);
        }

        off[n] = total;
        var blob = new byte[total];
        for (int i = 0; i < n; i++) Encoding.UTF8.GetBytes(_table[i], 0, _table[i].Length, blob, off[i]);

        var strCache = new string?[n];
        for (int i = 0; i < n; i++) strCache[i] = _table[i];
        return new EcuIndex
        {
            Blob = blob, StrOff = off, StrCache = strCache,
            ProjectStr = _projectStr.ToArray(),
            Href = _href.ToArray(), Name = _name.ToArray(), Group = _group.ToArray(), Addr = _addr.ToArray(),
            Protocol = _protocol.ToArray(),
            ProjStart = _projStart.ToArray(), ProjIds = _projIds.ToArray(), AiStart = _aiStart.ToArray(),
            AiDiag = _aiDiag.ToArray(), AiSupplier = _aiSupplier.ToArray(), AiSoft = _aiSoft.ToArray(),
            AiVersion = _aiVersion.ToArray(), AiDiagValue = _aiDiagValue.ToArray(),
        };
    }
}
