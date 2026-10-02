using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ddt4All.Core.Ecu;

namespace Ddt4All.Core.Loading;

/// <summary>
/// Writes an <see cref="EcuFile"/> in the internal JSON format, with exactly the keys/ordering/omission
/// rules of the Python <c>EcuFile.dumpJson</c> (default values are omitted; HTML tags are stripped from comments).
/// </summary>
public static partial class EcuJsonDumper
{
    private static readonly JsonWriterOptions Options = new()
    {
        Indented = true,
        IndentCharacter = '\t',
        IndentSize = 1,
        // Python's json.dumps escapes non-ASCII; relaxed escaping keeps the file readable and is valid JSON either way.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Dumps to a JSON string.</summary>
    public static string Dump(EcuFile file)
    {
        var buf = new ArrayBufferWriter<byte>(1 << 14);
        Write(file, buf);
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    /// <summary>Dumps to a stream (UTF-8, no BOM).</summary>
    public static void Write(EcuFile file, Stream stream)
    {
        using var w = new Utf8JsonWriter(stream, Options);
        Write(file, w);
    }

    /// <summary>Dumps to a buffer writer.</summary>
    public static void Write(EcuFile file, IBufferWriter<byte> output)
    {
        using var w = new Utf8JsonWriter(output, Options);
        Write(file, w);
    }

    [GeneratedRegex("<.*?>")]
    private static partial Regex HtmlTag();

    /// <summary>Python <c>cleanhtml</c>: removes everything between '&lt;' and '&gt;'.</summary>
    public static string CleanHtml(string s) => s.Contains('<') ? HtmlTag().Replace(s, "") : s;

    private static void Write(EcuFile f, Utf8JsonWriter w)
    {
        w.WriteStartObject();

        w.WriteStartArray("autoidents");
        foreach (var a in f.AutoIdents)
        {
            w.WriteStartObject();
            w.WriteString("diagversion", a.DiagVersion);
            w.WriteString("supplier", a.Supplier);
            w.WriteString("soft", a.Soft);
            w.WriteString("version", a.Version);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteString("ecuname", f.EcuName);

        w.WriteStartObject("obd");
        w.WriteString("protocol", f.Protocol.ToWire());
        if (f.Protocol == EcuProtocol.Can)
        {
            w.WriteString("send_id", f.SendId);
            w.WriteString("recv_id", f.RecvId);
            w.WriteNumber("baudrate", f.BaudRate);
        }

        if (f.Protocol == EcuProtocol.Kwp2000) w.WriteBoolean("fastinit", f.FastInit);
        w.WriteString("funcaddr", f.FuncAddr);
        w.WriteString("funcname", f.FuncName);
        if (f.Kw1.Length > 0) w.WriteString("kw1", f.Kw1);
        if (f.Kw2.Length > 0) w.WriteString("kw2", f.Kw2);
        w.WriteEndObject();

        w.WriteStartObject("data");
        foreach (var d in f.Data) WriteData(w, d);
        w.WriteEndObject();

        w.WriteStartArray("requests");
        foreach (var r in f.Requests) WriteRequest(w, r);
        w.WriteEndArray();

        w.WriteStartArray("devices");
        foreach (var d in f.Devices)
        {
            w.WriteStartObject();
            w.WriteNumber("dtc", d.Dtc);
            w.WriteNumber("dtctype", d.DtcType);
            w.WriteStartObject("devicedata");
            foreach (var kv in d.DeviceData) w.WriteString(kv.Key, kv.Value);
            w.WriteEndObject();
            w.WriteString("name", d.Name);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        if (f.Endianness != ByteOrder.Default) w.WriteString("endian", EcuProtocolNames.OrderToWire(f.Endianness));
        w.WriteEndObject();
        w.Flush();
    }

    private static void WriteData(Utf8JsonWriter w, EcuData d)
    {
        w.WriteStartObject(d.Name);
        if (d.BitsCount != 8) w.WriteNumber("bitscount", d.BitsCount);
        if (d.Scaled) w.WriteBoolean("scaled", true);
        if (d.Signed) w.WriteBoolean("signed", true);
        if (d.Byte) w.WriteBoolean("byte", true);
        if (d.Binary) w.WriteBoolean("binary", true);
        if (d.BytesCount != 1) w.WriteNumber("bytescount", d.BytesCount);
        if (d.BytesAscii) w.WriteBoolean("bytesascii", true);
        if (d.Step != 1) w.WriteNumber("step", d.Step);
        if (d.Offset != 0) w.WriteNumber("offset", d.Offset);
        if (d.DivideBy != 1) w.WriteNumber("divideby", d.DivideBy);
        if (d.Format.Length > 0) w.WriteString("format", d.Format);
        if (d.Lists.Count > 0)
        {
            w.WriteStartObject("lists");
            foreach (var kv in d.Lists) w.WriteString(kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), kv.Value);
            w.WriteEndObject();
        }

        if (d.Unit.Length > 0) w.WriteString("unit", d.Unit);
        if (d.Comment.Length > 0) w.WriteString("comment", CleanHtml(d.Comment));
        w.WriteEndObject();
    }

    private static void WriteItems(Utf8JsonWriter w, string key, NamedCollection<DataItem> items)
    {
        if (items.Count == 0) return;
        w.WriteStartObject(key);
        foreach (var i in items)
        {
            w.WriteStartObject(i.Name);
            if (i.FirstByte != 0) w.WriteNumber("firstbyte", i.FirstByte);
            if (i.BitOffset != 0) w.WriteNumber("bitoffset", i.BitOffset);
            if (i.Ref) w.WriteBoolean("ref", true);
            if (i.Endian != ByteOrder.Default) w.WriteString("endian", EcuProtocolNames.OrderToWire(i.Endian));
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    private static void WriteRequest(Utf8JsonWriter w, EcuRequest r)
    {
        w.WriteStartObject();
        if (r.MinBytes != 0) w.WriteNumber("minbytes", r.MinBytes);
        if (r.ShiftBytesCount != 0) w.WriteNumber("shiftbytescount", r.ShiftBytesCount);
        if (r.ReplyBytes.Length > 0) w.WriteString("replybytes", r.ReplyBytes);
        if (r.ManualSend) w.WriteBoolean("manualsend", true);
        if (r.SentBytes.Length > 0) w.WriteString("sentbytes", r.SentBytes);
        w.WriteString("name", r.Name);
        w.WriteStartArray("deny_sds");
        if ((r.Denied & SessionAccess.NoSds) != 0) w.WriteStringValue("nosds");
        if ((r.Denied & SessionAccess.Plant) != 0) w.WriteStringValue("plant");
        if ((r.Denied & SessionAccess.AfterSales) != 0) w.WriteStringValue("aftersales");
        if ((r.Denied & SessionAccess.Engineering) != 0) w.WriteStringValue("engineering");
        if ((r.Denied & SessionAccess.Supplier) != 0) w.WriteStringValue("supplier");
        w.WriteEndArray();
        WriteItems(w, "sendbyte_dataitems", r.SendItems);
        WriteItems(w, "receivebyte_dataitems", r.ReceiveItems);
        w.WriteEndObject();
    }
}
