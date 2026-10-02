using System.Globalization;
using System.Xml;

namespace Ddt4All.Core.Loading;

internal static class XmlHelpers
{
    public static XmlReaderSettings CreateSettings() => new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        CheckCharacters = false,
        CloseInput = false,
    };

    public static XmlReader Create(Stream s) => XmlReader.Create(s, CreateSettings());

    public static XmlReader Create(TextReader s) => XmlReader.Create(s, CreateSettings());

    public static int Int(string? s, int def = 0)
    {
        if (string.IsNullOrWhiteSpace(s)) return def;
        s = s.Trim();
        if (int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v)) return v;
        // Python int(float-string) is not valid, but be lenient with "12.0"
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return (int)d;
        return def;
    }

    public static long Long(string? s, long def = 0)
    {
        if (string.IsNullOrWhiteSpace(s)) return def;
        return long.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v) ? v : def;
    }

    /// <summary>Accepts both '.' and ',' decimal separators.</summary>
    public static double Double(string? s, double def)
    {
        if (string.IsNullOrWhiteSpace(s)) return def;
        s = s.Trim().Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : def;
    }

    /// <summary>
    /// Consumes the current element and returns the value of its first child text node
    /// (like minidom's <c>firstChild.nodeValue</c>), or "" if the first child is not text.
    /// The reader ends on the node after the element.
    /// </summary>
    public static string ReadFirstText(XmlReader r)
    {
        if (r.IsEmptyElement) { r.Read(); return ""; }
        int depth = r.Depth;
        string result = "";
        bool first = true;
        r.Read();
        while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
        {
            if (first && (r.NodeType == XmlNodeType.Text || r.NodeType == XmlNodeType.CDATA))
                result = r.Value;
            first = false;
            if (r.NodeType == XmlNodeType.Element) r.Skip(); else r.Read();
        }

        r.Read(); // past end element
        return result;
    }

    /// <summary>Python <c>hex(int(v))[2:].upper()</c> (no padding); "" when not an integer.</summary>
    public static string HexUpper(string? decimalText, int pad = 0)
    {
        if (!long.TryParse(decimalText?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v)) return "";
        string h = v.ToString("X", CultureInfo.InvariantCulture);
        return pad > h.Length ? h.PadLeft(pad, '0') : h;
    }
}
