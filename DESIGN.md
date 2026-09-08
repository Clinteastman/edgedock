# EdgeDock design

EdgeDock is a native WinUI dashboard for a wide screen behind a keyboard.
Its identity is dark Mica with purple accents, rounded content surfaces and
large controls. The web page keeps its own appearance.

## Shell

A custom 36px windowed title area reveals Mica and retains Windows caption
buttons and dragging. It disappears in full screen. A separate 52px control
row keeps widget visibility, full screen and Settings above the content.
No shell buttons cover the embedded page. F11 toggles full screen; Escape is
left to active content. The full-screen button always provides a way out.

## Layout

The optional web pane sits beside one to three native widget panels, on either
side. With no web pane, panels divide the available width equally. With web
content, each panel prefers 240–440 logical pixels; the layout reduces the web
area before making native controls narrower than 240px. Outer spacing and gaps
are 10px. Content corners are 14px.

Each panel shows its widget name, page position and 52px previous/next buttons.
A native FlipView supplies touch paging. Only the selected widget view is
created; switching pages unloads the previous view. Empty and missing widgets
show an explanation directing the user to Settings.

## Widgets

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
panel count, per-panel enabled widgets and selected page. The library lists
bundled widgets and preserves unavailable saved IDs. Save applies the layout;
Cancel discards draft edits. The layout cannot become entirely empty.

## Materials and accessibility

- Accent: #9B7CFF; primary media/save actions: #6046B8.
- Muted text: #C8C2D4; native panels layer translucent #1A1821 over Mica.
- Windows supplies backdrop fallbacks when Mica is unavailable.
- Use native icons, clear accessible names and 52px minimum button targets.
- Trim long names; never hide primary controls behind artwork. Preserve keyboard
  access alongside touch gestures.

Desktop shallow-layout checks are recorded in docs/testing.md. Physical touch
and swipe testing on the Xeneon remains a separate hardware check.
