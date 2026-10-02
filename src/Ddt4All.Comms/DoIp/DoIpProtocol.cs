using System.Buffers.Binary;

namespace Ddt4All.Comms.DoIp;

/// <summary>ISO 13400-2 payload types.</summary>
public enum DoIpPayloadType : ushort
{
    GenericHeaderNack = 0x0000,
    VehicleIdentificationRequest = 0x0001,
    VehicleIdentificationRequestWithEid = 0x0002,
    VehicleIdentificationRequestWithVin = 0x0003,
    /// <summary>Vehicle announcement / identification response.</summary>
    VehicleAnnouncement = 0x0004,
    RoutingActivationRequest = 0x0005,
    RoutingActivationResponse = 0x0006,
    AliveCheckRequest = 0x0007,
    AliveCheckResponse = 0x0008,
    EntityStatusRequest = 0x4001,
    EntityStatusResponse = 0x4002,
    PowerModeInformationRequest = 0x4003,
    PowerModeInformationResponse = 0x4004,
    DiagnosticMessage = 0x8001,
    DiagnosticMessagePositiveAck = 0x8002,
    DiagnosticMessageNegativeAck = 0x8003,
}

/// <summary>A decoded DoIP message.</summary>
public readonly record struct DoIpMessage(DoIpPayloadType Type, byte[] Payload);

/// <summary>DoIP header framing (protocol version 0x02, inverse 0xFD, 8 byte header).</summary>
public static class DoIpFraming
{
    public const byte ProtocolVersion = 0x02;
    public const int HeaderLength = 8;
    public const int DefaultPort = 13400;
    /// <summary>Upper bound accepted for a payload (guards against corrupt length fields).</summary>
    public const int MaxPayload = 4 * 1024 * 1024;

    public static byte[] Build(DoIpPayloadType type, ReadOnlySpan<byte> payload)
    {
        var buf = new byte[HeaderLength + payload.Length];
        buf[0] = ProtocolVersion; buf[1] = 0xFD;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), (ushort)type);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(buf.AsSpan(HeaderLength));
        return buf;
    }

    /// <summary>Parses and validates a header; returns the payload length.</summary>
    public static int ParseHeader(ReadOnlySpan<byte> header, out DoIpPayloadType type)
    {
        if (header.Length < HeaderLength) throw new ProtocolException("Short DoIP header.");
        if (header[0] != (byte)~header[1]) throw new ProtocolException("Invalid DoIP version/inverse-version bytes.");
        type = (DoIpPayloadType)BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
        uint len = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        if (len > MaxPayload) throw new ProtocolException($"DoIP payload length {len} exceeds limit.");
        return (int)len;
    }

    /// <summary>Parses a complete datagram (UDP).</summary>
    public static DoIpMessage ParseDatagram(ReadOnlySpan<byte> data)
    {
        int len = ParseHeader(data, out var type);
        if (data.Length < HeaderLength + len) throw new ProtocolException("Truncated DoIP datagram.");
        return new DoIpMessage(type, data.Slice(HeaderLength, len).ToArray());
    }

    /// <summary>Builds a diagnostic message payload: source, target, user data.</summary>
    public static byte[] BuildDiagnosticPayload(ushort source, ushort target, ReadOnlySpan<byte> data)
    {
        var p = new byte[4 + data.Length];
        BinaryPrimitives.WriteUInt16BigEndian(p, source);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), target);
        data.CopyTo(p.AsSpan(4));
        return p;
    }
}

/// <summary>A vehicle / DoIP entity found by UDP discovery.</summary>
public sealed record DoIpVehicle(System.Net.IPEndPoint EndPoint, string Vin, ushort LogicalAddress, byte[] Eid, byte[] Gid, byte FurtherAction, byte? SyncStatus)
{
    public string EidHex => Hex.ToString(Eid);
    public string GidHex => Hex.ToString(Gid);

    /// <summary>Decodes a vehicle announcement / identification response payload.</summary>
    public static DoIpVehicle Parse(System.Net.IPEndPoint from, ReadOnlySpan<byte> p)
    {
        if (p.Length < 32) throw new ProtocolException("Vehicle identification payload too short.");
        return new DoIpVehicle(from,
            System.Text.Encoding.ASCII.GetString(p[..17]).TrimEnd('\0', ' '),
            BinaryPrimitives.ReadUInt16BigEndian(p[17..]),
            p.Slice(19, 6).ToArray(), p.Slice(25, 6).ToArray(), p[31],
            p.Length > 32 ? p[32] : null);
    }

    /// <summary>Encodes into the payload layout (used by the simulated gateway).</summary>
    public byte[] ToPayload()
    {
        var p = new byte[33];
        var vin = System.Text.Encoding.ASCII.GetBytes(Vin.PadRight(17)[..17]);
        vin.CopyTo(p, 0);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(17), LogicalAddress);
        Eid.CopyTo(p, 19); Gid.CopyTo(p, 25);
        p[31] = FurtherAction; p[32] = SyncStatus ?? 0;
        return p;
    }
}
