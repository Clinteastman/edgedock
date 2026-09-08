# Testing

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

Not yet verified: corrected browser-focused shortcuts, restart/login persistence,
playback commands, offline/error flows, physical Xeneon touch, ARM64 and the full
display-scaling matrix. Use the visible exit button if a shortcut does not work.

The executable is a development prototype, not a signed or packaged release.

## Manual acceptance checks

- First run gives a clear settings action and does not contact a private server.
- A valid HTTP/HTTPS address loads; an invalid address is rejected.
- A failed navigation gives a readable error and a retry action.
- The saved address survives restart. Browser login persistence needs a real login test.
- Full screen enters and exits through the button, F11 and Escape.
- Test keyboard shortcuts with focus inside the web page as well as native controls.
- Empty media sessions do not crash the app; unsupported actions are disabled.
- A compatible media player exposes title/artist and responds to transport actions.
- Test at 2560x720 and increased Windows display scaling without clipped controls.

Physical Xeneon touch and the user's Home Assistant sign-in remain separate
hardware/integration checks. Do not infer these from build success.
