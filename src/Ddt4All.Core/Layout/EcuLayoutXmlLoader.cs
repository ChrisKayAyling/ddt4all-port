using System.Xml;
using Ddt4All.Core.Loading;

namespace Ddt4All.Core.Layout;

/// <summary>
/// Streaming parser for the screens of a DDT ECU XML file (Target/Categories/Category/Screen).
/// Port of <c>helpers.dumpDOC</c>.
/// </summary>
public static class EcuLayoutXmlLoader
{
    /// <summary>Loads from a file.</summary>
    public static EcuLayout Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        return Load(fs);
    }

    /// <summary>Loads from a stream.</summary>
    public static EcuLayout Load(Stream stream)
    {
        using var r = XmlHelpers.Create(stream);
        return Load(r);
    }

    /// <summary>Loads from XML text.</summary>
    public static EcuLayout LoadFromString(string xml)
    {
        using var r = XmlHelpers.Create(new StringReader(xml));
        return Load(r);
    }

    private static EcuLayout Load(XmlReader reader)
    {
        var layout = new EcuLayout();
        try
        {
            if (reader.MoveToContent() != XmlNodeType.Element) return layout;
            int rootDepth = reader.Depth;
            bool target = false;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.Depth != rootDepth + 1 || reader.LocalName != "Target") continue;
                using (var sub = reader.ReadSubtree())
                {
                    if (target) continue;
                    target = true;
                    ParseTarget(sub, layout);
                }
            }
        }
        catch (XmlException ex)
        {
            throw new Ecu.EcuFormatException("Invalid ECU XML: " + ex.Message, ex);
        }

        return layout;
    }

    private static void ParseTarget(XmlReader r, EcuLayout layout)
    {
        r.MoveToContent();
        if (r.IsEmptyElement) return;
        r.Read();
        while (!r.EOF && r.NodeType != XmlNodeType.EndElement)
        {
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "Categories")
            {
                var sub = r.ReadSubtree();
                ParseCategories(sub, layout);
                sub.Dispose();
                r.Read();
            }
            else if (r.NodeType == XmlNodeType.Element) r.Skip();
            else r.Read();
        }
    }

    private static void ParseCategories(XmlReader r, EcuLayout layout)
    {
        r.MoveToContent();
        if (r.IsEmptyElement) return;
        r.Read();
        while (!r.EOF && r.NodeType != XmlNodeType.EndElement)
        {
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "Category")
            {
                var sub = r.ReadSubtree();
                ParseCategory(sub, layout);
                sub.Dispose();
                r.Read();
            }
            else if (r.NodeType == XmlNodeType.Element) r.Skip();
            else r.Read();
        }
    }

    private static void ParseCategory(XmlReader r, EcuLayout layout)
    {
        r.MoveToContent();
        var cat = layout.GetOrAddCategory(r.GetAttribute("Name") ?? "");
        if (r.IsEmptyElement) return;
        r.Read();
        while (!r.EOF && r.NodeType != XmlNodeType.EndElement)
        {
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "Screen")
            {
                var sub = r.ReadSubtree();
                var screen = ParseScreen(sub);
                cat.ScreenNames.Add(screen.Name);
                layout.Screens.Add(screen); // later duplicates replace earlier ones (dict semantics)
                sub.Dispose();
                r.Read();
            }
            else if (r.NodeType == XmlNodeType.Element) r.Skip();
            else r.Read();
        }
    }

    private static LayoutColor Color(string? s, LayoutColor def) =>
        string.IsNullOrWhiteSpace(s) ? def : LayoutColor.FromColorRef(XmlHelpers.Long(s));

    private static SendCommand ParseSend(XmlReader r) =>
        new(r.GetAttribute("RequestName") ?? "", XmlHelpers.Int(r.GetAttribute("Delay")));

    private static Screen ParseScreen(XmlReader r)
    {
        r.MoveToContent();
        var s = new Screen
        {
            Name = r.GetAttribute("Name") ?? "",
            Width = XmlHelpers.Int(r.GetAttribute("Width")),
            Height = XmlHelpers.Int(r.GetAttribute("Height")),
            Color = Color(r.GetAttribute("Color"), default),
        };
        if (r.IsEmptyElement) return s;
        r.Read();
        int buttonIndex = 0;
        while (!r.EOF && r.NodeType != XmlNodeType.EndElement)
        {
            if (r.NodeType != XmlNodeType.Element) { r.Read(); continue; }
            switch (r.LocalName)
            {
                case "Send":
                    s.PreSend.Add(ParseSend(r));
                    r.Skip();
                    break;
                case "Label":
                    {
                        var lbl = new ScreenLabel
                        {
                            Text = r.GetAttribute("Text") ?? "",
                            Color = Color(r.GetAttribute("Color"), default),
                            Alignment = r.GetAttribute("Alignment") ?? "",
                        };
                        var sub = r.ReadSubtree();
                        ParseGeometry(sub, out var rect, out var font, out var fc, out _, out _);
                        lbl.Bounds = rect; lbl.Font = font; lbl.FontColor = fc;
                        s.Labels.Add(lbl);
                        sub.Dispose();
                        r.Read();
                    }
                    break;
                case "Display":
                case "Input":
                    {
                        bool input = r.LocalName == "Input";
                        ScreenDisplay d = input ? new ScreenInput() : new ScreenDisplay();
                        d.DataName = r.GetAttribute("DataName") ?? "";
                        d.RequestName = r.GetAttribute("RequestName") ?? "";
                        // Input without colour defaults to 0xAAAAAA; Display colour is mandatory in the reference.
                        d.Color = Color(r.GetAttribute("Color"), input ? LayoutColor.DefaultGrey : default);
                        d.Width = XmlHelpers.Int(r.GetAttribute("Width"));
                        var sub = r.ReadSubtree();
                        ParseGeometry(sub, out var rect, out var font, out var fc, out _, out _);
                        d.Bounds = rect; d.Font = font; d.FontColor = fc;
                        if (input) s.Inputs.Add((ScreenInput)d); else s.Displays.Add(d);
                        sub.Dispose();
                        r.Read();
                    }
                    break;
                case "Button":
                    {
                        var b = new ScreenButton { Text = r.GetAttribute("Text") ?? "" };
                        b.UniqueName = b.Text + "_" + buttonIndex++;
                        var sub = r.ReadSubtree();
                        ParseGeometry(sub, out var rect, out var font, out _, out var messages, out var sends);
                        b.Bounds = rect; b.Font = font;
                        if (messages != null) b.Messages.AddRange(messages);
                        if (sends != null) b.Send.AddRange(sends);
                        s.Buttons.Add(b);
                        sub.Dispose();
                        r.Read();
                    }
                    break;
                default:
                    r.Skip();
                    break;
            }
        }

        return s;
    }

    /// <summary>Reads Rectangle/Font (first of each) and, for buttons, Message/Send children.</summary>
    private static void ParseGeometry(XmlReader r, out LayoutRect rect, out LayoutFont font, out LayoutColor fontColor,
        out List<string>? messages, out List<SendCommand>? sends)
    {
        rect = default; font = LayoutFont.None; fontColor = LayoutColor.DefaultGrey;
        messages = null; sends = null;
        bool gotRect = false, gotFont = false;
        r.MoveToContent();
        if (r.IsEmptyElement) return;
        r.Read();
        while (!r.EOF && r.NodeType != XmlNodeType.EndElement)
        {
            if (r.NodeType != XmlNodeType.Element) { r.Read(); continue; }
            switch (r.LocalName)
            {
                case "Rectangle" when !gotRect:
                    gotRect = true;
                    // Python: int(float(x)/scale) -> truncation
                    rect = new LayoutRect(
                        (int)XmlHelpers.Double(r.GetAttribute("Left"), 0),
                        (int)XmlHelpers.Double(r.GetAttribute("Top"), 0),
                        (int)XmlHelpers.Double(r.GetAttribute("Width"), 0),
                        (int)XmlHelpers.Double(r.GetAttribute("Height"), 0));
                    break;
                case "Font" when !gotFont:
                    gotFont = true;
                    font = new LayoutFont(
                        r.GetAttribute("Name") ?? "",
                        XmlHelpers.Double(r.GetAttribute("Size"), 0),
                        r.GetAttribute("Bold") == "1",
                        r.GetAttribute("Italic") == "1");
                    fontColor = Color(r.GetAttribute("Color"), LayoutColor.DefaultGrey);
                    break;
                case "Message":
                    (messages ??= new()).Add(r.GetAttribute("Text") ?? "");
                    break;
                case "Send":
                    (sends ??= new()).Add(ParseSend(r));
                    break;
            }

            r.Skip();
        }
    }
}
