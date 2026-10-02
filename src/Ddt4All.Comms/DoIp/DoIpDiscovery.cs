using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Ddt4All.Comms.DoIp;

/// <summary>UDP vehicle identification (ISO 13400-2 clause 7).</summary>
public static class DoIpDiscovery
{
    /// <summary>
    /// Sends a vehicle identification request to <paramref name="target"/> (default: limited broadcast, port 13400) and yields
    /// each distinct entity answering within <paramref name="timeout"/>.
    /// </summary>
    public static async IAsyncEnumerable<DoIpVehicle> DiscoverAsync(TimeSpan timeout, IPEndPoint? target = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        target ??= new IPEndPoint(IPAddress.Broadcast, DoIpFraming.DefaultPort);
        using var udp = new UdpClient(target.AddressFamily) { EnableBroadcast = true };
        DisableUdpConnReset(udp);
        udp.Client.Bind(new IPEndPoint(target.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
        var request = DoIpFraming.Build(DoIpPayloadType.VehicleIdentificationRequest, ReadOnlySpan<byte>.Empty);
        await udp.SendAsync(request, target, ct).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var seen = new HashSet<string>();
        while (true)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { yield break; }

            DoIpVehicle? vehicle = null;
            try
            {
                var msg = DoIpFraming.ParseDatagram(r.Buffer);
                if (msg.Type == DoIpPayloadType.VehicleAnnouncement)
                    vehicle = DoIpVehicle.Parse(r.RemoteEndPoint, msg.Payload);
            }
            catch (ProtocolException) { /* ignore garbage */ }
            if (vehicle is not null && seen.Add(vehicle.EidHex + vehicle.LogicalAddress)) yield return vehicle;
        }
    }

    /// <summary>
    /// Windows reports an ICMP "port unreachable" reply to an earlier send as WSAECONNRESET on the next
    /// receive, which would abort discovery. SIO_UDP_CONNRESET = off restores the normal UDP behaviour.
    /// </summary>
    internal static void DisableUdpConnReset(UdpClient udp)
    {
        if (!OperatingSystem.IsWindows()) return;
        const int SIO_UDP_CONNRESET = -1744830452;
        try { udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null); }
        catch (SocketException) { /* best effort */ }
    }

    /// <summary>First entity found, or null on timeout.</summary>
    public static async Task<DoIpVehicle?> DiscoverFirstAsync(TimeSpan timeout, IPEndPoint? target = null, CancellationToken ct = default)
    {
        await foreach (var v in DiscoverAsync(timeout, target, ct).ConfigureAwait(false)) return v;
        return null;
    }

    /// <summary>Quick TCP reachability check of host:13400 (replaces the Python "online/offline" probe).</summary>
    public static async Task<bool> IsReachableAsync(string host, int port = DoIpFraming.DefaultPort, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var c = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromMilliseconds(500));
        try { await c.ConnectAsync(host, port, cts.Token).ConfigureAwait(false); return true; }
        catch { return false; }
    }
}
