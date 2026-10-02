# Ddt4All.App architecture

Avalonia 12 / .NET 9, MVVM (CommunityToolkit source generators), compiled bindings by default, no reflection DI
(`Services/AppHost.cs` registers everything with explicit factory lambdas).

## Layout
- `Views/` View (.axaml) + `ViewModels/` ViewModel pairs: MainWindow, Dashboard(Connect), EcuBrowser, Dtc, Terminal, Sniffer, Log, Settings, About, ConfirmDialog; Screens/Requests/DataEditor/Plugins are placeholder VMs sharing `PlaceholderView`.
- `Services/` app services and the seam interfaces (`Interfaces.cs`), `Services/Fakes/` fake implementations.
- `Localization/` `Loc` runtime + `{loc:Tr 'English text'}` XAML extension; `Locales/*.json` embedded catalogs.
- `Styles/` `Theme.axaml` (light/dark brushes) and `Controls.axaml`.
- Pages are created lazily by `Func<PageId, PageViewModel>` in `AppHost.CreatePage` and cached by `MainWindowViewModel`.
  Views are bound through DataTemplates in `App.axaml`.

## Integration seams (where Core/Comms plug in)
Replace the fakes by passing real implementations in `AppHostOptions` (or editing `AppHost.Create`):
| Interface | Fake | Real implementation should |
|---|---|---|
| `IEcuCatalogService` | `FakeEcuCatalogService` (5000 rows) | wrap the Core ecu.zip index (names/ids only, disk-cached); `LoadAsync` off the UI thread; map to `EcuEntry` (fill lowercase `SearchKey`); expose `DatabasePath` from `AppSettings.EcuZipPath`/`EcuDirectory`. |
| `IConnectionService` | `FakeConnectionService` | wrap Comms: `ListPortsAsync`, `ConnectAsync(ConnectionRequest)` (profile, baud, TCP, flow control, simulator), expose `Transport` (`Core.Abstractions.IEcuTransport`), raise `PropertyChanged` for State/StatusText/Adapter (any thread; the UI marshals), `SendRawAsync` for the terminal (AT/ST or hex). |
| `IDiagnosticsService` | `FakeDiagnosticsService` | read/clear DTCs via Core request model over `Transport`. |
| `ISnifferService` | `FakeSnifferService` | Comms sniffer; deliver `CanFrame` batches from any thread (VM coalesces at 20 Hz). |
| Screens/Requests/DataEditor/Plugins | placeholders | add real VM + view, register in `AppHost.CreatePage`, remove the placeholder class. |
Safety: `SessionState.IsExpertMode` (never persisted, reset each launch) and `IsSafeRequest(serviceId)` (safe SIDs 10,12,14,17,19,1A,21,22,23,3E from the Python reference) must gate every write path.
`SessionState.CurrentEcu` feeds the status bar.

## Services
Settings: JSON in `AppPaths.ConfigDir` (APPDATA / ~/Library/Application Support / ~/.config, `DDT4All.NET`), atomic debounced saves, source-generated serializer, corrupt files recovered. Logging: `Microsoft.Extensions.Logging` with in-memory ring buffer (log page) + daily rolling file under `AppPaths.LogDir`; level changeable at runtime (`LogLevelSwitch`). Toasts: `INotificationService`. Dialogs/file pickers: `IDialogService`, `IFilePickerService` (fakeable). Theme: `IThemeService` (system/light/dark + accent via Fluent palettes). Single instance: lock file + named pipe activation (`--allow-multiple` bypasses).
Env `DDT4ALL_HOME` redirects config/data (tests).

## Localization
`tools/po2json.py` converts the Python `.po` files to flat JSON (msgid -> msgstr; untranslated/fuzzy dropped), embedded and parsed lazily for the active language only. Keys are English strings, so missing translations fall back to English. Switching language is live (`Loc.SetLanguage`). Only ~22 of ~165 UI strings of the new UI exist in the Python catalogs; the rest need translation (add them to the .po workflow or an overlay).

## Testing / screenshots
`tests/Ddt4All.App.Tests` (xunit v3 + Avalonia.Headless + Skia): smoke tests for all pages, expert gating, localization, settings, filtering; `ScreenshotTests` write `docs/screenshots/*.png`. Run with `DOTNET_ROOT=$HOME/.dotnet dotnet test tests/Ddt4All.App.Tests`.
`Ddt4All.App --headless-check [--shot f.png]` boots the real app headless and prints a startup timeline.
