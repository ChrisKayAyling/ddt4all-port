using System.ComponentModel;
using Ddt4All.App.Models;
using Ddt4All.App.Services;
using Ddt4All.App.ViewModels;
using Ddt4All.App.ViewModels.Plugins;
using Ddt4All.Comms;
using Ddt4All.Comms.Simulation;
using Ddt4All.Core.Abstractions;
using Ddt4All.Core.Ecu;
using Ddt4All.Plugins;
using Ddt4All.Plugins.Tests;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using CoreProtocol = Ddt4All.Core.Ecu.EcuProtocol;

namespace Ddt4All.App.Tests;

/// <summary>Connection stub exposing a scripted simulated transport.</summary>
internal sealed class StubConnection(IAddressableTransport? transport) : IConnectionService
{
    public ConnectionState State => transport is null ? ConnectionState.Disconnected : ConnectionState.Connected;
    public string StatusText => "";
    public AdapterInfo? Adapter => null;
    public IEcuTransport? Transport => transport;
    public IAddressableTransport? AddressableTransport => transport;
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    public Task<IReadOnlyList<SerialPortInfo>> ListPortsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SerialPortInfo>>([]);
    public Task ConnectAsync(ConnectionRequest request, CancellationToken ct = default) => Task.CompletedTask;
    public Task DisconnectAsync() => Task.CompletedTask;
    public Task<string> SendRawAsync(string command, CancellationToken ct = default) => Task.FromResult("");
}

public class PluginsPageTests
{
    private static EcuFile Megane2Uch() => new Def("UCH_84_J84_03_60").List("VSC UCH vierge (NbBadgeAppris=0)", (0, "Codée"), (1, "Vierge"))
        .Req("Start Diagnostic Session", "10C0").Req("StartDiagSession Etude", "10C1")
        .Req("Status général des opérations badges Bits", "2106", recv: [("VSC UCH vierge (NbBadgeAppris=0)", 3)])
        .Req("RAZ EEPROM", "3B0100").Build();

    private static EcuFile Card() => new Def("UCH_84_J84_03_60").Hex("Code APV", 6)
        .List("VSC Code APV_Reconnu", (0, "0"), (1, "1")).List("VSC ModeAPV_ReaffArmé", (0, "0"), (1, "1")).List("VSC ModeAPV_AppUCH_Armé", (0, "0"), (1, "1"))
        .Hex("VSC NbTotalDeBadgeAppris", 1).Hex("VSC Code_IDE", 2)
        .Req("Start Diagnostic Session", "10C0").Req("Trame AB: Trame réservée", "21AB")
        .Req("ACCEDER AU MODE APRES-VENTE", "3B00000000000000", send: [("Code APV", 3)]).Req("APPRENDRE BADGE", "3B02")
        .Req("Status général des opérations badges Bits", "2106", recv: [("VSC Code APV_Reconnu", 3), ("VSC ModeAPV_ReaffArmé", 4), ("VSC ModeAPV_AppUCH_Armé", 5)])
        .Req("Status général des opérations badges Octets", "2108", recv: [("VSC NbTotalDeBadgeAppris", 3), ("VSC Code_IDE", 4)])
        .Req("SORTIE DU MODE APV : VALIDATION", "3B03").Req("SORTIE DU MODE APV : ABANDON", "3B04").Build();

    private static EcuScript UchScript(Func<string> bits) => new EcuScript().On("10", req => new[] { new EcuReply(new byte[] { 0x50, req[1] }) })
        .On("2106", _ => new[] { EcuReply.Of(bits()) }).On("3B01", "7B01")
        .On("21AB", "61AB02FC0D08514C86555400000000000000008DE8EE1679D3C9A7A7CCF6AC00000000000000002A")
        .On("3B00", "7B00").On("3B02", "7B02").On("3B03", "7B03").On("3B04", "7B04").On("2108", "6108010123");

    private sealed class Rig : IDisposable
    {
        public TestApp App = new("en", "light");
        public string Bits = "610600";
        public EcuScript Script;
        public SimulatedTransport Transport = new();
        public PluginsViewModel Vm;
        public Rig(bool connected = true, bool expert = false, string theme = "light")
        {
            App.Dispose(); App = new TestApp("en", theme); Script = UchScript(() => Bits);
            Transport.AddCan("UCH_84_J84_03_60", 0x745, Script);
            var defs = new DictionaryEcuDefinitionProvider().Add("UCH_84_J84_03_60", Megane2Uch());
            // card programming uses the same ECU name, so give it the richer definition through a chained provider
            var card = new DictionaryEcuDefinitionProvider().Add("UCH_84_J84_03_60", Card());
            var session = App.Services.GetRequiredService<SessionState>();
            session.IsExpertMode = expert;
            Vm = new PluginsViewModel(PluginRegistry.CreateDefault(), new SwitchingProvider(defs, card, () => Vm?.SelectedRow?.Id == "megane2-card-programming"),
                new StubConnection(connected ? Transport : null), session, App.Services.GetRequiredService<INotificationService>());
            App.Main.CurrentPage = Vm;
            TestApp.Pump();
        }
        public void Select(string id) { Vm.SelectedRow = Vm.Rows.OfType<PluginRow>().First(r => r.Id == id); TestApp.Pump(); }
        public void Dispose() => App.Dispose();
    }

    private sealed class SwitchingProvider(IEcuDefinitionProvider a, IEcuDefinitionProvider b, Func<bool> useB) : IEcuDefinitionProvider
    {
        public ValueTask<EcuFile?> LoadAsync(string name, CancellationToken ct = default) => (useB() ? b : a).LoadAsync(name, ct);
    }

    [AvaloniaFact]
    public void List_groups_by_category_and_filters_by_search_and_category()
    {
        using var r = new Rig();
        Assert.Equal(13, r.Vm.Rows.OfType<PluginRow>().Count());
        Assert.Equal(6, r.Vm.Rows.OfType<GroupHeaderRow>().Count());
        r.Vm.SearchText = "eps";
        Assert.Equal(3, r.Vm.Rows.OfType<PluginRow>().Count());
        r.Vm.SearchText = "";
        r.Vm.SelectedCategory = r.Vm.Categories.First(c => c.Id == "UCH Tools");
        Assert.Equal(4, r.Vm.Rows.OfType<PluginRow>().Count());
        r.Vm.SearchText = "zzzz";
        Assert.True(r.Vm.IsEmpty);
    }

    [AvaloniaFact]
    public async Task Write_action_is_blocked_without_expert_mode_and_needs_explicit_confirmation()
    {
        using var r = new Rig(expert: false);
        r.Select("megane2-uch-reset");
        var reset = r.Vm.Actions.First(a => a.Id == "reset");
        Assert.Equal("Expert mode required", reset.BlockReason);
        await reset.RunCommand.ExecuteAsync(null);                // disabled: nothing happens
        Assert.False(r.Vm.IsConfirming);
        Assert.Empty(r.Script.Received);

        r.App.Services.GetRequiredService<SessionState>().IsExpertMode = true;
        TestApp.Pump(); await r.App.PumpAsync(50);
        Assert.Null(reset.BlockReason);
        await reset.RunCommand.ExecuteAsync(null);
        Assert.True(r.Vm.IsConfirming);                           // confirmation step shown, still nothing sent
        Assert.Empty(r.Script.Received);
        await r.Vm.ConfirmAndRunCommand.ExecuteAsync(null);       // not acknowledged yet: no-op
        Assert.True(r.Vm.IsConfirming);
        Assert.Empty(r.Script.Received);

        r.Vm.Acknowledged = true;
        await r.Vm.ConfirmAndRunCommand.ExecuteAsync(null);
        Assert.False(r.Vm.IsConfirming);
        Assert.Equal(PluginOutcome.Success, r.Vm.Result!.Outcome); // script reports "Codée", so the gate lets the reset through
        Assert.Equal(["10C0", "2106", "10C1", "3B0100"], r.Script.Received.Select(Convert.ToHexString));
    }

    [AvaloniaFact]
    public async Task Read_only_action_runs_immediately_and_logs_steps()
    {
        using var r = new Rig();
        r.Select("megane2-uch-reset");
        await r.Vm.Actions.First(a => a.Id == "check").RunCommand.ExecuteAsync(null);
        await r.App.PumpAsync(50);
        Assert.Equal(PluginOutcome.Success, r.Vm.Result!.Outcome);
        Assert.Equal("coded", r.Vm.Result.State);
        Assert.Contains(r.Vm.Log, l => l.IsTx && l.Text.Contains("10 C0"));
        Assert.Contains(r.Vm.Log, l => l.IsStep);
        Assert.Equal(["10C0", "2106"], r.Script.Received.Select(Convert.ToHexString));
        Assert.False(r.Vm.IsRunning);
    }

    [AvaloniaFact]
    public async Task Not_connected_blocks_everything_that_needs_hardware()
    {
        using var r = new Rig(connected: false, expert: true);
        r.Select("megane2-uch-reset");
        Assert.All(r.Vm.Actions, a => Assert.Equal("Connect to an adapter first", a.BlockReason));
        r.Select("vin-crc");
        var calc = r.Vm.Actions.Single();
        calc.Parameters[0].Value = "VF1BB05C123456789";
        TestApp.Pump();
        Assert.Null(calc.BlockReason);                             // CRC calculator needs no adapter
        await calc.RunCommand.ExecuteAsync(null);
        Assert.Equal("55FA", r.Vm.Result!.Values.Single().Value);
    }

    [AvaloniaFact]
    public async Task Cancel_stops_a_run_between_requests()
    {
        using var r = new Rig(expert: true);
        r.Transport.Latency = TimeSpan.FromMilliseconds(300);
        r.Select("megane2-uch-reset");
        var check = r.Vm.Actions.First(a => a.Id == "check");
        var run = check.RunCommand.ExecuteAsync(null);
        await r.App.PumpAsync(100);
        Assert.True(r.Vm.IsRunning);
        Assert.True(r.Vm.CancelRunCommand.CanExecute(null));
        r.Vm.CancelRunCommand.Execute(null);
        await run;
        Assert.Equal(PluginOutcome.Cancelled, r.Vm.Result!.Outcome);
        Assert.False(r.Vm.IsRunning);
    }

    [AvaloniaFact]
    public async Task Operator_prompts_are_answered_from_the_page()
    {
        using var r = new Rig(expert: true);
        r.Bits = "6106010100";
        r.Select("megane2-card-programming");
        var learn = r.Vm.Actions.First(a => a.Id == "learn");
        learn.Parameters.First(p => p.Id == "algo").Value = "Algo 1";
        await learn.RunCommand.ExecuteAsync(null);
        r.Vm.Acknowledged = true;
        var run = r.Vm.ConfirmAndRunCommand.ExecuteAsync(null);
        for (int i = 0; i < 100 && !r.Vm.HasPrompt; i++) await r.App.PumpAsync(20);
        Assert.True(r.Vm.HasPrompt);
        Assert.Equal(["Learn card", "Validate", "Abandon"], r.Vm.PromptChoices.Select(c => c.Text));
        r.Vm.PromptChoices[1].Command.Execute(null);               // Validate
        await run;
        Assert.Equal(PluginOutcome.Success, r.Vm.Result!.Outcome);
        Assert.Contains("3B03", r.Script.Received.Select(Convert.ToHexString));
    }

    // ------------------------------------------------------------------ screenshots (docs/screenshots)

    [AvaloniaFact]
    public async Task Screenshots()
    {
        using (var r = new Rig(connected: false, expert: false))
        {
            r.Select("megane2-uch-reset");
            await r.App.PumpAsync(300);
            r.App.Screenshot("16-plugins-list-detail-safe-mode");
        }
        using (var r = new Rig(expert: true))
        {
            r.Select("megane2-uch-reset");
            var reset = r.Vm.Actions.First(a => a.Id == "reset");
            await reset.RunCommand.ExecuteAsync(null);
            r.Vm.Acknowledged = true;
            await r.App.PumpAsync(300);
            r.App.Screenshot("17-plugins-confirmation");
        }
        using (var r = new Rig(expert: true, theme: "dark"))
        {
            r.Select("megane2-uch-reset");
            await r.Vm.Actions.First(a => a.Id == "reset").RunCommand.ExecuteAsync(null);
            r.Vm.Acknowledged = true;
            await r.Vm.ConfirmAndRunCommand.ExecuteAsync(null);
            await r.App.PumpAsync(300);
            r.App.Screenshot("18-plugins-result-dark");
        }
        using (var r = new Rig(expert: true))
        {
            r.Bits = "6106010100";
            r.Select("megane2-card-programming");
            var learn = r.Vm.Actions.First(a => a.Id == "learn");
            await learn.RunCommand.ExecuteAsync(null);
            r.Vm.Acknowledged = true;
            var run = r.Vm.ConfirmAndRunCommand.ExecuteAsync(null);
            for (int i = 0; i < 100 && !r.Vm.HasPrompt; i++) await r.App.PumpAsync(20);
            await r.App.PumpAsync(200);
            r.App.Screenshot("19-plugins-operator-prompt");
            r.Vm.PromptChoices[2].Command.Execute(null);
            await run;
        }
    }
}
