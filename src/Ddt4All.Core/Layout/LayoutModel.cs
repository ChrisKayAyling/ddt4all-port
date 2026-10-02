using System.Globalization;
using Ddt4All.Core.Ecu;

namespace Ddt4All.Core.Layout;

/// <summary>sRGB colour. DDT stores colours as Windows COLORREF (0xBBGGRR) integers.</summary>
public readonly record struct LayoutColor(byte R, byte G, byte B)
{
    /// <summary>Default grey used by DDT for missing font/input colours (0xAAAAAA).</summary>
    public static readonly LayoutColor DefaultGrey = new(0xAA, 0xAA, 0xAA);

    /// <summary>From a COLORREF value ("Color" attribute of the XML), exactly like Python <c>colorConvert</c>.</summary>
    public static LayoutColor FromColorRef(long colorRef) =>
        new((byte)(colorRef & 0xFF), (byte)((colorRef >> 8) & 0xFF), (byte)((colorRef >> 16) & 0xFF));

    /// <summary>0xRRGGBB packed (handy for Avalonia <c>Color.FromUInt32(0xFF000000 | value)</c>).</summary>
    public uint ToRgb() => ((uint)R << 16) | ((uint)G << 8) | B;

    /// <summary>"rgb(r,g,b)" as stored in the layout JSON.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"rgb({R},{G},{B})");

    /// <summary>Parses "rgb(r,g,b)" (whitespace tolerant). Returns false on malformed text.</summary>
    public static bool TryParse(ReadOnlySpan<char> s, out LayoutColor color)
    {
        color = default;
        s = s.Trim();
        if (!s.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase) || !s.EndsWith(")")) return false;
        s = s.Slice(4, s.Length - 5);
        Span<int> v = stackalloc int[3];
        int n = 0;
        foreach (var range in s.Split(','))
        {
            if (n >= 3 || !int.TryParse(s[range], NumberStyles.Integer, CultureInfo.InvariantCulture, out v[n]) || v[n] is < 0 or > 255)
                return false;
            n++;
        }

        if (n != 3) return false;
        color = new LayoutColor((byte)v[0], (byte)v[1], (byte)v[2]);
        return true;
    }
}

/// <summary>Integer rectangle in DDT design units (pixels at scale 1).</summary>
public readonly record struct LayoutRect(int Left, int Top, int Width, int Height);

/// <summary>Font description of a screen element.</summary>
public sealed record LayoutFont(string Name, double Size, bool Bold, bool Italic)
{
    /// <summary>Empty font (name "", size 0).</summary>
    public static readonly LayoutFont None = new("", 0, false, false);
}

/// <summary>A request to send before/when something happens ("Send" element): request name + delay in ms.</summary>
public readonly record struct SendCommand(string RequestName, int DelayMs);

/// <summary>Static text.</summary>
public sealed class ScreenLabel
{
    /// <summary>Text.</summary>
    public string Text { get; set; } = "";
    /// <summary>Background colour.</summary>
    public LayoutColor Color { get; set; }
    /// <summary>Font colour.</summary>
    public LayoutColor FontColor { get; set; } = LayoutColor.DefaultGrey;
    /// <summary>Alignment text from the file ("0","1","2"... as stored).</summary>
    public string Alignment { get; set; } = "";
    /// <summary>Bounding box.</summary>
    public LayoutRect Bounds { get; set; }
    /// <summary>Font.</summary>
    public LayoutFont Font { get; set; } = LayoutFont.None;
}

/// <summary>Read-only display of a decoded value (data <see cref="DataName"/> of request <see cref="RequestName"/>).</summary>
public class ScreenDisplay
{
    /// <summary>Data name (key into <see cref="EcuFile.Data"/> and into the request's receive items).</summary>
    public string DataName { get; set; } = "";
    /// <summary>Request that provides the value.</summary>
    public string RequestName { get; set; } = "";
    /// <summary>Background colour.</summary>
    public LayoutColor Color { get; set; }
    /// <summary>Font colour.</summary>
    public LayoutColor FontColor { get; set; } = LayoutColor.DefaultGrey;
    /// <summary>Width of the value part (the label part is the rest of <see cref="Bounds"/>).</summary>
    public int Width { get; set; }
    /// <summary>Bounding box.</summary>
    public LayoutRect Bounds { get; set; }
    /// <summary>Font.</summary>
    public LayoutFont Font { get; set; } = LayoutFont.None;
}

/// <summary>Editable input bound to a data of a request's sent items.</summary>
public sealed class ScreenInput : ScreenDisplay
{
}

/// <summary>Push button that sends requests.</summary>
public sealed class ScreenButton
{
    /// <summary>Caption.</summary>
    public string Text { get; set; } = "";
    /// <summary>Caption plus running index ("Reset_0"), unique inside a screen.</summary>
    public string UniqueName { get; set; } = "";
    /// <summary>Bounding box.</summary>
    public LayoutRect Bounds { get; set; }
    /// <summary>Font.</summary>
    public LayoutFont Font { get; set; } = LayoutFont.None;
    /// <summary>Confirmation / info messages shown before sending.</summary>
    public List<string> Messages { get; } = new();
    /// <summary>Requests sent when the button is pressed.</summary>
    public List<SendCommand> Send { get; } = new();
}

/// <summary>A diagnostic screen (one page of the DDT UI).</summary>
public sealed class Screen : INamed
{
    /// <summary>Screen name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Design width.</summary>
    public int Width { get; set; }
    /// <summary>Design height.</summary>
    public int Height { get; set; }
    /// <summary>Background colour.</summary>
    public LayoutColor Color { get; set; }
    /// <summary>Requests to send when the screen is opened (the "Send" elements directly below the screen).</summary>
    public List<SendCommand> PreSend { get; } = new();
    /// <summary>Static labels.</summary>
    public List<ScreenLabel> Labels { get; } = new();
    /// <summary>Value displays.</summary>
    public List<ScreenDisplay> Displays { get; } = new();
    /// <summary>Buttons.</summary>
    public List<ScreenButton> Buttons { get; } = new();
    /// <summary>Inputs.</summary>
    public List<ScreenInput> Inputs { get; } = new();
}

/// <summary>A category (folder) of screens, in display order.</summary>
public sealed class ScreenCategory
{
    /// <summary>Category name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Names of the screens in this category.</summary>
    public List<string> ScreenNames { get; } = new();
}

/// <summary>Complete UI layout of an ECU: categories and screens, ready to be rendered by a UI.</summary>
public sealed class EcuLayout
{
    /// <summary>Categories in definition order (duplicate category names are merged like in the Python dict).</summary>
    public List<ScreenCategory> Categories { get; } = new();
    /// <summary>Screens in definition order.</summary>
    public NamedCollection<Screen> Screens { get; } = new();

    /// <summary>An empty layout (no screens).</summary>
    public static EcuLayout Empty => new();

    /// <summary>Category by name or null.</summary>
    public ScreenCategory? GetCategory(string name)
    {
        foreach (var c in Categories) if (c.Name == name) return c;
        return null;
    }

    /// <summary>Screen by name or null.</summary>
    public Screen? GetScreen(string name) => Screens.TryGetValue(name, out var s) ? s : null;

    internal ScreenCategory GetOrAddCategory(string name)
    {
        var c = GetCategory(name);
        if (c == null) { c = new ScreenCategory { Name = name }; Categories.Add(c); }
        return c;
    }
}
