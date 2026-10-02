using System.Globalization;
using System.Text;
using Ddt4All.App.Localization;
using Ddt4All.Comms;
using Ddt4All.Comms.Scanning;
using Ddt4All.Core.Database;
using Microsoft.Extensions.Logging;
using CommsProtocol = Ddt4All.Comms.EcuProtocol;
using CoreProtocol = Ddt4All.Core.Ecu.EcuProtocol;

namespace Ddt4All.App.Services.Real;

/// <summary>
/// ECU auto-scan: probes every address of the database through <see cref="EcuScanner"/> (Python <c>identify_new</c>: session 10 03,
/// 22 F1 A0 / F18A / F194 / F195, with <c>identify_old</c>: 10 C0 + 21 80 as fallback) and identifies the answers with <see cref="EcuDatabase.Match"/>.
/// The Python scanner converts the diag version byte to a DECIMAL string which Core then compares as hex; <see cref="Interpret"/> replicates that.
/// </summary>
public sealed class EcuScanService : IScanService
{
    private readonly IEcuCatalogService _catalog;
    private readonly IConnectionService _connection;
    private readonly CanAddressing _addressing;
    private readonly ILogger<EcuScanService>? _log;

    public EcuScanService(IEcuCatalogService catalog, IConnectionService connection, CanAddressing? addressing = null, ILogger<EcuScanService>? log = null)
    { _catalog = catalog; _connection = connection; _addressing = addressing ?? CanAddressing.Default; _log = log; }

    public bool CanScan => _catalog.Database is not null && _connection.State == Models.ConnectionState.Connected && _connection.AddressableTransport is not null;

    public async Task<IReadOnlyList<FoundEcu>> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, Action<FoundEcu>? onFound, CancellationToken ct)
    {
        var db = _catalog.Database ?? throw new InvalidOperationException(_catalog.Error ?? Loc.T("No ecu.zip found."));
        var transport = _connection.AddressableTransport ?? throw new InvalidOperationException(Loc.T("Not connected"));
        var candidates = BuildCandidates(db, _addressing, request);
        var found = new List<FoundEcu>();
        var scanner = new EcuScanner(transport);
        _log?.LogInformation("Scanning {Count} addresses", candidates.Count);
        await foreach (var r in scanner.ScanAsync(candidates, progress, ct).ConfigureAwait(false))
        {
            if (!r.Found) continue;
            var f = Interpret(r, db, _addressing);
            if (f is null)
            {
                // identify_new answered the session but not the identification: Python then falls back to identify_old
                var old = r.Candidate.Fallback;
                if (old is null) continue;
                var again = await new EcuScanner(transport, new ScanOptions { Deduplicate = false, CloseProtocolWhenDone = false })
                    .ScanToListAsync([old with { Fallback = null }], null, ct).ConfigureAwait(false);
                if (again.Count == 0 || !again[0].Found || Interpret(again[0] with { AnsweredBy = old }, db, _addressing) is not { } f2) continue;
                f = f2;
            }
            found.Add(f);
            onFound?.Invoke(f);
        }
        return found;
    }

    // ------------------------------------------------------------------------- candidates

    internal static IReadOnlyList<ScanCandidate> BuildCandidates(EcuDatabase db, CanAddressing addr, ScanRequest req)
    {
        var list = new List<ScanCandidate>();
        IEnumerable<string> canAddrs, kwpAddrs;
        if (!string.IsNullOrEmpty(req.Project))
        {
            var pa = db.GetProjectAddresses(req.Project);
            canAddrs = pa.Where(p => p.Protocol == CoreProtocol.Can).Select(p => p.Address);
            kwpAddrs = pa.Where(p => p.Protocol == CoreProtocol.Kwp2000).Select(p => p.Address);
        }
        else { canAddrs = db.GetAddresses(CoreProtocol.Can); kwpAddrs = db.GetAddresses(CoreProtocol.Kwp2000); }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in canAddrs)
        {
            if (a is "00" or "FF" || !seen.Add(a) || addr.Lookup(a) is not { } ids) continue;
            var label = Label(a, addr);
            var old = new ScanCandidate(label, CommsProtocol.Can, ids.Tx, ids.Rx, a, Hex.Parse("2180"))
            { StartSession = Hex.Parse("10C0"), EndSession = Hex.Parse("1081") };
            list.Add(new ScanCandidate(label, CommsProtocol.Can, ids.Tx, ids.Rx, a, Hex.Parse("22F1A0"))
            {
                StartSession = Hex.Parse("1003"), EndSession = Hex.Parse("1001"),
                FollowUpRequests = [Hex.Parse("22F18A"), Hex.Parse("22F194"), Hex.Parse("22F195")],
                Fallback = old,
            });
        }
        if (req.IncludeKLine)
        {
            seen.Clear();
            foreach (var a in kwpAddrs)
            {
                if (!seen.Add(a)) continue;
                list.Add(new ScanCandidate(Label(a, addr), CommsProtocol.Kwp2000Fast, null, null, a, Hex.Parse("2180")) { StartSession = Hex.Parse("10C0") });
            }
        }
        return list;
    }

    private static string Label(string addr, CanAddressing a) => a.NameOf(addr) is { Length: > 0 } n ? $"{addr} {n}" : addr;

    // ------------------------------------------------------------------------- interpretation

    /// <summary>Turns a positive scan answer into identification strings and matches them against the database (null = could not parse).</summary>
    internal static FoundEcu? Interpret(ScanResult r, EcuDatabase db, CanAddressing addr)
    {
        var c = r.AnsweredBy ?? r.Candidate;
        string diag, supplier, soft, version;
        var resp = r.Response ?? [];
        bool isNew = c.IdentRequest.Length >= 3 && c.IdentRequest.Span[1] == 0xF1 && c.IdentRequest.Span[2] == 0xA0;
        if (isNew)
        {
            if (resp.Length < 4 || r.ExtraResponses.Count < 3 || r.ExtraResponses.Any(e => e.Length < 3 || e[0] != 0x62)) return null;
            diag = Dec(resp[3]);
            supplier = Utf8Ignore(Tail(r.ExtraResponses[0], 3, 63));
            soft = Utf8Ignore(TrimEnd(Tail(r.ExtraResponses[1], 3, int.MaxValue), 0x00, 0xFF, 0x20));
            version = Utf8Ignore(Tail(r.ExtraResponses[2], 3, 16));
        }
        else
        {
            // "61 80 ...": the Python code slices the spaced hex text; byte offsets 7 / 8..10 / 16..17 / 18..19, needs > 59 chars (>= 21 bytes)
            if (resp.Length < 21) return null;
            diag = Dec(resp[7]);
            try { supplier = new UTF8Encoding(false, true).GetString(resp, 8, 3); } catch (DecoderFallbackException) { return null; }
            soft = Convert.ToHexString(resp, 16, 2);
            version = Convert.ToHexString(resp, 18, 2);
        }
        if (diag.Length == 0) return null;

        bool can = c.Protocol == CommsProtocol.Can;
        var funcAddr = c.FuncAddr ?? "";
        var match = db.Match(new AutoIdentQuery(diag, supplier, soft, version, can ? CoreProtocol.Can : CoreProtocol.Kwp2000, funcAddr));
        var kind = match.Kind switch { MatchKind.Exact => FoundEcuMatch.Exact, MatchKind.Approximate => FoundEcuMatch.Approximate, _ => FoundEcuMatch.None };
        return new FoundEcu(funcAddr, addr.NameOf(funcAddr), can ? "CAN" : "KWP2000", diag, supplier.Trim(), soft.Trim(), version.Trim(),
            kind, match.IsMatch ? match.Entry.Href : null, match.IsMatch ? match.Entry.EcuName : null, match.IsMatch ? match.Entry.Group : null,
            c.ToAddress());
    }

    /// <summary>Python <c>str(int(byte))</c>: the decimal text of the byte (Core compares it as hex).</summary>
    internal static string Dec(byte b) => b.ToString(CultureInfo.InvariantCulture);

    private static byte[] Tail(byte[] data, int skip, int max)
    {
        if (data.Length <= skip) return [];
        return data.AsSpan(skip, Math.Min(max, data.Length - skip)).ToArray();
    }

    private static byte[] TrimEnd(byte[] data, params byte[] chars)
    {
        int n = data.Length;
        while (n > 0 && Array.IndexOf(chars, data[n - 1]) >= 0) n--;
        return data.AsSpan(0, n).ToArray();
    }

    private static readonly Encoding LenientUtf8 = Encoding.GetEncoding("utf-8", EncoderFallback.ReplacementFallback, new DecoderReplacementFallback(""));
    /// <summary>Python <c>decode("utf8", "ignore")</c>.</summary>
    private static string Utf8Ignore(byte[] data) => LenientUtf8.GetString(data);
}
