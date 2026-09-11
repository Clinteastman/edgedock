# EdgeDock

A Windows dashboard for a wide second screen. Put Home Assistant or another
web page beside native Windows widgets, with a Mica backdrop.

Designed for the 2560 × 720 Corsair Xeneon Edge, but usable on a regular monitor.
Independent community project; not affiliated with Corsair.

**Early prototype.** Source is public so other display owners can try it and
contribute. This is not a signed installer or a finished consumer release.

## What it does

- Saves named web cards and shows one or two side by side.
- Drag the gaps to resize two web cards or every widget panel together; sizes
  are saved, and the same widget width is shared across the group.
- Hides web scrollbar bars while retaining page scrolling.
- Keeps your browser sign-in locally between launches.
- Shows track and artist information from compatible Windows media players.
- Displays artwork supplied by the current media player, with a neutral fallback.
- Places the media panel on the right, on the left, or hides it.
- Offers a quick show/hide button, adjustable panel width and optional artwork.
- Provides previous, play/pause and next controls when the player supports them.
- Switches between windowed and full-screen modes using F11 or the controls drawer.
- Keeps the app menu available while in full screen.
- Supports one to three native widget panels, with an optional web area.
- Lets each panel switch between its chosen widgets independently.
- Includes a local library of media, PC volume/mute and Windows Settings shortcuts.
- Offers **Widget view**: all installed widgets in one horizontally scrolling row,
  with a button to return to your dashboard layout.

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

1. Open **Settings**, add a web card with a name and full `http://` or `https://`
   address, and choose **Save layout**. Add more cards there when needed.
2. Sign in through the web page as normal.
3. Choose one or two web panels and the card for each in **EdgeDock controls**.
   Your existing saved dashboard becomes the first card automatically.
   Choose the widget panel count beside the widget selectors.
   Choose panel position in **Settings**. In **Widget library**,
   tick the widgets each panel should contain. Swipe to switch, or choose a page
   in the controls drawer.
4. Move the window onto your second display and use **F11** or the full-screen
   control in the drawer. Click or swipe down on the top-right handle to open it,
   including in full screen. No utility bar takes space from the dashboard.

**Escape belongs to your web page.** EdgeDock does not use it to exit full screen.
Mica's appearance follows Windows' backdrop behaviour; it falls back to a solid
material when transparency is unavailable or the window is inactive.
Settings also offers **Acrylic**, for a frosted view of what is behind the window.
Use **Background visibility** to adjust either material, then **Save layout**.
50 is the default strength; higher values reveal more background.
This affects native app surfaces; web pages retain their own backgrounds.

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
[Developing widgets](docs/widget-development.md) · [MIT licence](LICENSE)

Useful next steps after trying the prototype: saved page shortcuts, monitor
placement, a media-session picker and a signed installer. These are ideas, not
features claimed by this build. The widget library currently contains bundled
widgets; an online catalogue and third-party package installation are future work.
