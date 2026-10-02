using Ddt4All.Comms.Simulation;
using Ddt4All.Core.Ecu;
using Ddt4All.Plugins;
using Ddt4All.Plugins.Vehicles;
using CoreProtocol = Ddt4All.Core.Ecu.EcuProtocol;

namespace Ddt4All.Plugins.Tests;

/// <summary>One scripted-ECU scenario for a "check + reset" plugin.</summary>
public sealed record VirginCase(string Id, Func<IPlugin> Plugin, Func<EcuFile> Def, string StatusPrefix, string VirginReply, string CodedReply,
    string VirginState, string CodedState, string[] CheckWire, string[] ResetWire, string ResetPrefix, bool Gated, string[]? ExtraRules = null)
{
    public override string ToString() => Id;

    public EcuScript Script(string? statusReply, string? resetReply = null)
    {
        var s = new EcuScript();
        // sessions: echo the session byte
        s.On("10", req => new[] { new EcuReply(new byte[] { 0x50, req[1] }) });
        if (ExtraRules is not null)
            for (int i = 0; i < ExtraRules.Length; i += 2) s.On(ExtraRules[i], ExtraRules[i + 1]);
        if (statusReply is not null) s.On(StatusPrefix, statusReply);
        if (resetReply is not null) s.On(ResetPrefix, resetReply);
        return s;
    }
}

public class VirginizerTests
{
    private static readonly string VinHex = Convert.ToHexString("VF1BB05C123456789"u8.ToArray());

    public static IEnumerable<object[]> Cases()
    {
        // ---------------- airbags (reset is not gated, like the Python buttons which were always enabled)
        yield return [new VirginCase("ab90", () => new Ab90AirbagPlugin(),
            () => new Def("AB90_J77_X85").List("crash détecté", (0, "pas de crash"), (1, "crash détecté")).Hex("code d'accès pour reset UCE", 4)
                .Req("Start Diagnostic Session", "10C0").Req("Synthèse état UCE", "2102", recv: [("crash détecté", 3)])
                .Req("Reset crash ou accès au mode fournisseur", "3B0100000000", send: [("code d'accès pour reset UCE", 3)]).Build(),
            "2102", "610200", "610201", "nocrash", "crashdetected", ["10C0", "2102"], ["10C0", "3B0122041998"], "3B01", false)];

        yield return [new VirginCase("megane3-airbag", () => new Megane3AirbagPlugin(),
            () => new Def("MRSZ_X95_L38_L43_L47_20110505T101858").List("Session Name", (0xC0, "extendedDiagnosticSession"), (0xFA, "systemSupplierSpecific"))
                .List("crash détecté", (0, "pas de crash"), (1, "crash détecté")).Hex("code d'accès pour reset UCE", 4)
                .Req("Start Diagnostic Session", "1000", send: [("Session Name", 2)]).Req("Synthèse état UCE avant crash", "2102", recv: [("crash détecté", 3)])
                .Req("Reset crash ou accès au mode fournisseur", "3B0100000000", send: [("code d'accès pour reset UCE", 3)]).Build(),
            "2102", "610200", "610201", "nocrash", "crashdetected", ["10C0", "2102"], ["10FA", "3B0127081977"], "3B01", false)];

        yield return [new VirginCase("rsat4-airbag", () => new Rsat4AirbagPlugin(),
            () => new Def("RSAT4_ACU_eng_v15_20150511T131328").List("Session Name", (0xC0, "extendedDiagnosticSession"))
                .List("crash detected", (0, "no crash detected"), (1, "crash detected")).Hex("CLEDEV For reset crash", 4)
                .Req("Start Diagnostic Session", "1000", send: [("Session Name", 2)]).Req("Reading of ECU state synthesis", "2102", recv: [("crash detected", 3)])
                .Req("Reset Crash", "3B0100000000", send: [("CLEDEV For reset crash", 3)]).Build(),
            "2102", "610200", "610201", "nocrash", "crashdetected", ["10C0", "2102"], ["10C0", "3B0113041976"], "3B01", false)];

        // ---------------- EPS (reset gated on "coded")
        yield return [new VirginCase("clio4-eps", () => new Clio4EpsPlugin(),
            () => new Def("X98ph2_X87ph2_EPS_HFP_v1.00_20150622T140219_20160726T172209").List("DongleState", (0, "NotOperational"), (1, "OperationalBlanked"), (2, "OperationalLearnt"))
                .Hex("Dongle.Code", 2)
                .Req("StartDiagnosticSession.extendedSession", "10C0").Req("StartDiagnosticSession.supplierSession", "10FA")
                .Req("DataRead.DongleState", "22D001", recv: [("DongleState", 4)])
                .Req("SRBLID.DongleBlanking.Request", "3101FF0000", send: [("Dongle.Code", 4)]).Build(),
            "22D001", "62D00101", "62D00102", "virgin", "coded", ["10C0", "22D001"], ["10C0", "22D001", "10FA", "3101FF1976"], "3101", true)];

        yield return [new VirginCase("megane3-eps", () => new Megane3EpsPlugin(),
            () => new Def("DAE_X95_X38_X10_v1.88_20120228T113904").List("DID - Dongle state", (0, "Not operational"), (1, "Operational blank"), (2, "Operational learnt"))
                .Req("SDS - Start Diagnostic Session $C0", "10C0").Req("SDS - Start Diagnostic Session $FA", "10FA")
                .Req("DataRead.DID - Dongle state", "22D001", recv: [("DID - Dongle state", 4)])
                .Req("SRBLID - Dongle blanking", "3101FF01").Build(),
            "22D001", "62D00101", "62D00102", "virgin", "coded", ["10C0", "22D001"], ["10C0", "22D001", "10FA", "3101FF01"], "3101", true)];

        yield return [new VirginCase("clio3-eps", () => new Clio3EpsPlugin(),
            () => new Def("DAE_J77_X85_Gen2___v3.7").Ascii("VIN", 17).Ascii("VINW", 17)
                .List("Dongle status", (0, Clio3EpsPlugin.VirginText), (1, "Système CODE"))
                .Req("Start Diagnostic Session", "10C0").Req("SDS - Start Diagnostic $FB", "10FB")
                .Req("RDBLI - VIN", "2281", recv: [("VIN", 3)]).Req("RDBLI - System Frame", "2201", recv: [("Dongle status", 3)])
                .Req("WDBLI - Erase of Dongle_ID code", "2E0100").Build(),
            "2201", "620100", "620101", "virgin", "coded", ["10C0", "2281", "2201"], ["10C0", "2281", "2201", "10FB", "2E0100"], "2E01", true,
            ["2281", "6281" + VinHex])];

        // ---------------- UCH (reset gated on "coded")
        yield return [new VirginCase("laguna2-uch", () => new Laguna2UchPlugin(),
            () => new Def("UCH___M2S_X74_et_X73", CoreProtocol.Kwp2000, funcAddr: "26").List("Session Name", (0x81, "Etude"), (0x85, "APV")).List("UCH vierge", (0, "non"), (1, "oui"))
                .Req("Start Diagnostic Session", "1000", send: [("Session Name", 2)]).Req("Lecture Etats Antidémarrage et acces", "2150", recv: [("UCH vierge", 3)])
                .Req("Effacement_données_antidem_acces", "3B5000").Build(),
            "2150", "615001", "615000", "virgin", "coded", ["1081", "1085", "2150"], ["1081", "1085", "2150", "1081", "3B5000"], "3B50", true)];

        yield return [new VirginCase("laguna3-uch", () => new Laguna3UchPlugin(),
            () => new Def("BCM_X91_L43_S_S_SWC_v1.30_20140613T140906").List("BCM_IS_BLANK_S", (0, "false"), (1, "true"))
                .Req("Start Diagnostic Session", "10C0").Req("Read_A_AC_General_Identifiers_Learning_Status_(bits)_BCM_Input/Output", "22D800", recv: [("BCM_IS_BLANK_S", 4)])
                .Req("SR_RESERVED VSC 1", "3101A001").Build(),
            "22D800", "62D80001", "62D80000", "virgin", "coded", ["10C0", "22D800"], ["10C0", "22D800", "10C0", "3101A001"], "3101", true)];

        yield return [new VirginCase("megane3-uch", () => new Megane3UchPlugin(),
            () => new Def("BCM_X95_SW_2_V_1_2").List("VSC UCH vierge (NbBadgeAppris=0)", (0, "inactif"), (1, "Actif"))
                .Req("Start Diagnostic Session", "10C0").Req("Read_A_AC_General_Identifiers_Learning_Status_(bits)_BCM_Input/Output", "22D800", recv: [("VSC UCH vierge (NbBadgeAppris=0)", 4)])
                .Req("SR_RESERVED VSC 1", "3101A001").Build(),
            "22D800", "62D80001", "62D80000", "virgin", "coded", ["10C0", "22D800"], ["10C0", "22D800", "10C0", "3101A001"], "3101", true)];

        yield return [new VirginCase("megane2-uch", () => new Megane2UchPlugin(),
            () => new Def("UCH_84_J84_03_60").List("VSC UCH vierge (NbBadgeAppris=0)", (0, "Codée"), (1, "Vierge"))
                .Req("Start Diagnostic Session", "10C0").Req("StartDiagSession Etude", "10C1")
                .Req("Status général des opérations badges Bits", "2106", recv: [("VSC UCH vierge (NbBadgeAppris=0)", 3)])
                .Req("RAZ EEPROM", "3B0100").Build(),
            "2106", "610601", "610600", "virgin", "coded", ["10C0", "2106"], ["10C0", "2106", "10C1", "3B0100"], "3B01", true)];
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task Check_reports_the_expected_state_and_sends_the_expected_bytes(VirginCase c)
    {
        var (virgin, _, vs) = await Harness.RunAsync(c.Plugin(), "check", c.Def(), c.Script(c.VirginReply));
        Assert.Equal(PluginOutcome.Success, virgin.Outcome);
        Assert.Equal(c.VirginState, virgin.State);
        Assert.Equal(c.CheckWire, Harness.Wire(vs));

        var (coded, _, _) = await Harness.RunAsync(c.Plugin(), "check", c.Def(), c.Script(c.CodedReply));
        Assert.Equal(PluginOutcome.Success, coded.Outcome);
        Assert.Equal(c.CodedState, coded.State);
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task Check_with_a_negative_reply_is_unexpected_not_a_crash(VirginCase c)
    {
        var (res, _, _) = await Harness.RunAsync(c.Plugin(), "check", c.Def(), c.Script("7F2131"));
        Assert.Equal(PluginOutcome.Warning, res.Outcome);
        Assert.Contains("UNEXPECTED RESPONSE", res.Summary);
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task Reset_sends_the_expected_sequence_when_allowed(VirginCase c)
    {
        var (res, log, script) = await Harness.RunAsync(c.Plugin(), "reset", c.Def(), c.Script(c.CodedReply, PositiveOf(c.ResetPrefix)));
        Assert.Equal(PluginOutcome.Success, res.Outcome);
        Assert.Equal("CLEAR EXECUTED", res.Summary);
        Assert.Equal(c.ResetWire, Harness.Wire(script));
        Assert.Contains(log.Events, e => e.Kind == PluginEventKind.Result);
    }

    private static string PositiveOf(string prefix) => ((byte)(Convert.ToByte(prefix[..2], 16) + 0x40)).ToString("X2") + prefix[2..];

    [Theory, MemberData(nameof(Cases))]
    public async Task Reset_negative_reply_is_reported_as_failed(VirginCase c)
    {
        var (res, _, script) = await Harness.RunAsync(c.Plugin(), "reset", c.Def(), c.Script(c.CodedReply, null)); // reset service answers 7F 11 by default
        Assert.Equal(PluginOutcome.Failed, res.Outcome);
        Assert.Contains("CLEAR FAILED", res.Summary);
        Assert.Equal(c.ResetWire, Harness.Wire(script));
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task Reset_is_gated_on_coded_like_the_python_buttons(VirginCase c)
    {
        var (res, _, script) = await Harness.RunAsync(c.Plugin(), "reset", c.Def(), c.Script(c.VirginReply, PositiveOf(c.ResetPrefix)));
        if (!c.Gated)
        {
            Assert.Equal(PluginOutcome.Success, res.Outcome); // airbag: always allowed
            return;
        }
        Assert.Equal(PluginOutcome.Blocked, res.Outcome);
        Assert.DoesNotContain(Harness.Wire(script), w => w.StartsWith(c.ResetPrefix, StringComparison.Ordinal));
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task Reset_is_refused_when_the_state_cannot_be_read(VirginCase c)
    {
        if (!c.Gated) return;
        var (res, _, script) = await Harness.RunAsync(c.Plugin(), "reset", c.Def(), c.Script("7F2131", PositiveOf(c.ResetPrefix)));
        Assert.Equal(PluginOutcome.Blocked, res.Outcome);
        Assert.DoesNotContain(Harness.Wire(script), w => w.StartsWith(c.ResetPrefix, StringComparison.Ordinal));
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task Writing_actions_need_expert_mode_and_confirmation_and_send_nothing(VirginCase c)
    {
        var (a, _, sa) = await Harness.RunAsync(c.Plugin(), "reset", c.Def(), c.Script(c.CodedReply, PositiveOf(c.ResetPrefix)), expert: false);
        Assert.Equal(PluginOutcome.Blocked, a.Outcome);
        Assert.Contains("Expert mode", a.Summary);
        Assert.Empty(sa.Received);
        var (b, _, sb) = await Harness.RunAsync(c.Plugin(), "reset", c.Def(), c.Script(c.CodedReply, PositiveOf(c.ResetPrefix)), confirmed: false);
        Assert.Equal(PluginOutcome.Blocked, b.Outcome);
        Assert.Empty(sb.Received);
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task Check_works_without_expert_mode(VirginCase c)
    {
        var (res, _, _) = await Harness.RunAsync(c.Plugin(), "check", c.Def(), c.Script(c.CodedReply), expert: false, confirmed: false);
        Assert.Equal(PluginOutcome.Success, res.Outcome);
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task Refused_session_aborts_a_write_before_anything_is_written(VirginCase c)
    {
        var s = new EcuScript().Negative("10", 0x22);
        var (res, _, script) = await Harness.RunAsync(c.Plugin(), "reset", c.Def(), s);
        Assert.NotEqual(PluginOutcome.Success, res.Outcome);
        Assert.DoesNotContain(Harness.Wire(script), w => w.StartsWith(c.ResetPrefix, StringComparison.Ordinal));
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task Missing_request_in_the_definition_stops_before_any_traffic(VirginCase c)
    {
        var def = c.Def();
        var broken = new Def(def.EcuName, def.Protocol, def.SendId, def.RecvId, def.FuncAddr).Build(); // no requests at all
        var (res, _, script) = await Harness.RunAsync(c.Plugin(), "reset", broken, c.Script(c.CodedReply));
        Assert.Equal(PluginOutcome.Failed, res.Outcome);
        Assert.Contains("lacks required request", res.Summary);
        Assert.Empty(script.Received);
    }
}
