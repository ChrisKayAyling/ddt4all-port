using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Ddt4All.App.Services;

public sealed record AccentOption(string Id, string Name, Color Light, Color Dark)
{
    public IBrush Swatch => new SolidColorBrush(Light);
}

public interface IThemeService
{
    IReadOnlyList<AccentOption> Accents { get; }
    void Apply(string mode, string accentId);
}

public sealed class ThemeService : IThemeService
{
    public IReadOnlyList<AccentOption> Accents { get; } =
    [
        new("blue", "Blue", Color.Parse("#2563EB"), Color.Parse("#3B82F6")),
        new("teal", "Teal", Color.Parse("#0D9488"), Color.Parse("#14A394")),
        new("green", "Green", Color.Parse("#16A34A"), Color.Parse("#22A55A")),
        new("orange", "Orange", Color.Parse("#EA580C"), Color.Parse("#E8690F")),
        new("red", "Red", Color.Parse("#DC2626"), Color.Parse("#E5484D")),
        new("purple", "Purple", Color.Parse("#7C3AED"), Color.Parse("#8B5CF6")),
    ];

    public void Apply(string mode, string accentId)
    {
        var app = Application.Current;
        if (app is null) return;
        app.RequestedThemeVariant = mode switch { "light" => ThemeVariant.Light, "dark" => ThemeVariant.Dark, _ => ThemeVariant.Default };

        var accent = Accents.FirstOrDefault(a => a.Id == accentId) ?? Accents[0];
        foreach (var style in app.Styles)
        {
            if (style is FluentTheme fluent)
            {
                fluent.Palettes[ThemeVariant.Light] = new ColorPaletteResources { Accent = accent.Light };
                fluent.Palettes[ThemeVariant.Dark] = new ColorPaletteResources { Accent = accent.Dark };
                break;
            }
        }
    }
}
