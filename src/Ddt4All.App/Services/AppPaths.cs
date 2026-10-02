namespace Ddt4All.App.Services;

/// <summary>Per-user, cross-platform locations. Override the root with DDT4ALL_HOME (used by tests).</summary>
public static class AppPaths
{
    private const string AppDirName = "DDT4All.NET";

    private static readonly string? Override = Environment.GetEnvironmentVariable("DDT4ALL_HOME");

    /// <summary>Windows: %APPDATA%\DDT4All.NET, macOS: ~/Library/Application Support/DDT4All.NET, Linux: ~/.config/DDT4All.NET</summary>
    public static string ConfigDir { get; } = Override is { Length: > 0 }
        ? Path.Combine(Override, "config")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppDirName);

    /// <summary>Windows: %LOCALAPPDATA%\DDT4All.NET, Linux: ~/.local/share/DDT4All.NET</summary>
    public static string DataDir { get; } = Override is { Length: > 0 }
        ? Path.Combine(Override, "data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppDirName);

    public static string LogDir => Path.Combine(DataDir, "logs");
    public static string SettingsFile => Path.Combine(ConfigDir, "settings.json");
    public static string DefaultEcuDir => Path.Combine(DataDir, "ecus");
}
