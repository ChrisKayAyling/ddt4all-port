using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Ddt4All.App.Services;

public sealed record LogEntry(DateTime Time, LogLevel Level, string Category, string Message, string? Exception)
{
    public string TimeText => Time.ToString("HH:mm:ss.fff");
    public string LevelText => Level switch
    {
        LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
        LogLevel.Warning => "WRN", LogLevel.Error => "ERR", LogLevel.Critical => "CRT", _ => "---"
    };
    public bool IsWarn => Level == LogLevel.Warning;
    public bool IsErr => Level >= LogLevel.Error;
    public bool IsDim => Level <= LogLevel.Debug;
    public string ShortCategory => Category.Contains('.') ? Category[(Category.LastIndexOf('.') + 1)..] : Category;
    public string Text => Exception is null ? Message : Message + Environment.NewLine + Exception;
    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm:ss.fff} [{LevelText}] {Category}: {Text}";
}

/// <summary>Process-wide minimum level, changeable at runtime from Settings.</summary>
public static class LogLevelSwitch
{
    private static volatile int _level = (int)LogLevel.Information;
    public static LogLevel Minimum { get => (LogLevel)_level; set => _level = (int)value; }
    public static bool TryParse(string? s, out LogLevel level) => Enum.TryParse(s, true, out level);
}

public interface ILogStore
{
    IReadOnlyList<LogEntry> Snapshot();
    event Action<LogEntry>? EntryAdded;     // may be raised on any thread
    void Clear();
}

/// <summary>Bounded in-memory ring buffer for the in-app log viewer.</summary>
public sealed class InMemoryLogStore : ILogStore
{
    private readonly object _gate = new();
    private readonly Queue<LogEntry> _q = new();
    private const int Capacity = 5000;
    public event Action<LogEntry>? EntryAdded;

    public void Add(LogEntry e)
    {
        lock (_gate)
        {
            _q.Enqueue(e);
            while (_q.Count > Capacity) _q.Dequeue();
        }
        EntryAdded?.Invoke(e);
    }

    public IReadOnlyList<LogEntry> Snapshot() { lock (_gate) return _q.ToArray(); }
    public void Clear() { lock (_gate) _q.Clear(); }
}

public sealed class InMemoryLoggerProvider(InMemoryLogStore store) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new L(store, categoryName);
    public void Dispose() { }

    private sealed class L(InMemoryLogStore store, string cat) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= LogLevelSwitch.Minimum;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            store.Add(new LogEntry(DateTime.Now, logLevel, cat, formatter(state, exception), exception?.ToString()));
        }
    }
}

/// <summary>Daily-rolling file logger with a background writer (never blocks callers). Keeps 14 days.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly Channel<LogEntry> _ch = Channel.CreateUnbounded<LogEntry>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;
    private readonly string _dir;

    public FileLoggerProvider(string dir)
    {
        _dir = dir;
        _writer = Task.Run(WriteLoop);
    }

    public ILogger CreateLogger(string categoryName) => new L(_ch.Writer, categoryName);

    private async Task WriteLoop()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            foreach (var old in Directory.EnumerateFiles(_dir, "ddt4all-*.log"))
                if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-14)) { try { File.Delete(old); } catch { } }
        }
        catch { /* logging must never crash the app */ }

        StreamWriter? w = null; string? currentName = null;
        try
        {
            var reader = _ch.Reader;
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var e))
                {
                    var name = Path.Combine(_dir, $"ddt4all-{e.Time:yyyyMMdd}.log");
                    if (name != currentName)
                    {
                        w?.Dispose();
                        w = new StreamWriter(new FileStream(name, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = false };
                        currentName = name;
                    }
                    w!.WriteLine(e.ToString());
                }
                w?.Flush();
            }
        }
        catch { /* disk full / permissions: drop */ }
        finally { w?.Dispose(); }
    }

    public void Dispose() { _ch.Writer.TryComplete(); _writer.Wait(1000); }

    private sealed class L(ChannelWriter<LogEntry> w, string cat) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= LogLevelSwitch.Minimum;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            w.TryWrite(new LogEntry(DateTime.Now, logLevel, cat, formatter(state, exception), exception?.ToString()));
        }
    }
}
