using Ddt4All.Comms;
using Ddt4All.Core.Database;
using Ddt4All.Core.Ecu;

namespace Ddt4All.Plugins;

/// <summary>Resolves an ECU definition by the name the Python plugins use (<c>EcuFile("UCH_84_J84_03_60", True)</c>).</summary>
public interface IEcuDefinitionProvider
{
    /// <summary>The definition or null if not available.</summary>
    ValueTask<EcuFile?> LoadAsync(string name, CancellationToken ct = default);
}

/// <summary>In-memory definitions (tests, embedded fallbacks).</summary>
public sealed class DictionaryEcuDefinitionProvider : IEcuDefinitionProvider
{
    private readonly Dictionary<string, EcuFile> _files = new(StringComparer.OrdinalIgnoreCase);
    public DictionaryEcuDefinitionProvider Add(string name, EcuFile file) { _files[name] = file; return this; }
    public ValueTask<EcuFile?> LoadAsync(string name, CancellationToken ct = default) => new(_files.GetValueOrDefault(name));
}

/// <summary>Loads "name.json"/"name.xml" from the ecu directory.</summary>
public sealed class DirectoryEcuDefinitionProvider(Func<string?> directory) : IEcuDefinitionProvider
{
    public DirectoryEcuDefinitionProvider(string directory) : this(() => directory) { }

    public async ValueTask<EcuFile?> LoadAsync(string name, CancellationToken ct = default)
    {
        var dir = directory();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        foreach (var ext in new[] { ".json", ".xml" })
        {
            var p = Path.Combine(dir, name + ext);
            if (File.Exists(p)) return await Task.Run(() => EcuFile.Load(p), ct).ConfigureAwait(false);
        }
        return null;
    }
}

/// <summary>Loads a definition from an opened ecu.zip database (member "name.json"/"name.xml", else the unique entry named <c>name</c>).</summary>
public sealed class EcuDatabaseDefinitionProvider(Func<EcuDatabase?> database) : IEcuDefinitionProvider
{
    public async ValueTask<EcuFile?> LoadAsync(string name, CancellationToken ct = default)
    {
        var db = database();
        if (db is null) return null;
        EcuEntry? entry = db.FindByHref(name) ?? db.FindByHref(name + ".json") ?? db.FindByHref(name + ".xml");
        if (entry is null)
        {
            var byName = db.FindByName(name);
            if (byName.Count > 0) entry = byName[0];
        }
        return entry is { } e ? await db.LoadEcuAsync(e, ct).ConfigureAwait(false) : null;
    }
}

/// <summary>Tries each provider in order.</summary>
public sealed class CompositeEcuDefinitionProvider(params IEcuDefinitionProvider[] providers) : IEcuDefinitionProvider
{
    public async ValueTask<EcuFile?> LoadAsync(string name, CancellationToken ct = default)
    {
        foreach (var p in providers)
            if (await p.LoadAsync(name, ct).ConfigureAwait(false) is { } f) return f;
        return null;
    }
}

/// <summary>Maps an <see cref="EcuFile"/> to the transport address (Python <c>EcuFile.connect_to_hardware</c>).</summary>
public static class EcuAddressing
{
    /// <summary>Returns null (with <paramref name="error"/>) when the protocol cannot be reached.</summary>
    public static EcuAddress? FromDefinition(EcuFile ecu, out string? error)
    {
        error = null;
        string name = ecu.EcuName;
        switch (ecu.Protocol)
        {
            case Ddt4All.Core.Ecu.EcuProtocol.Can:
                if (!TryHex(ecu.SendId, out _) || !TryHex(ecu.RecvId, out _) || ecu.SendId == "00" && ecu.RecvId == "00")
                { error = $"ECU definition '{name}' has no CAN identifiers."; return null; }
                var speed = ecu.BaudRate == 250000 ? CanSpeed.Kbps250 : CanSpeed.Default;
                return EcuAddress.Can(name, ecu.SendId, ecu.RecvId, speed); // 8 hex digits = 29 bit, like Python
            case Ddt4All.Core.Ecu.EcuProtocol.Kwp2000:
            case Ddt4All.Core.Ecu.EcuProtocol.Iso:
                return EcuAddress.KLine(name, ecu.FastInit ? Comms.EcuProtocol.Kwp2000Fast : Comms.EcuProtocol.Kwp2000Slow, ecu.FuncAddr);
            case Ddt4All.Core.Ecu.EcuProtocol.Iso8:
                return EcuAddress.KLine(name, Comms.EcuProtocol.Iso8, ecu.FuncAddr);
            default:
                error = $"Protocol {ecu.Protocol} of '{name}' is not supported by plugins.";
                return null;
        }
    }

    private static bool TryHex(string s, out uint v) => uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out v);
}
