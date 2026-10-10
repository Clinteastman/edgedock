# Testing

## Screen schedule (10 October 2026)

- Debug x64 build: zero warnings/errors. 54 console checks pass, including
  night windows across midnight, within a day and disabled, day/night/blank
  decisions, recovery from bad values, and persistence.
- Read-only DDC/CI check on this PC: the Xeneon Edge reports brightness 95 of
  100; the main LG TV screen does not support DDC/CI (would use software
  dimming, and only if EdgeDock were full screen there).
- Through EdgeDock's own brightness code, using hidden 1x1 helper windows (no
  visible change): read the Edge at 95, wrote 95, read back 95; the primary
  screen reported "not supported".
- After review: closing while blanked holds the close until brightness is
  restored; the blanked monitor is remembered by device path and only it is
  restored (if it disconnected, when it returns); waking restores the level
  in use just before blanking; waking outside full screen clears software
  dimming; the dim and blank layers are now topmost, above the drawer and
  Settings. Build, checks and a normal capture repeated (code otherwise).
- Second review: the monitor's level from before EdgeDock first changed it is
  saved with the layout and restored, only to that monitor (same matcher as
  the saved screen, never a guess), when the schedule is off, outside full
  screen, and on close. Runtime: a saved restore for the connected Edge (95,
  no visible change) was applied and cleared within one tick; one for a
  monitor that is not connected stayed saved through running and closing,
  and closing was not blocked. Waking applies a day/night change that
  happened while blank. 55 console checks pass.
- Not tested at runtime: night transitions, idle blanking and waking (need
  real time of day and five or more idle minutes).
- Later review (build and console checks only; these paths need a DDC/CI
  monitor that refuses writes, a locked settings file or a monitor unplugged at
  the right moment, so they were not exercised): the monitor is changed only
  after its original level is saved on disk; a second monitor is left alone
  (software dimming instead) until the first one's level is put back; a level
  that fails to apply is retried, and the pre-blank level is kept until the new
  one is really written; saving unrelated settings no longer rewrites the level;
  closing waits for any in-flight blank or wake to finish completely before
  restoring.

## Media source picker (10 October 2026)

- Debug x64 build: zero warnings/errors. 29 console checks pass, including
  readable names for desktop (`chrome.exe`, full paths) and packaged app IDs.
- Harness against real Windows media sessions, with a second silent test
  session created by a small app: both listed (playing first), Automatic chose
  the playing one, pinning Chrome switched the snapshot to Chrome, Automatic
  switched back. Pinning the test app and letting it exit fell back to Chrome
  automatically.
- Captured EdgeDock window showed "Now playing · Silentplayer" with the chooser
  arrow. Opening the menu needs real input and was not tested.
- After review: a user's choice is applied with a generation number, so a
  slower background re-selection can no longer undo it; a failed refresh keeps
  the app count so the picker stays usable; long app names are capped to the
  panel width so they trim. Both harness scenarios repeated with the same
  results.
- After review: a pinned app with several sessions (for example two players
  or tabs) now follows whichever of them starts playing. Harness with one test
  app holding two silent sessions: pinned showed "Track A", then "Track B"
  after playback moved. The old code passed this run too, because Windows also
  moved its own current session; the change covers the case where Windows'
  current session belongs to another app, which was not reproduced.
- Second review: the shown session pausing now also triggers a fresh choice,
  covering B reporting "playing" before A reports "paused". Harness run with B
  started 0.4 s before A paused: switched to "Track B". Plain order still passes.

## Clock and weather widget (10 October 2026)

- Debug x64 build: zero warnings/errors. 56 console checks pass, including
  forecast parsing (only hours still to come, today's range, "0°" not "-0°"),
  bad responses, WMO code descriptions by day and night, place search names,
  request contents (rounded coordinates, escaped search text, units), and
  persistence of the place and units.
- Checked the live Open-Meteo responses first (Manchester): fields and units
  matched the parser.
- Captured EdgeDock window (test place: Manchester) showed 20:10, the date,
  overcast 11°, high 13° and low 10°, six hourly cells and the update time.
- After review: the next weather refresh is scheduled from when the forecast
  was fetched (never sooner than 2 minutes, so offline retries do not spin);
  Settings and README now say that Find sends the typed town to Open-Meteo.
  Capture repeated and live.
- Not tested yet: the Settings search and save flow (needs real input).

## PC activity widget (10 October 2026)

- Debug x64 build: zero warnings/errors. 35 console checks pass, including
  processor maths, byte rates, the graphics rule, readable units, graph mapping and history.
- A console harness using the same service read, once a second: processor
  12-20% (Windows' own counter said 7% moments later), memory 31.5 of 63.4 GB,
  graphics 0.5% with 3.4 GB video memory, disk and network rates. Each reading
  took about 50 ms, which is why sampling runs off the UI thread.
- A captured EdgeDock window (isolated profile, 720-pixel-tall panel) showed all
  five rows with live graphs and no clipping after tightening the row spacing.
- After review: PDH "new data" readings are accepted; graphics load is the
  busiest individual engine (two 3D engines at 60% read 60%, not 100%);
  unavailable disk or network rates read "not available" rather than 0; video
  memory shows the busiest adapter (`GPU Adapter Memory` has one instance per
  adapter, confirmed on this PC: two). 36 console checks pass.
- Second review: performance counters are opened on the thread pool too, and
  network rates are tracked per adapter, so an adapter connecting or leaving
  causes no spike or drop (console check). 37 console checks pass; a fresh
  capture still showed all five rows live.
- Third review: visibility now also requires the widget to intersect the
  window, so a card scrolled out of view in Widget view stops sampling (code
  only; Widget view needs input to open). Repeat CPU check: 563 ms visible,
  16 ms hidden over 20 seconds.
- Fourth review: coming back into view re-primes the counters and clears the
  graphs, so no reading averages the hidden gap. Capture still live; a
  minimised window used 47 ms of CPU over 15 seconds.
- Fifth review: a gap of more than 3 seconds between samples (sleep, stall)
  re-primes before showing values, and a reading that becomes unavailable
  clears its graph instead of leaving old activity in place (code; capture
  unchanged and live).
- Sampling checks real visibility each second. EdgeDock's CPU time over 20
  seconds: 625 ms with the widget showing, 109 ms with widgets hidden.

## Start on the Edge (9 October 2026)

- Debug and Release x64 builds: zero warnings/errors. 38 console checks pass, including
  monitor matching by device path, by model after a port change, by unique
  resolution, refusal to guess, and persistence of the screen and full-screen
  choice.
- Isolated profile on Windows 11 with three monitors (Xeneon Edge 2560x720 at
  150%, plus two others). A profile remembering the Edge in full screen opened
  full screen on the Edge by itself. A second launch exited and left one copy.
- F11 saved full screen off together with the Edge's device path as read by
  EdgeDock itself; the next launch opened windowed, centred on the Edge.
  Moving the window to the main screen updated the remembered screen.
- After review: a screen whose device path cannot be read is remembered by
  resolution (console check); saving with sign-in start on rewrites an entry
  pointing at another copy; moving the window to the Edge and closing it 0.4
  seconds later still saved the Edge (runtime, isolated profile).
- Second review: screen and full-screen saves now merge onto the saved file
  inside the save lock; a data folder spelled with and without a trailing
  slash shares one instance (runtime: the second spelling handed over). Moves
  are ignored while the saved screen is disconnected (code only).
- Third pass: screen saves merge onto the last successfully saved settings,
  never a re-read of the file. Runtime: with settings.json damaged while
  running, a drag rewrote the full layout (card and left placement kept) plus
  the new screen. A failed screen save shows the "not saved" warning. Closing
  0.4 seconds after a drag holds the close, saves, then closes; the new
  screen was kept. Closing 1.6 seconds after a drag (debounce fired, save in
  flight) also waits for the save; the new screen was kept.
- Fifth pass, saved screen missing (fake monitor): a move 3 seconds after
  launch was ignored; a move 14 seconds after launch replaced it, keeping the
  card and full-screen choice. Two close requests straight after a drag still
  saved the new screen.
- Sixth pass: restoring a minimised window counts as a placement change, and
  a later drag replaces a missing screen even after EdgeDock stepped aside.
  Runtime: with the saved screen missing, a move right after restoring was
  ignored and a move 13 seconds later was saved.
- With no saved screen (first run or older settings), the screen the window
  opened on is saved 1.5 seconds after launch (runtime: saved without any move,
  card kept).
- Seventh pass: a held close keeps waiting while new screen saves join the
  chain (code only). Sign-in start honours Windows' Startup apps switch
  (StartupApproved value; odd first byte means disabled; console check), and
  turning it on in EdgeDock clears an earlier "disabled". Not run against the
  real registry, to avoid creating a sign-in entry on this PC.
- Eighth pass: a windowed EdgeDock whose screen disconnects is marked to move
  back when the screen returns (code only; needs a real disconnect).
- Not yet tested: unplugging or switching off the Edge while full screen
  (minimise and return), sign-in start after a real reboot, and the Startup
  toggle writing the Run key (only the isolated, disabled state was used).

## Page zoom per card (10 October 2026)

- Debug x64 build: zero warnings/errors. 31 console checks pass, including zoom
  limits, per-card persistence and 100% for older settings.
- Measured in the built-in browser pane first: CSS `zoom: 0.8` turned a 700px
  `100vh` element into 560px, so that approach was rejected.
- EdgeDock runtime, isolated profile, a test page reporting its own viewport to
  a local server: at 100% the page saw 1047x454 and a 454px `100vh`; at 80% it
  saw 1308x567 with a 567px `100vh` (fills the panel). After the page navigated
  itself, the second load reported the same sizes, so zoom survives navigation.
- After review: zoom updates run one at a time, folding requests that arrive
  mid-update into one more pass with the latest size and zoom (DevTools calls
  can otherwise complete out of order). Re-measured: still 1308x567 at 80%.
- Not yet tested: that taps and clicks land on the right element at 80%
  (needs real input), and zoom after moving between monitors of different scale.

## Web card recovery (9 October 2026)

- Debug x64 build: zero warnings/errors. 28 console checks pass, including
  retry back-off, the resume-reload rule and the crash limit.
- Isolated profile, card pointing at a local port with nothing listening:
  the card showed it was waiting, and loaded by itself 19 seconds after a
  local server started. Refused connections report `Unknown`, so retries now
  cover every failure except certificate, credential and redirect errors.
- Killing the page's renderer process reloaded the card in the same second
  (server log). Killing the shared browser process recreated the card and
  loaded it in the same second, with a no-cache server proving a real load.
  The fourth browser crash within ten minutes showed "Web cards stopped";
  Try again then recreated and loaded the card.
- Web content itself was not screenshotted (browser windows were masked); load
  evidence comes from the test server's request log.
- After review: removing the last card stops pending retries, the retry timer
  ignores a panel with no card, and the removed page is unloaded to a blank
  page behind the setup screen (code only; a UI deletion run was abandoned
  because test-window focus kept being taken by notifications). A failing
  card was confirmed to keep retrying (503 every 5, 10, 20 and 40 seconds).
- Second review (code only): re-showing an unchanged card that is retrying
  keeps its retry and status; reloads skip panels without a card, so waking
  or Reload never turns a setup panel into "Link blocked".
- Not yet tested: reload after a real sleep and wake, and a real network outage.

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

Draggable dividers, 11 September 2026: 25 settings and layout checks pass. They
cover web split migration, persistence, invalid values, narrow-view render
clamping, shared widget-width conversion and left/right placement direction.
The dividers use pointer capture, apply changes without rebuilding web views,
save only when a drag or keyboard step completes, and restore the prior value
after a cancelled capture. Release x64 rebuild passed with zero warnings/errors.
Native checks with an isolated two-web/two-widget profile at 150% scaling
confirmed both mouse drags: the web split changed from 0.5 to 0.6162, and a
100px logical group drag increased each of two widgets by 50px. Both values
were saved. Keyboard adjustment was also checked. A full transparent hit area
inside the divider is essential; the decorative line alone did not receive
pointer presses. Physical Xeneon touch verification remains separate.
Full screen removes the outer workspace inset, squares dashboard panel edges
and immediately refreshes the WebView clip while retaining divider gaps.
Startup applies the saved root and widget bounds before WebView2 creates its
child windows. This removes the known 340px-to-690px startup layout transition;
isolated wheel checks scrolled each web pane independently after resizing.
This does not yet confirm the user's exact Home Assistant/YouTube combination.
Native F11 checks confirmed no outer inset or rounded edge gaps in full screen.

Web input coordinates, 11 September 2026: on a display scaled to 150%, moving
the window by 302 physical pixels moved a WebView context menu by 453 pixels.
The menu's screen position was being scaled a second time, while ordinary
clicks still matched the rendered page. The executable had no DPI-awareness
declaration, so the process used Windows DPI virtualization. EdgeDock now
declares Per-Monitor V2 awareness in its application manifest, with the older
system-aware fallback. The published manifest was extracted from the executable
and contains both declarations. Native checks of the rebuilt `artifacts/latest`
confirmed the context menu opens beside the pointer, including after moving the
window. In an isolated two-web/two-widget profile, the wheel scrolled each web
pane independently under the pointer and changed only the hovered widget page.
Launching the rebuilt executable through Explorer restored the user's normal
profile (the diagnostic launcher saw a separate profile). In that live layout,
Home Assistant's context menu opened at the pointer, and Home Assistant and
YouTube each scrolled independently under the pointer. Pages were returned to
their starting scroll positions; no dashboard controls or playback were changed.
Physical touchscreen verification remains separate.

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
