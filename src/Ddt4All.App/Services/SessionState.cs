using CommunityToolkit.Mvvm.ComponentModel;

namespace Ddt4All.App.Services;

/// <summary>Shared, app-wide runtime state (not persisted). Expert mode is deliberately reset on every launch.</summary>
public sealed partial class SessionState : ObservableObject
{
    [ObservableProperty] private bool _isExpertMode;
    [ObservableProperty] private string? _currentEcu;

    /// <summary>Diagnostic service ids that are harmless (read-only) outside expert mode (from the Python reference).</summary>
    private static readonly byte[] SafeServices = [0x10, 0x12, 0x14, 0x17, 0x19, 0x1A, 0x21, 0x22, 0x23, 0x3E];

    public bool IsSafeRequest(byte serviceId) => Array.IndexOf(SafeServices, serviceId) >= 0;
}
