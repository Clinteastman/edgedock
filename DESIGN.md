# EdgeDock design

## Overview

EdgeDock is a native WinUI 3 control surface for a wide, behind-keyboard display. The source defines a dark dashboard with one large web pane and a persistent native control strip underneath. This document records the implemented XAML and window settings; it is a source review, not a screenshot or device assessment.

## Colors

| Token or use | Value |
| --- | --- |
| Shell background | `#FF111016` |
| Panel background | `#FF1A1821` |
| Purple accent | `#FF9B7CFF` |
| Muted text | `#FFC8C2D4` |
| Settings panel | `#FF24202E` |
| Status overlay | `#E6111016` |
| Error and media-status text | `#FFFFC2C7` |
| Full-screen exit button | `#E6221E2C` |
| Title-bar hover background | `#FF342B48` |

## Typography

The application uses the WinUI default font. Button text is 16 px. The setup title is 32 px semi-bold and its supporting text is 18 px. Web-status titles are 26 px semi-bold with 17 px supporting text. The current track is 19 px semi-bold, its artist is 15 px, and the media status is 13 px. The address field is 17 px.

## Layout

The initial client area is 1600 x 720 pixels and the source enforces a minimum client area of 900 x 420 pixels. The root grid has two rows: the web area takes all remaining height and the native strip takes automatic height.

The web area contains the WebView2 control, setup and web-status overlays, and a top-right full-screen exit button with an 18 px margin. The lower strip uses 18 px horizontal and 12 px vertical padding, 20 px column spacing, and three columns: expandable track information with a 240 px minimum, media controls, and settings/reload/full-screen actions. A settings panel appears beneath the strip content with a 12 px top margin, 12 px padding, and a 10 px corner radius.

## Components

Buttons use a shared style: minimum 52 x 52 px, 18 x 10 px padding, 8 px corner radius, and 16 px text. The media controls are Previous, Play/Pause, and Next. Play/Pause changes its icon and accessible name with playback state.

The embedded page uses a `#111016` default background. Setup, loading, failed-navigation, and failed-process states are native overlays. Full screen uses the Windows AppWindow full-screen presenter and offers a visible “Exit full screen” button as well as F11 and Escape handling.

## Do's and Don'ts

Do keep the main web page visually dominant and retain the native media and action strip at the bottom. Do preserve large, labelled controls, text trimming for long media metadata, and visible status/error states. Do use the purple accent sparingly against the dark shell.

Don't add terminal panes, extra application embeds, telemetry, or dashboard credentials to this interface. Don't replace the full-screen exit control with a gesture-only route. Don't reduce the shared touch target below 52 x 52 px.
