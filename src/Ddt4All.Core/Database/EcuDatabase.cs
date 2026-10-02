using System.Buffers;
using System.IO.Compression;
using System.Text;
using Ddt4All.Core.Ecu;
using Ddt4All.Core.Layout;
using Ddt4All.Core.Loading;

namespace Ddt4All.Core.Database;

/// <summary>Options for <see cref="EcuDatabase.Open(string, EcuDatabaseOptions?)"/>.</summary>
public sealed class EcuDatabaseOptions
{
    /// <summary>
    /// Directory for the persisted index cache. null = per-user default
    /// (<c>%LOCALAPPDATA%/ddt4all-net/cache</c> or the XDG equivalent); empty string disables caching.
    /// </summary>
    public string? CacheDirectory { get; set; }
    /// <summary>Ignore an existing cache and rebuild (the cache is rewritten).</summary>
    public bool ForceRebuild { get; set; }
}

/// <summary>
/// The ECU database: an index over <c>ecu.zip</c> (names, addresses, projects, autoidents) with fast search and
/// auto-identification, plus lazy loading of individual ECU definitions and layouts from the archive.
/// <para>
/// The first <see cref="Open(string, EcuDatabaseOptions?)"/> parses <c>db.json</c> once and persists a compact binary index; subsequent opens only
/// read that cache (the zip's central directory is not even touched until an ECU is loaded).
/// All query methods are thread-safe; archive reads are serialised internally.
/// </para>
/// </summary>
public sealed class EcuDatabase : IDisposable
{
    private readonly EcuIndex _ix;
    private readonly object _zipLock = new();
    private ZipArchive? _zip;
    private FileStream? _zipStream;
    private Dictionary<string, List<ProjectAddress>>? _vehicleMap;
    private Dictionary<EcuProtocol, List<string>>? _addresses;
    private Dictionary<string, string>? _groupByAddress;

    private EcuDatabase(EcuIndex ix, string? zipPath, bool fromCache)
    {
        _ix = ix;
        ZipPath = zipPath;
        LoadedFromCache = fromCache;
    }

    /// <summary>Path of the opened zip (null for in-memory databases).</summary>
    public string? ZipPath { get; }

    /// <summary>True if the index came from the on-disk cache instead of parsing db.json.</summary>
    public bool LoadedFromCache { get; }

    /// <summary>Number of ECU definitions.</summary>
    public int Count => _ix.Count;

    /// <summary>Total number of autoident records over all ECUs.</summary>
    public int AutoIdentCount => _ix.AutoIdentCount;

    /// <summary>The underlying index.</summary>
    public EcuIndex Index => _ix;

    /// <summary>ECU at position <paramref name="i"/>.</summary>
    public EcuEntry this[int i] => (uint)i < (uint)_ix.Count ? new EcuEntry(_ix, i) : throw new ArgumentOutOfRangeException(nameof(i));

    /// <summary>All entries in index order.</summary>
    public IEnumerable<EcuEntry> Entries
    {
        get { for (int i = 0; i < _ix.Count; i++) yield return new EcuEntry(_ix, i); }
    }

    // ------------------------------------------------------------------ opening

    /// <summary>Creates a database from a prebuilt index (tests, synthetic data, other sources). No archive is attached.</summary>
    public static EcuDatabase FromIndex(EcuIndex index) => new(index, null, false);

    /// <summary>Opens <c>ecu.zip</c>, using the persisted index cache when it matches the file.</summary>
    public static EcuDatabase Open(string zipPath, EcuDatabaseOptions? options = null)
    {
        options ??= new EcuDatabaseOptions();
        var fi = new FileInfo(zipPath);
        if (!fi.Exists) throw new FileNotFoundException("ecu.zip not found", zipPath);
        long len = fi.Length, ticks = fi.LastWriteTimeUtc.Ticks;

        string? cacheFile = ResolveCacheFile(zipPath, options);
        if (cacheFile != null && !options.ForceRebuild && File.Exists(cacheFile))
        {
            try
            {
                using var fs = new FileStream(cacheFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
                var cached = EcuIndex.TryLoad(fs, len, ticks);
                if (cached != null) return new EcuDatabase(cached, zipPath, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var ix = BuildIndexFromZip(zipPath);
        if (cacheFile != null) TrySaveCache(cacheFile, ix, len, ticks);
        return new EcuDatabase(ix, zipPath, false);
    }

    /// <summary>Builds the index by parsing <c>db.json</c> of the archive (no cache involved).</summary>
    public static EcuIndex BuildIndexFromZip(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry("db.json");
        var b = new EcuIndexBuilder();
        if (entry != null)
        {
            byte[] buf = ArrayPool<byte>.Shared.Rent(checked((int)entry.Length));
            try
            {
                using (var s = entry.Open()) s.ReadExactly(buf, 0, (int)entry.Length);
                DbJsonParser.Parse(buf.AsSpan(0, (int)entry.Length), b);
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
        }
        else
        {
            // Fallback for archives without db.json: derive entries from the ECU JSON members (slower).
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || e.FullName.Contains('/')) continue;
                try
                {
                    EcuFile f;
                    using (var s = e.Open()) f = EcuJsonLoader.Load(s);
                    b.Add(e.FullName, f.EcuName, f.FuncName, f.Protocol.ToWire(), f.FuncAddr, f.Projects, f.AutoIdents);
                }
                catch (EcuException) { }
            }
        }

        return b.Build();
    }

    private static string? ResolveCacheFile(string zipPath, EcuDatabaseOptions o)
    {
        string? dir = o.CacheDirectory;
        if (dir != null && dir.Length == 0) return null;
        dir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ddt4all-net", "cache");
        string full = Path.GetFullPath(zipPath);
        uint h = 2166136261;
        foreach (char c in full) h = (h ^ c) * 16777619;
        return Path.Combine(dir, $"ecu-index-{h:x8}.bin");
    }

    private static void TrySaveCache(string cacheFile, EcuIndex ix, long len, long ticks)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            string tmp = cacheFile + "." + Environment.ProcessId + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                ix.Save(fs, len, ticks);
            File.Move(tmp, cacheFile, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ lookup / search

    /// <summary>First entry with this href, or null.</summary>
    public EcuEntry? FindByHref(string href)
    {
        for (int i = 0; i < _ix.Count; i++)
            if (_ix.Str(_ix.Href[i]) == href) return new EcuEntry(_ix, i);
        return null;
    }

    /// <summary>All entries whose ECU name equals <paramref name="name"/> (exact).</summary>
    public List<EcuEntry> FindByName(string name)
    {
        var l = new List<EcuEntry>();
        for (int i = 0; i < _ix.Count; i++)
            if (_ix.Str(_ix.Name[i]) == name) l.Add(new EcuEntry(_ix, i));
        return l;
    }

    /// <summary>Filters the database. Results are in index order.</summary>
    public List<EcuEntry> Search(in EcuSearch q)
    {
        var l = new List<EcuEntry>();
        Search(q, l);
        return l;
    }

    /// <summary>Filters the database into an existing list (cleared first).</summary>
    public int Search(in EcuSearch q, List<EcuEntry> results)
    {
        results.Clear();
        var ix = _ix;
        int max = q.MaxResults > 0 ? q.MaxResults : int.MaxValue;

        int projIdx = -1;
        if (!string.IsNullOrEmpty(q.Project))
        {
            for (int p = 0; p < ix.ProjectStr.Length; p++)
                if (string.Equals(ix.Str(ix.ProjectStr[p]), q.Project, StringComparison.OrdinalIgnoreCase)) { projIdx = p; break; }
            if (projIdx < 0) return 0;
        }

        string? text = string.IsNullOrEmpty(q.Text) ? null : q.Text;
        for (int i = 0; i < ix.Count && results.Count < max; i++)
        {
            if (q.Protocol is { } proto && (EcuProtocol)ix.Protocol[i] != proto) continue;
            if (q.Address != null && !string.Equals(ix.Str(ix.Addr[i]), q.Address, StringComparison.OrdinalIgnoreCase)) continue;
            if (q.Group != null && !string.Equals(ix.Str(ix.Group[i]), q.Group, StringComparison.OrdinalIgnoreCase)) continue;
            if (projIdx >= 0)
            {
                bool found = false;
                for (int k = ix.ProjStart[i]; k < ix.ProjStart[i + 1]; k++)
                    if (ix.ProjIds[k] == projIdx) { found = true; break; }
                if (!found) continue;
            }

            if (text != null &&
                !ix.Str(ix.Name[i]).Contains(text, StringComparison.OrdinalIgnoreCase) &&
                !ix.Str(ix.Group[i]).Contains(text, StringComparison.OrdinalIgnoreCase) &&
                !ix.Str(ix.Href[i]).Contains(text, StringComparison.OrdinalIgnoreCase)) continue;

            results.Add(new EcuEntry(ix, i));
        }

        return results.Count;
    }

    /// <summary>All distinct project codes (upper-case), sorted.</summary>
    public IReadOnlyList<string> Projects
    {
        get
        {
            var a = new string[_ix.ProjectStr.Length];
            for (int i = 0; i < a.Length; i++) a[i] = _ix.Str(_ix.ProjectStr[i]);
            Array.Sort(a, StringComparer.OrdinalIgnoreCase);
            return a;
        }
    }

    private void EnsureDerived()
    {
        if (_vehicleMap != null) return;
        var vm = new Dictionary<string, List<ProjectAddress>>(StringComparer.OrdinalIgnoreCase);
        var addr = new Dictionary<EcuProtocol, List<string>>();
        var seen = new HashSet<(EcuProtocol, string)>();
        var groups = new Dictionary<string, string>();
        var ix = _ix;
        for (int i = 0; i < ix.Count; i++)
        {
            var proto = (EcuProtocol)ix.Protocol[i];
            string a = ix.Str(ix.Addr[i]);
            if (!addr.TryGetValue(proto, out var list)) addr[proto] = list = new List<string>();
            if (!list.Contains(a)) list.Add(a);
            string g = ix.Str(ix.Group[i]);
            if (g.Length > 0) groups.TryAdd(a, g);
            for (int k = ix.ProjStart[i]; k < ix.ProjStart[i + 1]; k++)
            {
                string p = ix.Str(ix.ProjectStr[ix.ProjIds[k]]);
                if (!vm.TryGetValue(p, out var pl)) vm[p] = pl = new List<ProjectAddress>();
                var pa = new ProjectAddress(proto, a);
                if (!pl.Contains(pa)) pl.Add(pa);
            }
        }

        _addresses = addr;
        _groupByAddress = groups;
        _vehicleMap = vm;
    }

    /// <summary>Distinct functional addresses of the given protocol in first-seen order (like available_addr_can/kwp).</summary>
    public IReadOnlyList<string> GetAddresses(EcuProtocol protocol)
    {
        EnsureDerived();
        return _addresses!.TryGetValue(protocol, out var l) ? l : Array.Empty<string>();
    }

    /// <summary>The (protocol, address) pairs used by a project (the Python <c>vehiclemap</c>); empty if unknown.</summary>
    public IReadOnlyList<ProjectAddress> GetProjectAddresses(string project)
    {
        EnsureDerived();
        return _vehicleMap!.TryGetValue(project, out var l) ? l : Array.Empty<ProjectAddress>();
    }

    /// <summary>Functional group of an address (first group seen for it), or null.</summary>
    public string? GetGroupForAddress(string address)
    {
        EnsureDerived();
        return _groupByAddress!.TryGetValue(address, out var g) ? g : null;
    }

    // ------------------------------------------------------------------ auto identification

    private static bool TryHex(string s, out long v) => HexUtil2.TryParse(s, out v);

    /// <summary>
    /// Finds the definition matching identification data read from an ECU. Port of <c>EcuScanner.check_ecu2</c>:
    /// the first exact match (diag version as hex number, supplier/soft/version as trimmed prefixes of the query) wins;
    /// otherwise, among entries whose supplier and soft match, the one with the numerically closest version.
    /// </summary>
    public EcuMatch Match(in AutoIdentQuery q)
    {
        var ix = _ix;
        bool qIsCan = q.Protocol == EcuProtocol.Can;
        bool qIsKwp = q.Protocol == EcuProtocol.Kwp2000;
        string qSup = q.Supplier.Trim(), qSoft = q.Soft.Trim(), qVer = q.Version.Trim();
        bool diagOk = TryHex(q.DiagVersion, out long qDiag);

        int bestEntry = -1, bestAi = -1;
        long bestDelta = 0xFFFFFF;
        bool qVerOk = TryHex(q.Version, out long qVerVal);

        for (int e = 0; e < ix.Count; e++)
        {
            var tp = (EcuProtocol)ix.Protocol[e];
            if (tp == EcuProtocol.Can && !qIsCan) continue;
            if (tp == EcuProtocol.Kwp2000 && !qIsKwp) continue;

            for (int k = ix.AiStart[e]; k < ix.AiStart[e + 1]; k++)
            {
                int dv = ix.AiDiagValue[k];
                // Empty diag version in the database never matches (Python returns None).
                if (ix.AiDiag[k] == 0) continue;

                string sup = ix.Str(ix.AiSupplier[k]);
                string soft = ix.Str(ix.AiSoft[k]);

                if (diagOk && dv >= 0 && dv == qDiag &&
                    qSup.AsSpan().StartsWith(sup.AsSpan().Trim(), StringComparison.Ordinal) &&
                    qSoft.AsSpan().StartsWith(soft.AsSpan().Trim(), StringComparison.Ordinal) &&
                    qVer.AsSpan().StartsWith(ix.Str(ix.AiVersion[k]).AsSpan().Trim(), StringComparison.Ordinal))
                {
                    return new EcuMatch(MatchKind.Exact, new EcuEntry(ix, e), k - ix.AiStart[e], q);
                }

                // approximate candidates: supplier + soft equal
                if (sup.Trim() == qSup && soft.Trim() == qSoft)
                {
                    var ap = tp == EcuProtocol.Kwp2000 || tp == EcuProtocol.Iso8 ? EcuProtocol.Kwp2000 : EcuProtocol.Can;
                    if (ap != (qIsKwp ? EcuProtocol.Kwp2000 : EcuProtocol.Can)) continue;
                    if (!qVerOk || !TryHex(ix.Str(ix.AiVersion[k]), out long tv)) continue;
                    long delta = Math.Abs(tv - qVerVal);
                    if (delta < bestDelta) { bestDelta = delta; bestEntry = e; bestAi = k - ix.AiStart[e]; }
                }
            }
        }

        return bestEntry >= 0
            ? new EcuMatch(MatchKind.Approximate, new EcuEntry(ix, bestEntry), bestAi, q)
            : new EcuMatch(MatchKind.None, default, -1, q);
    }

    // ------------------------------------------------------------------ archive access

    private ZipArchive OpenZip()
    {
        if (_zip != null) return _zip;
        if (ZipPath == null) throw new InvalidOperationException("This database has no archive attached");
        _zipStream = new FileStream(ZipPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        _zip = new ZipArchive(_zipStream, ZipArchiveMode.Read, leaveOpen: false);
        return _zip;
    }

    /// <summary>True if the archive contains the member.</summary>
    public bool ContainsEntry(string name)
    {
        lock (_zipLock) return OpenZip().GetEntry(name) != null;
    }

    /// <summary>Reads an archive member fully (e.g. "UCH.json", "UCH.json.layout", "graphics/x.gif"); null if absent.</summary>
    public byte[]? ReadEntry(string name)
    {
        lock (_zipLock)
        {
            var e = OpenZip().GetEntry(name);
            if (e == null) return null;
            var buf = new byte[e.Length];
            using var s = e.Open();
            s.ReadExactly(buf);
            return buf;
        }
    }

    /// <summary>Loads and parses the ECU definition of an entry (JSON, or XML if the member is ".xml").</summary>
    public EcuFile LoadEcu(EcuEntry entry)
    {
        string href = entry.Href;
        byte[]? data = ReadEntry(href) ?? ReadEntry(Path.GetFileName(href));
        if (data == null) throw new FileNotFoundException("ECU definition not found in archive", href);
        return EcuLoader.Load(href, data);
    }

    /// <summary>Loads the screen layout (".layout" member) of an entry; an empty layout if the member is absent.</summary>
    public EcuLayout LoadLayout(EcuEntry entry)
    {
        string href = entry.Href;
        byte[]? data = ReadEntry(href + ".layout") ?? ReadEntry(Path.GetFileName(href) + ".layout");
        if (data == null)
        {
            if (href.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && ReadEntry(href) is { } xml)
            {
                using var ms = new MemoryStream(xml);
                return EcuLayoutXmlLoader.Load(ms);
            }

            return EcuLayout.Empty;
        }

        return EcuLayoutJson.Load(data);
    }

    /// <summary>Background loading helper: <see cref="LoadEcu"/> on the thread pool.</summary>
    public Task<EcuFile> LoadEcuAsync(EcuEntry entry, CancellationToken ct = default) =>
        Task.Run(() => LoadEcu(entry), ct);

    /// <summary>Background loading helper: <see cref="LoadLayout"/> on the thread pool.</summary>
    public Task<EcuLayout> LoadLayoutAsync(EcuEntry entry, CancellationToken ct = default) =>
        Task.Run(() => LoadLayout(entry), ct);

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_zipLock)
        {
            _zip?.Dispose();
            _zip = null;
            _zipStream = null;
        }
    }
}

internal static class HexUtil2
{
    public static bool TryParse(string s, out long v)
    {
        s = s.Trim();
        if (s.Length == 0) { v = 0; return false; }
        return long.TryParse(s, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out v);
    }
}
