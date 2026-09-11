# EdgeDock design

EdgeDock is a native WinUI dashboard for a wide screen behind a keyboard.
Its identity is dark Mica with purple accents, rounded content surfaces and
large controls. The web page keeps its own appearance.

## Shell

A custom 36px windowed title area reveals Mica and retains Windows caption
buttons and dragging. It disappears in full screen. A small top-right overlay
handle opens the controls drawer by click or downward swipe. It reserves no
content row, including in web-only mode. The drawer contains Settings, full
screen, widget visibility, panel count and per-panel page selection. Configuration appears
over content only when requested.
F11 toggles full screen; Escape is left to active content.
Full screen removes the title row and outer 10px inset. Dashboard panels meet
the physical screen with square outside edges; internal 10px divider gaps stay.

## Layout

One or two optional web panels sit beside one to three native widget panels, on
either side. A web panel shows one named saved card; split web panels may show
different cards. With no web pane, panels divide the available width equally.
With web content, each native panel prefers 240–440 logical pixels; the layout
reduces the web area before making native controls narrower than 240px. Outer
spacing and gaps are 10px. Content corners are 14px. Hosted page scrollbar
chrome is hidden without disabling touch, keyboard, or mouse-wheel scrolling.

When two web panels are visible, their 10px gap is a draggable split handle.
The web/widget gap resizes every widget panel to the same width; moving it is
divided across the visible widget count. Handles support pointer and arrow-key
adjustment, save after an adjustment finishes, and restore the prior size if a
pointer gesture is cancelled. Viewport constraints clamp only the rendered
layout, so a preferred split returns when more space is available.

Panels have no shell header, page counter or navigation arrows. A native
FlipView supplies touch paging; its overlay arrows are hidden. The drawer's
page selectors provide an alternative for mouse and keyboard users.
Only the selected widget view is
created; switching pages unloads the previous view. Empty and missing widgets
show an explanation directing the user to Settings.

## Widgets

Widget view is a temporary alternative to the dashboard. All installed widgets
appear in a horizontally scrollable row with full-height content and the usual
panel width. The row pans as one surface. A horizontal scrollbar provides mouse
access. The controls drawer remains available to return to the saved dashboard.
Dashboard-only layout controls are unavailable while this mode is active.

- Media uses the shared Windows media session: title, artist, artwork and
  supported playback controls. Controls get reserved space; artwork shrinks to
  the remaining height and is capped at 180px.
- Audio displays the default multimedia playback device, volume and mute state.
  Loading or refreshing it never changes system volume.
- PC shortcuts opens named Windows Settings pages. These are shortcuts, not
  embedded replacements for the OS settings interfaces.

## Settings and library

Settings is a 480px overlay with a scrollable body and fixed close/save controls.
It contains web visibility and named saved web cards, panel placement/width,
artwork preference, per-panel enabled widgets and selected page. The controls
drawer selects one or two visible web panels and their cards; it is the only
place these quick controls appear. Web cards share the local WebView2 profile,
so sign-in stays on the computer. Legacy single-address settings migrate to one
named card. The library lists bundled widgets and preserves unavailable saved
IDs. Save applies the layout; Cancel discards draft edits. The layout cannot
become entirely empty.

## Materials and accessibility

- Accent: #9B7CFF; primary media/save actions: #6046B8.
- Muted text: #C8C2D4; native panels layer translucent #1A1821 over Mica.
- Appearance settings choose Mica (wallpaper tint) or Acrylic (blur behind the
  window). Windows supplies material fallbacks when transparency is unavailable.
- Background adjustment changes the material, never the opacity of text or
  controls. Mica remains opaque; its adjustment reveals more wallpaper colour.
  Acrylic's adjustment changes the visibility of blurred content behind it.
  The preference is saved with the layout; Cancel keeps the previous setting.
- Native widgets use Windows' standard card fill over a transparent host rather
  than stacking dark custom fills. The hosted web page keeps its own background.
- Use native icons, clear accessible names and 52px minimum button targets.
- Trim long names; never hide primary controls behind artwork. Preserve keyboard
  access alongside touch gestures.

Desktop shallow-layout checks are recorded in docs/testing.md. Physical touch
and swipe testing on the Xeneon remains a separate hardware check.
