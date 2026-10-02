using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Ddt4All.Comms.Scanning;

/// <summary>
/// Description of an ECU to probe. Deliberately independent of Core types: the app maps its ECU database entries
/// (address groups, projects.json, ecu list) to these.
/// </summary>
public sealed record ScanCandidate
{
    public ScanCandidate(string name, EcuProtocol protocol, string? txId, string? rxId, string? funcAddr, ReadOnlyMemory<byte> identRequest)
    {
        Name = name; Protocol = protocol; TxId = txId; RxId = rxId; FuncAddr = funcAddr; IdentRequest = identRequest;
    }

    public string Name { get; init; }
    public EcuProtocol Protocol { get; init; }
    /// <summary>CAN tester id in hex ("7E0", "18DA10F1"); for DoIP the ECU logical address.</summary>
    public string? TxId { get; init; }
    /// <summary>CAN ECU reply id in hex.</summary>
    public string? RxId { get; init; }
    /// <summary>K-line functional address in hex ("7A"), or the DDT "address" label for display.</summary>
    public string? FuncAddr { get; init; }
    /// <summary>Identification request, e.g. 22 F1 94 or 21 80.</summary>
    public ReadOnlyMemory<byte> IdentRequest { get; init; }

    /// <summary>Sent first (e.g. 10 C0 / 10 03); a negative or missing answer marks the ECU as not present.</summary>
    public ReadOnlyMemory<byte> StartSession { get; init; }
    /// <summary>Sent after identification to leave the session (e.g. 10 81 / 10 01).</summary>
    public ReadOnlyMemory<byte> EndSession { get; init; }
    /// <summary>More identification requests (supplier, soft version...) answered into <see cref="ScanResult.ExtraResponses"/>.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> FollowUpRequests { get; init; } = Array.Empty<ReadOnlyMemory<byte>>();
    /// <summary>Tried when this candidate does not yield a positive identification (the Python "old method" after "new").</summary>
    public ScanCandidate? Fallback { get; init; }
    public CanSpeed Speed { get; init; }
    public byte? ExtAddress { get; init; }

    /// <summary>Converts to the address understood by transports.</summary>
    public EcuAddress ToAddress()
    {
        switch (Protocol)
        {
            case EcuProtocol.Can:
            {
                var a = EcuAddress.Can(Name, TxId ?? "0", RxId ?? "0", Speed);
                return a with { ExtAddress = ExtAddress, FuncAddress = TryHexByte(FuncAddr) };
            }
            case EcuProtocol.DoIp:
                return new EcuAddress { Name = Name, Protocol = Protocol, TxId = TryHexUInt(TxId) };
            default:
                return new EcuAddress { Name = Name, Protocol = Protocol, FuncAddress = TryHexByte(FuncAddr) };
        }
    }

    private static byte? TryHexByte(string? s) => s is not null && byte.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var b) ? b : null;
    private static uint TryHexUInt(string? s) => s is not null && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : 0;
}

public enum ScanStatus
{
    /// <summary>Positive identification response received.</summary>
    Found,
    /// <summary>The ECU answered with a negative response.</summary>
    NegativeResponse,
    /// <summary>No answer.</summary>
    NoResponse,
    /// <summary>Adapter/protocol error (CAN ERROR, bus init failed...).</summary>
    Error,
    /// <summary>Not probed (address 00/FF or duplicate).</summary>
    Skipped,
}

/// <summary>Outcome for one candidate.</summary>
public sealed record ScanResult(
    ScanCandidate Candidate,
    ScanStatus Status,
    byte[]? Response,
    IReadOnlyList<byte[]> ExtraResponses,
    TimeSpan Elapsed,
    string? Message)
{
    public bool Found => Status == ScanStatus.Found;
    /// <summary>The candidate (primary or fallback) that actually answered.</summary>
    public ScanCandidate? AnsweredBy { get; init; }
}

/// <summary>Progress notification (also delivered once at the start with Completed = 0).</summary>
public readonly record struct ScanProgress(int Completed, int Total, string CurrentName, int Found);

/// <summary>Tuning for <see cref="EcuScanner"/>.</summary>
public sealed class ScanOptions
{
    /// <summary>Maximum time spent on one candidate (all its requests).</summary>
    public TimeSpan PerCandidateTimeout { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>Skip candidates whose address is 00 or FF (not ISO-TP reachable) like the Python scanner.</summary>
    public bool SkipNonIsoTpAddresses { get; set; } = true;
    /// <summary>Send <see cref="ScanCandidate.EndSession"/> after a successful identification.</summary>
    public bool EndSession { get; set; } = true;
    /// <summary>Skip candidates with a CAN TX id already probed.</summary>
    public bool Deduplicate { get; set; } = true;
    /// <summary>Stop after the first ECU that answers.</summary>
    public bool StopAfterFirst { get; set; }
    /// <summary>Release the protocol (ATPC) after the scan.</summary>
    public bool CloseProtocolWhenDone { get; set; } = true;
}

/// <summary>
/// Probes a list of candidates one after the other through an <see cref="IAddressableTransport"/> (ELM, DoIP, USB, simulation).
/// Fully asynchronous, cancellable and progress-reporting; never throws for a silent ECU.
/// </summary>
public sealed class EcuScanner
{
    private readonly IAddressableTransport _transport;

    public EcuScanner(IAddressableTransport transport, ScanOptions? options = null)
    {
        _transport = transport;
        Options = options ?? new ScanOptions();
    }

    public ScanOptions Options { get; }

    /// <summary>
    /// Streams results as each candidate completes. Cancelling <paramref name="ct"/> throws <see cref="OperationCanceledException"/>
    /// to the consumer; results already yielded stay valid.
    /// </summary>
    public async IAsyncEnumerable<ScanResult> ScanAsync(IReadOnlyList<ScanCandidate> candidates, IProgress<ScanProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        int found = 0, done = 0, total = candidates.Count;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        progress?.Report(new ScanProgress(0, total, "", 0));
        try
        {
            foreach (var c in candidates)
            {
                ct.ThrowIfCancellationRequested();
                ScanResult result;
                if (ShouldSkip(c, seen)) result = new ScanResult(c, ScanStatus.Skipped, null, Array.Empty<byte[]>(), TimeSpan.Zero, "skipped");
                else
                {
                    progress?.Report(new ScanProgress(done, total, c.Name, found));
                    result = await ProbeAsync(c, ct).ConfigureAwait(false);
                }
                done++;
                if (result.Found) found++;
                progress?.Report(new ScanProgress(done, total, c.Name, found));
                yield return result;
                if (result.Found && Options.StopAfterFirst) break;
            }
        }
        finally
        {
            if (Options.CloseProtocolWhenDone)
            {
                try { await _transport.CloseProtocolAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* adapter may be gone */ }
            }
        }
    }

    /// <summary>Runs the whole scan and returns all results.</summary>
    public async Task<IReadOnlyList<ScanResult>> ScanToListAsync(IReadOnlyList<ScanCandidate> candidates, IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        var list = new List<ScanResult>(candidates.Count);
        await foreach (var r in ScanAsync(candidates, progress, ct).ConfigureAwait(false)) list.Add(r);
        return list;
    }

    private bool ShouldSkip(ScanCandidate c, HashSet<string> seen)
    {
        if (Options.SkipNonIsoTpAddresses && c.Protocol == EcuProtocol.Can &&
            (string.Equals(c.FuncAddr, "00", StringComparison.OrdinalIgnoreCase) || string.Equals(c.FuncAddr, "FF", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (Options.Deduplicate)
        {
            var key = $"{c.Protocol}:{c.TxId}:{c.RxId}:{c.FuncAddr}";
            if (!seen.Add(key)) return true;
        }
        return false;
    }

    private async Task<ScanResult> ProbeAsync(ScanCandidate primary, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        ScanResult? last = null;
        for (var c = primary; c is not null; c = c.Fallback)
        {
            last = await ProbeOneAsync(c, primary, sw, ct).ConfigureAwait(false);
            if (last.Found) return last with { AnsweredBy = c };
        }
        return last!;
    }

    private async Task<ScanResult> ProbeOneAsync(ScanCandidate c, ScanCandidate reported, Stopwatch sw, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Options.PerCandidateTimeout);
        var extra = new List<byte[]>();
        try
        {
            await _transport.ConnectEcuAsync(c.ToAddress(), cts.Token).ConfigureAwait(false);

            if (!c.StartSession.IsEmpty)
            {
                var s = await _transport.RequestAsync(c.StartSession, cts.Token).ConfigureAwait(false);
                if (s.Length == 0 || s[0] != c.StartSession.Span[0] + 0x40)
                    return new ScanResult(reported, s.Length > 0 && s[0] == 0x7F ? ScanStatus.NegativeResponse : ScanStatus.NoResponse, s, extra, sw.Elapsed, "session refused");
            }

            if (c.IdentRequest.IsEmpty) return new ScanResult(reported, ScanStatus.Found, Array.Empty<byte>(), extra, sw.Elapsed, null);

            var rsp = await _transport.RequestAsync(c.IdentRequest, cts.Token).ConfigureAwait(false);
            ScanResult result;
            if (rsp.Length > 0 && rsp[0] == c.IdentRequest.Span[0] + 0x40)
            {
                foreach (var f in c.FollowUpRequests)
                {
                    try { extra.Add(await _transport.RequestAsync(f, cts.Token).ConfigureAwait(false)); }
                    catch (CommsException) { extra.Add(Array.Empty<byte>()); }
                }
                result = new ScanResult(reported, ScanStatus.Found, rsp, extra, sw.Elapsed, null);
            }
            else if (rsp.Length >= 3 && rsp[0] == 0x7F)
                result = new ScanResult(reported, ScanStatus.NegativeResponse, rsp, extra, sw.Elapsed, NegativeResponses.Describe(rsp[2]));
            else result = new ScanResult(reported, ScanStatus.NoResponse, rsp, extra, sw.Elapsed, "unexpected response");

            if (Options.EndSession && !c.EndSession.IsEmpty)
            {
                try { await _transport.RequestAsync(c.EndSession, cts.Token).ConfigureAwait(false); } catch (CommsException) { }
            }
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ScanResult(reported, ScanStatus.NoResponse, null, extra, sw.Elapsed, "timeout");
        }
        catch (EcuTimeoutException e)
        {
            return new ScanResult(reported, ScanStatus.NoResponse, null, extra, sw.Elapsed, e.Message);
        }
        catch (CommsException e)
        {
            return new ScanResult(reported, ScanStatus.Error, null, extra, sw.Elapsed, e.Message);
        }
    }
}
