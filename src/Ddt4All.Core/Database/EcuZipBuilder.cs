using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Ddt4All.Core.Ecu;
using Ddt4All.Core.Layout;
using Ddt4All.Core.Loading;

namespace Ddt4All.Core.Database;

/// <summary>
/// Creates an <c>ecu.zip</c> from XML ECU files (port of <c>helpers.zipConvertXML</c>): each ECU becomes
/// "NAME.json" + "NAME.json.layout", plus the "db.json" identification map. Extra files (graphics) can be added verbatim.
/// </summary>
public sealed class EcuZipBuilder : IDisposable
{
    private readonly ZipArchive _zip;
    private readonly List<(string href, EcuFile file)> _idents = new();

    /// <summary>Starts writing an archive into <paramref name="output"/> (the stream stays open after <see cref="Dispose"/>).</summary>
    public EcuZipBuilder(Stream output, CompressionLevel level = CompressionLevel.Optimal)
    {
        _zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        Level = level;
    }

    /// <summary>Compression level for new members.</summary>
    public CompressionLevel Level { get; }

    /// <summary>Converts one XML ECU definition. <paramref name="xmlName"/> is e.g. "UCH_X95.xml"; the member is "UCH_X95.json".</summary>
    public void AddXml(string xmlName, Stream xml)
    {
        using var ms = new MemoryStream();
        xml.CopyTo(ms);
        ms.Position = 0;
        var file = EcuXmlLoader.Load(ms);
        ms.Position = 0;
        var layout = EcuLayoutXmlLoader.Load(ms);
        string href = Path.ChangeExtension(Path.GetFileName(xmlName), ".json");
        WriteText(href, file.DumpJson());
        WriteText(href + ".layout", EcuLayoutJson.Dump(layout));
        _idents.Add((href, file));
    }

    /// <summary>Adds an already parsed ECU (and optional layout).</summary>
    public void Add(string href, EcuFile file, EcuLayout? layout = null)
    {
        WriteText(href, file.DumpJson());
        WriteText(href + ".layout", EcuLayoutJson.Dump(layout ?? EcuLayout.Empty));
        _idents.Add((href, file));
    }

    /// <summary>Adds an arbitrary member (graphics, certificates...).</summary>
    public void AddFile(string name, ReadOnlySpan<byte> content)
    {
        var e = _zip.CreateEntry(name, Level);
        using var s = e.Open();
        s.Write(content);
    }

    private void WriteText(string name, string text) => AddFile(name, Encoding.UTF8.GetBytes(text));

    /// <summary>Writes db.json and finishes the archive.</summary>
    public void Dispose()
    {
        var e = _zip.CreateEntry("db.json", Level);
        using (var s = e.Open())
        using (var w = new Utf8JsonWriter(s, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            foreach (var (href, f) in _idents)
            {
                w.WriteStartObject(href);
                w.WriteString("address", f.FuncAddr);
                w.WriteString("group", f.FuncName);
                w.WriteString("protocol", f.Protocol.ToWire());
                w.WriteStartArray("projects");
                foreach (var p in f.Projects) w.WriteStringValue(p);
                w.WriteEndArray();
                w.WriteString("ecuname", f.EcuName);
                w.WriteStartArray("autoidents");
                foreach (var a in f.AutoIdents)
                {
                    w.WriteStartObject();
                    w.WriteString("diagnostic_version", a.DiagVersion);
                    w.WriteString("supplier_code", a.Supplier);
                    w.WriteString("soft_version", a.Soft);
                    w.WriteString("version", a.Version);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }

        _zip.Dispose();
    }
}
