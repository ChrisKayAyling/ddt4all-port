using System.Diagnostics;

namespace Ddt4All.App.Services;

/// <summary>Cheap startup timeline: milestones relative to the process start.</summary>
public static class StartupTrace
{
    private static readonly long Origin = Stopwatch.GetTimestamp();
    private static readonly double ProcessToMainMs = ComputeProcessToMain();
    private static readonly List<(string Name, double Ms)> Marks = new();

    private static double ComputeProcessToMain()
    {
        try { return (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds; }
        catch { return 0; }
    }

    public static void Mark(string name)
    {
        lock (Marks) Marks.Add((name, Stopwatch.GetElapsedTime(Origin).TotalMilliseconds));
    }

    public static IReadOnlyList<(string Name, double Ms)> Snapshot() { lock (Marks) return Marks.ToArray(); }
    public static double RuntimeStartMs => ProcessToMainMs;

    public static string Report()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"process start -> Main: {ProcessToMainMs:0} ms (runtime + assembly load)");
        foreach (var (n, ms) in Snapshot()) sb.AppendLine($"  +{ms,7:0.0} ms  {n}");
        return sb.ToString();
    }
}
