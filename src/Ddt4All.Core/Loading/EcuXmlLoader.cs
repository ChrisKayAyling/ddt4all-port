using System.Xml;
using System.Xml.Linq;
using Ddt4All.Core.Ecu;

namespace Ddt4All.Core.Loading;

/// <summary>
/// Streaming loader for Renault DDT ECU XML files (the format found in the original ecus/*.xml).
/// Screens/layout are not parsed here, see <see cref="Layout.EcuLayoutXmlLoader"/>.
/// </summary>
public static class EcuXmlLoader
{
    /// <summary>Loads from a file.</summary>
    public static EcuFile Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        return Load(fs);
    }

    /// <summary>Loads from a stream (the stream is not closed).</summary>
    public static EcuFile Load(Stream stream)
    {
        using var r = XmlHelpers.Create(stream);
        return Load(r);
    }

    /// <summary>Loads from XML text.</summary>
    public static EcuFile LoadFromString(string xml)
    {
        using var r = XmlHelpers.Create(new StringReader(xml));
        return Load(r);
    }

    private static EcuFile Load(XmlReader reader)
    {
        var file = new EcuFile();
        try
        {
            ParseDocument(reader, file);
        }
        catch (XmlException ex)
        {
            throw new EcuFormatException("Invalid ECU XML: " + ex.Message, ex);
        }

        file.ResolveReferences();
        return file;
    }

    private static void ParseDocument(XmlReader reader, EcuFile file)
    {
        if (reader.MoveToContent() != XmlNodeType.Element)
            throw new EcuFormatException("XML not found");
        int rootDepth = reader.Depth;
        bool targetSeen = false, requestsSeen = false;
        List<EcuData>? datas = null;

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            switch (reader.LocalName)
            {
                case "Target" when reader.Depth == rootDepth + 1:
                    {
                        using var sub = reader.ReadSubtree();
                        if (!targetSeen) { targetSeen = true; ParseTarget(sub, file); }
                        break;
                    }
                case "Device":
                    {
                        using var sub = reader.ReadSubtree();
                        var dev = ParseDevice(sub);
                        file.Devices.Add(dev);
                        break;
                    }
                case "Requests":
                    {
                        requestsSeen = true;
                        string? endian = reader.GetAttribute("Endian");
                        if (!string.IsNullOrEmpty(endian)) file.Endianness = EcuProtocolNames.ParseOrder(endian);
                        // children are parsed by this same loop ("Request" below)
                        break;
                    }
                case "Request":
                    {
                        using var sub = reader.ReadSubtree();
                        file.Requests.Add(ParseRequest(sub));
                        break;
                    }
                case "Data":
                    {
                        using var sub = reader.ReadSubtree();
                        (datas ??= new()).Add(ParseData(sub));
                        break;
                    }
            }
        }

        // Python only reads <Data> when at least one <Requests> element exists.
        if (requestsSeen && datas != null)
            foreach (var d in datas) file.Data.Add(d);
    }

    // ------------------------------------------------------------------ Target

    private static void ParseTarget(XmlReader r, EcuFile file)
    {
        r.MoveToContent();
        file.EcuName = r.GetAttribute("Name") ?? "";
        if (r.IsEmptyElement) return;
        r.Read();
        bool ai = false, pr = false, fn = false, can = false, k = false;
        while (!r.EOF && r.NodeType != XmlNodeType.EndElement)
        {
            if (r.NodeType != XmlNodeType.Element) { r.Read(); continue; }
            switch (r.LocalName)
            {
                case "AutoIdents" when !ai:
                    ai = true;
                    ParseAutoIdents((XElement)XNode.ReadFrom(r), file);
                    break;
                case "Projects" when !pr:
                    pr = true;
                    foreach (var p in ((XElement)XNode.ReadFrom(r)).Elements()) file.Projects.Add(p.Name.LocalName);
                    break;
                case "Function" when !fn:
                    fn = true;
                    {
                        string? addr = r.GetAttribute("Address");
                        if (!string.IsNullOrWhiteSpace(addr)) file.FuncAddr = XmlHelpers.HexUpper(addr, 2);
                        file.FuncName = r.GetAttribute("Name") ?? "";
                        r.Skip();
                    }
                    break;
                case "CAN" when !can:
                    can = true;
                    ParseCan((XElement)XNode.ReadFrom(r), file);
                    break;
                case "K" when !k:
                    k = true;
                    ParseK((XElement)XNode.ReadFrom(r), file);
                    break;
                default:
                    r.Skip(); // Categories (screens) and anything else
                    break;
            }
        }
    }

    private static void ParseAutoIdents(XElement el, EcuFile file)
    {
        foreach (var ai in el.Elements("AutoIdent"))
            file.AutoIdents.Add(new AutoIdent(
                (string?)ai.Attribute("DiagVersion") ?? "",
                (string?)ai.Attribute("Supplier") ?? "",
                (string?)ai.Attribute("Soft") ?? "",
                (string?)ai.Attribute("Version") ?? ""));
    }

    private static string CanIdOf(XElement parent, string name) =>
        XmlHelpers.HexUpper((string?)parent.Element(name)?.Element("CANId")?.Attribute("Value"));

    private static void ParseCan(XElement can, EcuFile file)
    {
        file.Protocol = EcuProtocol.Can;
        file.BaudRate = XmlHelpers.Int((string?)can.Attribute("BaudRate"));
        string s = CanIdOf(can, "SendId");
        if (s.Length > 0) file.SendId = s;
        string rcv = CanIdOf(can, "ReceiveId");
        if (rcv.Length > 0) file.RecvId = rcv;
    }

    private static void ParseK(XElement k, EcuFile file)
    {
        var kwp = k.Element("KWP");
        var iso8 = k.Element("ISO8");
        XElement? kwSource = null;
        if (kwp != null)
        {
            file.Protocol = EcuProtocol.Kwp2000;
            var fast = kwp.Element("FastInit");
            if (fast != null) { file.FastInit = true; kwSource = fast; }
            else kwSource = kwp.Element("ISO8");
        }
        else if (iso8 != null)
        {
            file.FastInit = false;
            file.Protocol = EcuProtocol.Iso8;
            kwSource = iso8;
        }

        if (kwSource != null)
        {
            file.Kw1 = XmlHelpers.HexUpper((string?)kwSource.Element("KW1")?.Attribute("Value"));
            file.Kw2 = XmlHelpers.HexUpper((string?)kwSource.Element("KW2")?.Attribute("Value"));
        }
    }

    // ------------------------------------------------------------------ Device

    private static EcuDevice ParseDevice(XmlReader r)
    {
        r.MoveToContent();
        var dev = new EcuDevice
        {
            Name = r.GetAttribute("Name") ?? "",
            Dtc = XmlHelpers.Int(r.GetAttribute("DTC")),
            DtcType = XmlHelpers.Int(r.GetAttribute("Type")),
        };
        while (r.Read())
        {
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "DeviceData")
                dev.DeviceData[r.GetAttribute("Name") ?? ""] = r.GetAttribute("FailureFlag") ?? "";
        }

        return dev;
    }

    // ------------------------------------------------------------------ Request

    private static DataItem ParseDataItem(XmlReader r)
    {
        var di = new DataItem { Name = r.GetAttribute("Name") ?? "" };
        string? fb = r.GetAttribute("FirstByte");
        if (!string.IsNullOrEmpty(fb)) di.FirstByte = XmlHelpers.Int(fb);
        string? bo = r.GetAttribute("BitOffset");
        if (!string.IsNullOrEmpty(bo)) di.BitOffset = XmlHelpers.Int(bo);
        string? en = r.GetAttribute("Endian");
        if (!string.IsNullOrEmpty(en)) di.Endian = EcuProtocolNames.ParseOrder(en);
        if (r.GetAttribute("Ref") == "1") di.Ref = true;
        return di;
    }

    private static EcuRequest ParseRequest(XmlReader r)
    {
        r.MoveToContent();
        var req = new EcuRequest { Name = r.GetAttribute("Name") ?? "" };
        bool deny = false, shift = false, reply = false, received = false, sent = false, sentBytes = false;
        bool inReceived = false, inSent = false;
        int recvDepth = -1, sentDepth = -1;
        bool needRead = true;

        while (true)
        {
            if (needRead) { if (!r.Read()) break; }
            needRead = true;

            if (r.NodeType == XmlNodeType.EndElement)
            {
                if (inReceived && r.Depth == recvDepth) inReceived = false;
                else if (inSent && r.Depth == sentDepth) inSent = false;
                continue;
            }

            if (r.NodeType != XmlNodeType.Element) continue;

            switch (r.LocalName)
            {
                case "DenyAccess" when !deny:
                    deny = true;
                    if (!r.IsEmptyElement)
                    {
                        int d = r.Depth;
                        while (r.Read() && !(r.NodeType == XmlNodeType.EndElement && r.Depth == d))
                        {
                            if (r.NodeType != XmlNodeType.Element || r.Depth != d + 1) continue;
                            switch (r.LocalName)
                            {
                                case "NoSDS": req.Denied |= SessionAccess.NoSds; break;
                                case "Plant": req.Denied |= SessionAccess.Plant; break;
                                case "AfterSales": req.Denied |= SessionAccess.AfterSales; break;
                                case "Engineering": req.Denied |= SessionAccess.Engineering; break;
                                case "Supplier": req.Denied |= SessionAccess.Supplier; break;
                            }
                        }
                    }
                    break;
                case "ManuelSend":
                    req.ManualSend = true;
                    break;
                case "ShiftBytesCount" when !shift:
                    shift = true;
                    req.ShiftBytesCount = XmlHelpers.Int(XmlHelpers.ReadFirstText(r));
                    needRead = false;
                    break;
                case "ReplyBytes" when !reply:
                    reply = true;
                    req.ReplyBytes = XmlHelpers.ReadFirstText(r);
                    needRead = false;
                    break;
                case "Received" when !received:
                    received = true;
                    req.MinBytes = XmlHelpers.Int(r.GetAttribute("MinBytes"));
                    if (!r.IsEmptyElement) { inReceived = true; recvDepth = r.Depth; }
                    break;
                case "Sent" when !sent:
                    sent = true;
                    if (!r.IsEmptyElement) { inSent = true; sentDepth = r.Depth; }
                    break;
                case "SentBytes" when inSent && !sentBytes:
                    sentBytes = true;
                    req.SentBytes = XmlHelpers.ReadFirstText(r);
                    needRead = false;
                    break;
                case "DataItem":
                    if (inReceived) req.ReceiveItems.Add(ParseDataItem(r));
                    else if (inSent) req.SendItems.Add(ParseDataItem(r));
                    break;
            }
        }

        return req;
    }

    // ------------------------------------------------------------------ Data

    private static EcuData ParseData(XmlReader r)
    {
        r.MoveToContent();
        var d = new EcuData { Name = r.GetAttribute("Name") ?? "" };

        bool desc = false, comment = false, hasBytes = false, hasBits = false, hasScaled = false;
        string? bytesCount = null, bytesAscii = null, bitsCount = null, bitsSigned = null;
        bool binary = false;
        string? step = null, offset = null, divide = null, format = null, unit = null;
        bool inBits = false, inList = false;
        int bitsDepth = -1, listDepth = -1;
        bool needRead = true;

        while (true)
        {
            if (needRead) { if (!r.Read()) break; }
            needRead = true;

            if (r.NodeType == XmlNodeType.EndElement)
            {
                if (inBits && r.Depth == bitsDepth) inBits = false;
                if (inList && r.Depth == listDepth) inList = false;
                continue;
            }

            if (r.NodeType != XmlNodeType.Element) continue;

            switch (r.LocalName)
            {
                case "Description" when !desc:
                    desc = true;
                    d.Description = XmlHelpers.ReadFirstText(r);
                    needRead = false;
                    break;
                case "Comment" when !comment:
                    comment = true;
                    d.Comment = XmlHelpers.ReadFirstText(r);
                    needRead = false;
                    break;
                case "List":
                    if (!r.IsEmptyElement) { inList = true; listDepth = r.Depth; }
                    break;
                case "Item" when inList:
                    {
                        // Python: int(Value) (decimal); invalid entries are skipped here instead of crashing
                        string? v = r.GetAttribute("Value");
                        if (long.TryParse(v?.Trim(), System.Globalization.NumberStyles.AllowLeadingSign,
                                System.Globalization.CultureInfo.InvariantCulture, out long key))
                            d.AddListItem(key, r.GetAttribute("Text") ?? "");
                    }
                    break;
                case "Bytes" when !hasBytes:
                    hasBytes = true;
                    bytesCount = r.GetAttribute("count");
                    bytesAscii = r.GetAttribute("ascii");
                    break;
                case "Bits" when !hasBits:
                    hasBits = true;
                    bitsCount = r.GetAttribute("count");
                    bitsSigned = r.GetAttribute("signed");
                    if (!r.IsEmptyElement) { inBits = true; bitsDepth = r.Depth; }
                    break;
                case "Binary" when inBits:
                    binary = true;
                    break;
                case "Scaled" when inBits && !hasScaled:
                    hasScaled = true;
                    step = r.GetAttribute("Step");
                    offset = r.GetAttribute("Offset");
                    divide = r.GetAttribute("DivideBy");
                    format = r.GetAttribute("Format");
                    unit = r.GetAttribute("Unit");
                    break;
            }
        }

        // Apply in the same order as the Python reference (Bytes first, then Bits overrides).
        if (hasBytes)
        {
            d.Byte = true;
            string bc = (bytesCount ?? "").Replace(',', '.');
            if (bc.Contains('.'))
            {
                double f = XmlHelpers.Double(bc, 0);
                d.BytesCount = (int)Math.Ceiling(f);
                d.BitsCount = (int)(f * 8);
            }
            else if (bc.Length > 0)
            {
                d.BytesCount = XmlHelpers.Int(bc);
                d.BitsCount = d.BytesCount * 8;
            }

            if (bytesAscii == "1") d.BytesAscii = true;
        }

        if (hasBits)
        {
            if (!string.IsNullOrEmpty(bitsCount))
            {
                d.BitsCount = XmlHelpers.Int(bitsCount);
                d.BytesCount = (int)Math.Ceiling(d.BitsCount / 8.0);
            }

            if (!string.IsNullOrEmpty(bitsSigned)) d.Signed = true;
            if (binary) d.Binary = true;
            if (hasScaled)
            {
                d.Scaled = true;
                if (!string.IsNullOrEmpty(step)) d.Step = XmlHelpers.Double(step, 1.0);
                if (!string.IsNullOrEmpty(offset)) d.Offset = XmlHelpers.Double(offset, 0.0);
                if (!string.IsNullOrEmpty(divide)) d.DivideBy = XmlHelpers.Double(divide, 1.0);
                if (!string.IsNullOrEmpty(format)) d.Format = format;
                if (!string.IsNullOrEmpty(unit)) d.Unit = unit;
            }
        }

        return d;
    }
}
