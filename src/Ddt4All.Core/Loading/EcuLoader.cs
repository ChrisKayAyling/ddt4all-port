using Ddt4All.Core.Ecu;

namespace Ddt4All.Core.Loading;

/// <summary>Format-sniffing loader entry points.</summary>
public static class EcuLoader
{
    /// <summary>
    /// Loads a file by extension; a path without extension tries ".xml" then ".json" (like the Python loader).
    /// </summary>
    public static EcuFile LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            if (File.Exists(path + ".xml")) path += ".xml";
            else if (File.Exists(path + ".json")) path += ".json";
            else throw new FileNotFoundException("Cannot load ECU file", path);
        }

        return path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
            ? EcuXmlLoader.Load(path)
            : EcuJsonLoader.Load(path);
    }

    /// <summary>Loads from bytes, deciding by <paramref name="name"/>'s extension (".xml" = XML, otherwise JSON).</summary>
    public static EcuFile Load(string name, ReadOnlySpan<byte> content)
    {
        if (name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            using var ms = new MemoryStream(content.ToArray());
            return EcuXmlLoader.Load(ms);
        }

        return EcuJsonLoader.Load(content);
    }
}
