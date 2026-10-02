using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace Ddt4All.Core.Tests.Synthetic;

/// <summary>Deterministic synthetic ECU definitions / databases used by tests and benchmarks.</summary>
public static class SyntheticData
{
    /// <summary>Renault-style ECU XML with <paramref name="requests"/> requests, ~<paramref name="dataPerRequest"/> data per request and screens.</summary>
    public static string CreateEcuXml(int requests, int dataPerRequest, int screens = 0, int seed = 7)
    {
        var rnd = new Random(seed);
        var sb = new StringBuilder(requests * dataPerRequest * 220 + screens * 4000);
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Ecu>\n<Target Name=\"SYNTH\">\n<AutoIdents>");
        for (int i = 0; i < 20; i++) sb.Append($"<AutoIdent DiagVersion=\"{i:X2}\" Supplier=\"SUP{i % 5}\" Soft=\"{1000 + i}\" Version=\"{i:X4}\"/>");
        sb.Append("</AutoIdents><Projects><X95/><X85/><L38/></Projects><Function Address=\"26\" Name=\"Injection\"/>");
        sb.Append("<CAN BaudRate=\"500000\"><SendId><CANId Value=\"1792\"/></SendId><ReceiveId><CANId Value=\"2024\"/></ReceiveId></CAN>\n");
        if (screens > 0)
        {
            sb.Append("<Categories>");
            int perCat = 10;
            for (int s = 0; s < screens; s++)
            {
                if (s % perCat == 0) sb.Append($"<Category Name=\"Cat{s / perCat}\">");
                sb.Append($"<Screen Name=\"Screen{s}\" Width=\"800\" Height=\"600\" Color=\"16777215\"><Send Delay=\"100\" RequestName=\"Req{s % Math.Max(1, requests)}\"/>");
                for (int e = 0; e < 8; e++)
                {
                    sb.Append($"<Label Text=\"Label {s}/{e}\" Color=\"255\" Alignment=\"1\"><Rectangle Left=\"{e * 10}\" Top=\"{e * 20}\" Height=\"30\" Width=\"120\"/><Font Name=\"Arial\" Size=\"8,5\" Bold=\"1\" Italic=\"0\" Color=\"16711680\"/></Label>");
                    int r = rnd.Next(requests), d = rnd.Next(dataPerRequest);
                    sb.Append($"<Display DataName=\"D{r}_{d}\" RequestName=\"Req{r}\" Color=\"8421504\" Width=\"80\"><Rectangle Left=\"140\" Top=\"{e * 20}\" Height=\"30\" Width=\"200\"/><Font Name=\"Tahoma\" Size=\"9\" Bold=\"0\" Italic=\"1\"/></Display>");
                }

                sb.Append("<Button Text=\"Go\"><Rectangle Left=\"10\" Top=\"500\" Height=\"40\" Width=\"100\"/><Font Name=\"Arial\" Size=\"10\" Bold=\"1\" Italic=\"0\"/><Message Text=\"Sure?\"/><Send Delay=\"0\" RequestName=\"Req0\"/></Button>");
                sb.Append("</Screen>");
                if (s % perCat == perCat - 1 || s == screens - 1) sb.Append("</Category>");
            }

            sb.Append("</Categories>");
        }

        sb.Append("</Target>\n");
        for (int i = 0; i < Math.Max(1, requests / 20); i++)
            sb.Append($"<Device Name=\"DTC{i}\" DTC=\"{i}\" Type=\"2\"><DeviceData Name=\"D{i}_0\" FailureFlag=\"FF\"/></Device>\n");
        sb.Append("<Requests Endian=\"Big\">\n");
        for (int r = 0; r < requests; r++)
        {
            sb.Append($"<Request Name=\"Req{r}\"><DenyAccess><NoSDS/></DenyAccess><Sent><SentBytes>22{r & 0xFF:X2}{(r >> 8) & 0xFF:X2}</SentBytes></Sent><Received MinBytes=\"{dataPerRequest * 2 + 3}\">");
            for (int d = 0; d < dataPerRequest; d++)
                sb.Append($"<DataItem Name=\"D{r}_{d}\" FirstByte=\"{2 + d * 2}\" BitOffset=\"0\"/>");
            sb.Append("</Received><ReplyBytes>62</ReplyBytes></Request>\n");
        }

        sb.Append("</Requests>\n");
        for (int r = 0; r < requests; r++)
            for (int d = 0; d < dataPerRequest; d++)
            {
                sb.Append($"<Data Name=\"D{r}_{d}\">");
                switch (d % 4)
                {
                    case 0:
                        sb.Append("<Description><![CDATA[Some description text]]></Description><Bits count=\"16\"><Scaled Step=\"0.25\" Offset=\"-40\" DivideBy=\"1\" Format=\"0.00\" Unit=\"C\"/></Bits>");
                        break;
                    case 1:
                        sb.Append("<List><Item Value=\"0\" Text=\"Off\"/><Item Value=\"1\" Text=\"On\"/><Item Value=\"2\" Text=\"Error\"/></List><Bits count=\"8\"/>");
                        break;
                    case 2:
                        sb.Append("<Bits count=\"16\" signed=\"1\"><Scaled Step=\"1\" Offset=\"0\" DivideBy=\"10\" Format=\"0.0\"/></Bits>");
                        break;
                    default:
                        sb.Append("<Comment><![CDATA[<i>raw</i> value]]></Comment><Bits count=\"16\"/>");
                        break;
                }

                sb.Append("</Data>\n");
            }

        sb.Append("</Ecu>");
        return sb.ToString();
    }

    private static readonly string[] Groups = Enumerable.Range(0, 40).Select(i => $"GROUP{i}").ToArray();
    private static readonly string[] Suppliers = Enumerable.Range(0, 30).Select(i => $"SUP{i:D2}").ToArray();

    /// <summary>The db.json content for <paramref name="ecus"/> entries with <paramref name="autoidents"/> autoidents each.</summary>
    public static byte[] CreateDbJson(int ecus, int autoidents, int seed = 1)
    {
        var rnd = new Random(seed);
        using var ms = new MemoryStream(ecus * (300 + autoidents * 110));
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            for (int e = 0; e < ecus; e++)
            {
                w.WriteStartObject($"ECU_{e:D5}.json");
                w.WriteString("address", rnd.Next(256).ToString("X2"));
                w.WriteString("group", Groups[rnd.Next(Groups.Length)]);
                w.WriteString("protocol", (e % 3) switch { 0 => "CAN", 1 => "KWP2000", _ => "ISO8" });
                w.WriteStartArray("projects");
                int np = rnd.Next(2, 7);
                for (int p = 0; p < np; p++) w.WriteStringValue($"PRJ{rnd.Next(250):D3}");
                w.WriteEndArray();
                w.WriteString("ecuname", $"Synthetic ECU {e:D5} {Groups[e % Groups.Length]}");
                w.WriteStartArray("autoidents");
                for (int a = 0; a < autoidents; a++)
                {
                    w.WriteStartObject();
                    w.WriteString("diagnostic_version", rnd.Next(1, 40).ToString("X2"));
                    w.WriteString("supplier_code", Suppliers[rnd.Next(Suppliers.Length)]);
                    w.WriteString("soft_version", rnd.Next(1000, 9999).ToString());
                    w.WriteString("version", rnd.Next(0, 0xFFFF).ToString("X4"));
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndObject();
        }

        return ms.ToArray();
    }

    /// <summary>Creates a zip with a synthetic db.json (and optionally extra members).</summary>
    public static void CreateDbZip(string path, int ecus, int autoidents, IEnumerable<(string name, byte[] data)>? extra = null)
    {
        using var fs = new FileStream(path, FileMode.Create);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var e = zip.CreateEntry("db.json", CompressionLevel.Fastest);
        using (var s = e.Open()) s.Write(CreateDbJson(ecus, autoidents));
        if (extra != null)
            foreach (var (name, data) in extra)
            {
                var x = zip.CreateEntry(name, CompressionLevel.Fastest);
                using var s = x.Open();
                s.Write(data);
            }
    }
}
