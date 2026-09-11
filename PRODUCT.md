# EdgeDock

<!-- impeccable:product-schema 1 -->

## Platform
Native Windows desktop.

## Stack
User preference: WinUI 3. Technology selection delegated to research with a preference for stable Microsoft tooling, embedded WebView2 and Windows media APIs.

## Users and purpose
A touch dashboard for owners of wide secondary displays, initially the Corsair Xeneon Edge 2560x720 behind a keyboard. Useful without Corsair widgets. Public source for other owners.

## Capabilities and constraints
- Full-screen mode with an obvious way out, usable on any monitor.
- Full screen uses the whole display edge to edge while keeping internal panel
  gaps available for resizing.
- One or two embedded, named user-configured web cards, initially Home Assistant; retain sign-in locally in the shared WebView2 profile.
- Native Windows media information and playback controls.
- Main web area beside a media panel with artwork and playback controls, inspired by Android Auto's split-screen arrangement.
- Media panel placement is configurable: right, left or hidden. Settings is at the top right; utility controls belong in settings rather than a permanent bottom bar.
- On-screen controls quickly hide/show the media panel. Settings also adjust panel width and artwork visibility, with preferences saved locally.
- F11 toggles full screen. Escape is reserved for the hosted page and must never exit EdgeDock full screen.
- Existing applications and terminals remain separate windows the user moves onto the display.
- No embedded terminal, ComfyUI integration, cloud service, telemetry or stored HA API tokens needed for this first version.
- Develop and test on a normal monitor before the hardware arrives.

## Accessibility
Plain English labels, readable text, large touch targets, keyboard access and clear focus indicators.

## Visual direction
User requests a polished Windows concept-mockup feel with real Mica material,
an artwork-led media panel, restrained purple accents and generous spacing.
Native surfaces must reveal Mica rather than cover it with a solid root background.
Maintain readable fallback materials when Mica/transparency is unavailable.

### Planned windowed title bar
User requests a custom Mica title bar that blends into the native shell in
windowed mode, with normal Windows caption buttons, drag/move behaviour and
accessible controls. It disappears in full screen. This is a later polish pass,
not a requirement to delay the current side-panel iteration.

## Open decisions
EdgeDock is a working name selected for the initial folder. More than two web
cards at once can follow actual use.

## Web cards

Settings saves HTTP/HTTPS pages by name. The controls drawer selects the card
in each visible web panel and switches between one and two panels without
reserving a permanent toolbar. Existing single-dashboard settings migrate to a
named card. Scrollbar chrome is hidden in hosted pages while normal page
scrolling remains available.

## Widget dashboard iteration

The native area becomes one to three independently swipeable widget panels.
Each panel has an accessible non-gesture way to change its page. Users can keep
two native panels beside the web dashboard, or turn off the web area and fill
the display with native widgets. Settings must prevent an entirely empty layout.

The first widget library contains media, local PC audio volume/mute, and Windows
Settings shortcuts. Users choose which widgets are available in each panel.
Widget choices and the selected page survive restart. Existing saved dashboards,
sign-in profiles, panel placement and artwork preferences must migrate intact.

Widgets should have a small documented registration and lifecycle contract so
we and contributors can add new native controls independently of the shell.
An online free widget catalogue and installable third-party packages are future
work. This iteration must not claim to download or safely sandbox third-party
code. The local library manages the widgets bundled with EdgeDock.

The gap between two web panels resizes their split. The gap beside the widget
group changes one shared width used by every visible widget panel. Both choices
survive restart, while temporary narrow-window limits leave them unchanged.

Preserve the Mica/purple visual identity. Put occasional shell controls in an
on-demand overlay rather than reserving content space. Check a shallow desktop viewport as well as the
normal development window. The requested custom windowed title bar may ship in
this polish pass when it preserves native caption and dragging behaviour.

The user wants content to use the shallow screen fully: no utility strip in
web-only mode and no shell headers or arrows above widgets. A top-right corner
gesture opens a controls drawer for settings and widget choices. Keep a small
mouse-accessible handle and keyboard access. Swipe between widgets, with a page
selector in the drawer as the non-gesture alternative.

## Widget view

A drawer button opens a separate view of all installed widgets in one horizontal
row, with no web pane. Swiping moves the whole row, so users can find a widget
and use its controls. Returning to the dashboard restores the existing layout;
this mode does not overwrite the saved panel count or widget choices.
