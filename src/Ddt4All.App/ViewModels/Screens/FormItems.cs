using System.Globalization;
using System.Windows.Input;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ddt4All.App.Localization;
using Ddt4All.Core.Ecu;
using Ddt4All.Core.Layout;

namespace Ddt4All.App.ViewModels.Screens;

internal static class FormMetrics
{
    /// <summary>Lines (1 or 2) a tile caption needs at the given tile width (12.5 px medium text ~ 6.9 px per char).</summary>
    public static int CaptionLines(string caption, string hint, double width)
    {
        double avail = width - 24 - (hint.Length > 0 ? hint.Length * 6 + 12 : 0) - 56;   // 56: room for the EDITED badge
        return caption.Length * 6.9 > avail ? 2 : 1;
    }
}

public enum FormRowKind : byte { Heading, Display, Input, Buttons, Note, SendBar }
public enum ValueState : byte { Offline, Waiting, Live, Stale, Error }
public enum EditorKind : byte { List, Number, Ascii, Hex }

/// <summary>One virtualised cell of the form page. Heights are deterministic so the panel can lay out 8000 of them without measuring.</summary>
public abstract class FormRow : ObservableObject
{
    public int Section { get; set; }
    public abstract FormRowKind Kind { get; }
    /// <summary>Recycling key: controls are only reused for rows that use the same template.</summary>
    public virtual string PoolKey => Kind.ToString();
    /// <summary>Spans the whole width of its card (headings, notes, button strips, send bars).</summary>
    public virtual bool FullWidth => false;
    public abstract double GetHeight(double width);
    /// <summary>Lower-case text the filter box searches.</summary>
    public string SearchKey { get; set; } = "";
}

public sealed class FormHeadingRow : FormRow
{
    public string Title { get; init; } = "";
    public int Count { get; init; }
    public override FormRowKind Kind => FormRowKind.Heading;
    public override bool FullWidth => true;
    public override double GetHeight(double width) => 34;
}

public sealed class FormNoteRow : FormRow
{
    public string Text { get; init; } = "";
    public override FormRowKind Kind => FormRowKind.Note;
    public override bool FullWidth => true;
    public override double GetHeight(double width)
    {
        int perLine = Math.Max(12, (int)((width - 20) / 6.9));
        int lines = 0;
        foreach (var part in Text.Split('\n')) lines += Math.Max(1, (int)Math.Ceiling(part.Length / (double)perLine));
        return 12 + Math.Min(lines, 12) * 17;
    }
}

/// <summary>Live read-only value: caption, monospace value box, unit, state, optional sparkline.</summary>
public sealed partial class FormDisplayRow : FormRow
{
    public int Slot { get; init; }
    public string Caption { get; init; } = "";
    public string RawName { get; init; } = "";
    public string Hint { get; init; } = "";
    public bool HasHint => Hint.Length > 0;
    public string Tooltip { get; init; } = "";
    public string Unit { get; init; } = "";
    /// <summary>Numeric value (scaled, no list/ASCII): sparkline + min/max make sense.</summary>
    public bool IsNumeric { get; init; }
    /// <summary>Shown inside an input tile as its "current" value (compact).</summary>
    public bool IsReading { get; set; }
    public override FormRowKind Kind => FormRowKind.Display;
    public override double GetHeight(double width) => 52 + 16 * FormMetrics.CaptionLines(Caption, Hint, width);

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowValue))] private string _valueText = "";
    [ObservableProperty] private string _unitText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsError)), NotifyPropertyChangedFor(nameof(IsStale)), NotifyPropertyChangedFor(nameof(IsLive)), NotifyPropertyChangedFor(nameof(IsWaiting)), NotifyPropertyChangedFor(nameof(ShowValue)), NotifyPropertyChangedFor(nameof(Placeholder))]
    private ValueState _state = ValueState.Offline;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _minMaxText = "";

    public bool IsError => State == ValueState.Error;
    public bool IsStale => State == ValueState.Stale;
    public bool IsLive => State == ValueState.Live;
    public bool IsWaiting => State is ValueState.Waiting or ValueState.Offline;
    public bool ShowValue => State is ValueState.Live or ValueState.Stale;
    public string Placeholder => State switch { ValueState.Error => "–", ValueState.Waiting => "…", _ => "–" };
    public bool ShowSpark => IsNumeric;

    internal ScreenLiveEngine? Engine { get; set; }
    /// <summary>Raised after a numeric sample arrived (sparkline invalidates itself).</summary>
    public event Action? Sampled;
    internal void RaiseSampled() => Sampled?.Invoke();
    public ICommand SelectCommand { get; internal set; } = null!;

    public int CopyRecent(Span<double> dest) => Engine is { } e ? e.CopyRecent(Slot, dest) : 0;

    /// <summary>Float noise from the codec ("30.400000000000002") is trimmed to 6 decimals for display; logs keep the raw numbers.</summary>
    private static string Tidy(string v)
    {
        int dot = v.IndexOf('.');
        if (dot < 0 || v.Length - dot - 1 <= 6 || v.Contains('e') || v.Contains('E')) return v;
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d.ToString("0.######", CultureInfo.InvariantCulture) : v;
    }

    internal void Apply(string? text, int status, bool running)
    {
        if (status == 0) { State = running ? ValueState.Waiting : ValueState.Offline; return; }
        if (status == 2 || text is null) { State = ValueState.Error; ValueText = ""; UnitText = ""; return; }
        if (Unit.Length > 0 && text.Length > Unit.Length + 1 && text.EndsWith(Unit, StringComparison.Ordinal) && text[^(Unit.Length + 1)] == ' ')
        { ValueText = text[..^(Unit.Length + 1)]; UnitText = Unit; }
        else { ValueText = text; UnitText = ""; }
        if (IsNumeric) ValueText = Tidy(ValueText);
        State = running ? ValueState.Live : ValueState.Stale;
    }
}

public abstract partial class FormInputRow : FormRow
{
    private static readonly Regex HexChars = new("^[0-9A-Fa-f ]*$", RegexOptions.Compiled);
    private readonly Func<FormInputRow, string, string?> _stage;
    private bool _suppress;

    protected FormInputRow(Func<FormInputRow, string, string?> stage) { _stage = stage; }

    public int Index { get; init; }
    public string Caption { get; init; } = "";
    public string RawName { get; init; } = "";
    public string RequestName { get; init; } = "";
    public string Hint { get; init; } = "";
    public bool HasHint => Hint.Length > 0;
    public string Tooltip { get; init; } = "";
    public string Unit { get; init; } = "";
    public bool HasUnit => Unit.Length > 0;
    public abstract EditorKind Editor { get; }
    public override string PoolKey => Editor == EditorKind.List ? "in-list" : Editor == EditorKind.Number ? "in-num" : "in-text";
    public bool IsList => Editor == EditorKind.List;
    public bool IsNumber => Editor == EditorKind.Number;
    public bool IsText => Editor is EditorKind.Ascii or EditorKind.Hex;
    public bool IsMono => Editor is EditorKind.Hex or EditorKind.Number;
    public IReadOnlyList<string> Choices { get; init; } = Array.Empty<string>();
    public decimal Minimum { get; init; } = decimal.MinValue;
    public decimal Maximum { get; init; } = decimal.MaxValue;
    public decimal Increment { get; init; } = 1;
    public int Decimals { get; init; }
    public string NumberFormat => "F" + Decimals.ToString(CultureInfo.InvariantCulture);
    public int MaxLength { get; init; }
    public string Watermark { get; init; } = "";
    public FormDisplayRow? Reading { get; init; }
    public bool HasReading => Reading is not null;
    public override FormRowKind Kind => FormRowKind.Input;
    public override double GetHeight(double width) => 46 + 16 * FormMetrics.CaptionLines(Caption, Hint, width) + (HasReading ? 32 : 0);

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string? _error;
    [ObservableProperty] private bool _isStaged;
    [ObservableProperty] private string? _text;
    [ObservableProperty] private decimal? _number;
    public bool HasError => Error is not null;

    partial void OnTextChanged(string? value)
    {
        if (_suppress) return;
        if (value is null) return;
        if (IsList) Commit(value);
        else if (Editor == EditorKind.Hex) Error = value.Length == 0 || HexChars.IsMatch(value) ? null : Loc.T("Hex digits only");
        else Error = null;
    }

    partial void OnNumberChanged(decimal? value)
    {
        if (_suppress || value is null) return;
        Commit(value.Value.ToString(NumberFormat, CultureInfo.InvariantCulture));
    }

    /// <summary>Validates the current editor text with the codec and stages it (called on Enter / focus loss / selection).</summary>
    public void CommitText() { if (Text is { Length: > 0 } t && Editor != EditorKind.List) Commit(t); else if (Text is { Length: 0 } && IsStaged) Revert(); }

    private void Commit(string text)
    {
        if (Editor == EditorKind.Hex && !HexChars.IsMatch(text)) { Error = Loc.T("Hex digits only"); return; }
        var err = _stage(this, text);
        Error = err;
        if (err is null) IsStaged = true;
    }

    /// <summary>Back to the unedited state (after a send / discard).</summary>
    public void Revert()
    {
        _suppress = true;
        try { Text = null; Number = null; Error = null; IsStaged = false; }
        finally { _suppress = false; }
    }

    /// <summary>Programmatic edit (tests, classic canvas bridge).</summary>
    public void SetEdit(string text)
    {
        _suppress = true;
        try
        {
            if (IsNumber && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) Number = d; else Text = text;
        }
        finally { _suppress = false; }
        Error = null; IsStaged = true;
    }

    /// <summary>Editor kind, bounds and choices derived from the ECU data definition.</summary>
    public static (EditorKind Kind, IReadOnlyList<string> Choices, decimal Min, decimal Max, decimal Inc, int Decimals, int MaxLen, string Watermark, string Unit) Describe(EcuData? d)
    {
        if (d is null) return (EditorKind.Ascii, Array.Empty<string>(), 0, 0, 1, 0, 0, "", "");
        if (d.BytesAscii) return (EditorKind.Ascii, Array.Empty<string>(), 0, 0, 1, 0, Math.Max(1, d.BytesCount), Loc.T("Text") + " · ≤ " + d.BytesCount, "");
        if (d.HasList)
        {
            var c = d.Lists.OrderBy(kv => kv.Key).Select(kv => kv.Value).Distinct().ToList();
            return (EditorKind.List, c, 0, 0, 1, 0, 0, "", "");
        }
        if (d.Scaled)
        {
            double div = d.DivideBy == 0 ? 1 : d.DivideBy;
            double inc = Math.Abs(d.Step / div); if (inc <= 0 || double.IsNaN(inc)) inc = 1;
            int bits = Math.Clamp(d.BitsCount, 1, 63);
            double rawMin = d.Signed && bits is 8 or 16 or 32 ? -Math.Pow(2, bits - 1) : 0;
            double rawMax = d.Signed && bits is 8 or 16 or 32 ? Math.Pow(2, bits - 1) - 1 : Math.Pow(2, bits) - 1;
            double a = (rawMin * d.Step + d.Offset) / div, b = (rawMax * d.Step + d.Offset) / div;
            double lo = Math.Min(a, b), hi = Math.Max(a, b);
            const double lim = 1e12;
            int dec = 0;
            int dot = d.Format.IndexOf('.');
            if (dot >= 0) dec = Math.Min(6, d.Format.Length - dot - 1);
            else { double x = inc; while (dec < 6 && Math.Abs(x - Math.Round(x)) > 1e-9) { x *= 10; dec++; } }
            return (EditorKind.Number, Array.Empty<string>(), (decimal)Math.Clamp(lo, -lim, lim), (decimal)Math.Clamp(hi, -lim, lim),
                (decimal)inc, dec, 0, "", d.Unit);
        }
        return (EditorKind.Hex, Array.Empty<string>(), 0, 0, 1, 0, Math.Max(2, d.RawByteLength * 2 + d.RawByteLength - 1), "hex · " + d.RawByteLength + " B", "");
    }
}

public sealed class FormListInputRow(Func<FormInputRow, string, string?> stage) : FormInputRow(stage) { public override EditorKind Editor => EditorKind.List; }
public sealed class FormNumberInputRow(Func<FormInputRow, string, string?> stage) : FormInputRow(stage) { public override EditorKind Editor => EditorKind.Number; }
public sealed class FormTextInputRow(Func<FormInputRow, string, string?> stage, EditorKind kind) : FormInputRow(stage) { public override EditorKind Editor { get; } = kind; }

public sealed partial class FormButtonItem : ObservableObject
{
    public string Text { get; init; } = "";
    public string Tooltip { get; init; } = "";
    /// <summary>Sends something other than read-only services (destructive: marked, confirmed, expert only).</summary>
    public bool IsWrite { get; init; }
    public double Width { get; init; }
    public ICommand PressCommand { get; internal set; } = null!;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Locked))] private bool _expert;
    public bool Locked => IsWrite && !Expert;
    public bool IsSafe => !IsWrite;
    public static double EstimateWidth(string text) => Math.Clamp(text.Length * 7.6 + 46, 96, 300);
}

public sealed class FormButtonsRow : FormRow
{
    public const double Spacing = 8, RowHeight = 36;
    public IReadOnlyList<FormButtonItem> Buttons { get; init; } = Array.Empty<FormButtonItem>();
    public override FormRowKind Kind => FormRowKind.Buttons;
    public override bool FullWidth => true;
    public override double GetHeight(double width)
    {
        double x = 0; int rows = 1;
        foreach (var b in Buttons)
        {
            double w = Math.Min(b.Width, width);
            if (x > 0 && x + w > width + 0.5) { rows++; x = 0; }
            x += w + Spacing;
        }
        return rows * RowHeight + (rows - 1) * Spacing + 4;
    }
}

/// <summary>"n edits staged for REQUEST  [Discard] [Send]": one per write request group.</summary>
public sealed partial class FormSendBarRow : FormRow
{
    public string RequestName { get; init; } = "";
    public override FormRowKind Kind => FormRowKind.SendBar;
    public override bool FullWidth => true;
    public override double GetHeight(double width) => 44;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasStaged)), NotifyPropertyChangedFor(nameof(Summary))] private int _stagedCount;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Locked))] private bool _expert;
    public bool HasStaged => StagedCount > 0;
    public bool Locked => !Expert;
    public string Summary => StagedCount == 0 ? RequestName : Loc.F("{0} · {1} staged", RequestName, StagedCount);
    public ICommand SendCommand { get; internal set; } = null!;
    public ICommand DiscardCommand { get; internal set; } = null!;
}
