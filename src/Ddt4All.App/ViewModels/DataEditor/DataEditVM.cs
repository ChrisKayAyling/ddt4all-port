using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ddt4All.App.Localization;
using Ddt4All.Core.Codec;
using Ddt4All.Core.Ecu;
using Ddt4All.Core.Editing;

namespace Ddt4All.App.ViewModels;

/// <summary>One enumeration entry (raw value to text) of a data definition.</summary>
public sealed partial class ListEntryVM : ObservableObject
{
    private readonly DataEditVM _owner;
    public ListEntryVM(DataEditVM owner, long value, string text) { _owner = owner; _value = value; _text = text; }

    private long _value;
    private string _text;
    public long Value { get => _value; set { if (SetProperty(ref _value, value)) _owner.EntriesChanged(this); } }
    public string Text { get => _text; set { if (SetProperty(ref _text, value ?? "")) _owner.EntriesChanged(this); } }
    [ObservableProperty] private bool _duplicate;
    public string Hex => "0x" + _value.ToString("X", CultureInfo.InvariantCulture);
    [RelayCommand] private void Remove() => _owner.RemoveEntry(this);
}

/// <summary>Editable view of one <see cref="EcuData"/>: width, type flags, scaling, enumeration and a decode preview.</summary>
public sealed partial class DataEditVM : EditableVM
{
    private bool _loading;

    public DataEditVM(IEditHost host, EcuData data) : base(host)
    {
        Data = data;
        _loading = true;
        foreach (var kv in data.Lists.OrderBy(k => k.Key)) Entries.Add(new ListEntryVM(this, kv.Key, kv.Value));
        _loading = false;
        UsedBy = host.Ecu.DataUsage(data.Name);
        UpdatePreview();
    }

    public EcuData Data { get; }
    public ObservableCollection<ListEntryVM> Entries { get; } = new();
    public IReadOnlyList<string> UsedBy { get; }
    public string UsedByText => UsedBy.Count == 0 ? Loc.T("Not used by any request") : string.Join(", ", UsedBy.Take(12)) + (UsedBy.Count > 12 ? $" (+{UsedBy.Count - 12})" : "");

    public string Name
    {
        get => Data.Name;
        set
        {
            if (value == Data.Name) return;
            if (string.IsNullOrWhiteSpace(value) || Host.Ecu.Data.Contains(value)) { Host.Warn(Loc.T("Data names must be unique and not empty.")); OnPropertyChanged(); return; }
            var old = Data.Name;
            Structural(() => Host.Ecu.RenameData(old, value), data: value);
        }
    }

    public string Description { get => Data.Description; set => Edit(Data.Description, value ?? "", v => Data.Description = v); }
    public string Comment { get => Data.Comment; set => Edit(Data.Comment, value ?? "", v => Data.Comment = v); }
    public int Bits
    {
        get => Data.BitsCount;
        set { if (Edit(Data.BitsCount, Math.Clamp(value, 1, 4096), v => Data.SetBits(v), alsoNotify: [nameof(Bytes), nameof(WidthWarning)])) UpdatePreview(); }
    }
    public int Bytes
    {
        get => Data.BytesCount;
        set { if (Edit(Data.BytesCount, Math.Clamp(value, 1, 512), v => Data.SetBytes(v), alsoNotify: [nameof(Bits), nameof(WidthWarning)])) UpdatePreview(); }
    }
    public string? WidthWarning => Data.BytesCount != (Data.BitsCount + 7) / 8 ? Loc.T("Byte count does not match the bit count.") : null;

    public bool Scaled { get => Data.Scaled; set { if (Edit(Data.Scaled, value, v => Data.Scaled = v, alsoNotify: [nameof(NotScaled)])) UpdatePreview(); } }
    public bool NotScaled => !Data.Scaled;
    public bool Signed { get => Data.Signed; set { if (Edit(Data.Signed, value, v => Data.Signed = v)) UpdatePreview(); } }
    public bool Ascii { get => Data.BytesAscii; set { if (Edit(Data.BytesAscii, value, v => { Data.BytesAscii = v; if (v) Data.Byte = true; })) UpdatePreview(); } }
    public bool Binary { get => Data.Binary; set => Edit(Data.Binary, value, v => Data.Binary = v); }

    private static string D(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    private static bool P(string s, out double v) => double.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    public string StepText { get => D(Data.Step); set { if (P(value, out var v) && Edit(Data.Step, v, x => Data.Step = x, nameof(StepText))) UpdatePreview(); } }
    public string OffsetText { get => D(Data.Offset); set { if (P(value, out var v) && Edit(Data.Offset, v, x => Data.Offset = x, nameof(OffsetText))) UpdatePreview(); } }
    public string DivideByText { get => D(Data.DivideBy); set { if (P(value, out var v) && Edit(Data.DivideBy, v, x => Data.DivideBy = x, nameof(DivideByText), nameof(DivideWarning))) UpdatePreview(); } }
    public string? DivideWarning => Data.Scaled && Data.DivideBy == 0 ? Loc.T("Divide by must not be zero.") : null;
    public string Format { get => Data.Format; set { if (Edit(Data.Format, value ?? "", v => Data.Format = v)) UpdatePreview(); } }
    public string Unit { get => Data.Unit; set => Edit(Data.Unit, value ?? "", v => Data.Unit = v); }

    // ------------------------------------------------------------------ enumeration
    public bool HasEntries => Entries.Count > 0;

    internal void EntriesChanged(ListEntryVM e)
    {
        if (_loading) return;
        Host.BeginEdit(RuntimeHelpersId + ":list");
        ApplyEntries();
        Host.Modified();
    }

    private string RuntimeHelpersId => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this).ToString(CultureInfo.InvariantCulture);

    private void ApplyEntries()
    {
        var seen = new HashSet<long>();
        var dup = new HashSet<long>();
        foreach (var e in Entries) if (!seen.Add(e.Value)) dup.Add(e.Value);
        foreach (var e in Entries) e.Duplicate = dup.Contains(e.Value);
        Data.SetList(Entries.Select(e => new KeyValuePair<long, string>(e.Value, e.Text)));
        OnPropertyChanged(nameof(HasEntries));
        UpdatePreview();
    }

    internal void RemoveEntry(ListEntryVM e)
    {
        Host.BeginEdit("list-remove:" + Guid.NewGuid());
        Entries.Remove(e);
        ApplyEntries();
        Host.Modified();
    }

    [RelayCommand]
    private void AddEntry()
    {
        Host.BeginEdit("list-add:" + Guid.NewGuid());
        long next = Entries.Count == 0 ? 0 : Entries.Max(e => e.Value) + 1;
        _loading = true;
        Entries.Add(new ListEntryVM(this, next, "Value " + next));
        _loading = false;
        ApplyEntries();
        Host.Modified();
    }

    // ------------------------------------------------------------------ preview
    [ObservableProperty] private string _testHex = "00";
    [ObservableProperty] private string _testResult = "";

    partial void OnTestHexChanged(string value) => UpdatePreview();

    /// <summary>Decodes <see cref="TestHex"/> with the current definition (right-aligned, big-endian as stored in a frame).</summary>
    public void UpdatePreview()
    {
        int len = Data.RawByteLength;
        var parsed = HexUtil.Parse(TestHex ?? "");
        if (parsed == null) { TestResult = Loc.T("Not valid hex"); return; }
        var frame = new byte[Math.Max(len, 1)];
        // right-align like a value in a frame
        for (int i = 0; i < parsed.Length && i < frame.Length; i++) frame[frame.Length - 1 - i] = parsed[parsed.Length - 1 - i];
        var item = new DataItem("t", 1, 0, ByteOrder.Big);
        var tmp = Data;
        string? s = tmp.GetDisplayValue(frame, item, ByteOrder.Big);
        TestResult = s == null ? Loc.T("(cannot decode)") : s + (Data.Scaled && Data.Unit.Length > 0 ? " " + Data.Unit : "");
    }
}
