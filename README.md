# DDT4All.NET

Cross-platform (Windows, Linux, macOS) C# / .NET 9 / Avalonia port of [ddt4all](https://github.com/cedricp/ddt4all), built for speed.
Loads ECU definitions from `ecu.zip`, talks to ELM327-family / OBDLink / VLinker / VGate adapters (serial, Bluetooth, WiFi), DoIP and USB CAN.

Features: ECU browser + auto-scan, live-data screens, request console, DTC read/clear, CAN sniffer, ECU data editor, 13 vehicle plugins, 14 languages (partial), built-in ECU simulator for trying it without hardware.

Install: see [docs/INSTALL.md](docs/INSTALL.md). Build: [docs/BUILDING.md](docs/BUILDING.md). Architecture: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

**Safety:** same warnings as upstream. Expert mode can write to a live vehicle's ECUs; only use it if you understand what you are doing.
Licensed GPL-3.0-or-later (derived from ddt4all).
