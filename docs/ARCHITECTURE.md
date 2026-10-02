# DDT4All.NET – architecture & ground rules

Port of the Python project ddt4all (reference checkout:
`/tmp/claude-1000/-home-arc-claude-rc/87fd8699-fc28-59a3-8cff-1f345e2918d4/scratchpad/ddt4all`, read-only) to cross-platform C# / .NET 9 + Avalonia 12.

Toolchain: `export PATH=$HOME/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1`. No sudo available.

## Projects (strict ownership – only edit files in your own project)
| Project | Role |
|---|---|
| src/Ddt4All.Core | Pure logic, no UI, no I/O besides ecu.zip/files. ECU model, XML+JSON loaders, bit codec, ecu.zip database & index, screens/layout model. |
| src/Ddt4All.Comms | Transports: ELM327-family, serial, ISO-TP, CAN/KWP/ISO8 init, scanner, DoIP, sniffer, simulator. Implements `Core.Abstractions.IEcuTransport`. |
| src/Ddt4All.App | Avalonia UI (MVVM, CommunityToolkit.Mvvm, compiled bindings). |
| tests/* | xUnit tests + BenchmarkDotNet. Each agent owns tests for its project. |
| installer/ | Packaging scripts (publish, deb/tar/AppImage, Inno Setup, macOS app bundle). |

## Performance rules (performance is the primary requirement)
- Load ECU definitions lazily: index ecu.zip once (names/ids only, cached to disk), parse an ECU file only when opened.
- Use `System.Text.Json` source generators (no reflection), `XmlReader`/span parsing rather than DOM where hot.
- Bit/byte codec works on `Span<byte>`, no string-of-bits manipulation, no per-call allocations on hot paths.
- UI: virtualised lists, compiled bindings, never block the UI thread on I/O or parsing, batch/throttle live-data updates (<= ~30 Hz).
- Must be trim/AOT-friendly where reasonable (avoid reflection).

## Contract
`Ddt4All.Core.Abstractions.IEcuTransport` is the only seam Core needs from hardware. Do not change it without telling the orchestrator.
Behavioural fidelity with the Python reference matters (value decoding, endianness quirks, request building).
