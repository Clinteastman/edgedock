# Developing widgets

EdgeDock's widget library is local: it lists widgets bundled with the app.
It is not an online store or a third-party package installer.

Each panel has its own list of enabled widget IDs and remembers which one is
selected. A widget's stable ID is separate from its display name, so renaming a
widget should not break saved layouts. Keep IDs unique and do not reuse an old
ID for an unrelated widget.

## Contribution rules

Implement a WinUI `UserControl` and register a `WidgetDescriptor` in
`WidgetRegistry.CreateBuiltIns()` under `src/EdgeDock/Widgets`. The descriptor
implements `IWidgetDescriptor`: stable `Id`, `DisplayName`, `Description` and a
`Create()` factory. For example:

```csharp
new WidgetDescriptor("example.clock", "Clock", "Local time.", () => new ClockWidget())
```

The panel creates the selected view on demand and removes it when the page
changes. Use `Loaded` and `Unloaded` to manage subscriptions and timers; handle
being loaded again. The media widget receives the shell's shared media service;
other built-ins currently manage their own integration services.

The library discovers registered descriptors automatically. No MainWindow layout
change is needed to add a self-contained widget. This contract is a starting
point for contributions, not a promised stable binary plugin SDK.

- Keep the widget's view and PC integration separate from the dashboard shell.
- Use the existing Mica surfaces, typography and touch-sized controls.
- Fit both a narrow panel and a shallow display; allow scrolling where needed.
- Start subscriptions or timers when the widget is active and release them
  when it is removed. Do not let hidden widgets keep unnecessary work running.
- Represent unavailable devices and unsupported controls honestly.
- Do not change OS state just because the widget loads or refreshes.
- Keep credentials out of source, settings exports and diagnostic screenshots.
- Document any network access and external applications the widget requires.

## Future third-party packages

The current registration seam is for source contributions. Loading separately
installed widgets will need a versioned contract, package identity, compatibility
checks and a clear trust decision. Native widget code would run with the user's
permissions unless we deliberately introduce isolation; a library listing alone
does not sandbox it.

A future free catalogue can list packages, authors, source links and supported
versions. Download, updates, installation and removal should be designed as a
separate feature, with user control over what code gets installed.
