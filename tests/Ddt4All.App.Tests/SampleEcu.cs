using Ddt4All.Core.Abstractions;
using Ddt4All.Core.Ecu;
using Ddt4All.Core.Loading;

namespace Ddt4All.App.Tests;

internal static class SampleEcu
{
    public const string Xml = """
<?xml version="1.0" encoding="UTF-8"?>
<Ecu>
  <Target Name="EDC17C84 Engine">
    <Projects><X52/><X95/></Projects>
    <Function Address="1" Name="Injection" />
    <CAN BaudRate="500000"><SendId><CANId Value="2016" /></SendId><ReceiveId><CANId Value="2024" /></ReceiveId></CAN>
  </Target>
  <Requests Endian="Big">
    <Request Name="Read VIN"><ReplyBytes>62F190</ReplyBytes><Sent><SentBytes>22F190</SentBytes></Sent>
      <Received MinBytes="20"><DataItem Name="VIN" FirstByte="4" /></Received></Request>
    <Request Name="Read coolant temperature"><Sent><SentBytes>22F405</SentBytes></Sent>
      <Received MinBytes="4"><DataItem Name="Coolant" FirstByte="4" /><DataItem Name="FanState" FirstByte="5" /></Received></Request>
    <Request Name="Read engine speed"><Sent><SentBytes>22F40C</SentBytes></Sent>
      <Received MinBytes="5"><DataItem Name="EngineSpeed" FirstByte="4" /></Received></Request>
    <Request Name="Start diagnostic session"><Sent><SentBytes>1003</SentBytes></Sent><Received MinBytes="2" /></Request>
    <Request Name="Set fan mode"><Sent><SentBytes>2EF42000</SentBytes><DataItem Name="FanMode" FirstByte="4" /></Sent>
      <Received MinBytes="3" /></Request>
    <Request Name="Set idle offset"><Sent><SentBytes>2EF42100</SentBytes><DataItem Name="IdleOffset" FirstByte="4" /></Sent>
      <Received MinBytes="3" /></Request>
    <Request Name="Write calibration"><Sent><SentBytes>2EF430000020</SentBytes><DataItem Name="Calibration" FirstByte="4" /><DataItem Name="Tag" FirstByte="6" /></Sent>
      <Received MinBytes="3" /></Request>
    <Request Name="ECU reset"><Sent><SentBytes>1101</SentBytes></Sent><Received MinBytes="2" /></Request>
  </Requests>
  <Data Name="VIN"><Description>Vehicle identification number</Description><Bytes count="17" ascii="1" /></Data>
  <Data Name="Coolant"><Description>Coolant temperature</Description><Bits count="8"><Scaled Step="1" Offset="-40" DivideBy="1" Format="0" Unit="°C" /></Bits></Data>
  <Data Name="FanState"><List><Item Value="0" Text="Off" /><Item Value="1" Text="Low" /><Item Value="2" Text="High" /></List><Bits count="8" /></Data>
  <Data Name="EngineSpeed"><Bits count="16"><Scaled Step="1" Offset="0" DivideBy="4" Format="0" Unit="rpm" /></Bits></Data>
  <Data Name="FanMode"><List><Item Value="0" Text="Automatic" /><Item Value="1" Text="Forced on" /><Item Value="2" Text="Forced off" /></List><Bits count="8" /></Data>
  <Data Name="IdleOffset"><Bits count="8"><Scaled Step="5" Offset="0" DivideBy="10" Format="0.0" Unit="%" /></Bits></Data>
  <Data Name="Calibration"><Bytes count="2" /></Data>
  <Data Name="Tag"><Bytes count="1" ascii="1" /></Data>
</Ecu>
""";

    public static EcuFile Create() => EcuXmlLoader.LoadFromString(Xml);

    /// <summary>Canned replies; records what was sent.</summary>
    public sealed class StubTransport : IEcuTransport
    {
        public List<byte[]> Sent { get; } = new();
        public ValueTask<byte[]> RequestAsync(ReadOnlyMemory<byte> request, CancellationToken ct = default)
        {
            var r = request.ToArray();
            Sent.Add(r);
            byte[] reply = (r.Length >= 3 && r[0] == 0x22 && r[1] == 0xF1 && r[2] == 0x90) ? [0x62, 0xF1, 0x90, .. "VF1RL000AB1234567"u8.ToArray()]
                : (r.Length >= 3 && r[0] == 0x22 && r[1] == 0xF4 && r[2] == 0x05) ? [0x62, 0xF4, 0x05, 0x7B, 0x02]
                : (r.Length >= 3 && r[0] == 0x22 && r[1] == 0xF4 && r[2] == 0x0C) ? [0x62, 0xF4, 0x0C, 0x0B, 0xB8]
                : r[0] == 0x2E ? [0x6E, r[1], r[2]]
                : [0x7F, r[0], 0x31];
            return ValueTask.FromResult(reply);
        }
    }
}
