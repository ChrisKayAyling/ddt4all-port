using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Ddt4All.Comms.DoIp;

namespace Ddt4All.Comms.Simulation;

/// <summary>
/// Loopback DoIP entity for tests/demo mode: answers UDP vehicle identification requests, accepts TCP connections,
/// performs routing activation and routes diagnostic messages to <see cref="EcuScript"/>s by logical address.
/// </summary>
public sealed class SimulatedDoIpGateway : IAsyncDisposable
{
    private readonly TcpListener _tcp;
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<ushort, EcuScript> _ecus = new();
    private readonly List<Task> _tasks = new();

    public SimulatedDoIpGateway(DoIpVehicle vehicle)
    {
        Vehicle = vehicle;
        _tcp = new TcpListener(IPAddress.Loopback, 0);
        _tcp.Start();
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        _tasks.Add(Task.Run(AcceptLoopAsync));
        _tasks.Add(Task.Run(UdpLoopAsync));
    }

    public static SimulatedDoIpGateway Create(string vin = "VF1RFB00000000001", ushort logicalAddress = 0x1000) =>
        new(new DoIpVehicle(new IPEndPoint(IPAddress.Loopback, 0), vin, logicalAddress, new byte[] { 1, 2, 3, 4, 5, 6 }, new byte[] { 6, 5, 4, 3, 2, 1 }, 0, 0));

    public DoIpVehicle Vehicle { get; }
    public int TcpPort => ((IPEndPoint)_tcp.LocalEndpoint).Port;
    public int UdpPort => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    public IPEndPoint UdpEndPoint => new(IPAddress.Loopback, UdpPort);

    /// <summary>Number of routing activations accepted.</summary>
    public int Activations;
    /// <summary>If set the gateway rejects routing activation with this code.</summary>
    public byte? ActivationDenyCode { get; set; }
    /// <summary>Gateway sends an alive-check request after each routing activation (the client must answer).</summary>
    public bool SendAliveCheck { get; set; }
    public int AliveCheckResponses;

    public SimulatedDoIpGateway AddEcu(ushort logicalAddress, EcuScript script) { _ecus[logicalAddress] = script; return this; }

    private async Task UdpLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var r = await _udp.ReceiveAsync(_stop.Token).ConfigureAwait(false);
                try
                {
                    var m = DoIpFraming.ParseDatagram(r.Buffer);
                    if (m.Type is DoIpPayloadType.VehicleIdentificationRequest)
                        await _udp.SendAsync(DoIpFraming.Build(DoIpPayloadType.VehicleAnnouncement, Vehicle.ToPayload()), r.RemoteEndPoint, _stop.Token).ConfigureAwait(false);
                }
                catch (ProtocolException) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var c = await _tcp.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                lock (_tasks) _tasks.Add(Task.Run(() => ServeAsync(c)));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        var s = client.GetStream();
        var hdr = new byte[DoIpFraming.HeaderLength];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await s.ReadExactlyAsync(hdr, _stop.Token).ConfigureAwait(false);
                int len = DoIpFraming.ParseHeader(hdr, out var type);
                var p = new byte[len];
                if (len > 0) await s.ReadExactlyAsync(p, _stop.Token).ConfigureAwait(false);
                switch (type)
                {
                    case DoIpPayloadType.RoutingActivationRequest:
                    {
                        var resp = new byte[9];
                        p.AsSpan(0, 2).CopyTo(resp);
                        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(2), Vehicle.LogicalAddress);
                        resp[4] = ActivationDenyCode ?? 0x10;
                        if (ActivationDenyCode is null) Interlocked.Increment(ref Activations);
                        await s.WriteAsync(DoIpFraming.Build(DoIpPayloadType.RoutingActivationResponse, resp), _stop.Token).ConfigureAwait(false);
                        if (SendAliveCheck && ActivationDenyCode is null)
                            await s.WriteAsync(DoIpFraming.Build(DoIpPayloadType.AliveCheckRequest, ReadOnlySpan<byte>.Empty), _stop.Token).ConfigureAwait(false);
                        break;
                    }
                    case DoIpPayloadType.AliveCheckRequest:
                        await s.WriteAsync(DoIpFraming.Build(DoIpPayloadType.AliveCheckResponse, new byte[] { (byte)(Vehicle.LogicalAddress >> 8), (byte)Vehicle.LogicalAddress }), _stop.Token).ConfigureAwait(false);
                        break;
                    case DoIpPayloadType.AliveCheckResponse:
                        Interlocked.Increment(ref AliveCheckResponses);
                        break;
                    case DoIpPayloadType.DiagnosticMessage when p.Length >= 5:
                    {
                        ushort sa = BinaryPrimitives.ReadUInt16BigEndian(p), ta = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(2));
                        if (!_ecus.TryGetValue(ta, out var script))
                        {
                            var nack = new byte[5];
                            BinaryPrimitives.WriteUInt16BigEndian(nack, ta); BinaryPrimitives.WriteUInt16BigEndian(nack.AsSpan(2), sa); nack[4] = 0x03; // unknown target
                            await s.WriteAsync(DoIpFraming.Build(DoIpPayloadType.DiagnosticMessageNegativeAck, nack), _stop.Token).ConfigureAwait(false);
                            break;
                        }
                        var ack = new byte[5];
                        BinaryPrimitives.WriteUInt16BigEndian(ack, ta); BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(2), sa);
                        await s.WriteAsync(DoIpFraming.Build(DoIpPayloadType.DiagnosticMessagePositiveAck, ack), _stop.Token).ConfigureAwait(false);
                        foreach (var r in script.Handle(p.AsSpan(4).ToArray()))
                            await s.WriteAsync(DoIpFraming.Build(DoIpPayloadType.DiagnosticMessage, DoIpFraming.BuildDiagnosticPayload(ta, sa, r.Payload)), _stop.Token).ConfigureAwait(false);
                        break;
                    }
                }
            }
        }
        catch (Exception) { /* client went away / shutting down */ }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _tcp.Stop();
        _udp.Dispose();
        Task[] tasks;
        lock (_tasks) tasks = _tasks.ToArray();
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        _stop.Dispose();
    }
}
