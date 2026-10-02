using Ddt4All.Comms;
using Ddt4All.Comms.Elm;
using Ddt4All.Comms.Serial;
using Ddt4All.Comms.Simulation;

namespace Ddt4All.Comms.Tests;

public class HandshakeTests
{
    private static EcuScript Script() => new EcuScript().On("22F186", "62F18603");
    private static readonly EcuAddress Ecu = EcuAddress.Can("E", "7E0", "7E8");

    [Fact]
    public async Task Baud_negotiation_finds_the_adapter_speed()
    {
        // Host starts at 38400 but the adapter only talks 115200.
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("E", 0x7E0, 0x7E8, Script())),
            new SimulatedElmOptions { RequiredBaud = 115200 }, o => { o.InitialBaud = 38400; });
        Assert.Equal(115200, rig.Elm.Connection.Link.BaudRate);
        Assert.Equal(115200, rig.Elm.Info.BaudRate);
        await rig.Elm.ConnectEcuAsync(Ecu);
        Assert.Equal(Hex.Parse("62F18603"), await rig.Elm.RequestAsync(Hex.Parse("22F186")));
    }

    [Fact]
    public async Task Without_auto_baud_a_wrong_speed_fails()
    {
        var bus = new SimulatedBus();
        var (host, dev) = SimulatedElm.Create(bus, new SimulatedElmOptions { RequiredBaud = 115200 });
        await using var _ = dev;
        await using var h = host;
        var ex = await Assert.ThrowsAsync<EcuTimeoutException>(() => ElmTransport.ConnectAsync(host, new ElmOptions
        {
            AutoBaud = false, FirstProbeTimeout = TimeSpan.FromMilliseconds(100),
        }));
        Assert.Contains("No ELM327", ex.Message);
    }

    [Fact]
    public async Task No_adapter_at_all_times_out_quickly()
    {
        var bus = new SimulatedBus();
        var (host, dev) = SimulatedElm.Create(bus);
        dev.Mute = true;
        await using var _ = dev;
        await using var h = host;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<EcuTimeoutException>(() => ElmTransport.ConnectAsync(host, new ElmOptions
        {
            FirstProbeTimeout = TimeSpan.FromMilliseconds(50), FallbackProbeTimeout = TimeSpan.FromMilliseconds(20),
        }));
        Assert.True(sw.ElapsedMilliseconds < 3000);
    }

    [Fact]
    public async Task Garbage_device_is_rejected()
    {
        var (host, dev) = InMemorySerialLink.CreatePair();
        // A "device" that answers every line with nonsense.
        var echo = Task.Run(async () =>
        {
            var buf = new byte[64];
            try { while (true) { int n = await dev.ReadAsync(buf); if (n == 0) break; await dev.WriteAsync("HELLO WORLD\r>"u8.ToArray()); } } catch { }
        });
        await using var h = host;
        await Assert.ThrowsAsync<EcuTimeoutException>(() => ElmTransport.ConnectAsync(host, new ElmOptions
        {
            AutoBaud = false, FirstProbeTimeout = TimeSpan.FromMilliseconds(100),
        }));
        await dev.DisposeAsync();
        await echo;
    }

    [Fact]
    public async Task Stn_adapter_is_detected_as_obdlink_and_speed_switch_uses_ST_SBR()
    {
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("E", 0x7E0, 0x7E8, Script())),
            new SimulatedElmOptions { Version = "ELM327 v1.4b", StnId = "STN1110 v4.2.0", RequiredBaud = 115200, SupportedBauds = new[] { 115200, 500000, 1000000 } },
            o => { o.Profile = ElmProfiles.ObdLinkSx; o.InitialBaud = 115200; o.TargetBaud = 500000; }, hostBaud: 115200);
        Assert.True(rig.Elm.Info.IsStn);
        Assert.Equal("obdlink", rig.Elm.Profile.Key);
        Assert.Equal("STN1110 V4.2.0", rig.Elm.Info.StnId);
        Assert.Equal(500000, rig.Elm.Connection.Link.BaudRate);
        Assert.Contains("ST SBR 500000", rig.Device.Commands);
        await rig.Elm.ConnectEcuAsync(Ecu);
        Assert.Equal(Hex.Parse("62F18603"), await rig.Elm.RequestAsync(Hex.Parse("22F186")));
    }

    [Fact]
    public async Task Elm327_speed_switch_uses_ATBRD_with_the_confirmation_handshake()
    {
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("E", 0x7E0, 0x7E8, Script())),
            new SimulatedElmOptions { RequiredBaud = 38400 },
            o => { o.TargetBaud = 115200; });
        Assert.Equal(115200, rig.Elm.Connection.Link.BaudRate);
        Assert.Contains("ATBRD 23", rig.Device.Commands);   // 4 000 000 / 115200 = 35 = 0x23
        await rig.Elm.ConnectEcuAsync(Ecu);
        Assert.Equal(Hex.Parse("62F18603"), await rig.Elm.RequestAsync(Hex.Parse("22F186")));
    }

    [Fact]
    public async Task Unsupported_speed_switch_is_refused_and_connection_survives()
    {
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("E", 0x7E0, 0x7E8, Script())),
            new SimulatedElmOptions { SupportedBauds = new[] { 115200 } });
        await Assert.ThrowsAsync<ProtocolException>(async () => await rig.Elm.SwitchBaudAsync(500000));
        await rig.Elm.ConnectEcuAsync(Ecu);
        Assert.Equal(Hex.Parse("62F18603"), await rig.Elm.RequestAsync(Hex.Parse("22F186")));
    }

    [Theory]
    [InlineData("VLINKER", "vlinker")]
    [InlineData("STD_BT", "elm327")]
    [InlineData("STD_WIFI", "elm327")]
    [InlineData("STD_USB", "elm327_usb")]
    [InlineData("OBDLINK", "obdlink")]
    [InlineData("OBDLINK_EX", "obdlink_ex")]
    [InlineData("ELS27", "els27_v5")]
    [InlineData("VGATE", "vgate")]
    [InlineData("DERLEK", "derlek_usb_diag2")]
    [InlineData("DERLEK_USB_DIAG3", "derlek_usb_diag3")]
    [InlineData("USBCAN", "usbcan")]
    [InlineData("something-else", "elm327")]
    public void Adapter_type_names_map_to_profiles(string adapterType, string key) =>
        Assert.Equal(key, ElmProfiles.FromAdapterType(adapterType).Key);

    [Fact]
    public void Profiles_match_the_readme_device_table()
    {
        var v = ElmProfiles.VLinker; Assert.Equal((38400, 3.0, false), (v.DefaultBaud, v.TimeoutSeconds, v.RtsCts));
        var g = ElmProfiles.VGate; Assert.Equal((115200, 2.0), (g.DefaultBaud, g.TimeoutSeconds)); Assert.Contains(1000000, g.AvailableBauds);
        var o = ElmProfiles.ObdLinkSx; Assert.Equal((115200, 2.0, true), (o.DefaultBaud, o.TimeoutSeconds, o.RtsCts)); Assert.Contains(2000000, o.AvailableBauds);
        Assert.True(ElmProfiles.ObdLinkEx.RtsCts);
        var e = ElmProfiles.Elm327; Assert.Equal((38400, 5.0), (e.DefaultBaud, e.TimeoutSeconds));
        var s = ElmProfiles.Els27V5; Assert.Equal((38400, 4.0), (s.DefaultBaud, s.TimeoutSeconds)); Assert.NotEmpty(s.PostInitCommands);
        Assert.Equal(4.0, ElmProfiles.DerlekDiag2.TimeoutSeconds);
        Assert.Equal(12, ElmProfiles.All.Count);
        Assert.Equal("vlinker", ElmProfiles.PreferredOrder[0]);
    }

    [Theory]
    [InlineData("ELM327 v1.5", "elm327")]
    [InlineData("VGATE iCAR Pro", "vgate")]
    [InlineData("OBDLink SX r2.2", "obdlink")]
    [InlineData("STN1110 v4.2.0", "obdlink")]
    [InlineData("DERLEK USB-DIAG3", "derlek_usb_diag3")]
    [InlineData("ELS27 V5", "els27_v5")]
    [InlineData("vLinker FS", "vlinker")]
    [InlineData("garbage", "unknown")]
    public void Profile_detection_from_version_text(string text, string key) => Assert.Equal(key, ElmProfiles.DetectFromVersion(text).Key);

    [Theory]
    [InlineData("FTDI FT232R USB UART", null, null, null)]
    [InlineData("OBDII to RS232 Interpreter", null, null, "vlinker")]
    [InlineData("USB Serial", 0x0403, 0x6015, "obdlink")]
    [InlineData("whatever", 0x0483, 0x5741, "derlek_usb_diag3")]
    public void Profile_guess_from_port_description(string desc, int? vid, int? pid, string? key) =>
        Assert.Equal(key, ElmProfiles.GuessFromDescription(desc, vid, pid)?.Key);

    [Fact]
    public async Task ELS27_profile_runs_post_init_commands()
    {
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("E", 0x7E0, 0x7E8, Script())),
            new SimulatedElmOptions { Version = "ELS27 V5" }, o => o.Profile = ElmProfiles.Els27V5);
        Assert.Contains("ATSP6", rig.Device.Commands);
        Assert.Contains("ATSH81", rig.Device.Commands);
    }

    [Fact]
    public async Task Voltage_and_raw_commands()
    {
        await using var rig = await TestRig.CreateAsync(_ => { }, new SimulatedElmOptions { Voltage = 13.8 });
        Assert.Equal(13.8, await rig.Elm.ReadVoltageAsync());
        var r = await rig.Elm.SendAtAsync("ATDPN");
        Assert.Equal("A", r.Lines.Single());
        var bad = await rig.Elm.SendAtAsync("ATXYZ");
        Assert.Equal("?", bad.Error);
    }

    [Fact]
    public async Task Dispose_resets_the_adapter()
    {
        var rig = await TestRig.CreateAsync(_ => { });
        await rig.Elm.DisposeAsync();
        await Task.Delay(50);
        Assert.Equal("ATZ", rig.Device.Commands.Last());
        await rig.Device.DisposeAsync();
    }
}
