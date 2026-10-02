using Ddt4All.App.Localization;
using Ddt4All.App.Models;
using Ddt4All.Core.Database;
using Ddt4All.Core.Ecu;
using Ddt4All.Core.Layout;
using Microsoft.Extensions.Logging;
using AppEcuEntry = Ddt4All.App.Models.EcuEntry;

namespace Ddt4All.App.Services.Real;

/// <summary>
/// Catalog over <see cref="EcuDatabase"/> (ecu.zip). The index (names / ids only) is opened off the UI thread and cached on
/// disk by Core, so the second start only reads the binary cache. ECU definitions are parsed lazily by <see cref="LoadEcuAsync"/>.
/// </summary>
public sealed class EcuCatalogService : IEcuCatalogService, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly ILogger<EcuCatalogService>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private EcuDatabase? _db;
    private string? _openedPath;
    private EcuCatalog? _catalog;

    public EcuCatalogService(ISettingsService settings, ILogger<EcuCatalogService>? log = null)
    {
        _settings = settings; _log = log;
        DatabasePath = Resolve();
    }

    public string? DatabasePath { get; private set; }
    public bool IsAvailable => DatabasePath is not null && Error is null;
    public string? Error { get; private set; }
    public EcuDatabase? Database => _db;
    public event Action? Changed;

    /// <summary>Candidate locations, best first: settings, ecus dir from settings, env override, app data dir, next to the executable, cwd.</summary>
    internal string? Resolve()
    {
        var s = _settings.Current;
        var candidates = new List<string?>
        {
            s.EcuZipPath,
            s.EcuDirectory is { Length: > 0 } d ? Path.Combine(d, "ecu.zip") : null,
            Environment.GetEnvironmentVariable("DDT4ALL_ECU_ZIP"),
            Path.Combine(AppPaths.DataDir, "ecu.zip"),
            Path.Combine(AppPaths.DefaultEcuDir, "ecu.zip"),
            Path.Combine(AppContext.BaseDirectory, "ecu.zip"),
            Path.Combine(Environment.CurrentDirectory, "ecu.zip"),
        };
        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c) && File.Exists(c)) return Path.GetFullPath(c);
        return null;
    }

    public async Task<EcuCatalog> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = Resolve();
            if (path is null)
            {
                DisposeDb();
                DatabasePath = null;
                Error = Loc.T("No ecu.zip found.");
                return _catalog = EcuCatalog.Empty;
            }
            if (_catalog is not null && _db is not null && string.Equals(path, _openedPath, StringComparison.Ordinal)) return _catalog;
            return await OpenLockedAsync(path, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<EcuCatalog> OpenLockedAsync(string path, CancellationToken ct)
    {
        DisposeDb();
        DatabasePath = path;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var (db, catalog) = await Task.Run(() =>
            {
                var db = EcuDatabase.Open(path, new EcuDatabaseOptions { CacheDirectory = Path.Combine(AppPaths.DataDir, "cache") });
                return (db, Build(db));
            }, ct).ConfigureAwait(false);
            _db = db; _openedPath = path; _catalog = catalog; Error = null;
            _log?.LogInformation("ECU database {Path}: {Count} ECUs ({Source}) in {Ms} ms", path, db.Count, db.LoadedFromCache ? "cache" : "parsed", sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _catalog = EcuCatalog.Empty; Error = ex.Message;
            _log?.LogWarning(ex, "Could not open ECU database {Path}", path);
        }
        Changed?.Invoke();
        return _catalog;
    }

    public async Task<bool> SetDatabasePathAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) { Error = Loc.F("File not found: {0}", path); Changed?.Invoke(); return false; }
        _settings.Update(s => s.EcuZipPath = path);
        _settings.SaveNow();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await OpenLockedAsync(Path.GetFullPath(path), ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
        return Error is null;
    }

    public async Task<EcuFile> LoadEcuAsync(string href, CancellationToken ct = default)
    {
        var db = await RequireDbAsync(ct).ConfigureAwait(false);
        var e = db.FindByHref(href) ?? throw new InvalidOperationException(Loc.F("ECU '{0}' is not in the database.", href));
        return await db.LoadEcuAsync(e, ct).ConfigureAwait(false);
    }

    public async Task<EcuLayout?> LoadLayoutAsync(string href, CancellationToken ct = default)
    {
        var db = await RequireDbAsync(ct).ConfigureAwait(false);
        var e = db.FindByHref(href) ?? throw new InvalidOperationException(Loc.F("ECU '{0}' is not in the database.", href));
        try { return await db.LoadLayoutAsync(e, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or KeyNotFoundException) { return null; }
    }

    private async Task<EcuDatabase> RequireDbAsync(CancellationToken ct)
    {
        if (_db is null) await LoadAsync(ct).ConfigureAwait(false);
        return _db ?? throw new InvalidOperationException(Error ?? Loc.T("No ecu.zip found."));
    }

    internal static EcuCatalog Build(EcuDatabase db)
    {
        var list = new List<AppEcuEntry>(db.Count + db.Count / 2);
        var protocols = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var e in db.Entries)
        {
            var proto = ProtocolName(e.Protocol);
            protocols.Add(proto);
            var ai = e.AutoIdentCount > 0 ? e.GetAutoIdent(0) : default;
            var supplier = ai.Supplier ?? "";
            var projects = e.Projects;
            // one row per project so the browser can group / filter by vehicle; ECUs without a project get a single row
            var rows = projects.Count == 0 ? [""] : projects;
            foreach (var p in rows)
                list.Add(new AppEcuEntry
                {
                    Id = e.Href, Name = e.EcuName, Address = e.Address, Protocol = proto, Project = p, ProjectName = "",
                    Group = e.Group, Supplier = supplier, Version = ai.Version ?? "", File = e.Href,
                    VariantCount = Math.Max(1, e.AutoIdentCount),
                    SearchKey = $"{e.EcuName} {e.Group} {e.Address} {proto} {p} {supplier} {e.Href}".ToLowerInvariant(),
                });
        }
        return new EcuCatalog(list, db.Projects.Where(p => p.Length > 0).ToArray(), protocols.ToArray());
    }

    internal static string ProtocolName(EcuProtocol p) => p switch
    {
        EcuProtocol.Can => "CAN", EcuProtocol.Kwp2000 => "KWP2000", EcuProtocol.Iso8 => "ISO8", EcuProtocol.DoIp => "DoIP", _ => p.ToString(),
    };

    private void DisposeDb() { _db?.Dispose(); _db = null; _openedPath = null; }
    public void Dispose() { DisposeDb(); _gate.Dispose(); }
}
