using Ddt4All.App.Models;

namespace Ddt4All.App.Services.Fakes;

/// <summary>Deterministic ~5000-entry catalog for design-time, tests and perf checks.</summary>
public sealed class FakeEcuCatalogService : IEcuCatalogService
{
    private readonly int _count;
    private EcuCatalog? _cache;
    public FakeEcuCatalogService(int count = 5000) { _count = count; }

    public string? DatabasePath => null;
    public bool IsAvailable => true;
    public string? Error => null;
    public Ddt4All.Core.Database.EcuDatabase? Database => null;
    public event Action? Changed { add { } remove { } }
    public Task<bool> SetDatabasePathAsync(string path, CancellationToken ct = default) => Task.FromResult(true);
    public Task<Ddt4All.Core.Ecu.EcuFile> LoadEcuAsync(string href, CancellationToken ct = default) => throw new NotSupportedException("Fake catalog has no ECU definitions.");
    public Task<Ddt4All.Core.Layout.EcuLayout?> LoadLayoutAsync(string href, CancellationToken ct = default) => Task.FromResult<Ddt4All.Core.Layout.EcuLayout?>(null);

    private static readonly string[] Names =
    [
        "Injection", "ABS-VDC", "Airbag", "UCH", "Cluster", "Radio", "Climate Control", "Gearbox", "EPS", "Parking Brake",
        "BCM", "Telematics", "Immobiliser", "TPMS", "Battery Management", "Hybrid Control", "ADAS Camera", "Radar", "Door Module", "Lighting",
    ];
    private static readonly string[] Suppliers = ["Siemens", "Bosch", "Continental", "Valeo", "Delphi", "Denso", "Marelli", "Autoliv"];
    private static readonly string[] Protocols = ["CAN", "CAN", "CAN", "KWP2000 Fast", "KWP2000 Slow", "ISO8", "DoIP"];
    private static readonly string[] Models =
    [
        "Clio IV", "Megane III", "Megane IV", "Captur", "Kadjar", "Scenic III", "Twingo III", "Zoe", "Kangoo II", "Master III",
        "Trafic III", "Laguna III", "Fluence", "Duster", "Sandero II", "Logan II", "Talisman", "Espace V", "Koleos", "Arkana",
    ];

    public Task<EcuCatalog> LoadAsync(CancellationToken ct = default) => Task.Run(() => _cache ??= Build(), ct);

    private EcuCatalog Build()
    {
        var rng = new Random(42);
        var list = new List<EcuEntry>(_count);
        var projects = new SortedSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < _count; i++)
        {
            var mi = rng.Next(Models.Length);
            var code = $"X{(mi * 7 + 10):00}";
            var project = $"[{code}]";
            var name = $"{Names[rng.Next(Names.Length)]} {Suppliers[rng.Next(Suppliers.Length)]} {(char)('A' + rng.Next(26))}{rng.Next(100, 999)}";
            var addr = rng.Next(1, 0xFE).ToString("X2");
            var protocol = Protocols[rng.Next(Protocols.Length)];
            var supplier = Suppliers[rng.Next(Suppliers.Length)];
            var version = $"{rng.Next(1, 9)}.{rng.Next(0, 9)}.{rng.Next(0, 99)}";
            projects.Add(project);
            var e = new EcuEntry
            {
                Id = $"ecu{i:00000}",
                Name = name, Address = addr, Protocol = protocol, Project = project,
                ProjectName = Models[mi], Supplier = supplier, Version = version,
                File = $"{name.Replace(' ', '_')}_{i:00000}.json", VariantCount = rng.Next(1, 6),
                SearchKey = $"{name} {addr} {protocol} {project} {Models[mi]} {supplier}".ToLowerInvariant(),
            };
            list.Add(e);
        }
        return new EcuCatalog(list, projects.ToArray(), Protocols.Distinct().Order().ToArray());
    }
}
