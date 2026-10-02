using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace Ddt4All.App.Localization;

/// <summary>XAML: Text="{loc:Tr 'Read DTC'}" - one-way binding that refreshes on language change.</summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }
    public TrExtension(string key) { Key = key; }

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding(nameof(Loc.TrItem.Value)) { Source = Loc.Instance.GetItem(Key), Mode = BindingMode.OneWay };
}
