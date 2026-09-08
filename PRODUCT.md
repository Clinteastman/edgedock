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
- Existing applications and terminals remain separate windows the user moves onto the display.
- No embedded terminal, ComfyUI integration, cloud service, telemetry or stored HA API tokens needed for this first version.
- Develop and test on a normal monitor before the hardware arrives.

## Accessibility
Plain English labels, readable text, large touch targets, keyboard access and clear focus indicators.

## Open decisions
EdgeDock is a working name selected for the initial folder. First version uses a single saved web page; richer page layouts can follow actual use.
