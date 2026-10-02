using System.Text.Json;
using Ddt4All.Core.Ecu;

namespace Ddt4All.Core.Loading;

/// <summary>Loader for the internal JSON ECU format (System.Text.Json source generated, reflection free).</summary>
public static class EcuJsonLoader
{
    /// <summary>Loads from UTF-8 JSON bytes (an optional BOM is skipped).</summary>
    public static EcuFile Load(ReadOnlySpan<byte> utf8Json)
    {
        EcuFileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(StripBom(utf8Json), CoreJsonContext.Default.EcuFileDto);
        }
        catch (JsonException ex)
        {
            throw new EcuFormatException("Invalid ECU JSON: " + ex.Message, ex);
        }

        return dto == null ? throw new EcuFormatException("Empty ECU JSON") : Convert(dto);
    }

    /// <summary>Loads from a stream.</summary>
    public static EcuFile Load(Stream stream)
    {
        EcuFileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(stream, CoreJsonContext.Default.EcuFileDto);
        }
        catch (JsonException ex)
        {
            throw new EcuFormatException("Invalid ECU JSON: " + ex.Message, ex);
        }

        return dto == null ? throw new EcuFormatException("Empty ECU JSON") : Convert(dto);
    }

    /// <summary>Loads from a file.</summary>
    public static EcuFile Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        return Load(fs);
    }

    /// <summary>Loads from JSON text.</summary>
    public static EcuFile LoadFromString(string json) => Load(System.Text.Encoding.UTF8.GetBytes(json));

    internal static ReadOnlySpan<byte> StripBom(ReadOnlySpan<byte> b) =>
        b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? b.Slice(3) : b;

    private static EcuFile Convert(EcuFileDto dto)
    {
        var f = new EcuFile();
        if (dto.Obd is { } obd)
        {
            f.Protocol = EcuProtocolNames.Parse(obd.Protocol);
            if (obd.SendId != null) f.SendId = obd.SendId;
            if (obd.RecvId != null) f.RecvId = obd.RecvId;
            if (obd.BaudRate is { } br) f.BaudRate = br;
            if (obd.FastInit is { } fi) f.FastInit = fi;
            if (obd.FuncAddr != null) f.FuncAddr = obd.FuncAddr;
            if (obd.FuncName != null) f.FuncName = obd.FuncName;
            if (obd.Kw1 != null) f.Kw1 = obd.Kw1;
            if (obd.Kw2 != null) f.Kw2 = obd.Kw2;
        }

        if (dto.Endian != null) f.Endianness = EcuProtocolNames.ParseOrder(dto.Endian);
        if (dto.EcuName != null) f.EcuName = dto.EcuName;
        if (dto.AutoIdents != null)
            foreach (var a in dto.AutoIdents)
                f.AutoIdents.Add(new AutoIdent(a.DiagVersion ?? "", a.Supplier ?? "", a.Soft ?? "", a.Version ?? ""));

        if (dto.Devices != null)
            foreach (var d in dto.Devices)
            {
                var dev = new EcuDevice { Name = d.Name ?? "", Dtc = d.Dtc ?? 0, DtcType = d.DtcType ?? 0 };
                if (d.DeviceData != null) foreach (var kv in d.DeviceData) dev.DeviceData[kv.Key] = kv.Value;
                f.Devices.Add(dev);
            }

        if (dto.Requests != null)
            foreach (var r in dto.Requests)
            {
                var req = new EcuRequest
                {
                    Name = r.Name ?? throw new EcuFormatException("Request without name"),
                    MinBytes = r.MinBytes ?? 0,
                    ShiftBytesCount = r.ShiftBytesCount ?? 0,
                    ReplyBytes = r.ReplyBytes ?? "",
                    ManualSend = r.ManualSend ?? false,
                    SentBytes = r.SentBytes ?? "",
                };
                if (r.DenySds != null)
                    foreach (var s in r.DenySds)
                        req.Denied |= s switch
                        {
                            "nosds" => SessionAccess.NoSds,
                            "plant" => SessionAccess.Plant,
                            "aftersales" => SessionAccess.AfterSales,
                            "engineering" => SessionAccess.Engineering,
                            "supplier" => SessionAccess.Supplier,
                            _ => SessionAccess.None,
                        };
                if (r.SendByteDataItems != null)
                    foreach (var kv in r.SendByteDataItems) req.SendItems.Add(ToItem(kv.Key, kv.Value));
                if (r.ReceiveByteDataItems != null)
                    foreach (var kv in r.ReceiveByteDataItems) req.ReceiveItems.Add(ToItem(kv.Key, kv.Value));
                f.Requests.Add(req);
            }

        if (dto.Data != null)
            foreach (var kv in dto.Data)
            {
                var v = kv.Value;
                var d = new EcuData { Name = kv.Key };
                if (v != null)
                {
                    if (v.BitsCount is { } bc) d.BitsCount = bc;
                    if (v.BytesAscii is { } ba) d.BytesAscii = ba;
                    if (v.Scaled is { } sc) d.Scaled = sc;
                    if (v.Signed is { } sg) d.Signed = sg;
                    if (v.Byte is { } by) d.Byte = by;
                    if (v.Binary is { } bi) d.Binary = bi;
                    if (v.Step is { } st) d.Step = st;
                    if (v.Offset is { } of) d.Offset = of;
                    if (v.DivideBy is { } dv) d.DivideBy = dv;
                    if (v.Format != null) d.Format = v.Format;
                    if (v.BytesCount is { } bt) d.BytesCount = bt;
                    if (v.Unit != null) d.Unit = v.Unit;
                    if (v.Comment != null) d.Comment = v.Comment;
                    if (v.Description != null) d.Description = v.Description;
                    if (v.Lists != null)
                        foreach (var l in v.Lists)
                            if (long.TryParse(l.Key, System.Globalization.NumberStyles.AllowLeadingSign,
                                    System.Globalization.CultureInfo.InvariantCulture, out long k))
                                d.AddListItem(k, l.Value);
                }

                f.Data.Add(d);
            }

        f.ResolveReferences();
        return f;
    }

    private static DataItem ToItem(string name, DataItemDto? v) => new()
    {
        Name = name,
        FirstByte = v?.FirstByte ?? 0,
        BitOffset = v?.BitOffset ?? 0,
        Ref = v?.Ref ?? false,
        Endian = v?.Endian == null ? ByteOrder.Default : EcuProtocolNames.ParseOrder(v.Endian),
    };
}
