using System.Globalization;
using System.Text;
using Ddt4All.Comms;
using Ddt4All.Comms.Simulation;
using Ddt4All.Core.Ecu;
using Ddt4All.Core.Layout;
using Ddt4All.Core.Loading;

namespace Ddt4All.App.Tests;

/// <summary>ECU definitions + simulated transports used by the Screens tests.</summary>
internal static class ScreenFixtures
{
    public static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Ddt4All.sln"))) d = d.Parent;
        return d!.FullName;
    }

    public const string RichReadReply = "6101A0123401053C80FF56463132333435363738393031323334353637";

    public static (EcuFile Ecu, EcuLayout Layout) Rich()
    {
        var path = Path.Combine(RepoRoot(), "tests", "Ddt4All.Core.Tests", "TestData", "rich_ecu.xml");
        return (EcuFile.Load(path), EcuLayoutXmlLoader.Load(path));
    }

    public static async Task<(SimulatedTransport Transport, EcuScript Script)> RichTransportAsync()
    {
        var script = new EcuScript()
            .On("1003", "5003")
            .On("2101", RichReadReply)
            .On("2E01", "6E01")
            .On("3101", "7101FF00");
        var t = new SimulatedTransport().AddCan("RICH_ECU", 0x700, script);
        await t.ConnectEcuAsync(new EcuAddress { Name = "RICH_ECU", TxId = 0x700 });
        return (t, script);
    }

    /// <summary>A realistic-looking instrument-cluster style screen with moving values.</summary>
    public static (EcuFile, EcuLayout) Demo()
    {
        var sb = new StringBuilder();
        string[] names = ["Engine speed", "Vehicle speed", "Coolant temperature", "Intake air temperature", "Battery voltage", "Throttle position", "Injection time", "Fuel pressure", "Lambda", "Boost pressure", "Gear", "Ignition advance"];
        string[] units = ["rpm", "km/h", "C", "C", "V", "%", "ms", "bar", "", "bar", "", "deg"];
        sb.Append("<Ecu><Target Name=\"DEMO\"><CAN BaudRate=\"500000\"><SendId><CANId Value=\"1792\"/></SendId><ReceiveId><CANId Value=\"2024\"/></ReceiveId></CAN><Categories>");
        sb.Append("<Category Name=\"Measures\"><Screen Name=\"Engine parameters\" Width=\"660\" Height=\"430\" Color=\"15921906\"><Send Delay=\"0\" RequestName=\"StartSession\"/>");
        sb.Append("<Label Text=\"Engine measurements\" Color=\"9127187\" Alignment=\"2\"><Rectangle Left=\"10\" Top=\"10\" Height=\"34\" Width=\"640\"/><Font Name=\"Arial\" Size=\"13\" Bold=\"1\" Italic=\"0\" Color=\"16777215\"/></Label>");
        for (int i = 0; i < names.Length; i++)
        {
            int col = i % 2, row = i / 2;
            long color = row % 2 == 0 ? 16247773 : 15921906; // light blue / light grey as COLORREF
            sb.Append($"<Display DataName=\"{names[i]}\" RequestName=\"ReadData\" Color=\"{color}\" Width=\"190\"><Rectangle Left=\"{10 + col * 325}\" Top=\"{58 + row * 38}\" Height=\"32\" Width=\"315\"/><Font Name=\"Tahoma\" Size=\"10\" Bold=\"0\" Italic=\"0\" Color=\"3355443\"/></Display>");
        }
        sb.Append("<Label Text=\"Adjustments\" Color=\"9127187\" Alignment=\"0\"><Rectangle Left=\"10\" Top=\"296\" Height=\"28\" Width=\"640\"/><Font Name=\"Arial\" Size=\"11\" Bold=\"1\" Italic=\"0\" Color=\"16777215\"/></Label>");
        sb.Append("<Input DataName=\"Idle mode\" RequestName=\"WriteConfig\" Color=\"15921906\" Width=\"190\"><Rectangle Left=\"10\" Top=\"332\" Height=\"32\" Width=\"315\"/><Font Name=\"Tahoma\" Size=\"10\" Bold=\"0\" Italic=\"0\" Color=\"3355443\"/></Input>");
        sb.Append("<Input DataName=\"Idle offset\" RequestName=\"WriteConfig\" Color=\"15921906\" Width=\"190\"><Rectangle Left=\"335\" Top=\"332\" Height=\"32\" Width=\"315\"/><Font Name=\"Tahoma\" Size=\"10\" Bold=\"0\" Italic=\"0\" Color=\"3355443\"/></Input>");
        sb.Append("<Button Text=\"Apply settings\"><Rectangle Left=\"10\" Top=\"378\" Height=\"38\" Width=\"150\"/><Font Name=\"Arial\" Size=\"10\" Bold=\"1\" Italic=\"0\"/><Message Text=\"Write the idle settings to the ECU?\"/><Send Delay=\"0\" RequestName=\"WriteConfig\"/></Button>");
        sb.Append("<Button Text=\"Reset adaptations\"><Rectangle Left=\"170\" Top=\"378\" Height=\"38\" Width=\"170\"/><Font Name=\"Arial\" Size=\"10\" Bold=\"0\" Italic=\"0\"/><Message Text=\"All learned values will be erased.\"/><Send Delay=\"0\" RequestName=\"Reset\"/></Button>");
        sb.Append("<Button Text=\"Refresh\"><Rectangle Left=\"350\" Top=\"378\" Height=\"38\" Width=\"110\"/><Font Name=\"Arial\" Size=\"10\" Bold=\"0\" Italic=\"0\"/><Send Delay=\"0\" RequestName=\"ReadData\"/></Button>");
        sb.Append("</Screen><Screen Name=\"Status\" Width=\"400\" Height=\"200\" Color=\"16777215\"/></Category></Categories></Target>");
        sb.Append("<Requests Endian=\"Big\"><Request Name=\"StartSession\"><Sent><SentBytes>1003</SentBytes></Sent><Received MinBytes=\"2\"/></Request>");
        sb.Append("<Request Name=\"ReadData\"><Sent><SentBytes>2101</SentBytes></Sent><Received MinBytes=\"20\">");
        for (int i = 0; i < names.Length; i++) sb.Append($"<DataItem Name=\"{names[i]}\" FirstByte=\"{2 + i * 2}\" BitOffset=\"0\"/>");
        sb.Append("</Received></Request>");
        sb.Append("<Request Name=\"WriteConfig\"><ManuelSend/><ShiftBytesCount>2</ShiftBytesCount><Sent><SentBytes>2E0100000000</SentBytes><DataItem Name=\"Idle mode\" FirstByte=\"3\" BitOffset=\"0\"/><DataItem Name=\"Idle offset\" FirstByte=\"4\" BitOffset=\"0\"/></Sent><Received MinBytes=\"3\"/></Request>");
        sb.Append("<Request Name=\"Reset\"><Sent><SentBytes>3101FF00</SentBytes></Sent><Received MinBytes=\"4\"/></Request></Requests>");
        double[] step = [0.25, 1, 1, 1, 0.1, 0.4, 0.01, 0.1, 0.001, 0.01, 1, 0.5];
        double[] off = [0, 0, -40, -40, 0, 0, 0, 0, 0, 0, 0, -20];
        string[] fmt = ["0", "0", "0", "0", "0.0", "0.0", "0.00", "0.0", "0.000", "0.00", "0", "0.0"];
        for (int i = 0; i < names.Length; i++)
        {
            if (names[i] == "Gear") { sb.Append("<Data Name=\"Gear\"><List><Item Value=\"1\" Text=\"First\"/><Item Value=\"2\" Text=\"Second\"/><Item Value=\"3\" Text=\"Third\"/><Item Value=\"4\" Text=\"Fourth\"/></List><Bits count=\"16\"/></Data>"); continue; }
            sb.Append($"<Data Name=\"{names[i]}\"><Bits count=\"16\"><Scaled Step=\"{step[i].ToString(CultureInfo.InvariantCulture)}\" Offset=\"{off[i].ToString(CultureInfo.InvariantCulture)}\" DivideBy=\"1\" Format=\"{fmt[i]}\" Unit=\"{units[i]}\"/></Bits></Data>");
        }
        sb.Append("<Data Name=\"Idle mode\"><List><Item Value=\"0\" Text=\"Automatic\"/><Item Value=\"1\" Text=\"Fixed\"/><Item Value=\"2\" Text=\"Disabled\"/></List><Bits count=\"8\"/></Data>");
        sb.Append("<Data Name=\"Idle offset\"><Bits count=\"8\"><Scaled Step=\"10\" Offset=\"0\" DivideBy=\"1\" Format=\"0\" Unit=\"rpm\"/></Bits></Data>");
        sb.Append("</Ecu>");
        var xml = sb.ToString();
        return (EcuXmlLoader.LoadFromString(xml), EcuLayoutXmlLoader.LoadFromString(xml));
    }

    /// <summary>Simulated ECU for <see cref="Demo"/>: values move over time.</summary>
    public static async Task<(SimulatedTransport, EcuScript)> DemoTransportAsync(int items = 12)
    {
        int n = 0;
        var script = new EcuScript().On("1003", "5003").On("2E01", "6E01").On("3101", "7101FF00");
        script.On("2101", _ =>
        {
            int k = Interlocked.Increment(ref n);
            var b = new byte[1 + items * 2]; b[0] = 0x61;
            void W(int i, double v) { int raw = (int)Math.Clamp(v, 0, 65535); b[1 + i * 2] = (byte)(raw >> 8); b[2 + i * 2] = (byte)raw; }
            for (int i = 0; i < items; i++) W(i, 1000 + 400 * Math.Sin(k / 6.0 + i) + i * 20);
            if (items >= 12)
            {
                double t = k / 7.0;
                W(0, (2400 + 1100 * Math.Sin(t) + 300 * Math.Sin(t * 2.3)) / 0.25);      // engine speed rpm
                W(1, 62 + 18 * Math.Sin(t / 2));                                           // km/h
                W(2, 130 + Math.Min(k / 20, 8));                                           // coolant (raw + 40 offset) ~ 90 C
                W(3, 65 + 2 * Math.Sin(t / 3));                                            // intake air ~ 25 C
                W(4, 138 + 3 * Math.Sin(t * 1.7));                                         // 13.8 V
                W(5, 30 + 20 * Math.Max(0, Math.Sin(t)));                                  // throttle %
                W(6, 350 + 60 * Math.Sin(t * 1.1));                                        // injection ms
                W(7, 350 + 15 * Math.Sin(t * 3));                                          // fuel pressure
                W(8, 1000 + 40 * Math.Sin(t * 4));                                         // lambda
                W(9, 120 + 50 * Math.Max(0, Math.Sin(t)));                                 // boost
                W(10, 1 + (k / 25) % 4);                                                   // gear
                W(11, 64 + 10 * Math.Sin(t));                                              // advance
            }
            return [new EcuReply(b)];
        });
        var t = new SimulatedTransport().AddCan("DEMO", 0x700, script);
        await t.ConnectEcuAsync(new EcuAddress { Name = "DEMO", TxId = 0x700 });
        return (t, script);
    }

    /// <summary>Big screen: <paramref name="count"/> displays laid out in a grid, one request.</summary>
    public static (EcuFile, EcuLayout) Big(int count)
    {
        var sb = new StringBuilder("<Ecu><Target Name=\"BIG\"><Categories><Category Name=\"All\"><Screen Name=\"Everything\" Width=\"1200\" Height=\"900\" Color=\"16777215\">");
        for (int i = 0; i < count; i++)
            sb.Append($"<Display DataName=\"V{i}\" RequestName=\"ReadAll\" Color=\"{(i % 2 == 0 ? 15921906 : 16247773)}\" Width=\"70\"><Rectangle Left=\"{8 + (i % 6) * 196}\" Top=\"{8 + (i / 6) * 28}\" Height=\"26\" Width=\"190\"/><Font Name=\"Tahoma\" Size=\"8\" Bold=\"0\" Italic=\"0\" Color=\"0\"/></Display>");
        sb.Append("</Screen></Category></Categories></Target><Requests Endian=\"Big\"><Request Name=\"ReadAll\"><Sent><SentBytes>2101</SentBytes></Sent><Received MinBytes=\"2\">");
        for (int i = 0; i < count; i++) sb.Append($"<DataItem Name=\"V{i}\" FirstByte=\"{2 + i * 2}\" BitOffset=\"0\"/>");
        sb.Append("</Received></Request></Requests>");
        for (int i = 0; i < count; i++) sb.Append($"<Data Name=\"V{i}\"><Bits count=\"16\"><Scaled Step=\"0.1\" Offset=\"0\" DivideBy=\"1\" Format=\"0.0\" Unit=\"u\"/></Bits></Data>");
        sb.Append("</Ecu>");
        var xml = sb.ToString();
        return (EcuXmlLoader.LoadFromString(xml), EcuLayoutXmlLoader.LoadFromString(xml));
    }

    public static async Task<SimulatedTransport> BigTransportAsync(int count)
    {
        int n = 0;
        var script = new EcuScript().On("2101", _ =>
        {
            int k = Interlocked.Increment(ref n);
            var b = new byte[1 + count * 2]; b[0] = 0x61;
            for (int i = 0; i < count; i++) { int raw = 5000 + (int)(3000 * Math.Sin(k / 5.0 + i)); b[1 + i * 2] = (byte)(raw >> 8); b[2 + i * 2] = (byte)raw; }
            return [new EcuReply(b)];
        });
        var t = new SimulatedTransport().AddCan("BIG", 0x700, script);
        await t.ConnectEcuAsync(new EcuAddress { Name = "BIG", TxId = 0x700 });
        return t;
    }
}
