using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Ddt4All.Comms.DoIp;

/// <summary>Connection settings for <see cref="DoIpClient"/>.</summary>
public sealed class DoIpOptions
{
    public string Host { get; set; } = "192.168.0.12";
    public int Port { get; set; } = DoIpFraming.DefaultPort;
    /// <summary>Tester logical address (external test equipment range 0x0E00-0x0EFF).</summary>
    public ushort SourceAddress { get; set; } = 0x0E80;
    /// <summary>Default ECU logical address when <see cref="EcuAddress"/> does not specify one.</summary>
    public ushort TargetAddress { get; set; } = 0x0E01;
    /// <summary>Routing activation type (0 = default).</summary>
    public byte ActivationType { get; set; }
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(4);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(4);
    /// <summary>Wait after "response pending" (P2*).</summary>
    public TimeSpan PendingTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public int MaxPendingResponses { get; set; } = 64;
}

/// <summary>
/// DoIP (ISO 13400-2) diagnostic client over TCP implementing <see cref="IAddressableTransport"/>.
/// A background reader answers alive-check requests and queues diagnostic messages, so no polling is involved.
/// For <see cref="ConnectEcuAsync"/> the ECU logical address is taken from <see cref="EcuAddress.TxId"/>
/// (falling back to <see cref="EcuAddress.RxId"/>, then <see cref="DoIpOptions.TargetAddress"/>).
/// </summary>
public sealed class DoIpClient : IAddressableTransport
{
    private readonly DoIpOptions _opt;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Channel<DoIpMessage> _inbox = Channel.CreateUnbounded<DoIpMessage>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private Task? _reader;
    private readonly CancellationTokenSource _stop = new();
    private ushort _target;

    public DoIpClient(DoIpOptions options)
    {
        _opt = options; _target = options.TargetAddress;
    }

    public EcuAddress? CurrentEcu { get; private set; }
    /// <summary>Logical address of the DoIP entity (gateway) reported by routing activation.</summary>
    public ushort EntityAddress { get; private set; }
    public bool IsConnected => _tcp?.Connected == true;

    /// <summary>Connects TCP and performs routing activation.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _tcp = new TcpClient { NoDelay = true };
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(_opt.ConnectTimeout);
            try { await _tcp.ConnectAsync(_opt.Host, _opt.Port, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new EcuTimeoutException($"DoIP connect to {_opt.Host}:{_opt.Port} timed out."); }
            catch (SocketException e) { throw new LinkClosedException($"DoIP connect failed: {e.Message}", e); }
        }
        _stream = _tcp.GetStream();
        _reader = Task.Run(ReadLoopAsync);

        var payload = new byte[7];
        BinaryPrimitives.WriteUInt16BigEndian(payload, _opt.SourceAddress);
        payload[2] = _opt.ActivationType;
        await SendAsync(DoIpPayloadType.RoutingActivationRequest, payload, ct).ConfigureAwait(false);

        var msg = await ReceiveAsync(_opt.RequestTimeout, ct, m => m.Type is DoIpPayloadType.RoutingActivationResponse or DoIpPayloadType.GenericHeaderNack).ConfigureAwait(false);
        if (msg.Type == DoIpPayloadType.GenericHeaderNack || msg.Payload.Length < 5)
            throw new ProtocolException("DoIP gateway rejected the routing activation request.");
        EntityAddress = BinaryPrimitives.ReadUInt16BigEndian(msg.Payload.AsSpan(2));
        byte code = msg.Payload[4];
        if (code is not (0x10 or 0x11))
            throw new ProtocolException($"DoIP routing activation denied (code 0x{code:X2}).");
    }

    /// <summary>Convenience: connect and return a ready client.</summary>
    public static async Task<DoIpClient> OpenAsync(DoIpOptions options, CancellationToken ct = default)
    {
        var c = new DoIpClient(options);
        try { await c.ConnectAsync(ct).ConfigureAwait(false); }
        catch { await c.DisposeAsync().ConfigureAwait(false); throw; }
        return c;
    }

    public ValueTask ConnectEcuAsync(EcuAddress ecu, CancellationToken ct = default)
    {
        _target = ecu.TxId != 0 ? (ushort)ecu.TxId : ecu.RxId != 0 ? (ushort)ecu.RxId : _opt.TargetAddress;
        CurrentEcu = ecu;
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseProtocolAsync(CancellationToken ct = default) { CurrentEcu = null; return ValueTask.CompletedTask; }

    /// <summary>Sends an alive-check request; true when the entity answered.</summary>
    public async Task<bool> AliveCheckAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SendAsync(DoIpPayloadType.AliveCheckRequest, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
            await ReceiveAsync(_opt.RequestTimeout, ct, m => m.Type == DoIpPayloadType.AliveCheckResponse).ConfigureAwait(false);
            return true;
        }
        catch (EcuTimeoutException) { return false; }
        finally { _gate.Release(); }
    }

    public async ValueTask<byte[]> RequestAsync(ReadOnlyMemory<byte> request, CancellationToken ct = default)
    {
        if (request.IsEmpty) throw new ArgumentException("Empty request.", nameof(request));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (_inbox.Reader.TryRead(out _)) { }   // stale
            await SendAsync(DoIpPayloadType.DiagnosticMessage, DoIpFraming.BuildDiagnosticPayload(_opt.SourceAddress, _target, request.Span), ct).ConfigureAwait(false);

            // transport-level acknowledge
            var ack = await ReceiveAsync(_opt.RequestTimeout, ct,
                m => m.Type is DoIpPayloadType.DiagnosticMessagePositiveAck or DoIpPayloadType.DiagnosticMessageNegativeAck).ConfigureAwait(false);
            if (ack.Type == DoIpPayloadType.DiagnosticMessageNegativeAck)
                throw new ProtocolException($"DoIP diagnostic message NACK (code 0x{(ack.Payload.Length > 4 ? ack.Payload[4] : 0):X2}).");

            int sid = request.Span[0];
            int pending = 0;
            var timeout = _opt.RequestTimeout;
            while (true)
            {
                var msg = await ReceiveAsync(timeout, ct, m => m.Type == DoIpPayloadType.DiagnosticMessage).ConfigureAwait(false);
                if (msg.Payload.Length < 5) continue;
                ushort src = BinaryPrimitives.ReadUInt16BigEndian(msg.Payload);
                if (src != _target) continue;
                var data = msg.Payload.AsSpan(4).ToArray();
                if (IsoTp.IsPending(data, sid))
                {
                    if (++pending > _opt.MaxPendingResponses) throw new EcuTimeoutException("Too many 'response pending' messages.");
                    timeout = _opt.PendingTimeout;
                    continue;
                }
                return data;
            }
        }
        finally { _gate.Release(); }
    }

    private async ValueTask SendAsync(DoIpPayloadType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var s = _stream ?? throw new LinkClosedException("DoIP not connected.");
        var frame = DoIpFraming.Build(type, payload.Span);
        try { await s.WriteAsync(frame, ct).ConfigureAwait(false); }
        catch (IOException e) { throw new LinkClosedException("DoIP write failed.", e); }
    }

    private async Task<DoIpMessage> ReceiveAsync(TimeSpan timeout, CancellationToken ct, Func<DoIpMessage, bool> accept)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var m = await _inbox.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
                if (accept(m)) return m;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new EcuTimeoutException("DoIP response timed out."); }
        catch (ChannelClosedException) { throw new LinkClosedException("DoIP connection closed."); }
    }

    private async Task ReadLoopAsync()
    {
        var stream = _stream!;
        var header = new byte[DoIpFraming.HeaderLength];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, _stop.Token).ConfigureAwait(false);
                int len = DoIpFraming.ParseHeader(header, out var type);
                var payload = new byte[len];
                if (len > 0) await stream.ReadExactlyAsync(payload, _stop.Token).ConfigureAwait(false);
                if (type == DoIpPayloadType.AliveCheckRequest)
                {
                    var p = new byte[2];
                    BinaryPrimitives.WriteUInt16BigEndian(p, _opt.SourceAddress);
                    await stream.WriteAsync(DoIpFraming.Build(DoIpPayloadType.AliveCheckResponse, p), _stop.Token).ConfigureAwait(false);
                    continue;
                }
                _inbox.Writer.TryWrite(new DoIpMessage(type, payload));
            }
        }
        catch (Exception e)
        {
            _inbox.Writer.TryComplete(e is OperationCanceledException or EndOfStreamException or IOException ? null : e);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _tcp?.Dispose();
        if (_reader is not null) { try { await _reader.ConfigureAwait(false); } catch { } }
        _inbox.Writer.TryComplete();
        _stop.Dispose();
        _gate.Dispose();
    }
}
