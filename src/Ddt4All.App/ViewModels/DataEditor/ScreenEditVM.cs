using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Ddt4All.App.Localization;
using Ddt4All.Core.Layout;

namespace Ddt4All.App.ViewModels;

/// <summary>Basic properties of one screen (name, size, colour) plus a summary of its widgets and request references.</summary>
public sealed partial class ScreenEditVM : EditableVM
{
    private readonly Action<ScreenEditVM> _delete;

    public ScreenEditVM(IEditHost host, Screen screen, string category, Action<ScreenEditVM> delete) : base(host)
    {
        Screen = screen; Category = category; _delete = delete;
    }

    public Screen Screen { get; }
    public string Category { get; }

    public string Name
    {
        get => Screen.Name;
        set
        {
            var layout = Host.Layout;
            if (layout == null || value == Screen.Name) return;
            if (string.IsNullOrWhiteSpace(value) || layout.Screens.Contains(value)) { Host.Warn(Loc.T("Screen names must be unique and not empty.")); OnPropertyChanged(); return; }
            var old = Screen.Name;
            Structural(() =>
            {
                foreach (var c in layout.Categories)
                    for (int i = 0; i < c.ScreenNames.Count; i++)
                        if (c.ScreenNames[i] == old) c.ScreenNames[i] = value;
                Screen.Name = value;
                layout.Screens.Reindex();
            });
        }
    }

    public int Width { get => Screen.Width; set => Edit(Screen.Width, Math.Max(0, value), v => Screen.Width = v); }
    public int Height { get => Screen.Height; set => Edit(Screen.Height, Math.Max(0, value), v => Screen.Height = v); }

    public string ColorHex
    {
        get => $"#{Screen.Color.R:X2}{Screen.Color.G:X2}{Screen.Color.B:X2}";
        set
        {
            var t = (value ?? "").Trim().TrimStart('#');
            if (t.Length == 6 && uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                Edit(Screen.Color, new LayoutColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb), v => Screen.Color = v, nameof(ColorHex), nameof(ColorBrushValue));
            else OnPropertyChanged();
        }
    }

    public Avalonia.Media.Color ColorBrushValue => Avalonia.Media.Color.FromRgb(Screen.Color.R, Screen.Color.G, Screen.Color.B);

    public string Summary => Loc.F("{0} labels · {1} displays · {2} inputs · {3} buttons", Screen.Labels.Count, Screen.Displays.Count, Screen.Inputs.Count, Screen.Buttons.Count);

    /// <summary>Requests referenced by the screen (displays, inputs, buttons, pre-send) that do not exist in the ECU.</summary>
    public IReadOnlyList<string> MissingRequests
    {
        get
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var d in Screen.Displays) names.Add(d.RequestName);
            foreach (var d in Screen.Inputs) names.Add(d.RequestName);
            foreach (var b in Screen.Buttons) foreach (var s in b.Send) names.Add(s.RequestName);
            foreach (var s in Screen.PreSend) names.Add(s.RequestName);
            names.Remove("");
            return names.Where(n => Host.Ecu.GetRequest(n) == null).ToList();
        }
    }

    public bool HasMissing => MissingRequests.Count > 0;
    public string MissingText => string.Join(", ", MissingRequests);

    [RelayCommand] private void Delete() => _delete(this);
}
