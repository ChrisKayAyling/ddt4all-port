using Avalonia.Controls;
using Ddt4All.App.Models;

namespace Ddt4All.App.Views;

/// <summary>ListBox whose group-header rows are inert (not selectable, not focusable). Items stay virtualised.</summary>
public sealed class EcuListBox : ListBox
{
    protected override Type StyleKeyOverride => typeof(ListBox);

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        base.PrepareContainerForItemOverride(container, item, index);
        var header = item is GroupHeaderRow;
        container.IsHitTestVisible = !header;
        container.Focusable = !header;
        container.Classes.Set("group-header", header);
    }
}
