using System.Diagnostics;
using Ddt4All.App.Models;

namespace Ddt4All.App.Services.Fakes;

public sealed class FakeSnifferService : ISnifferService
{
    private CancellationTokenSource? _cts;
    public bool IsRunning => _cts is not null;
    public event Action<IReadOnlyList<CanFrame>>? FramesReceived;

    private static readonly uint[] Ids = [0x0C6, 0x12E, 0x186, 0x1F6, 0x29A, 0x35C, 0x391, 0x3F7, 0x5DA, 0x5E8, 0x621, 0x7BB];

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_cts is not null) return Task.CompletedTask;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            var rng = new Random(7);
            byte counter = 0;
            while (!token.IsCancellationRequested)
            {
                var batch = new List<CanFrame>(32);
                for (int i = 0; i < 24; i++)
                {
                    var id = Ids[rng.Next(Ids.Length)];
                    var data = new byte[8];
                    rng.NextBytes(data);
                    data[0] = (byte)(id & 0xFF); data[7] = counter++;
                    batch.Add(new CanFrame(id, data, Stopwatch.GetTimestamp()));
                }
                FramesReceived?.Invoke(batch);
                try { await Task.Delay(20, token); } catch (OperationCanceledException) { break; }
            }
        }, token);
        return Task.CompletedTask;
    }

    public Task StopAsync() { _cts?.Cancel(); _cts = null; return Task.CompletedTask; }
}
