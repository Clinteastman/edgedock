# Testing

## Web cards (9 September 2026)

- Release x64 build completed with zero warnings/errors; 19 settings checks
  passed.
- In an isolated local-fixture profile, first run showed the web-card setup;
  Settings and drawer controls rendered. Two named cards were supplied through
  the isolated profile and rendered side by side. No permanent toolbar was added.
- The fixture rendered in both panels. Saved cards and the selected cards are
  retained by the local profile; legacy single-dashboard settings migrate to a
  named card.
- Hosted scrollbar chrome was hidden. Keyboard PageDown moved both the document
  (0 to 130) and an inner scroller (0 to 150). One mouse-wheel document movement
  was observed, but the automation input is not reliable enough to count as
  comprehensive mouse-wheel validation. The user reports normal wheel scrolling
  working.
- The final `artifacts/latest` build also hid the late-created shadow-root
  scrollbar after its contents were replaced; PageDown moved its scroll counter
  from 0 to 26. Physical Xeneon touch and Home Assistant sign-in remain separate.
- Full end-to-end card editing and drawer/widget-view return wheel behaviour
  remain manual follow-up checks; settings migration and selection persistence
  are covered by the console checks.

## Controls drawer, Widget view and appearance (9 September 2026)

- Release x64 build passed with zero warnings/errors; 13 settings checks pass.
- Isolated 900x480 logical desktop preview: drawer opens over the dashboard;
  keyboard focus cycles to its panel selectors. Changing three panels to two
  immediately updates the layout and saves the count with existing selections.
- Widget view hides the web pane, renders all three installed widgets at full
  height, and its scrollbar moves the whole row. Back to dashboard restores the
  prior web/panel layout. No media playback or audio volume was changed.
- Selecting Acrylic in Settings and saving visibly changes the window backdrop;
  the saved profile records Acrylic. Automated checks cover its persistence and
  safe fallback from invalid values. Existing profiles default to Mica.
- The user confirmed the earlier corner-drawer appearance. Physical touchscreen
  gestures and a fresh fullscreen test for this combined build remain unverified.
- Test screenshots containing live metadata were not saved or published.

## Initial prototype validation

Checked on Windows 11 x64 in September 2026:

- Debug and Release builds completed with zero warnings and zero errors.
- First-run window rendered with readable text and native touch-sized controls.
- The embedded browser loaded `https://example.com` from the settings UI.
- Windows media-session access returned real title/artist data; unsupported
  previous/next controls were disabled. Playback was not changed during this test.
- The full-screen button entered true full screen; Escape returned to windowed
  mode when native controls had focus.
- Browser-focused F11/Escape exposed a bug in the first implementation. The
  JavaScript shortcut bridge was replaced with an app-thread Windows message
  hook. The corrected Release build passes, but its interactive retest is pending:
  the Windows inspection tool failed with a foreground-process error.
- Source review led to atomic settings saves and media-session generation checks.

The user subsequently confirmed the first version worked in their own test.
They requested that Escape remain available to the embedded page; that binding
is intentionally removed in the next layout iteration.

Not yet verified across devices: restart/login persistence,
playback commands, offline/error flows, physical Xeneon touch, ARM64 and the full
display-scaling matrix. Use Settings to change full-screen mode if a shortcut does not work.

The executable is a development prototype, not a signed or packaged release.

## Side-panel iteration validation

Checked on Windows 11 x64 on 8 September 2026, using a separate test profile:

- Release build: zero warnings and zero errors.
- Five settings checks pass: legacy URL migration, layout persistence, invalid
  enum/width recovery, unknown-field preservation and malformed JSON recovery.
- F11 entered full screen from web content and returned to windowed mode.
- The local keyboard fixture received Escape while EdgeDock stayed full screen.
- The new on-screen Exit full screen button returned to windowed mode.
- The quick media toggle hid the panel; Settings remained available.
- Saving Left placement and disabling artwork visibly moved the panel and freed
  the artwork space. The resulting settings file retained the dashboard URL.
- Real Windows media metadata and artwork rendered; unsupported transport
  controls were disabled. Playback commands were not invoked.
- Native shell and sidebar rendered with no bottom utility strip. Desktop
  screenshots were inspected locally; no private media screenshots are published.
- Independent source review checked artwork lifetime, settings migration,
  Escape handling and Mica exposure. Its touch-exit finding was fixed and retested.

Exact 2560x720 / 150% scaling and physical touch remain unverified. A window-resize
attempt did not produce the intended shallow viewport, so it is not counted as
a passing layout test. Browser sign-in and full playback testing also remain open.

Run the settings checks with `dotnet run --project tests/EdgeDock.Checks -c Release`.
For isolated manual tests, set `EDGEDOCK_DATA_DIR` to a temporary directory before
launching EdgeDock. `tests/keyboard-fixture.html` provides a local Escape counter.

## Widget dashboard validation

Checked on Windows 11 x64 on 8 September 2026:

- Release build completed with zero warnings and errors.
- Eleven console checks cover old settings, per-panel selections, hidden web,
  unknown widget IDs, empty layouts, oversized imports and partially null lists.
- A read-only Core Audio check returned an available named endpoint and a valid
  volume value. No volume/mute or media playback commands were invoked.
- A 1707x480 logical preview (150% scale, roughly 2560px wide) showed web plus
  two native panels. Saving web-hidden mode expanded native panels; changing
  the count to three created a third panel with a clear empty state.
- Independent page navigation reached PC shortcuts without changing the other
  panel. Returning to Media restored the current artwork and metadata.
- Reopening and saving the library retained the current selected widget ID.
- Restart retained three panels and hidden web mode in the isolated profile.
- The initial shallow render exposed clipped media controls. The corrected
  render kept artwork, title/artist and all playback buttons visible together.
- Settings scrolls with a fixed Save layout button. The widget library uses
  readable widget names instead of internal IDs in the selection control.
- F11 hid the custom title bar; Escape kept full screen active; the on-screen
  exit restored the title bar and native caption buttons.

Touch swipes use WinUI FlipView but have not yet been exercised on the physical
Xeneon. Volume writes, OS shortcut launches, HA sign-in and the wider hardware
compatibility matrix remain untested. Screenshots containing live media stayed
local. The test app used a separate profile and did not edit the user's layout.

Use EDGEDOCK_PREVIEW_WIDTH and EDGEDOCK_PREVIEW_HEIGHT (logical pixels) with
EDGEDOCK_DATA_DIR for repeatable development previews; normal launches ignore
these optional variables when unset.

## Ongoing acceptance checks

Backdrop adjustment, 9 September 2026: 15 settings checks pass, including
the saved background visibility value, legacy default and invalid-value recovery.
Native testing used an isolated profile at 900x480 logical pixels. Mica revealed
wallpaper colour across the title bar and widgets after removing the FlipView's
opaque default background. Changing visibility to 75 and saving persisted the
value, and restarting retained it. The final runnable build showed the corrected
dark Mica tint at 75, then switched to Acrylic through Settings and saved it
without crashing. Release rebuild passed with zero warnings/errors. Physical
touchscreen testing remains pending hardware.

When refreshing an existing development output folder, use `dotnet build
src/EdgeDock/EdgeDock.csproj -c Release -p:Platform=x64 -t:Rebuild -o artifacts/latest`.
Publishing over a previous build can leave stale loose XBF files alongside the
new assembly, causing a startup XAML parse failure. Test the actual refreshed
executable, not just a separate build directory.

Compact controls update, 9 September 2026: Release build passed with zero
warnings/errors. Native preview at 1707x480 logical confirmed the separate
utility strip is gone, the gear menu is inside the first widget header, and
there are no native FlipView overlay arrows in the accessibility tree. Media
transport buttons remain visible. Opening Settings and saving rebuilt panels
kept the menu available; hiding widgets moved it into the web panel's header.
Physical swipe verification remains pending hardware.

- First run gives a clear settings action and does not contact a private server.
- A valid HTTP/HTTPS address loads; an invalid address is rejected.
- A failed navigation gives a readable error and a retry action.
- The saved address survives restart. Browser login persistence needs a real login test.
- Full screen enters and exits through Settings and F11. Escape must leave
  EdgeDock in full screen and remain available to the embedded page.
- Right/left/hidden media placement survives restart without changing the URL.
- Artwork matches the current session, clears when unavailable, and does not
  cover transport buttons at 150% display scaling.
- Mica is exposed in the native shell with readable inactive/fallback surfaces.
- Test keyboard shortcuts with focus inside the web page as well as native controls.
- Empty media sessions do not crash the app; unsupported actions are disabled.
- A compatible media player exposes title/artist and responds to transport actions.
- Test at 2560x720 and increased Windows display scaling without clipped controls.

Physical Xeneon touch and the user's Home Assistant sign-in remain separate
hardware/integration checks. Do not infer these from build success.
