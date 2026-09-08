# EdgeDock design

## Overview

EdgeDock is a dark native WinUI 3 dashboard for a wide, behind-keyboard display. The implemented layout gives the web dashboard most of the window and places optional native media controls in a left or right side panel. A top-right media visibility button and Settings button remain on screen.

Mica is enabled with a transparent root surface. The normal Windows title bar has transparent colour settings, but a custom windowed Mica title bar is planned work; it is not described here as implemented.

This document records the current XAML and window source. It is not a screenshot or device assessment.

## Colors

| Token or use | Value |
| --- | --- |
| Purple accent | `#FF9B7CFF` |
| Shell / setup background | `#FF111016` |
| Native panel token | `#FF1A1821` |
| Muted text | `#FFC8C2D4` |
| Web surface | `#FF0B0A0E` |
| Translucent media panel | `#B31A1821` |
| Translucent settings panel | `#F224202E` |
| Artwork placeholder | `#FF292532` |
| Web-status overlay | `#E6111016` |
| Error text | `#FFFFC2C7` |
| Primary action | `#FF6046B8` |
| Title-bar hover background | `#FF342B48` |

## Typography

The source uses the WinUI default font. Buttons use 16 px text. Setup title/supporting text are 32 px semi-bold and 18 px. Web-status title/supporting text are 26 px semi-bold and 17 px. The media title and artist are 20 px semi-bold and 16 px; media status is 13 px. Settings title is 24 px and the address field is 17 px.

## Layout

The app starts at a 1600 x 720 pixel client area and enforces a 900 x 420 pixel minimum. A transparent root contains a workspace with 10 px padding and 10 px column spacing. The web surface is rounded to 14 px and uses the available space.

The media panel is 340 logical pixels by default, configurable from 240 to 440 pixels. At smaller widths it is capped at 42% of the available root width after 30 pixels of workspace allowance. It can sit right, sit left, or be hidden; hiding it gives the web surface the full workspace. The panel has 20 x 16 px padding, 14 px corners, 12 px row spacing, and an optional album-art area constrained to 120-300 px high and 300 px wide.

The top-right media visibility button and Settings button are 52 x 52 px, positioned with 20 px top/right margins. Settings is a 420 px wide, vertically scrollable panel with 20 px padding, a 14 px corner radius, and 80/20 px top/bottom margins. Its panel-width slider covers 240-440 px in 10 px steps.

## Components

Buttons share a 52 x 52 px minimum target, 18 x 10 px padding, 8 px corners, and 16 px text. The media panel has Previous, Play/Pause, and Next controls; their enabled state, play icon, accessible name, track metadata, and optional artwork follow the current Windows media session. Artwork uses a native placeholder when absent or disabled.

The WebView2 surface has native setup, loading, navigation-error, and process-failure overlays. Settings contains the dashboard address, media-panel placement and width, artwork preference, reload, and full-screen control. F11 toggles full screen; Escape remains available to the embedded page and Windows. A labelled top-right Exit full screen button appears only while full screen is active.

## Do's and Don'ts

Do keep the web dashboard dominant, preserve the side panel's 240-440 px limits, and retain visible on-screen access to Settings and media visibility. Do use translucent native surfaces so Mica remains visible around them. Do retain large touch targets, ellipsised track text, and clear loading/error states.

Don't add terminal panes, extra application embeds, telemetry, dashboard credentials, or opaque full-window shell backgrounds. Don't treat this source-derived document as a rendered visual verdict. Don't reduce the shared touch target below 52 x 52 px.
