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

## Layout

The optional web pane sits beside one to three native widget panels, on either
side. With no web pane, panels divide the available width equally. With web
content, each panel prefers 240–440 logical pixels; the layout reduces the web
area before making native controls narrower than 240px. Outer spacing and gaps
are 10px. Content corners are 14px.

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
It contains web visibility/address, panel placement/width, artwork preference,
per-panel enabled widgets and selected page. Panel count lives beside the page
selectors in the controls drawer and applies immediately. The library lists
bundled widgets and preserves unavailable saved IDs. Save applies the layout;
Cancel discards draft edits. The layout cannot become entirely empty.

## Materials and accessibility

- Accent: #9B7CFF; primary media/save actions: #6046B8.
- Muted text: #C8C2D4; native panels layer translucent #1A1821 over Mica.
- Appearance settings choose Mica (wallpaper tint) or Acrylic (blur behind the
  window). Windows supplies material fallbacks when transparency is unavailable.
- Native widgets use Windows' standard card fill over a transparent host rather
  than stacking dark custom fills. The hosted web page keeps its own background.
- Use native icons, clear accessible names and 52px minimum button targets.
- Trim long names; never hide primary controls behind artwork. Preserve keyboard
  access alongside touch gestures.

Desktop shallow-layout checks are recorded in docs/testing.md. Physical touch
and swipe testing on the Xeneon remains a separate hardware check.
