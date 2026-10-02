# Ddt4All.Comms – public API summary

Hardware communication for DDT4All.NET. Everything is `async`/cancellable; no polling sleeps on hot paths (serial input is pumped through
`System.IO.Pipelines`). The only Core dependency is `Core.Abstractions.IEcuTransport`.
Namespaces: `Ddt4All.Comms` (+ `.Serial`, `.Elm`, `.Diagnostics`, `.Scanning`, `.Sniffer`, `.DoIp`, `.Usb`, `.Simulation`).

## Core types

| Type | Purpose |
|---|---|
| `IAddressableTransport : IEcuTransport, IAsyncDisposable` | `ConnectEcuAsync(EcuAddress)`, `CloseProtocolAsync()`, `CurrentEcu`, `RequestAsync(bytes)`. Implemented by `ElmTransport`, `DoIpClient`, `UsbCanTransport`, `SimulatedTransport`, `ReplayTransport`. |
| `EcuAddress` | `Name, Protocol (Can/Kwp2000Fast/Kwp2000Slow/Iso8/DoIp), TxId, RxId, Is29Bit, ExtAddress, FuncAddress, Speed (Default/Kbps500/Kbps250/Auto), StartSession`. Helpers `EcuAddress.Can("n","7E0","7E8")` (8 hex digits = 29 bit), `EcuAddress.KLine(...)`. DoIP: `TxId` = ECU logical address. |
| `Hex` | `Parse`, `TryParse`, `ToString(bytes, spaced)`. |
| Exceptions | `CommsException` base; `EcuTimeoutException` (no answer / NO DATA), `ElmErrorException` (CAN ERROR, BUS INIT: ERROR, ...), `NegativeResponseException` (helpers only), `ProtocolException`, `LinkClosedException`. |
| `NegativeResponses.Describe(nrc)` | Text table ported from the Python constants. |

`RequestAsync` returns the full response payload. A final negative response is returned as `7F sid nrc` (not thrown); `7F xx 78` (pending) is
waited out transparently (P2* = `ElmOptions.PendingTimeout`). Silence throws `EcuTimeoutException`. Use `RequestPositiveAsync` to turn NRCs into exceptions.

## Serial layer (`Ddt4All.Comms.Serial`)
- `ISerialLink` (`ReadAsync/WriteAsync/SetBaudRateAsync/DiscardInput`), implementations `SystemSerialLink` (System.IO.Ports, RTS/CTS option),
  `TcpSerialLink` (WiFi ELM, e.g. `192.168.0.10:35000`), `InMemorySerialLink.CreatePair()` (tests/simulation).
- `SerialLinkFactory.OpenAsync(name, SerialLinkOptions)`: `host:port` -> TCP, else serial (Bluetooth adapters appear as OS serial ports: COMx, /dev/rfcomm*, /dev/cu.*).
- `PortEnumerator.GetPorts(includeNetworkDefaults)` / `GetPortsAsync(probe: true)` -> `PortInfo(Name, Description, HardwareId, Kind, Status, SuggestedProfileKey, Vid, Pid)`;
  Windows/Linux (sysfs USB descriptions, phantom ttyS filtered, rfcomm/ttyUSB/ttyACM)/macOS (`/dev/cu.*`). `Classify`, `TryParseEndpoint`.

## ELM327 driver (`Ddt4All.Comms.Elm`)
```csharp
var elm = await ElmTransport.OpenAsync("/dev/ttyUSB0", new ElmOptions { Profile = ElmProfiles.FromAdapterType("VLINKER"), TargetBaud = 115200 });
// or ElmTransport.ConnectAsync(ISerialLink, options)
await elm.ConnectEcuAsync(EcuAddress.Can("EDC", "7E0", "7E8") with { StartSession = Hex.Parse("10C0") });
byte[] rsp = await elm.RequestAsync(Hex.Parse("22F190"), ct);
elm.StartKeepAlive();            // optional background tester-present when idle
```
- `ElmProfiles`: VLinker FS, VGate iCar Pro, ELM327 (original/clone/USB), OBDLink SX/EX (RTS/CTS), ELS27 / ELS27 V5, DERLEK USB-DIAG2/3, USB CAN, with baud/timeout/flow control from the README table;
  `FromAdapterType("STD_BT"|"OBDLINK"|...)`, `DetectFromVersion(ATI text)`, `GuessFromDescription(port description, vid, pid)`, `PreferredOrder`.
- Connect: wake, ATZ/ATI (+AT@1, STI), baud negotiation over `ProbeBauds` (`AutoBaud`), standard ATE0/L0/S0/H0, profile auto-detect, optional speed switch (`ATBRD` handshake / `ST SBR`) via `ElmOptions.TargetBaud` or `SwitchBaudAsync`.
- CAN: 11/29 bit (`ATCP`/`ATSH`/`ATFC*`/`ATCRA`, ATSP 6-9), 250/500k and `CanSpeed.Auto`, ISO-TP **Manual** (default; CAF0, driver does PCI, flow control, block size, STmin, BUFFER FULL back-off, ISO-TP extended address byte, optional padding, broadcast-frame filtering, pending via ATMA/STMA) or **ElmAutomatic** (CAF1, requests <= 7 bytes).
- K-line: KWP2000 fast (`ATSP5/ATFI`), slow (`ATSP4/ATIIA/ATSI`, falls back to fast), ISO8 (`ATSP3/ATSI`), startCommunication.
- Retries for transient adapter errors (`ElmOptions.Retries`), serialised access (async lock), resync after timeout/cancel, `Counters`, `Trace` callback, `SendAtAsync`, `ReadVoltageAsync`, raw `Connection` (`ElmConnection`).
- Session keep-alive: the ECU's `StartSession` is re-sent after `KeepAliveInterval` idle (Python behaviour); `StartKeepAlive()` for a background tester present.

## Diagnostics helpers (`Ddt4All.Comms.Diagnostics`, extension methods on any `IEcuTransport`)
`RequestPositiveAsync`, `RequestHexAsync`, `StartSessionAsync`, `TesterPresentAsync`, `ReadDataByIdentifierAsync` (22), `ReadDataByLocalIdentifierAsync` (21),
`ReadDtcsUdsAsync` (19 02 mask, default 0xAF) / `ReadDtcsKwpAsync` (18), `ClearDtcsUdsAsync` / `ClearDtcsKwpAsync` (14), `EcuResetAsync`, `Dtc.Text` (P/C/B/U formatting).

## Scanner (`Ddt4All.Comms.Scanning`)
```csharp
var cands = new[] { new ScanCandidate("UCH", EcuProtocol.Can, "745", "765", "26", Hex.Parse("22F194")) { StartSession = Hex.Parse("1003"), EndSession = Hex.Parse("1001"), Fallback = oldMethodCandidate } };
await foreach (var r in new EcuScanner(transport).ScanAsync(cands, progress /*IProgress<ScanProgress>*/, ct)) ...
```
`ScanResult(Candidate, Status Found/NegativeResponse/NoResponse/Error/Skipped, Response, ExtraResponses, Elapsed, Message, AnsweredBy)`; `ScanOptions` (per-candidate timeout, skip 00/FF, dedupe, stop after first, close protocol). Cancellation throws `OperationCanceledException` to the consumer; the adapter stays usable.

## Sniffer (`Ddt4All.Comms.Sniffer`)
`new CanSniffer(elm, new SnifferOptions { FilterId = "7E8", Speed250 = false })`:
`FramesAsync(ct)` (every frame), `BatchesAsync(interval, ct)` (throttled, default ~30 Hz, for UI), `AggregatedAsync(interval, ct)` (per-ID table with count/period).
Uses ATMA (STMA on STN); bounded drop-oldest queue (`DroppedFrames`); the adapter is released/re-addressed on exit. `SniffedFrame(Timestamp, Id, Extended, Data)`.

## DoIP (`Ddt4All.Comms.DoIp`)
Real ISO 13400-2 framing (the Python code used non-standard message ids). `DoIpDiscovery.DiscoverAsync(timeout, target?)` (UDP broadcast vehicle identification -> `DoIpVehicle`), `DoIpClient.OpenAsync(DoIpOptions)` (TCP + routing activation, background reader answers alive checks, diagnostic ack/NACK, 7F78 pending), implements `IAddressableTransport`.

## USB CAN (`Ddt4All.Comms.Usb`)
`UsbCanTransport` over an `IUsbControlTransfer` seam (vendor/HID control transfers, protocol ported from usb_can.py) + `UsbDeviceCatalog` (VID/PID table). No libusb implementation is shipped.

## Simulation (`Ddt4All.Comms.Simulation`) – no hardware needed
```csharp
var bus = new SimulatedBus();
bus.Add(new FakeCanEcu("EDC", 0x7E0, 0x7E8, new EcuScript().On("22F190", "62F190...").Pending("31", 2, "7101").Negative("2E", 0x31)) { BlockSize = 3 });
bus.Add(new FakeKwpEcu("UCH", 0x7A, script));
var (host, device) = SimulatedElm.Create(bus, new SimulatedElmOptions { StnId = "STN1110 v4.2.0" });
var elm = await ElmTransport.ConnectAsync(host);
```
- `SimulatedElm`: software ELM327/STN speaking the real AT protocol (echo/linefeed/spaces/headers, SP/SH/CP/CRA/FC*, CAF0/CAF1, ATR0/1, ATST, ATBRD, ST SBR, ATMA/STMA, ATSI/ATFI/IIA, ATRV, ...). Fault injection: `Mute`, `InjectErrorOnNextData`, `DropNextCommands`, `Latency`, `RequiredBaud`; `Commands` log. ECU delays are virtual (instant).
- `FakeCanEcu` (ISO-TP both directions, FC block size/STmin, 11/29 bit, ext address), `FakeKwpEcu`, `EcuScript` (prefix rules, sequences, NRC, pending, silent).
- `SimulatedTransport` (scripts, no ELM), `ReplayTransport` (also loads the Python `ecu_*.txt` logs), `SimulatedDoIpGateway` (loopback UDP+TCP), `SimulatedBus.GenerateTrafficAsync` for the sniffer.

## Known gaps
- No libusb implementation for `IUsbControlTransfer`; Windows port descriptions (WMI) not queried; BlueZ RFCOMM binding left to the OS.
- ELM STPX/"opt_stpx_full"/`opt_can2` (STN dual-CAN) and the L1 command cache of the Python driver are not ported (Manual ISO-TP is used on all adapters).
- `IsoTpMode.ElmAutomatic` cannot wait out "response pending"; K-line pending relies on ATMA behaving like on CAN.
- ELM327 baud switching/monitor handshakes are verified against the simulator only (no real hardware tested).
- Python's `ecu_*.txt`/`elm_*.txt` log writers are not ported; use `ElmOptions.Trace`.
