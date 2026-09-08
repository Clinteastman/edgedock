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
- Embedded user-configured web page, initially Home Assistant; retain sign-in locally.
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
EdgeDock is a working name selected for the initial folder. First version uses a single saved web page; richer page layouts can follow actual use.
