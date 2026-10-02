using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Ddt4All.Core.Codec;
using Ddt4All.Core.Database;
using Ddt4All.Core.Ecu;
using Ddt4All.Core.Layout;
using Ddt4All.Core.Loading;
using Ddt4All.Core.Tests.Synthetic;

// Usage:
//   dotnet run -c Release -- --short            BenchmarkDotNet, short job (default when no args)
//   dotnet run -c Release -- --emit <dir>       write the synthetic large ECU (xml+json) for the Python comparison
//   dotnet run -c Release -- --time <file>      stopwatch load time of an ECU file (xml/json)
if (args.Length >= 2 && args[0] == "--emit")
{
    Directory.CreateDirectory(args[1]);
    string xml = SyntheticData.CreateEcuXml(Bench.Requests, Bench.DataPerRequest, Bench.Screens);
    File.WriteAllText(Path.Combine(args[1], "large.xml"), xml);
    File.WriteAllText(Path.Combine(args[1], "large.json"), EcuXmlLoader.LoadFromString(xml).DumpJson());
    Console.WriteLine("emitted " + args[1]);
    return;
}

if (args.Length >= 2 && args[0] == "--time")
{
    for (int i = 0; i < 6; i++)
    {
        var sw = Stopwatch.StartNew();
        var f = EcuFile.Load(args[1]);
        Console.WriteLine($"load #{i}: {sw.Elapsed.TotalMilliseconds:F1} ms ({f.Requests.Count} requests, {f.Data.Count} data)");
    }

    return;
}

var cfg = DefaultConfig.Instance.AddJob(Job.ShortRun.WithWarmupCount(2).WithIterationCount(5)).WithOptions(ConfigOptions.DisableOptimizationsValidator);
BenchmarkSwitcher.FromAssembly(typeof(Bench).Assembly).Run(args.Where(a => a != "--short").ToArray(), cfg);

[MemoryDiagnoser]
public class Bench
{
    public const int Requests = 2000, DataPerRequest = 8, Screens = 200;
    private string _xml = "", _json = "";
    private byte[] _jsonBytes = Array.Empty<byte>();
    private EcuFile _ecu = null!;
    private EcuRequest _req = null!;
    private byte[] _frame = Array.Empty<byte>();
    private EcuData _scaled = null!, _list = null!;
    private DataItem _item = null!;
    private string _zip = "", _cacheDir = "";
    private byte[] _dbJson = Array.Empty<byte>();
    private EcuDatabase _db = null!;

    [GlobalSetup]
    public void Setup()
    {
        _xml = SyntheticData.CreateEcuXml(Requests, DataPerRequest, Screens);
        _ecu = EcuXmlLoader.LoadFromString(_xml);
        _json = _ecu.DumpJson();
        _jsonBytes = System.Text.Encoding.UTF8.GetBytes(_json);
        _req = _ecu.Requests["Req5"];
        _frame = new byte[40];
        new Random(1).NextBytes(_frame);
        _scaled = _ecu.Data["D5_0"];
        _list = _ecu.Data["D5_1"];
        _item = _req.ReceiveItems[0];

        string dir = Path.Combine(Path.GetTempPath(), "ddt4all-bench");
        Directory.CreateDirectory(dir);
        _zip = Path.Combine(dir, "bench.zip");
        _cacheDir = Path.Combine(dir, "cache");
        SyntheticData.CreateDbZip(_zip, 5000, 10);
        _dbJson = SyntheticData.CreateDbJson(5000, 10);
        using (EcuDatabase.Open(_zip, new EcuDatabaseOptions { CacheDirectory = _cacheDir, ForceRebuild = true })) { }
        _db = EcuDatabase.Open(_zip, new EcuDatabaseOptions { CacheDirectory = _cacheDir });
    }

    // ---- decode / encode
    [Benchmark] public bool Decode_scaled_numeric() => _scaled.TryGetNumeric(_frame, _req.ReceiveItems[0], ByteOrder.Big, out _);
    [Benchmark] public string? Decode_scaled_display() => _scaled.GetDisplayValue(_frame, _req.ReceiveItems[0], ByteOrder.Big);
    [Benchmark] public string? Decode_list_display() => _list.GetDisplayValue(_frame, _req.ReceiveItems[1], ByteOrder.Big);
    [Benchmark] public bool Raw_read_u64() => BitCodec.TryReadUInt64(_frame, 3, 0, 16, false, out _);
    [Benchmark] public int Decode_whole_response_8_values() => _req.DecodeResponse(_frame).Values.Count;
    [Benchmark] public bool Encode_scaled() { Span<byte> f = stackalloc byte[8]; return _scaled.TrySetNumeric(f, _item, ByteOrder.Big, 12.5, out _); }
    [Benchmark] public byte[] BuildRequest() => _req.BuildRequest();

    // ---- loading a large ECU (2000 requests, 16000 data, 200 screens)
    [Benchmark] public EcuFile Load_xml_large() => EcuXmlLoader.LoadFromString(_xml);
    [Benchmark] public EcuFile Load_json_large() => EcuJsonLoader.Load(_jsonBytes);
    [Benchmark] public EcuLayout Load_layout_xml_large() => EcuLayoutXmlLoader.LoadFromString(_xml);
    [Benchmark] public int Dump_json_large() => _ecu.DumpJson().Length;

    // ---- database (5000 ECUs x 10 autoidents)
    [Benchmark] public EcuIndex Index_build_from_db_json()
    {
        var b = new EcuIndexBuilder();
        DbJsonParser_Parse(b);
        return b.Build();
    }

    private void DbJsonParser_Parse(EcuIndexBuilder b) => DbJsonParser.Parse(_dbJson, b);

    [Benchmark] public EcuIndex Index_build_from_zip() => EcuDatabase.BuildIndexFromZip(_zip);
    [Benchmark] public int Open_with_cache()
    {
        using var db = EcuDatabase.Open(_zip, new EcuDatabaseOptions { CacheDirectory = _cacheDir });
        return db.Count;
    }

    [Benchmark] public int Search_text() => _db.Search(new EcuSearch { Text = "ECU 0420" }).Count;
    [Benchmark] public int Search_project_protocol() => _db.Search(new EcuSearch { Project = "PRJ042", Protocol = EcuProtocol.Can }).Count;
    [Benchmark] public MatchKind Match_autoident() => _db.Match(new AutoIdentQuery("1F", "SUP12", "5555", "1234")).Kind;
}
