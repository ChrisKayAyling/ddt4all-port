using System.IO.Pipes;
using System.Text;

namespace Ddt4All.App.Services;

/// <summary>
/// Only one DDT4All should own the serial adapter. The first instance holds an exclusive lock file and listens on
/// a named pipe; later launches ask it to activate its window and exit. Pass --allow-multiple to bypass.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private FileStream? _lock;
    private CancellationTokenSource? _cts;
    public event Action? ActivationRequested;

    private static string PipeName => "DDT4All.NET-" + Environment.UserName;

    /// <returns>true if this process is the primary instance.</returns>
    public bool TryBecomePrimary()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ConfigDir);
            _lock = new FileStream(Path.Combine(AppPaths.ConfigDir, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return true; } // cannot lock: do not block startup
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ListenAsync(_cts.Token));
        return true;
    }

    public static void SignalPrimary()
    {
        try
        {
            using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            c.Connect(800);
            c.Write(Encoding.UTF8.GetBytes("activate"));
        }
        catch { /* primary is shutting down / unreachable */ }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);
                var buf = new byte[16];
                _ = await server.ReadAsync(buf, ct);
                ActivationRequested?.Invoke();
            }
        }
        catch { /* shutting down */ }
    }

    public void Dispose() { _cts?.Cancel(); _lock?.Dispose(); }
}
