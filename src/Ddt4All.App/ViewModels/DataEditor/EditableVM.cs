using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Ddt4All.Core.Ecu;

namespace Ddt4All.App.ViewModels;

/// <summary>What the editor view models need from the page: undo snapshots, change notification, shared names.</summary>
public interface IEditHost
{
    EcuFile Ecu { get; }
    Ddt4All.Core.Layout.EcuLayout? Layout { get; }
    IReadOnlyList<string> DataNames { get; }
    /// <summary>Called before a mutation; snapshots for undo (consecutive edits of the same key within ~1.5 s are coalesced).</summary>
    void BeginEdit(string key);
    /// <summary>A value changed (marks dirty, schedules revalidation).</summary>
    void Modified();
    /// <summary>Structure changed (names, counts): rebuilds lists and selection.</summary>
    void Structural(string? selectRequest = null, string? selectData = null);
    void Warn(string message);
}

/// <summary>Base of the property wrappers: every setter goes through <see cref="Edit{T}"/> (undo + dirty tracking).</summary>
public abstract class EditableVM : ObservableObject
{
    protected EditableVM(IEditHost host) { Host = host; }
    protected IEditHost Host { get; }

    protected bool Edit<T>(T current, T value, Action<T> apply, [CallerMemberName] string? prop = null, params string[] alsoNotify)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return false;
        Host.BeginEdit(RuntimeHelpers.GetHashCode(this) + ":" + prop);
        apply(value);
        OnPropertyChanged(prop);
        foreach (var n in alsoNotify) OnPropertyChanged(n);
        Host.Modified();
        return true;
    }

    protected void Structural(Action mutate, string? request = null, string? data = null)
    {
        Host.BeginEdit("structural:" + Guid.NewGuid());
        mutate();
        Host.Structural(request, data);
    }
}
