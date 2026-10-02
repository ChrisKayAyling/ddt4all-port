using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Ddt4All.Core.Ecu;
using Ddt4All.Core.Layout;

namespace Ddt4All.App.Views.Screens;

public enum SceneKind : byte { Label, Display, Input, Button }

/// <summary>One drawable element. Plain data + lazily created text layouts; no controls, no bindings.</summary>
public sealed class SceneItem
{
    public SceneKind Kind;
    public Rect Bounds;                 // design pixels
    public double LabelWidth;           // Display/Input: width of the caption part
    public string Caption = "";
    public string Value = "";
    public IBrush Back = Brushes.Transparent, Fore = Brushes.Black;
    public Typeface Face;
    public double FontSize;
    public int Alignment;               // labels: 0 left, 1 right?, 2 centre (as stored; mapped in canvas)
    public int Slot = -1;               // Display: index in screen.Displays (live engine slot)
    public string DataName = "", RequestName = "";
    public string Tooltip = "";
    public bool Staged;                 // input holds a not yet sent value
    public string[]? Choices;           // input backed by a list
    public ScreenButton? Button;
    public ScreenInput? Input;

    internal FormattedText? CaptionText, ValueText;
    internal string? ValueTextSource;
    internal double ValueTextWidth;

    public Rect ValueRect => new(Bounds.X + LabelWidth, Bounds.Y, Math.Max(0, Bounds.Width - LabelWidth), Bounds.Height);
    public Rect LabelRect => new(Bounds.X, Bounds.Y, Math.Min(LabelWidth, Bounds.Width), Bounds.Height);
}

/// <summary>Flat, render-ready description of a <see cref="Screen"/>.</summary>
public sealed class ScreenScene
{
    public double Width { get; }
    public double Height { get; }
    public IBrush Background { get; }
    public SceneItem[] Items { get; }
    public int DisplayCount { get; }

    private readonly Dictionary<int, SceneItem> _bySlot = new();

    public SceneItem? BySlot(int slot) => _bySlot.GetValueOrDefault(slot);

    private ScreenScene(double w, double h, IBrush bg, SceneItem[] items, int displays)
    {
        Width = w; Height = h; Background = bg; Items = items; DisplayCount = displays;
        foreach (var i in items) if (i.Kind == SceneKind.Display) _bySlot[i.Slot] = i;
    }

    private static readonly Dictionary<uint, IBrush> Brushes_ = new();
    internal static IBrush Brush(LayoutColor c)
    {
        uint k = c.ToRgb();
        lock (Brushes_)
        {
            if (!Brushes_.TryGetValue(k, out var b)) Brushes_[k] = b = new ImmutableSolidColorBrush(Color.FromUInt32(0xFF000000 | k));
            return b;
        }
    }

    private static Typeface MakeFace(LayoutFont f) =>
        new(string.IsNullOrWhiteSpace(f.Name) ? FontFamily.Default : new FontFamily($"{f.Name}, Inter, sans-serif"),
            f.Italic ? FontStyle.Italic : FontStyle.Normal, f.Bold ? FontWeight.Bold : FontWeight.Normal);

    // DDT font sizes are in points at 96 dpi
    private static double MakeSize(LayoutFont f) => Math.Clamp((f.Size <= 0 ? 8.5 : f.Size) * 96.0 / 72.0, 7, 60);

    private static Rect R(LayoutRect r, double k = 1) => new(r.Left * k, r.Top * k, Math.Max(1, r.Width * k), Math.Max(1, r.Height * k));

    /// <summary>Layouts converted from DDT2000 are stored in twips (15 per pixel): detect them by the control height (a real pixel layout has ~20-40 px rows).</summary>
    internal static double LayoutScale(Screen s)
    {
        var h = new List<int>(s.Displays.Count + s.Inputs.Count + s.Buttons.Count);
        foreach (var d in s.Displays) h.Add(d.Bounds.Height);
        foreach (var d in s.Inputs) h.Add(d.Bounds.Height);
        foreach (var b in s.Buttons) h.Add(b.Bounds.Height);
        if (h.Count == 0) return 1;
        h.Sort();
        return h[h.Count / 2] >= 120 ? 1.0 / 15 : 1.0;
    }

    public static ScreenScene Build(Screen s, EcuFile? ecu)
    {
        double k = LayoutScale(s);
        var items = new List<SceneItem>(s.Labels.Count + s.Displays.Count + s.Inputs.Count + s.Buttons.Count);
        foreach (var l in s.Labels)
            items.Add(new SceneItem
            {
                Kind = SceneKind.Label, Bounds = R(l.Bounds, k), Caption = l.Text, Back = Brush(l.Color), Fore = Brush(l.FontColor),
                Face = MakeFace(l.Font), FontSize = MakeSize(l.Font), Alignment = int.TryParse(l.Alignment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) ? a : 0,
            });
        for (int i = 0; i < s.Displays.Count; i++)
        {
            var d = s.Displays[i];
            items.Add(new SceneItem
            {
                Kind = SceneKind.Display, Bounds = R(d.Bounds, k), LabelWidth = d.Width * k, Caption = d.DataName, Value = "",
                Back = Brush(d.Color), Fore = Brush(d.FontColor), Face = MakeFace(d.Font), FontSize = MakeSize(d.Font),
                Slot = i, DataName = d.DataName, RequestName = d.RequestName, Tooltip = Describe(ecu, d),
            });
        }
        foreach (var inp in s.Inputs)
        {
            string[]? choices = null;
            if (ecu?.GetData(inp.DataName) is { } data && data.Items.Count > 0)
            {
                choices = data.Items.Keys.ToArray();
                Array.Sort(choices, StringComparer.Ordinal);
            }
            items.Add(new SceneItem
            {
                Kind = SceneKind.Input, Bounds = R(inp.Bounds, k), LabelWidth = inp.Width * k, Caption = inp.DataName, Value = "",
                Back = Brush(inp.Color), Fore = Brush(inp.FontColor), Face = MakeFace(inp.Font), FontSize = MakeSize(inp.Font),
                DataName = inp.DataName, RequestName = inp.RequestName, Choices = choices, Input = inp, Tooltip = Describe(ecu, inp),
            });
        }
        foreach (var b in s.Buttons)
            items.Add(new SceneItem
            {
                Kind = SceneKind.Button, Bounds = R(b.Bounds, k), Caption = b.Text, Face = MakeFace(b.Font), FontSize = MakeSize(b.Font),
                Button = b, Tooltip = string.Join(", ", b.Send.Select(x => x.RequestName)),
            });

        double w = s.Width * k, h = s.Height * k;
        foreach (var i in items) { w = Math.Max(w, i.Bounds.Right + 4); h = Math.Max(h, i.Bounds.Bottom + 4); }
        return new ScreenScene(Math.Max(w, 200), Math.Max(h, 120), Brush(s.Color), items.ToArray(), s.Displays.Count);
    }

    internal static string Describe(EcuFile? ecu, ScreenDisplay d)
    {
        if (ecu is null) return d.DataName;
        var sb = new System.Text.StringBuilder(d.RequestName).Append('\n');
        if (ecu.GetData(d.DataName) is { } data)
        {
            if (data.Comment.Length > 0) sb.Append(data.Comment).Append('\n');
            sb.Append("NumBits=").Append(data.BitsCount);
        }
        if (ecu.GetRequest(d.RequestName) is { } req)
        {
            sb.Append("\nRequest=").Append(req.SentBytes);
            if (req.ReceiveItems.TryGetValue(d.DataName, out var it)) sb.Append(" FirstByte=").Append(it.FirstByte).Append(" BitOffset=").Append(it.BitOffset);
        }
        return sb.ToString();
    }
}
