using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Ddt4All.App.Localization;
using Ddt4All.Core.Ecu;

namespace Ddt4All.App.ViewModels;

public enum ParamKind { Hex, Scaled, Ascii, List }

/// <summary>Typed editor for one sent data item: list -> combo box, scaled -> number + unit, ASCII -> text, otherwise hex.</summary>
public sealed partial class ParamEditorViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _initialising = true;

    public ParamEditorViewModel(DataItem item, EcuData data, string initial, Action changed)
    {
        Item = item; Data = data; _changed = changed;
        Kind = data.HasList && !data.BytesAscii ? ParamKind.List : data.BytesAscii ? ParamKind.Ascii : data.Scaled ? ParamKind.Scaled : ParamKind.Hex;
        Unit = data.Unit;
        if (Kind == ParamKind.List)
        {
            var c = data.Lists.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
            if (!c.Contains(initial)) c.Insert(0, initial);
            Choices = c;
        }
        else Choices = Array.Empty<string>();
        _text = initial;
        Hint = BuildHint(data);
        _initialising = false;
    }

    public DataItem Item { get; }
    public EcuData Data { get; }
    public string Name => Item.Name;
    public ParamKind Kind { get; }
    public string Unit { get; }
    public string Hint { get; }
    public IReadOnlyList<string> Choices { get; }
    public bool IsList => Kind == ParamKind.List;
    public bool IsText => Kind != ParamKind.List;
    public bool HasUnit => Unit.Length > 0;
    public bool IsMono => Kind is ParamKind.Hex or ParamKind.Scaled;
    public string Position => string.Create(CultureInfo.InvariantCulture, $"byte {Item.FirstByte}{(Item.BitOffset != 0 ? "." + Item.BitOffset : "")} · {Data.BitsCount} bit");
    public string? Description => string.IsNullOrWhiteSpace(Data.Description) ? null : Data.Description;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string? _error;
    public bool HasError => Error != null;

    private string _text;
    public string Text
    {
        get => _text;
        set { if (SetProperty(ref _text, value ?? "") && !_initialising) _changed(); }
    }

    public string DefaultText { get; set; } = "";

    private static string BuildHint(EcuData d) => d.BytesAscii ? Loc.T("Text") + $" · {d.BytesCount}"
        : d.HasList ? Loc.T("Choose a value")
        : d.Scaled ? (d.Format.Length > 0 ? d.Format : Loc.T("Number")) + (d.Unit.Length > 0 ? " " + d.Unit : "")
        : Loc.T("Hex") + $" · {d.RawByteLength} B";
}
