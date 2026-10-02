using Ddt4All.Comms;
using Ddt4All.Comms.DoIp;
using Ddt4All.Comms.Diagnostics;
using Ddt4All.Comms.Scanning;
using Ddt4All.Comms.Simulation;

namespace Ddt4All.Comms.Tests;

public class DoIpTests
{
    private static async Task<(SimulatedDoIpGateway Gw, DoIpClient Client)> Setup(Action<SimulatedDoIpGateway>? cfg = null)
    {
        var gw = SimulatedDoIpGateway.Create();
        gw.AddEcu(0x1001, new EcuScript().On("22F190", "62F190414243").Pending("3101", 2, "7101", TimeSpan.Zero).Negative("2E", 0x33));
        cfg?.Invoke(gw);
        var c = await DoIpClient.OpenAsync(new DoIpOptions { Host = "127.0.0.1", Port = gw.TcpPort, RequestTimeout = TimeSpan.FromSeconds(2) });
        return (gw, c);
    }

    [Fact]
    public void Header_roundtrip_and_validation()
    {
        var frame = DoIpFraming.Build(DoIpPayloadType.DiagnosticMessage, new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { 0x02, 0xFD, 0x80, 0x01, 0, 0, 0, 3, 1, 2, 3 }, frame);
        var msg = DoIpFraming.ParseDatagram(frame);
        Assert.Equal(DoIpPayloadType.DiagnosticMessage, msg.Type);
        Assert.Equal(new byte[] { 1, 2, 3 }, msg.Payload);
        var bad = (byte[])frame.Clone(); bad[1] = 0x00;
        Assert.Throws<ProtocolException>(() => DoIpFraming.ParseDatagram(bad));
        Assert.Throws<ProtocolException>(() => DoIpFraming.ParseDatagram(frame[..10]));
        var huge = DoIpFraming.Build(DoIpPayloadType.DiagnosticMessage, ReadOnlySpan<byte>.Empty); huge[4] = 0x7F;
        Assert.Throws<ProtocolException>(() => DoIpFraming.ParseHeader(huge, out _));
    }

    [Fact]
    public async Task Discovery_finds_the_gateway()
    {
        await using var gw = SimulatedDoIpGateway.Create("VF1TESTVIN0000001", 0x1234);
        var v = await DoIpDiscovery.DiscoverFirstAsync(TimeSpan.FromSeconds(2), gw.UdpEndPoint);
        Assert.NotNull(v);
        Assert.Equal("VF1TESTVIN0000001", v!.Vin);
        Assert.Equal(0x1234, v.LogicalAddress);
        Assert.Equal("010203040506", v.EidHex);
    }

    [Fact]
    public async Task Discovery_times_out_without_entities()
    {
        var v = await DoIpDiscovery.DiscoverFirstAsync(TimeSpan.FromMilliseconds(150), new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1));
        Assert.Null(v);
    }

    [Fact]
    public async Task Routing_activation_and_request()
    {
        var (gw, c) = await Setup();
        await using var _g = gw; await using var _c = c;
        Assert.Equal(0x1000, c.EntityAddress);
        Assert.Equal(1, gw.Activations);
        await c.ConnectEcuAsync(new EcuAddress { Protocol = EcuProtocol.DoIp, TxId = 0x1001 });
        Assert.Equal(Hex.Parse("62F190414243"), await c.RequestAsync(Hex.Parse("22F190")));
        Assert.Equal(new byte[] { 0x41, 0x42, 0x43 }, await c.ReadDataByIdentifierAsync(0xF190));
    }

    [Fact]
    public async Task Pending_and_negative_responses()
    {
        var (gw, c) = await Setup();
        await using var _g = gw; await using var _c = c;
        await c.ConnectEcuAsync(new EcuAddress { Protocol = EcuProtocol.DoIp, TxId = 0x1001 });
        Assert.Equal(Hex.Parse("7101"), await c.RequestAsync(Hex.Parse("3101")));
        Assert.Equal(Hex.Parse("7F2E33"), await c.RequestAsync(Hex.Parse("2E0001")));
    }

    [Fact]
    public async Task Unknown_target_is_nacked()
    {
        var (gw, c) = await Setup();
        await using var _g = gw; await using var _c = c;
        await c.ConnectEcuAsync(new EcuAddress { Protocol = EcuProtocol.DoIp, TxId = 0x2222 });
        await Assert.ThrowsAsync<ProtocolException>(async () => await c.RequestAsync(Hex.Parse("22F190")));
    }

    [Fact]
    public async Task Routing_activation_denied_throws()
    {
        await using var gw = SimulatedDoIpGateway.Create();
        gw.ActivationDenyCode = 0x06;
        await Assert.ThrowsAsync<ProtocolException>(() => DoIpClient.OpenAsync(new DoIpOptions { Host = "127.0.0.1", Port = gw.TcpPort }));
    }

    [Fact]
    public async Task Connection_refused_is_reported()
    {
        await Assert.ThrowsAnyAsync<CommsException>(() => DoIpClient.OpenAsync(new DoIpOptions { Host = "127.0.0.1", Port = 1, ConnectTimeout = TimeSpan.FromSeconds(1) }));
    }

    [Fact]
    public async Task Alive_check_both_directions()
    {
        var (gw, c) = await Setup(g => g.SendAliveCheck = true);
        await using var _g = gw; await using var _c = c;
        await CanAddressingTests.WaitUntil(() => gw.AliveCheckResponses == 1);   // client answered the gateway's request
        Assert.True(await c.AliveCheckAsync());
    }

    [Fact]
    public async Task Silent_ecu_times_out_and_cancel_works()
    {
        var (gw, c) = await Setup(g => g.AddEcu(0x1002, new EcuScript().Silent("22")));
        await using var _g = gw; await using var _c = c;
        await c.ConnectEcuAsync(new EcuAddress { Protocol = EcuProtocol.DoIp, TxId = 0x1002 });
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await c.RequestAsync(Hex.Parse("22F190"), cts.Token));
        await c.ConnectEcuAsync(new EcuAddress { Protocol = EcuProtocol.DoIp, TxId = 0x1001 });
        Assert.Equal(Hex.Parse("62F190414243"), await c.RequestAsync(Hex.Parse("22F190")));
    }

    [Fact]
    public async Task Scanner_over_doip()
    {
        var (gw, c) = await Setup();
        await using var _g = gw; await using var _c = c;
        var list = await new EcuScanner(c).ScanToListAsync(new[]
        {
            new ScanCandidate("A", EcuProtocol.DoIp, "1001", null, null, Hex.Parse("22F190")),
            new ScanCandidate("B", EcuProtocol.DoIp, "2222", null, null, Hex.Parse("22F190")),
        });
        Assert.True(list[0].Found);
        Assert.False(list[1].Found);
    }
}
