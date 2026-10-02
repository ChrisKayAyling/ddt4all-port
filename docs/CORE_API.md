# Ddt4All.Core public API

Pure logic library (net9.0, trim/AOT friendly, no reflection JSON). Port of the Python `core/ecu/*`,
`core/parameters/helpers.py` (zip/layout conversion) and the screen/layout data. Namespaces:

| Namespace | Contents |
|---|---|
| `Ddt4All.Core.Ecu` | `EcuFile`, `EcuRequest`, `EcuData`, `DataItem`, `EcuDevice`, `AutoIdent`, `EcuResponse`, `DecodedValue`, enums `EcuProtocol`, `ByteOrder`, `SessionAccess`, `NamedCollection<T>`, exceptions |
| `Ddt4All.Core.Codec` | `BitCodec` (Span based bit fields), `HexUtil`, `NegativeResponses` (NRC table) |
| `Ddt4All.Core.Loading` | `EcuXmlLoader`, `EcuJsonLoader`, `EcuJsonDumper`, `EcuLoader` |
| `Ddt4All.Core.Layout` | `EcuLayout`, `Screen`, `ScreenCategory`, `ScreenLabel/Display/Input/Button`, `LayoutColor/Rect/Font`, `SendCommand`, `EcuLayoutXmlLoader`, `EcuLayoutJson` |
| `Ddt4All.Core.Database` | `EcuDatabase`, `EcuEntry`, `EcuSearch`, `AutoIdentQuery`, `EcuMatch`, `EcuIndex(Builder)`, `EcuZipBuilder`, `EcuDatabaseOptions` |
| `Ddt4All.Core.Abstractions` | `IEcuTransport` (unchanged) |

## Loading ECU definitions
```csharp
EcuFile ecu = EcuFile.Load("UCH.xml");            // by extension; no extension tries .xml then .json
EcuFile a = EcuXmlLoader.Load(stream);             // streaming XmlReader, no DOM
EcuFile b = EcuJsonLoader.Load(utf8Bytes);         // System.Text.Json source generated; BOM tolerated
string json = ecu.DumpJson();                      // same schema/ordering/omission rules as Python dumpJson
```
`EcuFile` has the Python fields: `EcuName, Endianness, Protocol (Can/Kwp2000/Iso8/Iso/DoIp), SendId, RecvId` (hex strings),
`BaudRate, FastInit, Kw1, Kw2, FuncName, FuncAddr, Projects, AutoIdents`, and ordered name-indexed collections
`Requests`, `Data`, `Devices` (`NamedCollection<T>`: list + `TryGetValue(name)`; duplicates replace in place). `GetRequest(name)` is
exact-then-case-insensitive. Loaders call `ResolveReferences()` so `DataItem.Data` points at its `EcuData`.
Format problems throw `EcuFormatException`.

Notes: the XML loader only keeps `<Data>` when a `<Requests>` element exists (like Python); `Projects` lists elements only
(Python also records `#text` for whitespace); the JSON loader additionally reads `autoidents` (Python ignores them).

## Screens / layout
```csharp
EcuLayout layout = EcuLayoutXmlLoader.Load("UCH.xml");        // Target/Categories/Category/Screen
EcuLayout l2 = EcuLayoutJson.Load(bytes);                      // the ".layout" files inside ecu.zip
string text = EcuLayoutJson.Dump(layout);                      // Python dumpDOC schema
foreach (var cat in layout.Categories) foreach (var name in cat.ScreenNames) { Screen s = layout.Screens[name]; ... }
```
`Screen`: `Width, Height, Color, PreSend (requests sent on open), Labels, Displays, Buttons, Inputs`.
Displays/Inputs carry `DataName` (key in `EcuFile.Data` and in the request's items), `RequestName`, `Bounds`, `Width` (value part),
`Font`, `Color`, `FontColor`. Buttons: `Text, UniqueName, Messages, Send (RequestName + DelayMs)`. Colours are `LayoutColor(R,G,B)`
(DDT COLORREF already converted; `ToRgb()` gives 0xRRGGBB). Coordinates are DDT design pixels (integers, truncated like Python).

## Bit codec / values
Frames are the raw diagnostic payload **including the service id byte**; `FirstByte` is 1-based (`1` = SID byte), exactly like DDT.
```csharp
EcuData d = ecu.Data["Coolant"]; DataItem item = req.ReceiveItems["Coolant"];
string? text = d.GetDisplayValue(frame, item, ecu.Endianness);   // = getDisplayValue (null if frame too short)
string? hex  = d.GetHexValue(frame, item, ecu.Endianness);       // = getHexValue, lower-case hex
bool ok = d.TryGetNumeric(frame, item, ecu.Endianness, out double v);   // allocation-free, for graphs/logging
d.TryGetRaw(frame, item, order, out ulong raw);                  // = getIntValue (<=64 bits)
d.TrySetValue(frameSpan, item, order, "12.5", out string? err);  // = setValue: scaled->decimal text, ascii->text, else hex text
d.TrySetNumeric / TrySetInteger / TrySetRaw(...)
BitCodec.TryReadUInt64 / TryReadRaw / TryWriteUInt64 / TryWriteRaw   // lowest level, Span<byte>
```
Behavioural fidelity (verified against the Python reference on 11 000 generated vectors, see Tests):
byte order resolution (item override > ECU default), the little-endian read/write quirks, signed 8/16 bit only for 1/2 byte values,
list mapping (`Lists`/`Items`), scale `(raw*Step+Offset)/DivideBy` with `Format` decimals (Python `%.Nf` rounding and `repr`/`str(int)` output),
ASCII with lenient UTF-8, lower-case hex for unscaled values.

Deliberate deviations (Python crashes or emits garbage there): little-endian fields with `BitOffset > 7` are rejected;
values wider than the field are rejected (frame untouched); negative scaled values are stored two's complement for signed fields and rejected
for unsigned; non-Latin-1 ASCII characters become `?`; missing frames/data give `null`/`false` instead of exceptions;
list texts are also resolved for scaled data when building requests; odd-length hex is invalid.

## Requests
```csharp
byte[] payload = req.BuildRequest(new Dictionary<string,string>{ ["Mode"]="ON", ["Offset"]="12.5" }); // throws EcuRequestException
bool ok = req.TryBuildRequest(inputs, out payload, out error);
bool set = req.TrySetInput(frameSpan, "Mode", "ON", out error);        // low level: edit a copy of GetSentBytesTemplate()
EcuResponse r = req.DecodeResponse(replyBytes);                        // r.IsNegative, r.NegativeResponseCode/Text, r.IsResponsePending, r.Values, r["Name"]
EcuResponse r2 = await req.SendAsync(transport, inputs, ct);            // IEcuTransport.RequestAsync + decode
EcuResponse r3 = await ecu.SendAsync(transport, "ReadData");
req.TryDecodeNumeric(frame, item, out double v); req.DecodeValue(frame, item);
```
`0x7F` replies produce `IsNegative` with `RejectedServiceId`, `NegativeResponseCode` and `NegativeResponseText`
(`NegativeResponses.Describe(nrc)`, Python `negrsp` table, "Unregistered error" otherwise). NRC `0x78` sets `IsResponsePending`
(retry/wait is the transport/caller's job). `Values` are in receive-item order; no 0x7F handling for `IEcuTransport` errors (they propagate).
Request flags: `ManualSend, MinBytes, ShiftBytesCount, ReplyBytes, SentBytes, Denied (SessionAccess flags), SendItems, ReceiveItems`.

## ECU database (ecu.zip)
```csharp
using var db = EcuDatabase.Open(@"ecu.zip");                    // first run: parse db.json + write cache; later: cache only
db.LoadedFromCache; db.Count; db[i]; db.Entries
List<EcuEntry> hits = db.Search(new EcuSearch { Text="uch", Project="X95", Address="26", Protocol=EcuProtocol.Can, Group="UCH", MaxResults=200 });
EcuMatch m = db.Match(new AutoIdentQuery(diag, supplier, soft, version, EcuProtocol.Can, address));
if (m.IsMatch) { EcuFile f = db.LoadEcu(m.Entry); EcuLayout l = db.LoadLayout(m.Entry); }   // lazy, thread-safe; *Async variants exist
db.Projects; db.GetAddresses(EcuProtocol.Can); db.GetProjectAddresses("X95PH2"); db.GetGroupForAddress("26"); db.ReadEntry("graphics/x.gif");
```
* `EcuEntry` (struct handle): `Href, EcuName, Group, Address, Protocol, Projects (upper-case), AutoIdents/GetAutoIdent(i)`.
* Cache: binary index (`EcuDatabaseOptions.CacheDirectory`, default `~/.local/share/ddt4all-net/cache` or `%LOCALAPPDATA%`; `""` disables;
  `ForceRebuild`). Keyed by path and validated against zip length + mtime; corrupt/stale cache is silently rebuilt. The zip itself is opened
  lazily on the first `LoadEcu`/`ReadEntry`.
* `Match` = `EcuScanner.check_ecu2`: first exact hit (diag version compared as **hex numbers**, supplier/soft/version: DB value, trimmed,
  must be a prefix of the reported one) else the closest-version entry whose supplier+soft are equal (`MatchKind.Approximate`). A CAN query
  never matches KWP entries and vice versa. NOTE: the Python scanner converts the diag byte to a *decimal* string before this hex comparison;
  Comms must reproduce that conversion (`int(byte).ToString()`) to get identical results. Verified against Python on 544 queries.
* `EcuZipBuilder` creates an ecu.zip from XML files (`AddXml`, `Add`, `AddFile`, `Dispose` writes `db.json`) = Python `zipConvertXML`.
* `EcuIndexBuilder`/`EcuDatabase.FromIndex` build in-memory databases without a zip (tests, other sources).
* No `ecu.zip` fallback: a zip without `db.json` is indexed by parsing its top-level `*.json` members (slow).

## Tests / vectors
`tests/Ddt4All.Core.Tests/tools/gen_vectors.py` re-generates `TestData/*.expected.json`, `codec_vectors.json`, `match_vectors.json`
by running the Python reference (third party modules stubbed). Tests do not need Python.
