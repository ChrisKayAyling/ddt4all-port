```

BenchmarkDotNet v0.15.8, Linux Debian GNU/Linux 12 (bookworm)
Intel Xeon CPU E31270 3.40GHz (Max: 1.60GHz), 1 CPU, 8 logical and 4 physical cores
.NET SDK 9.0.318
  [Host]   : .NET 9.0.20 (9.0.20, 9.0.2026.41315), X64 RyuJIT x86-64-v2
  ShortRun : .NET 9.0.20 (9.0.20, 9.0.2026.41315), X64 RyuJIT x86-64-v2

Job=ShortRun  IterationCount=5  LaunchCount=1  
WarmupCount=2  

```
| Method       | Mean     | Error    | StdDev   | Gen0   | Allocated |
|------------- |---------:|---------:|---------:|-------:|----------:|
| BuildRequest | 19.19 ns | 0.274 ns | 0.071 ns | 0.0076 |      32 B |
