# EdgeDock

A small Windows dashboard for a wide second screen. Put Home Assistant or another
web page above a strip of Windows media controls, then switch to full screen.

Designed for the 2560 × 720 Corsair Xeneon Edge, but usable on a regular monitor.
Independent community project; not affiliated with Corsair.

**Early prototype.** Source is public so other display owners can try it and
contribute. This is not a signed installer or a finished consumer release.

## What it does

- Opens a saved web address in an embedded browser.
- Keeps your browser sign-in locally between launches.
- Shows track and artist information from compatible Windows media players.
- Provides previous, play/pause and next controls when the player supports them.
- Switches between windowed and full-screen modes, with a visible exit button.

Your other apps stay ordinary Windows windows. Drag a terminal onto the display
when you need it, then return to EdgeDock. There is no terminal emulator or
ComfyUI-specific integration to configure.

## Build and run

Use Windows 11 x64, the .NET 10 SDK pinned in `global.json`, and Visual Studio's
Windows app development tools / Windows SDK. WebView2 Evergreen Runtime must be
installed. The app bundles the Windows App SDK runtime for development.

```powershell
dotnet build src/EdgeDock/EdgeDock.csproj -c Release -p:Platform=x64
```

The project is an unpackaged WinUI 3 app. Open `EdgeDock.sln` in Visual Studio or
run the generated `EdgeDock.exe` from the build output. Exact validation and
runtime limitations are recorded in [the testing notes](docs/testing.md).

## First use

1. Open **Settings**, enter your dashboard's full `http://` or `https://` address,
   and choose **Save and open**.
2. Sign in through the web page as normal.
3. Move the window onto your second display and choose **Full screen**.
4. Use **Exit full screen** to return to a normal window. F11 and Escape are also
   implemented; the latest browser-focus fix is awaiting an interactive retest.

Media controls use the current Windows media session. Some applications do not
publish one, or do not support every transport action. Controls are disabled when
unavailable. Unpackaged media access is a prototype compatibility check; signed
MSIX packaging with the appropriate capability is a future distribution step.

## Privacy

No EdgeDock account, telemetry or cloud backend. Your chosen web page still
connects to its own server as it would in a browser. Settings and browser data
stay in your Windows user profile; do not include them in bug reports.

EdgeDock never needs your Home Assistant API token. It does not bypass invalid
TLS certificates. This is a single-user desktop web view, not a hardened public
kiosk browser.

## Development

[Technology decisions](docs/technology.md) · [Contributing](CONTRIBUTING.md) ·
[MIT licence](LICENSE)

Useful next steps after trying the prototype: saved page shortcuts, monitor
placement, a media-session picker and a signed installer. These are ideas, not
features claimed by this first build.
