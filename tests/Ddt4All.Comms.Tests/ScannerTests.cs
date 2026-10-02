using Ddt4All.Comms;
using Ddt4All.Comms.Scanning;
using Ddt4All.Comms.Simulation;

namespace Ddt4All.Comms.Tests;

public class ScannerTests
{
    private static ScanCandidate Cand(string name, string tx, string rx, string func = "01", string ident = "22F194") =>
        new(name, EcuProtocol.Can, tx, rx, func, Hex.Parse(ident));

    private sealed class Collect : IProgress<ScanProgress>
    {
        public List<ScanProgress> Items { get; } = new();
        public void Report(ScanProgress v) { lock (Items) Items.Add(v); }
    }

    private static Task<TestRig> Rig() => TestRig.CreateAsync(b =>
    {
        b.Add(new FakeCanEcu("A", 0x7E0, 0x7E8, new EcuScript().On("22F194", "62F1945631")));
        b.Add(new FakeCanEcu("B", 0x7E1, 0x7E9, new EcuScript().Negative("22F194", 0x31)));
        b.Add(new FakeCanEcu("C", 0x7E2, 0x7EA, new EcuScript().Silent("22F194")));
    });

    [Fact]
    public async Task Scans_candidates_and_classifies_results()
    {
        await using var rig = await Rig();
        var progress = new Collect();
        var scanner = new EcuScanner(rig.Elm);
        var results = await scanner.ScanToListAsync(new[]
        {
            Cand("A", "7E0", "7E8"), Cand("B", "7E1", "7E9"), Cand("C", "7E2", "7EA"),
            Cand("Missing", "7E5", "7ED"), Cand("Func", "7E6", "7EE", func: "FF"),
        }, progress);
        Assert.Equal(new[] { ScanStatus.Found, ScanStatus.NegativeResponse, ScanStatus.NoResponse, ScanStatus.NoResponse, ScanStatus.Skipped },
            results.Select(r => r.Status).ToArray());
        Assert.Equal(Hex.Parse("62F1945631"), results[0].Response);
        Assert.Contains("Out Of Range", results[1].Message);
        Assert.Equal(5, progress.Items.Last().Completed);
        Assert.Equal(1, progress.Items.Last().Found);
        Assert.Equal(0, progress.Items.First().Completed);
        Assert.Contains("ATPC", rig.Device.Commands);   // protocol closed at the end
    }

    [Fact]
    public async Task Session_follow_up_and_end_session_are_used()
    {
        var script = new EcuScript().On("1003", "5003").On("22F194", "62F1945631").On("22F18A", "62F18A41").On("1001", "5001");
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("A", 0x7E0, 0x7E8, script)));
        var c = Cand("A", "7E0", "7E8") with
        {
            StartSession = Hex.Parse("1003"), EndSession = Hex.Parse("1001"),
            FollowUpRequests = new[] { (ReadOnlyMemory<byte>)Hex.Parse("22F18A") },
        };
        var r = (await new EcuScanner(rig.Elm).ScanToListAsync(new[] { c })).Single();
        Assert.True(r.Found);
        Assert.Equal(Hex.Parse("62F18A41"), r.ExtraResponses.Single());
        Assert.Equal(new[] { "1003", "22F194", "22F18A", "1001" }, script.Received.Select(x => Hex.ToString(x)).ToArray());
    }

    [Fact]
    public async Task Fallback_candidate_is_tried_when_primary_is_refused()
    {
        var script = new EcuScript().Negative("1003", 0x12).On("10C0", "50C0").On("2180", "6180AABB");
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("A", 0x7E0, 0x7E8, script)));
        var old = Cand("A-old", "7E0", "7E8", ident: "2180") with { StartSession = Hex.Parse("10C0") };
        var c = Cand("A", "7E0", "7E8") with { StartSession = Hex.Parse("1003"), Fallback = old };
        var r = (await new EcuScanner(rig.Elm).ScanToListAsync(new[] { c })).Single();
        Assert.True(r.Found);
        Assert.Equal("A-old", r.AnsweredBy!.Name);
        Assert.Equal("A", r.Candidate.Name);
    }

    [Fact]
    public async Task Duplicates_are_skipped_and_stop_after_first_works()
    {
        await using var rig = await Rig();
        var scanner = new EcuScanner(rig.Elm, new ScanOptions { StopAfterFirst = true });
        var list = await scanner.ScanToListAsync(new[] { Cand("A", "7E0", "7E8"), Cand("A2", "7E0", "7E8"), Cand("B", "7E1", "7E9") });
        Assert.Single(list);
        var all = await new EcuScanner(rig.Elm).ScanToListAsync(new[] { Cand("A", "7E0", "7E8"), Cand("A2", "7E0", "7E8") });
        Assert.Equal(ScanStatus.Skipped, all[1].Status);
    }

    [Fact]
    public async Task Cancellation_stops_the_scan_and_leaves_adapter_usable()
    {
        await using var rig = await Rig();
        using var cts = new CancellationTokenSource();
        var scanner = new EcuScanner(rig.Elm);
        var seen = new List<ScanResult>();
        var cands = Enumerable.Range(0, 20).Select(i => Cand("E" + i, (0x700 + i).ToString("X3"), (0x710 + i).ToString("X3"))).ToList();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var r in scanner.ScanAsync(cands, null, cts.Token))
            {
                seen.Add(r);
                if (seen.Count == 2) cts.Cancel();
            }
        });
        Assert.Equal(2, seen.Count);
        await rig.Elm.ConnectEcuAsync(EcuAddress.Can("A", "7E0", "7E8"));
        Assert.Equal(Hex.Parse("62F1945631"), await rig.Elm.RequestAsync(Hex.Parse("22F194")));
    }

    [Fact]
    public async Task Per_candidate_timeout_marks_no_response_and_scan_continues()
    {
        await using var rig = await TestRig.CreateAsync(b =>
        {
            b.Add(new FakeCanEcu("A", 0x7E0, 0x7E8, new EcuScript().On("22F194", "62F1945631")));
        });
        var scanner = new EcuScanner(rig.Elm, new ScanOptions { PerCandidateTimeout = TimeSpan.FromMilliseconds(300) });
        rig.Device.Mute = true;
        var t = scanner.ScanToListAsync(new[] { Cand("A", "7E0", "7E8") });
        await Task.Delay(500);
        rig.Device.Mute = false;
        var r = (await t).Single();
        Assert.Equal(ScanStatus.NoResponse, r.Status);
        await rig.Elm.ConnectEcuAsync(EcuAddress.Can("A", "7E0", "7E8"));
        Assert.Equal(Hex.Parse("62F1945631"), await rig.Elm.RequestAsync(Hex.Parse("22F194")));
    }

    [Fact]
    public async Task Works_with_simulated_transport_and_kline_candidates()
    {
        var t = new SimulatedTransport()
            .AddCan("A", 0x7E0, new EcuScript().On("22F194", "62F194AA"))
            .AddKLine("K", 0x7A, new EcuScript().On("2180", "6180BB"));
        var list = await new EcuScanner(t).ScanToListAsync(new[]
        {
            Cand("A", "7E0", "7E8"),
            new ScanCandidate("K", EcuProtocol.Kwp2000Fast, null, null, "7A", Hex.Parse("2180")),
            new ScanCandidate("Nope", EcuProtocol.Kwp2000Fast, null, null, "55", Hex.Parse("2180")),
        });
        Assert.Equal(new[] { ScanStatus.Found, ScanStatus.Found, ScanStatus.NoResponse }, list.Select(r => r.Status).ToArray());
    }

    [Fact]
    public void Candidate_converts_to_address()
    {
        var a = new ScanCandidate("X", EcuProtocol.Can, "18DA10F1", "18DAF110", "10", Hex.Parse("22F190")).ToAddress();
        Assert.True(a.Is29Bit);
        Assert.Equal(0x18DA10F1u, a.TxId);
        Assert.Equal((byte)0x10, a.FuncAddress);
        var k = new ScanCandidate("K", EcuProtocol.Kwp2000Slow, null, null, "7A", default).ToAddress();
        Assert.Equal((byte)0x7A, k.FuncAddress);
    }
}
