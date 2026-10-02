using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Ddt4All.App.Services;

public interface ISettingsService
{
    AppSettings Current { get; }
    /// <summary>Mutate settings and schedule a (debounced, atomic) save.</summary>
    void Update(Action<AppSettings> mutate);
    void SaveNow();
    event Action? Changed;
}

public sealed class SettingsService : ISettingsService, IDisposable
{
    private readonly string _path;
    private readonly ILogger<SettingsService>? _log;
    private readonly object _gate = new();
    private Timer? _timer;

    public AppSettings Current { get; private set; }
    public event Action? Changed;

    public SettingsService(string? path = null, ILogger<SettingsService>? log = null)
    {
        _path = path ?? AppPaths.SettingsFile;
        _log = log;
        Current = Load();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                using var fs = File.OpenRead(_path);
                return JsonSerializer.Deserialize(fs, AppSettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Could not read settings from {Path}; using defaults", _path);
            try { File.Move(_path, _path + ".corrupt", true); } catch { /* ignore */ }
        }
        return new AppSettings();
    }

    public void Update(Action<AppSettings> mutate)
    {
        lock (_gate)
        {
            mutate(Current);
            _timer ??= new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(400, Timeout.Infinite);
        }
        Changed?.Invoke();
    }

    public void SaveNow()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                using (var fs = File.Create(tmp))
                    JsonSerializer.Serialize(fs, Current, AppSettingsJsonContext.Default.AppSettings);
                File.Move(tmp, _path, true);
            }
            catch (Exception ex)
            {
                _log?.LogError(ex, "Failed to save settings to {Path}", _path);
            }
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        SaveNow();
    }
}
