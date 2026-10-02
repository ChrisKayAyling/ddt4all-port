using Ddt4All.Comms;
using Ddt4All.Comms.Simulation;
using Ddt4All.Core.Ecu;
using Ddt4All.Plugins;
using Ddt4All.Plugins.Vehicles;

namespace Ddt4All.Plugins.Tests;

public class VinCrcTests
{
    [Theory]
    [InlineData("123456789", "6E90")]            // CRC-16/X-25 check value 0x906E, byte swapped
    [InlineData("VF1BB05C123456789", "55FA")]    // reference computed with the crcmod "x-25" algorithm
    [InlineData("VF1KM0B0636524001", "DCA0")]
    [InlineData("", "0000")]
    public void Matches_the_python_reference(string vin, string expected) => Assert.Equal(expected, VinCrc.Calc(vin));

    [Fact]
    public async Task Plugin_needs_no_hardware_and_no_expert_mode()
    {
        var (res, _, script) = await Harness.RunAsync(new VinCrcPlugin(), "calculate", null, new EcuScript(), expert: false, confirmed: false,
            parameters: new() { ["vin"] = "vf1bb05c123456789" });
        Assert.Equal(PluginOutcome.Success, res.Outcome);
        Assert.Equal("55FA", res.Values.Single().Value);
        Assert.Empty(script.Received);
    }
}

public class Clio3VinTests
{
    private static EcuFile Def() => new Def("DAE_J77_X85_Gen2___v3.7").Ascii("VIN", 17).Hex("CRC VIN", 2)
        .Req("Start Diagnostic Session", "10C0")
        .Req("WDBLI - VIN", "3B81" + new string('0', 38), send: [("VIN", 3), ("CRC VIN", 20)]).Build();

    [Fact]
    public async Task Write_vin_sends_vin_and_swapped_crc()
    {
        var s = new EcuScript().On("10", "50C0").On("3B81", "7B81");
        var (res, _, script) = await Harness.RunAsync(new Clio3EpsPlugin(), "write-vin", Def(), s, parameters: new() { ["vin"] = "vf1bb05c123456789" });
        Assert.Equal(PluginOutcome.Success, res.Outcome);
        Assert.Equal(["10C0", "3B81" + Convert.ToHexString("VF1BB05C123456789"u8.ToArray()) + "55FA"], Harness.Wire(script));
    }

    [Theory]
    [InlineData("TOOSHORT")]
    [InlineData("VF1BB05C1234567890")]
    public async Task Bad_length_is_refused_before_anything_is_sent(string vin)
    {
        var (res, _, script) = await Harness.RunAsync(new Clio3EpsPlugin(), "write-vin", Def(), new EcuScript().On("10", "50C0"), parameters: new() { ["vin"] = vin });
        Assert.Equal(PluginOutcome.Blocked, res.Outcome);
        Assert.Empty(script.Received);
    }

    [Fact]
    public async Task Write_failure_is_reported()
    {
        var (res, _, _) = await Harness.RunAsync(new Clio3EpsPlugin(), "write-vin", Def(), new EcuScript().On("10", "50C0").Negative("3B81", 0x33),
            parameters: new() { ["vin"] = "VF1BB05C123456789" });
        Assert.Equal(PluginOutcome.Failed, res.Outcome);
        Assert.Equal("VIN WRITE FAILED", res.Summary);
    }
}

public class ZoeTests
{
    private static EcuFile Def()
    {
        var d = new Def("EVC_3180_RH5_510_V1.1_20210422T184714").Req("StartDiagnosticSession.Default", "1001").Req("StartDiagnosticSession.ExtendedDiagnosticSession", "1003");
        foreach (var c in ZoeWaterPumpPlugin.Counters)
        {
            d.Hex(c.DataName, 2);
            d.Req(c.ReadRequest, "22" + (c.Id == "low" ? "3349" : c.Id == "middle" ? "334A" : c.Id == "high" ? "334B" : "3531"), recv: [(c.DataName, 4)]);
            d.Req(c.WriteRequest, "2E" + (c.Id == "low" ? "3349" : c.Id == "middle" ? "334A" : c.Id == "high" ? "334B" : "3531") + "FFFF", send: [(c.DataName, 4)]);
        }
        return d.Build();
    }

    private static EcuScript Script() => new EcuScript().On("10", req => new[] { new EcuReply(new byte[] { 0x50, req[1] }) })
        .On("223349", "6233490012").On("22334A", "62334A0034").On("22334B", "62334B0056").On("223531", "6235310078")
        .On("2E", req => new[] { new EcuReply(new byte[] { 0x6E, req[1], req[2] }) });

    [Fact]
    public async Task Read_shows_all_four_counters_in_the_default_session()
    {
        var (res, _, script) = await Harness.RunAsync(new ZoeWaterPumpPlugin(), "read", Def(), Script(), expert: false, confirmed: false);
        Assert.Equal(PluginOutcome.Success, res.Outcome);
        Assert.Equal(["1001", "223349", "22334A", "22334B", "223531"], Harness.Wire(script));
        Assert.Equal(["0012", "0034", "0056", "0078"], res.Values.Select(v => v.Value));
    }

    [Fact]
    public async Task Reset_all_writes_zero_to_every_counter_in_the_extended_session()
    {
        var (res, _, script) = await Harness.RunAsync(new ZoeWaterPumpPlugin(), "reset-all", Def(), Script());
        Assert.Equal(PluginOutcome.Success, res.Outcome);
        Assert.Equal(["1003", "2E33490000", "2E334A0000", "2E334B0000", "2E35310000"], Harness.Wire(script));
    }

    [Theory]
    [InlineData("reset-low", "2E33490000")]
    [InlineData("reset-middle", "2E334A0000")]
    [InlineData("reset-high", "2E334B0000")]
    [InlineData("reset-timer", "2E35310000")]
    public async Task Single_resets_use_the_extended_session(string action, string wire)
    {
        var (res, _, script) = await Harness.RunAsync(new ZoeWaterPumpPlugin(), action, Def(), Script());
        Assert.Equal(PluginOutcome.Success, res.Outcome);
        Assert.Equal(["1003", wire], Harness.Wire(script));
    }

    [Fact]
    public async Task One_refused_counter_fails_the_run_but_the_others_are_still_attempted()
    {
        var s = Script().Negative("2E334A", 0x31);
        var (res, _, script) = await Harness.RunAsync(new ZoeWaterPumpPlugin(), "reset-all", Def(), s);
        Assert.Equal(PluginOutcome.Failed, res.Outcome);
        Assert.Contains("Middle Speed", res.Summary);
        Assert.Equal(5, script.Received.Count);
    }

    [Fact]
    public async Task Resets_need_expert_mode()
    {
        var (res, _, script) = await Harness.RunAsync(new ZoeWaterPumpPlugin(), "reset-all", Def(), Script(), expert: false);
        Assert.Equal(PluginOutcome.Blocked, res.Outcome);
        Assert.Empty(script.Received);
    }
}

public class CardAlgorithmTests
{
    [Theory]
    // expected values produced by running the Python a8()/a8_2() functions
    [InlineData("FC0D08514C86", false, "072E40D9C36F")]
    [InlineData("FC0D08514C86", true, "072E48D9C36F")]
    [InlineData("000000000000", false, "102020108188")]
    [InlineData("000000000000", true, "102028108188")]
    [InlineData("FFFFFFFFFFFF", false, "EFDFDFEF7E77")]
    [InlineData("FFFFFFFFFFFF", true, "EFDFD7EF7E77")]
    [InlineData("8DE8EE1679D3", false, "6596FF1D0731")]
    [InlineData("8de8 ee16 79d3", true, "6596F71D0731")]
    public void Pin_matches_python(string isk, bool algo2, string pin) => Assert.Equal(pin, MeganeCardAlgorithms.Pin(isk, algo2));

    [Theory]
    [InlineData("8DE8EE1679")]
    [InlineData("")]
    [InlineData("ZZ")]
    public void Bad_isk_gives_null(string isk) => Assert.Null(MeganeCardAlgorithms.Pin(isk));

    [Fact]
    public void Isk_is_bytes_19_to_24_of_the_reply()
    {
        var reply = Convert.FromHexString("61AB02FC0D08514C86555400000000000000008DE8EE1679D3C9A7A7CCF6AC00000000000000002A".Replace(" ", ""));
        Assert.Equal("8DE8EE1679D3", MeganeCardAlgorithms.ExtractIsk(reply));
        Assert.Null(MeganeCardAlgorithms.ExtractIsk(reply.AsSpan(0, 24)));
    }
}

public class CardProgrammingTests
{
    // Frame from the Python simulation reply: "61 AB 02 FC 0D 08 51 4C 86 55 54 00 ... 8D E8 EE 16 79 D3 C9 ..."
    private const string IskFrame = "61AB02FC0D08514C86555400000000000000008DE8EE1679D3C9A7A7CCF6AC00000000000000002A";

    private static EcuFile Def() => new Def("UCH_84_J84_03_60")
        .Hex("Code APV", 6).Hex("bit-apv", 0 + 1)
        .List("VSC Code APV_Reconnu", (0, "0"), (1, "1")).List("VSC ModeAPV_ReaffArmé", (0, "0"), (1, "1")).List("VSC ModeAPV_AppUCH_Armé", (0, "0"), (1, "1"))
        .Hex("VSC NbTotalDeBadgeAppris", 1).Hex("VSC Code_IDE", 2)
        .Req("Start Diagnostic Session", "10C0")
        .Req("Trame AB: Trame réservée", "21AB")
        .Req("ACCEDER AU MODE APRES-VENTE", "3B00000000000000", send: [("Code APV", 3)])
        .Req("APPRENDRE BADGE", "3B02")
        .Req("Status général des opérations badges Bits", "2106", recv: [("VSC Code APV_Reconnu", 3), ("VSC ModeAPV_ReaffArmé", 4), ("VSC ModeAPV_AppUCH_Armé", 5)])
        .Req("Status général des opérations badges Octets", "2108", recv: [("VSC NbTotalDeBadgeAppris", 3), ("VSC Code_IDE", 4)])
        .Req("SORTIE DU MODE APV : VALIDATION", "3B03")
        .Req("SORTIE DU MODE APV : ABANDON", "3B04").Build();

    private static EcuScript Script(Func<int, string>? bits = null)
    {
        int n = 0;
        var s = new EcuScript().On("10", "50C0").On("21AB", IskFrame).On("2108", "6108010123")
            .On("3B00", "7B00").On("3B02", "7B02").On("3B03", "7B03").On("3B04", "7B04");
        s.On("2106", _ => [EcuReply.Of((bits ?? (_ => "610601 0100".Replace(" ", "")))(n++))]);
        return s;
    }

    [Fact]
    public async Task Isk_action_is_read_only_and_computes_the_pin()
    {
        var (res, _, script) = await Harness.RunAsync(new Megane2CardProgrammingPlugin(), "isk", Def(), Script(), expert: false, confirmed: false,
            parameters: new() { ["algo"] = "Algo 2" });
        Assert.Equal(PluginOutcome.Success, res.Outcome);
        Assert.Equal(["10C0", "21AB"], Harness.Wire(script));
        Assert.Equal("8DE8EE1679D3", res.Values[0].Value);
        Assert.Equal("6596F71D0731", res.Values[1].Value);
    }

    [Fact]
    public async Task Isk_failure_is_reported()
    {
        var s = new EcuScript().On("10", "50C0").Negative("21AB", 0x22);
        var (res, _, _) = await Harness.RunAsync(new Megane2CardProgrammingPlugin(), "isk", Def(), s, expert: false, confirmed: false);
        Assert.Equal(PluginOutcome.Failed, res.Outcome);
        Assert.Contains("Cannot get ISK", res.Summary);
    }

    [Fact]
    public async Task Status_reads_apv_and_counters()
    {
        var (res, _, _) = await Harness.RunAsync(new Megane2CardProgrammingPlugin(), "status", Def(), Script(), expert: false, confirmed: false);
        Assert.Equal(PluginOutcome.Success, res.Outcome);
        Assert.Equal("PIN CODE OK / KEY LEARNING", res.Summary);
        Assert.Equal("01", res.Values[0].Value);
    }

    [Theory]
    [InlineData("610600" + "0000", "PIN CODE NOT RECOGNIZED")]
    [InlineData("610601" + "0000", "UCH NOT READY")]
    [InlineData("610601" + "0001", "PIN CODE OK / UCH LEARNING")]
    public async Task Status_text_matches_python(string bitsReply, string text)
    {
        var (res, _, _) = await Harness.RunAsync(new Megane2CardProgrammingPlugin(), "status", Def(), Script(_ => bitsReply), expert: false, confirmed: false);
        Assert.Equal(text, res.Summary);
    }

    [Fact]
    public async Task Learn_enters_apv_with_the_computed_pin_learns_and_validates()
    {
        var ui = new ScriptedInteraction(0, 1); // learn one card, then validate
        var (res, log, script) = await Harness.RunAsync(new Megane2CardProgrammingPlugin(), "learn", Def(), Script(), interaction: ui, parameters: new() { ["algo"] = "Algo 1" });
        Assert.Equal(PluginOutcome.Success, res.Outcome);
        Assert.Equal("validated", res.State);
        var wire = Harness.Wire(script).ToList();
        Assert.Equal(["10C0", "21AB", "3B006596FF1D0731"], wire.Take(3));
        Assert.Contains("3B02", wire);
        Assert.Equal("3B03", wire.Last(w => w.StartsWith("3B0", StringComparison.Ordinal) && w != "3B02" && !w.StartsWith("3B00", StringComparison.Ordinal)));
        Assert.DoesNotContain("3B04", wire);
        Assert.Equal(2, ui.Prompts.Count);
    }

    [Fact]
    public async Task Learn_with_manual_pin_skips_the_isk_read()
    {
        var (res, _, script) = await Harness.RunAsync(new Megane2CardProgrammingPlugin(), "learn", Def(), Script(), interaction: new ScriptedInteraction(1), parameters: new() { ["pin"] = "AABBCCDDEEFF" });
        Assert.Equal(PluginOutcome.Success, res.Outcome);
        Assert.DoesNotContain("21AB", Harness.Wire(script));
        Assert.Contains("3B00AABBCCDDEEFF", Harness.Wire(script));
    }

    [Fact]
    public async Task Abandon_leaves_apv_mode_without_validating()
    {
        var (res, _, script) = await Harness.RunAsync(new Megane2CardProgrammingPlugin(), "learn", Def(), Script(), interaction: new ScriptedInteraction(2));
        Assert.Equal(PluginOutcome.Cancelled, res.Outcome);
        Assert.Contains("3B04", Harness.Wire(script));
        Assert.DoesNotContain("3B03", Harness.Wire(script));
    }

    [Fact]
    public async Task Pin_not_recognised_abandons_and_fails()
    {
        var (res, _, script) = await Harness.RunAsync(new Megane2CardProgrammingPlugin(), "learn", Def(), Script(_ => "6106000000"), interaction: new ScriptedInteraction());
        Assert.Equal(PluginOutcome.Failed, res.Outcome);
        Assert.Contains("NOT RECOGNIZED", res.Summary);
        Assert.Equal("3B04", Harness.Wire(script).Last());
    }

    [Fact]
    public async Task Apv_refusal_does_not_continue_and_does_not_abandon_something_never_entered()
    {
        var s = Script(); // override: refuse the APV code
        var refusing = new EcuScript().On("10", "50C0").On("21AB", IskFrame).Negative("3B00", 0x35).On("3B04", "7B04");
        var (res, _, script) = await Harness.RunAsync(new Megane2CardProgrammingPlugin(), "learn", Def(), refusing, interaction: new ScriptedInteraction());
        Assert.Equal(PluginOutcome.Failed, res.Outcome);
        Assert.DoesNotContain("3B04", Harness.Wire(script));
        _ = s;
    }

    [Fact]
    public async Task Cancelling_while_in_apv_mode_still_abandons_it()
    {
        using var cts = new CancellationTokenSource();
        var ui = new CancelOnPrompt(cts);
        var (res, log, script) = await Harness.RunAsync(new Megane2CardProgrammingPlugin(), "learn", Def(), Script(), interaction: ui, ct: cts.Token);
        Assert.Equal(PluginOutcome.Cancelled, res.Outcome);
        Assert.Contains("3B04", Harness.Wire(script));
        Assert.DoesNotContain("3B03", Harness.Wire(script));
    }

    private sealed class CancelOnPrompt(CancellationTokenSource cts) : IPluginInteraction
    {
        public ValueTask<int> ChooseAsync(string title, string message, IReadOnlyList<string> choices, CancellationToken ct)
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return new(0);
        }
    }

    [Fact]
    public async Task Learn_without_interaction_support_fails_safely()
    {
        var (res, _, script) = await Harness.RunAsync(new Megane2CardProgrammingPlugin(), "learn", Def(), Script());
        Assert.Equal(PluginOutcome.Failed, res.Outcome);
        Assert.Contains("3B04", Harness.Wire(script)); // left APV mode
    }
}

public class RunnerTests
{
    private static EcuFile Ab90() => new Def("AB90_J77_X85").List("crash détecté", (0, "a"), (1, "crash détecté"))
        .Req("Start Diagnostic Session", "10C0").Req("Synthèse état UCE", "2102", recv: [("crash détecté", 3)]).Build();

    [Fact]
    public async Task Missing_definition_fails_without_traffic()
    {
        var t = new SimulatedTransport();
        var res = await new PluginRunner(new DictionaryEcuDefinitionProvider()).RunAsync(new Ab90AirbagPlugin(), "check", new PluginRunRequest { Transport = t });
        Assert.Equal(PluginOutcome.Failed, res.Outcome);
        Assert.Contains("not found", res.Summary);
    }

    [Fact]
    public async Task Not_connected_is_blocked()
    {
        var res = await new PluginRunner(new DictionaryEcuDefinitionProvider()).RunAsync(new Ab90AirbagPlugin(), "check", new PluginRunRequest());
        Assert.Equal(PluginOutcome.Blocked, res.Outcome);
    }

    [Fact]
    public async Task Unknown_action_and_bad_parameters_are_blocked()
    {
        var r = new PluginRunner(new DictionaryEcuDefinitionProvider());
        Assert.Equal(PluginOutcome.Blocked, (await r.RunAsync(new Ab90AirbagPlugin(), "nope", new PluginRunRequest())).Outcome);
        var bad = await r.RunAsync(new Megane2CardProgrammingPlugin(), "isk", new PluginRunRequest { Parameters = new Dictionary<string, string> { ["algo"] = "Algo 9" } });
        Assert.Equal(PluginOutcome.Blocked, bad.Outcome);
        var badPin = await r.RunAsync(new Megane2CardProgrammingPlugin(), "learn", new PluginRunRequest { ExpertMode = true, Confirmed = true, Parameters = new Dictionary<string, string> { ["pin"] = "XYZ" } });
        Assert.Equal(PluginOutcome.Blocked, badPin.Outcome);
    }

    [Fact]
    public async Task Silent_ecu_becomes_a_failed_result_not_an_exception()
    {
        var (res, _, _) = await Harness.RunAsync(new Ab90AirbagPlugin(), "check", Ab90(), new EcuScript { DefaultNegativeResponse = null });
        Assert.Equal(PluginOutcome.Failed, res.Outcome);
        Assert.Contains("Communication error", res.Summary);
    }

    [Fact]
    public async Task Cancellation_yields_cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (res, _, script) = await Harness.RunAsync(new Ab90AirbagPlugin(), "check", Ab90(), new EcuScript().On("10", "50C0"), ct: cts.Token);
        Assert.Equal(PluginOutcome.Cancelled, res.Outcome);
        Assert.Empty(script.Received);
    }

    [Fact]
    public async Task Read_only_actions_cannot_send_write_services()
    {
        // a hostile/mismatched definition whose "Synthèse état UCE" is really a write service
        var def = new Def("AB90_J77_X85").List("crash détecté", (0, "a"), (1, "crash détecté"))
            .Req("Start Diagnostic Session", "10C0").Req("Synthèse état UCE", "2E0200", recv: [("crash détecté", 3)]).Build();
        var (res, _, script) = await Harness.RunAsync(new Ab90AirbagPlugin(), "check", def, new EcuScript().On("10", "50C0").On("2E", "6E02"));
        Assert.Equal(PluginOutcome.Failed, res.Outcome);
        Assert.Contains("read-only", res.Summary);
        Assert.Equal(["10C0"], Harness.Wire(script));
    }

    [Fact]
    public async Task Progress_log_has_steps_tx_rx_and_a_result()
    {
        var def = Ab90();
        var (res, log, _) = await Harness.RunAsync(new Ab90AirbagPlugin(), "check", def, new EcuScript().On("10", "50C0").On("2102", "610201"));
        Assert.Equal("crashdetected", res.State);
        Assert.Contains(log.Events, e => e.Kind == PluginEventKind.Step);
        Assert.Equal(["10C0", "2102"], log.Tx);
        Assert.Contains(log.Events, e => e.Kind == PluginEventKind.Rx && e.Message == "61 02 01");
        Assert.Equal(PluginEventKind.Result, log.Events[^1].Kind);
    }

    [Fact]
    public void Registry_has_all_plugins_unique_ids_and_search()
    {
        var reg = PluginRegistry.CreateDefault();
        Assert.Equal(13, reg.All.Count);
        Assert.Equal(reg.All.Count, reg.All.Select(p => p.Info.Id).Distinct().Count());
        Assert.Equal(["Airbag Tools", "EPS Tools", "EVC Tools", "Keys", "UCH Tools", "VIN"], reg.Categories);
        Assert.Contains(reg.Search("megane eps"), p => p.Info.Id == "megane3-eps-reset");
        Assert.All(reg.Search(null, "UCH Tools"), p => Assert.Equal("UCH Tools", p.Info.Category));
        Assert.Throws<InvalidOperationException>(() => reg.Register(new VinCrcPlugin()));
    }

    [Fact]
    public void Every_plugin_that_writes_declares_warnings_and_every_writing_action_declares_requests()
    {
        foreach (var p in PluginRegistry.CreateDefault().All)
        {
            Assert.NotEmpty(p.Actions);
            if (p.Actions.Any(a => a.Writes)) Assert.NotEmpty(p.Info.Warnings);
            foreach (var a in p.Actions.Where(a => a.Writes && p.Info.EcuDefinition != null)) Assert.NotEmpty(a.RequiredRequests);
        }
    }

    [Fact]
    public void Addressing_follows_the_definition()
    {
        var can = EcuAddressing.FromDefinition(new Def("X", send: "18DA10F1", recv: "18DAF110").Build(), out _)!;
        Assert.True(can.Is29Bit); Assert.Equal(0x18DA10F1u, can.TxId);
        var k = EcuAddressing.FromDefinition(new Def("K", Ddt4All.Core.Ecu.EcuProtocol.Kwp2000, funcAddr: "26").Build(), out _)!;
        Assert.Equal(Ddt4All.Comms.EcuProtocol.Kwp2000Fast, k.Protocol); Assert.Equal((byte)0x26, k.FuncAddress);
        Assert.Null(EcuAddressing.FromDefinition(new Def("D", Ddt4All.Core.Ecu.EcuProtocol.DoIp).Build(), out var err)); Assert.NotNull(err);
        Assert.Null(EcuAddressing.FromDefinition(new Def("N", send: "00", recv: "00").Build(), out _));
    }
}
