using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Ddt4All.App.Localization;
using Ddt4All.App.ViewModels;
using Ddt4All.App.Views;

namespace Ddt4All.App.Services;

public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText, string? cancelText = null, bool danger = false);
    /// <summary>The expert-mode warning (README "Warnings"); requires an explicit acknowledgement.</summary>
    Task<bool> ConfirmExpertModeAsync();
}

public interface IFilePickerService
{
    Task<string?> PickFileAsync(string title, params (string Name, string[] Patterns)[] filters);
    Task<string?> PickFolderAsync(string title);
}

internal static class WindowLocator
{
    public static Window? Main =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}

public sealed class DialogService : IDialogService
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmText, string? cancelText = null, bool danger = false)
        => Show(new ConfirmDialogViewModel { Title = title, Message = message, ConfirmText = confirmText, CancelText = cancelText ?? Loc.T("Cancel"), IsDanger = danger });

    public Task<bool> ConfirmExpertModeAsync() => Show(new ConfirmDialogViewModel
    {
        Title = Loc.T("Enable expert mode?"),
        Message = Loc.T("Expert mode enables writing to ECUs. Wrong requests can cause serious damage on a live vehicle."),
        Bullets =
        [
            Loc.T("This application is work in progress. Use Expert mode only if you know what you are doing."),
            Loc.T("Do not use this software if you do not understand how a CAN network (or an ECU) works."),
            Loc.T("Maintenance, testing and research only. The author declines responsibility for any use. You are responsible."),
        ],
        RequireAcknowledgement = true,
        AcknowledgementText = Loc.T("I understand the risks and want to enable expert mode"),
        ConfirmText = Loc.T("Enable expert mode"),
        CancelText = Loc.T("Cancel"),
        IsDanger = true,
    });

    private static async Task<bool> Show(ConfirmDialogViewModel vm)
    {
        var owner = WindowLocator.Main;
        var dlg = new ConfirmDialog { DataContext = vm };
        if (owner is null) return false;
        return await dlg.ShowDialog<bool>(owner);
    }
}

public sealed class FilePickerService : IFilePickerService
{
    public async Task<string?> PickFileAsync(string title, params (string Name, string[] Patterns)[] filters)
    {
        var sp = WindowLocator.Main?.StorageProvider;
        if (sp is null) return null;
        var opts = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = filters.Length == 0 ? null : filters.Select(f => new FilePickerFileType(f.Name) { Patterns = f.Patterns }).ToArray()
        };
        var res = await sp.OpenFilePickerAsync(opts);
        return res.Count > 0 ? res[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var sp = WindowLocator.Main?.StorageProvider;
        if (sp is null) return null;
        var res = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return res.Count > 0 ? res[0].TryGetLocalPath() : null;
    }
}
