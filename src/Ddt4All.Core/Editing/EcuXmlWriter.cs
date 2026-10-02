using System.Globalization;
using System.Text;
using System.Xml;
using Ddt4All.Core.Ecu;

namespace Ddt4All.Core.Editing;

/// <summary>Writes an <see cref="EcuFile"/> as DDT-style ECU XML that <see cref="Loading.EcuXmlLoader"/> reads back (screens are not included).</summary>
public static class EcuXmlWriter
{
    /// <summary>XML text.</summary>
    public static string Write(EcuFile f)
    {
        var sb = new StringBuilder(1 << 14);
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, IndentChars = "    ", OmitXmlDeclaration = false, CheckCharacters = false }))
            Write(f, w);
        return sb.ToString();
    }

    /// <summary>Writes to a file (UTF-8).</summary>
    public static void Save(EcuFile f, string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = XmlWriter.Create(fs, new XmlWriterSettings { Indent = true, IndentChars = "    ", Encoding = new UTF8Encoding(false), CheckCharacters = false });
        Write(f, w);
    }

    private static string Dec(string hex) =>
        long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long v) ? v.ToString(CultureInfo.InvariantCulture) : "0";

    private static string N(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    private static void Write(EcuFile f, XmlWriter w)
    {
        w.WriteStartDocument();
        w.WriteStartElement("Ecu");

        w.WriteStartElement("Target");
        w.WriteAttributeString("Name", f.EcuName);
        if (f.AutoIdents.Count > 0)
        {
            w.WriteStartElement("AutoIdents");
            foreach (var a in f.AutoIdents)
            {
                w.WriteStartElement("AutoIdent");
                w.WriteAttributeString("DiagVersion", a.DiagVersion);
                w.WriteAttributeString("Supplier", a.Supplier);
                w.WriteAttributeString("Soft", a.Soft);
                w.WriteAttributeString("Version", a.Version);
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }

        if (f.Projects.Count > 0)
        {
            w.WriteStartElement("Projects");
            foreach (var p in f.Projects) w.WriteElementString(XmlConvert.EncodeLocalName(p), "");
            w.WriteEndElement();
        }

        w.WriteStartElement("Function");
        w.WriteAttributeString("Address", Dec(f.FuncAddr));
        w.WriteAttributeString("Name", f.FuncName);
        w.WriteEndElement();

        if (f.Protocol == EcuProtocol.Can)
        {
            w.WriteStartElement("CAN");
            w.WriteAttributeString("BaudRate", f.BaudRate.ToString(CultureInfo.InvariantCulture));
            CanId(w, "SendId", f.SendId);
            CanId(w, "ReceiveId", f.RecvId);
            w.WriteEndElement();
        }
        else if (f.Protocol is EcuProtocol.Kwp2000 or EcuProtocol.Iso8)
        {
            w.WriteStartElement("K");
            if (f.Protocol == EcuProtocol.Kwp2000)
            {
                w.WriteStartElement("KWP");
                w.WriteStartElement(f.FastInit ? "FastInit" : "ISO8");
                KeyWords(w, f);
                w.WriteEndElement();
                w.WriteEndElement();
            }
            else
            {
                w.WriteStartElement("ISO8");
                KeyWords(w, f);
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }

        w.WriteEndElement(); // Target

        foreach (var d in f.Devices)
        {
            w.WriteStartElement("Device");
            w.WriteAttributeString("Name", d.Name);
            w.WriteAttributeString("DTC", d.Dtc.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("Type", d.DtcType.ToString(CultureInfo.InvariantCulture));
            foreach (var kv in d.DeviceData)
            {
                w.WriteStartElement("DeviceData");
                w.WriteAttributeString("Name", kv.Key);
                w.WriteAttributeString("FailureFlag", kv.Value);
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }

        w.WriteStartElement("Requests");
        if (f.Endianness != ByteOrder.Default) w.WriteAttributeString("Endian", EcuProtocolNames.OrderToWire(f.Endianness));
        foreach (var r in f.Requests) WriteRequest(w, r);
        w.WriteEndElement();

        foreach (var d in f.Data) WriteData(w, d);

        w.WriteEndElement(); // Ecu
        w.WriteEndDocument();
    }

    private static void CanId(XmlWriter w, string name, string hex)
    {
        w.WriteStartElement(name);
        w.WriteStartElement("CANId");
        w.WriteAttributeString("Value", Dec(hex));
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void KeyWords(XmlWriter w, EcuFile f)
    {
        if (f.Kw1.Length > 0) { w.WriteStartElement("KW1"); w.WriteAttributeString("Value", Dec(f.Kw1)); w.WriteEndElement(); }
        if (f.Kw2.Length > 0) { w.WriteStartElement("KW2"); w.WriteAttributeString("Value", Dec(f.Kw2)); w.WriteEndElement(); }
    }

    private static void WriteItems(XmlWriter w, NamedCollection<DataItem> items)
    {
        foreach (var i in items)
        {
            w.WriteStartElement("DataItem");
            w.WriteAttributeString("Name", i.Name);
            if (i.FirstByte != 0) w.WriteAttributeString("FirstByte", i.FirstByte.ToString(CultureInfo.InvariantCulture));
            if (i.BitOffset != 0) w.WriteAttributeString("BitOffset", i.BitOffset.ToString(CultureInfo.InvariantCulture));
            if (i.Endian != ByteOrder.Default) w.WriteAttributeString("Endian", EcuProtocolNames.OrderToWire(i.Endian));
            if (i.Ref) w.WriteAttributeString("Ref", "1");
            w.WriteEndElement();
        }
    }

    private static void WriteRequest(XmlWriter w, EcuRequest r)
    {
        w.WriteStartElement("Request");
        w.WriteAttributeString("Name", r.Name);
        if (r.Denied != SessionAccess.None)
        {
            w.WriteStartElement("DenyAccess");
            if ((r.Denied & SessionAccess.NoSds) != 0) w.WriteElementString("NoSDS", "");
            if ((r.Denied & SessionAccess.Plant) != 0) w.WriteElementString("Plant", "");
            if ((r.Denied & SessionAccess.AfterSales) != 0) w.WriteElementString("AfterSales", "");
            if ((r.Denied & SessionAccess.Engineering) != 0) w.WriteElementString("Engineering", "");
            if ((r.Denied & SessionAccess.Supplier) != 0) w.WriteElementString("Supplier", "");
            w.WriteEndElement();
        }

        if (r.ManualSend) w.WriteElementString("ManuelSend", "");
        if (r.ShiftBytesCount != 0) w.WriteElementString("ShiftBytesCount", r.ShiftBytesCount.ToString(CultureInfo.InvariantCulture));
        if (r.ReplyBytes.Length > 0) w.WriteElementString("ReplyBytes", r.ReplyBytes);
        w.WriteStartElement("Sent");
        w.WriteElementString("SentBytes", r.SentBytes);
        WriteItems(w, r.SendItems);
        w.WriteEndElement();
        w.WriteStartElement("Received");
        if (r.MinBytes != 0) w.WriteAttributeString("MinBytes", r.MinBytes.ToString(CultureInfo.InvariantCulture));
        WriteItems(w, r.ReceiveItems);
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void WriteData(XmlWriter w, EcuData d)
    {
        w.WriteStartElement("Data");
        w.WriteAttributeString("Name", d.Name);
        if (d.Description.Length > 0) w.WriteElementString("Description", d.Description);
        if (d.Comment.Length > 0) { w.WriteStartElement("Comment"); w.WriteCData(d.Comment.Replace("]]>", "]]]]><![CDATA[>")); w.WriteEndElement(); }
        if (d.Lists.Count > 0)
        {
            w.WriteStartElement("List");
            foreach (var kv in d.Lists)
            {
                w.WriteStartElement("Item");
                w.WriteAttributeString("Value", kv.Key.ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("Text", kv.Value);
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }

        // The loader applies <Bytes> first and lets <Bits> override, so emit both when the width is not byte aligned.
        if (d.Byte || d.BytesAscii)
        {
            w.WriteStartElement("Bytes");
            w.WriteAttributeString("count", d.BytesCount.ToString(CultureInfo.InvariantCulture));
            if (d.BytesAscii) w.WriteAttributeString("ascii", "1");
            w.WriteEndElement();
        }

        bool needBits = !(d.Byte || d.BytesAscii) || d.BitsCount != d.BytesCount * 8 || d.Signed || d.Binary || d.Scaled;
        if (needBits)
        {
            w.WriteStartElement("Bits");
            w.WriteAttributeString("count", d.BitsCount.ToString(CultureInfo.InvariantCulture));
            if (d.Signed) w.WriteAttributeString("signed", "1");
            if (d.Binary) w.WriteElementString("Binary", "");
            if (d.Scaled)
            {
                w.WriteStartElement("Scaled");
                w.WriteAttributeString("Step", N(d.Step));
                w.WriteAttributeString("Offset", N(d.Offset));
                w.WriteAttributeString("DivideBy", N(d.DivideBy));
                if (d.Format.Length > 0) w.WriteAttributeString("Format", d.Format);
                if (d.Unit.Length > 0) w.WriteAttributeString("Unit", d.Unit);
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }

        w.WriteEndElement();
    }
}
