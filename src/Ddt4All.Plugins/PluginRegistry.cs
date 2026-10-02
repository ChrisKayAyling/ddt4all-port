using Ddt4All.Plugins.Vehicles;

namespace Ddt4All.Plugins;

/// <summary>
/// Explicit plugin list (no assembly scanning, so trimming/AOT keep working). Add new plugins in <see cref="CreateDefault"/>.
/// </summary>
public sealed class PluginRegistry
{
    private readonly List<IPlugin> _plugins = [];

    public IReadOnlyList<IPlugin> All => _plugins;

    public PluginRegistry Register(IPlugin plugin)
    {
        if (_plugins.Any(p => p.Info.Id == plugin.Info.Id)) throw new InvalidOperationException($"Duplicate plugin id '{plugin.Info.Id}'.");
        _plugins.Add(plugin);
        return this;
    }

    public IPlugin? Find(string id) => _plugins.FirstOrDefault(p => p.Info.Id == id);

    public IReadOnlyList<string> Categories => _plugins.Select(p => p.Info.Category).Distinct().Order(StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Case-insensitive search over name, category, vehicle and description; optional category filter.</summary>
    public IEnumerable<IPlugin> Search(string? text, string? category = null)
    {
        var terms = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in _plugins)
        {
            if (!string.IsNullOrEmpty(category) && p.Info.Category != category) continue;
            var hay = $"{p.Info.Name} {p.Info.Category} {p.Info.Vehicle} {p.Info.Description}";
            if (terms.All(t => hay.Contains(t, StringComparison.CurrentCultureIgnoreCase))) yield return p;
        }
    }

    /// <summary>All ports of the Python <c>ddt4all/plugins</c> folder.</summary>
    public static PluginRegistry CreateDefault() => new PluginRegistry()
        .Register(new VinCrcPlugin())
        .Register(new Ab90AirbagPlugin())
        .Register(new Megane3AirbagPlugin())
        .Register(new Rsat4AirbagPlugin())
        .Register(new Clio3EpsPlugin())
        .Register(new Clio4EpsPlugin())
        .Register(new Megane3EpsPlugin())
        .Register(new Laguna2UchPlugin())
        .Register(new Laguna3UchPlugin())
        .Register(new Megane2UchPlugin())
        .Register(new Megane3UchPlugin())
        .Register(new ZoeWaterPumpPlugin())
        .Register(new Megane2CardProgrammingPlugin());
}
