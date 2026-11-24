using EdgeDock;

var root = Path.Combine(Path.GetTempPath(), "EdgeDock-checks-" + Guid.NewGuid().ToString("N"));
var previousRoot = Environment.GetEnvironmentVariable("EDGEDOCK_DATA_DIR");
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("EDGEDOCK_DATA_DIR", root);
var file = Path.Combine(root, "settings.json");
var passed = 0;

void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine("PASS " + name);
    passed++;
}

try
{
    await File.WriteAllTextAsync(file, """{"DashboardUrl":"https://example.com/dashboard"}""");
    var store = new SettingsStore();
    var legacy = await store.LoadAsync();
    Check(legacy.DashboardUrl == "https://example.com/dashboard" && legacy.ShowArtwork &&
          legacy.MediaPanelPlacement == MediaPanelPlacement.Right && legacy.Material == BackdropMaterial.Mica && legacy.BackdropTransparency == 50,
          "Original URL-only settings retain the Mica default");
    Check(legacy.WebCards is [{ Id: "dashboard", Name: "Dashboard" }] &&
          legacy.WebPanelCount == 1 && legacy.WebPanelCardIds is ["dashboard"],
          "Original dashboard URL migrates to a named web card and selected panel");

    var twoCards = SettingsStore.Normalize(legacy with
    {
        WebCards =
        [
            new WebCardSettings("home", "Home", "https://example.com/home"),
            new WebCardSettings("energy", "Energy", "http://example.org/energy")
        ],
        WebPanelCount = 2,
        WebPanelCardIds = ["energy", "home"]
    });
    await store.SaveAsync(twoCards);
    var twoCardsRestarted = await new SettingsStore().LoadAsync();
    Check(twoCardsRestarted.WebCards?.Count == 2 && twoCardsRestarted.WebPanelCount == 2 &&
          twoCardsRestarted.WebPanelCardIds?.SequenceEqual(["energy", "home"]) == true,
          "Named web cards, split layout and per-panel choices survive restart");

    var oneVisible = SettingsStore.Normalize(twoCardsRestarted with { WebPanelCount = 1 });
    Check(oneVisible.WebPanelCount == 1 && oneVisible.WebPanelCardIds?.SequenceEqual(["energy", "home"]) == true,
          "Hiding the second web panel preserves its selected card");

    var resized = SettingsStore.Normalize(twoCardsRestarted with { WebSplitRatio = 0.68 });
    await store.SaveAsync(resized);
    var resizedRestarted = await new SettingsStore().LoadAsync();
    Check(resizedRestarted.WebSplitRatio == 0.68 &&
          SettingsStore.Normalize(resizedRestarted with { WebPanelCount = 1 }).WebSplitRatio == 0.68,
          "Web split preference survives restart and a temporary single-panel layout");
    Check(SettingsStore.Normalize(resizedRestarted with { WebSplitRatio = double.NaN }).WebSplitRatio == 0.5 &&
          SettingsStore.Normalize(resizedRestarted with { WebSplitRatio = -2 }).WebSplitRatio == PanelLayout.MinimumWebSplitRatio,
          "Invalid web split preferences recover to finite supported values");
    Check(PanelLayout.EffectiveWebSplitRatio(0.8, 600) == 0.6 &&
          PanelLayout.EffectiveWebSplitRatio(0.8, 1200) == 0.8,
          "A narrow view clamps the rendered web split without overwriting its preference");
    Check(PanelLayout.ResizeSharedWidgetWidth(300, 60, 2, MediaPanelPlacement.Right) == 270 &&
          PanelLayout.ResizeSharedWidgetWidth(300, 60, 2, MediaPanelPlacement.Left) == 330,
          "Widget divider distance is shared across panels and follows their side");
    Check(PanelLayout.EffectiveWidgetGroupWidth(1400, 440, 3, 2) == 900 &&
          1400 - 10 - PanelLayout.EffectiveWidgetGroupWidth(1400, 440, 3, 2) == 490,
          "Large widget preferences cannot starve two web panels when their minimums fit");
    var compressedWidgets = PanelLayout.EffectiveWidgetGroupWidth(900, 440, 3, 2);
    Check(compressedWidgets > 0 && compressedWidgets < 740 && 900 - 10 - compressedWidgets > 0,
          "Impossible narrow layouts compress both groups without changing saved preferences");

    var cleanedCards = SettingsStore.Normalize(legacy with
    {
        DashboardUrl = null,
        WebCards =
        [
            new WebCardSettings("same", "  First  ", "https://example.com/a"),
            new WebCardSettings("same", "", "http://example.org/b"),
            new WebCardSettings("bad", "Blocked", "file:///local/page")
        ],
        WebPanelCount = 8,
        WebPanelCardIds = ["missing", "same"]
    });
    Check(cleanedCards.WebCards?.Count == 2 && cleanedCards.WebCards.Select(card => card.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2 &&
          cleanedCards.WebCards[0].Name == "First" && cleanedCards.WebCards[1].Name == "example.org" &&
          cleanedCards.WebPanelCount == 2 && cleanedCards.WebPanelCardIds?.All(id => cleanedCards.WebCards.Any(card => card.Id == id)) == true,
          "Web card normalization drops unsafe URLs, repairs IDs, names and selections, and limits panels");

    await store.SaveAsync(legacy with { Material = BackdropMaterial.Acrylic });
    var acrylic = await new SettingsStore().LoadAsync();
    Check(acrylic.Material == BackdropMaterial.Acrylic,
          "Acrylic backdrop preference survives restart");

    await store.SaveAsync(acrylic with { BackdropTransparency = 73 });
    Check((await new SettingsStore().LoadAsync()).BackdropTransparency == 73,
          "Backdrop adjustment survives restart");
    await File.WriteAllTextAsync(file, """{"BackdropTransparency":999}""");
    Check((await new SettingsStore().LoadAsync()).BackdropTransparency == 100 &&
          SettingsStore.Normalize(legacy with { BackdropTransparency = -10 }).BackdropTransparency == 0 &&
          SettingsStore.Normalize(legacy with { BackdropTransparency = double.NaN }).BackdropTransparency == 50,
          "Invalid backdrop adjustment recovers to a finite supported value");

    await store.SaveAsync(legacy with
    {
        MediaPanelPlacement = MediaPanelPlacement.Hidden,
        LastVisibleMediaPanelPlacement = MediaPanelPlacement.Left,
        MediaPanelWidth = 300,
        ShowArtwork = false
    });
    var restarted = await new SettingsStore().LoadAsync();
    Check(restarted.DashboardUrl == legacy.DashboardUrl &&
          restarted.MediaPanelPlacement == MediaPanelPlacement.Hidden &&
          restarted.LastVisibleMediaPanelPlacement == MediaPanelPlacement.Left &&
          restarted.MediaPanelWidth == 300 && !restarted.ShowArtwork,
          "Hidden panel, restored side and artwork preferences survive restart");

    await File.WriteAllTextAsync(file, """{"DashboardUrl":"https://example.com","MediaPanelPlacement":"123","MediaPanelWidth":9999,"FutureOption":{"enabled":true}}""");
    store = new SettingsStore();
    var imported = await store.LoadAsync();
    Check(imported.MediaPanelPlacement == MediaPanelPlacement.Right && imported.MediaPanelWidth <= 440,
          "Invalid imported panel settings recover to a usable layout");
    await store.SaveAsync(imported);
    Check((await File.ReadAllTextAsync(file)).Contains("FutureOption"),
          "Saving known settings preserves future fields");

    await File.WriteAllTextAsync(file, """{"Material":"BlurrierThanAcrylic"}""");
    var invalidMaterial = await new SettingsStore().LoadAsync();
    Check(invalidMaterial.Material == BackdropMaterial.Mica,
          "Invalid backdrop preference falls back to Mica");

    await File.WriteAllTextAsync(file, "{broken");
    var recovered = await new SettingsStore().LoadAsync();
    Check(recovered.DashboardUrl is null && recovered.ShowArtwork && recovered.WebCards is not null && recovered.WebPanelCardIds is not null,
          "Damaged settings recover without crashing");

    await File.WriteAllTextAsync(file, """{"DashboardUrl":"https://example.com/dashboard","MediaPanelPlacement":"Left","MediaPanelWidth":300,"ShowArtwork":false}""");
    var upgraded = await new SettingsStore().LoadAsync();
    Check(upgraded.IsWebVisible && upgraded.WidgetSlots.Count == 1 &&
          upgraded.WidgetSlots[0].EnabledWidgetIds.Contains("media") &&
          upgraded.DashboardUrl == "https://example.com/dashboard" && !upgraded.ShowArtwork,
          "Existing sidebar settings gain a usable widget slot without losing preferences");

    store = new SettingsStore();
    await store.SaveAsync(upgraded with
    {
        IsWebVisible = false,
        WidgetSlots = new[]
        {
            new WidgetSlotSettings(new[] { "media", "audio" }, "audio"),
            new WidgetSlotSettings(new[] { "system", "future.example.widget" }, "future.example.widget")
        }
    });
    var widgets = await new SettingsStore().LoadAsync();
    Check(!widgets.IsWebVisible && widgets.WidgetSlots.Count == 2 &&
          widgets.WidgetSlots[0].SelectedWidgetId == "audio" &&
          widgets.WidgetSlots[1].SelectedWidgetId == "future.example.widget" &&
          widgets.DashboardUrl == upgraded.DashboardUrl,
          "Independent widget choices and hidden web dashboard survive restart");
    Check(widgets.WidgetSlots[1].EnabledWidgetIds.Contains("future.example.widget"),
          "Unavailable future widget IDs survive a save and restart");

    var empty = SettingsStore.Normalize(upgraded with
    {
        IsWebVisible = false,
        MediaPanelPlacement = MediaPanelPlacement.Hidden,
        WidgetSlots = Array.Empty<WidgetSlotSettings>()
    });
    Check(empty.IsWebVisible ||
          (empty.MediaPanelPlacement != MediaPanelPlacement.Hidden &&
           empty.WidgetSlots.Any(slot => slot.EnabledWidgetIds.Count > 0)),
          "An all-hidden empty layout always recovers a visible surface");

    var normalized = SettingsStore.Normalize(upgraded with
    {
        WidgetSlots = Enumerable.Range(0, 8)
            .Select(_ => new WidgetSlotSettings(new[] { "audio" }, "missing")).ToArray()
    });
    Check(normalized.WidgetSlots.Count is >= 1 and <= 3 &&
          normalized.WidgetSlots.All(slot => slot.SelectedWidgetId == "audio"),
          "Oversized imported layouts and missing selections recover consistently");

    await File.WriteAllTextAsync(file, """{"WidgetSlots":[null,{"EnabledWidgetIds":[null,"audio","audio"],"SelectedWidgetId":"missing"}],"IsWebVisible":false}""");
    var partial = await new SettingsStore().LoadAsync();
    Check(partial.WidgetSlots.Any(slot => slot.EnabledWidgetIds.Contains("audio")) &&
          partial.WidgetSlots.All(slot => slot.EnabledWidgetIds.All(id => !string.IsNullOrWhiteSpace(id))),
          "Partially malformed widget lists preserve usable entries without crashing");
    const string edgePath = @"\\?\DISPLAY#CRX0A1B#5&1a2b3c&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    const string edgeOtherPortPath = @"\\?\DISPLAY#CRX0A1B#5&9f8e7d&0&UID4356#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    const string mainPath = @"\\?\DISPLAY#GSM5B7F#5&1a2b3c&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    var edge = new DisplayInfo(edgePath, 2560, 720);
    var main = new DisplayInfo(mainPath, 3840, 2160);
    var edgePreference = DisplayPlacement.PreferenceFor(edge);
    Check(DisplayPlacement.HardwareIdFrom(edgePath) == "CRX0A1B" && DisplayPlacement.HardwareIdFrom("not-a-path") is null &&
          edgePreference?.HardwareId == "CRX0A1B",
          "Monitor model code is read from its device path");
    Check(DisplayPlacement.FindPreferred([main, edge], edgePreference) == 1,
          "The saved screen is found again by its device path");
    Check(DisplayPlacement.FindPreferred([main, new DisplayInfo(edgeOtherPortPath, 2560, 720)], edgePreference) == 1,
          "The saved screen is found by model after moving it to another port");
    Check(DisplayPlacement.FindPreferred([main, new DisplayInfo("", 2560, 720)], edgePreference) == 1,
          "A screen without a readable path is matched by its unique resolution");
    Check(DisplayPlacement.FindPreferred([main], edgePreference) == -1 &&
          DisplayPlacement.FindPreferred([main, new DisplayInfo(edgeOtherPortPath, 2560, 720), new DisplayInfo(edgeOtherPortPath.Replace("UID4356", "UID4357"), 2560, 720)],
              edgePreference with { DeviceId = "gone" }) == -1 &&
          DisplayPlacement.FindPreferred([main, edge], null) == -1,
          "A missing or ambiguous screen is never guessed");
    Check(SettingsStore.ResolveDataRoot() == Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
          "The data folder resolves without a trailing separator");
    Check(StartupApproval.AllowsRun(null) && StartupApproval.AllowsRun([]) &&
          StartupApproval.AllowsRun([0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]) && StartupApproval.AllowsRun([0x06]) &&
          !StartupApproval.AllowsRun([0x03, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8]) && !StartupApproval.AllowsRun([0x07]),
          "Sign-in start respects Windows' Startup apps switch");
    var sizeOnly = DisplayPlacement.PreferenceFor(new DisplayInfo("", 2560, 720));
    Check(sizeOnly is { DeviceId: null, HardwareId: null, Width: 2560, Height: 720 } &&
          DisplayPlacement.FindPreferred([main, new DisplayInfo("", 2560, 720)], sizeOnly) == 1 &&
          DisplayPlacement.PreferenceFor(new DisplayInfo("", 0, 0)) is null,
          "A screen without a readable path is remembered and found by resolution");

    store = new SettingsStore();
    await store.SaveAsync(upgraded with { Display = edgePreference, OpenFullScreen = true });
    var placed = await new SettingsStore().LoadAsync();
    Check(placed.Display == edgePreference && placed.OpenFullScreen &&
          placed.DashboardUrl == upgraded.DashboardUrl,
          "Saved screen and full-screen choice survive restart without changing the layout");
    await File.WriteAllTextAsync(file, """{"Display":{"DeviceId":"  ","Width":-5,"Height":99999999}}""");
    var badDisplay = await new SettingsStore().LoadAsync();
    Check(badDisplay.Display is null && !badDisplay.OpenFullScreen,
          "An unusable saved screen is ignored and full screen stays off");
    Check(WebRecovery.RetryDelay(0) == TimeSpan.FromSeconds(5) && WebRecovery.RetryDelay(1) == TimeSpan.FromSeconds(10) &&
          WebRecovery.RetryDelay(3) == TimeSpan.FromSeconds(40) && WebRecovery.RetryDelay(4) == TimeSpan.FromSeconds(60) &&
          WebRecovery.RetryDelay(50) == TimeSpan.FromSeconds(60) && WebRecovery.RetryDelay(-1) == TimeSpan.FromSeconds(5),
          "Connection retries back off from 5 seconds to a 60-second ceiling");
    var asleep = new DateTimeOffset(2026, 10, 9, 1, 0, 0, TimeSpan.Zero);
    Check(WebRecovery.ShouldReloadAfterResume(asleep, asleep.AddMinutes(30)) &&
          !WebRecovery.ShouldReloadAfterResume(asleep, asleep.AddSeconds(20)) &&
          WebRecovery.ShouldReloadAfterResume(null, asleep),
          "Web cards reload after a long sleep or a missed sleep start, not a brief one");
    var budget = new RecoveryBudget(3, TimeSpan.FromMinutes(10));
    Check(budget.TryConsume(asleep) && budget.TryConsume(asleep.AddMinutes(1)) && budget.TryConsume(asleep.AddMinutes(2)) &&
          !budget.TryConsume(asleep.AddMinutes(3)) && budget.TryConsume(asleep.AddMinutes(10)),
          "Automatic crash recovery stops after three tries in ten minutes, then allows more later");
    var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Tools\app.exe" };
    var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\Projects" };
    LauncherTargetKind Kind(string target) => LauncherTargets.Classify(target, files.Contains, folders.Contains);
    var checkedFolder = Path.Combine(Path.GetTempPath(), "edgedock-launcher-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(checkedFolder);
    var whilePresent = await LauncherTargets.ClassifyAsync(checkedFolder);
    Directory.Delete(checkedFolder);
    var afterRemoval = await LauncherTargets.ClassifyAsync(checkedFolder);
    Check(whilePresent == LauncherTargetKind.Folder && afterRemoval == LauncherTargetKind.Invalid,
          "Shared target checks look again once the previous check has finished");
    Check(Kind(@"C:\Tools\app.exe") == LauncherTargetKind.File && Kind(@"D:\Projects") == LauncherTargetKind.Folder &&
          Kind("https://example.com/") == LauncherTargetKind.WebPage && Kind(@"C:\Missing\gone.exe") == LauncherTargetKind.Invalid,
          "Launcher items open existing files, folders and web pages");
    Check(Kind(@"""C:\Tools\app.exe"" --wipe") == LauncherTargetKind.Invalid && Kind(@"app.exe") == LauncherTargetKind.Invalid &&
          Kind("cmd /c del x") == LauncherTargetKind.Invalid && Kind("file:///C:/Tools/app.exe") == LauncherTargetKind.Invalid &&
          Kind("javascript:alert(1)") == LauncherTargetKind.Invalid && Kind(@"\\?\C:\Tools\app.exe") == LauncherTargetKind.Invalid &&
          Kind("") == LauncherTargetKind.Invalid,
          "Launcher items reject command lines, arguments, relative paths and other schemes");
    Check(LauncherTargets.DefaultName(@"C:\Tools\app.exe") == "app" && LauncherTargets.DefaultName(@"D:\Projects\") == "Projects" &&
          LauncherTargets.DefaultName("https://www.example.com/x") == "www.example.com",
          "Launcher items get a readable default name");
    Check(LauncherTargets.DefaultName(@"D:\Projects.v2", LauncherTargetKind.Folder) == "Projects.v2" &&
          LauncherTargets.DefaultName(@"C:\Users\me\.config", LauncherTargetKind.Folder) == ".config" &&
          LauncherTargets.DefaultName(@"C:\Users\me\.profile") == ".profile" &&
          LauncherTargets.DefaultName(@"D:\Docs\report.v2.pdf", LauncherTargetKind.File) == "report.v2",
          "Folder names keep their dots; dot-only names are kept whole");
    store = new SettingsStore();
    await store.SaveAsync(upgraded with
    {
        Launchers =
        [
            new LauncherItem("a", " Tools ", @"C:\Tools\app.exe"),
            new LauncherItem("a", "", "https://example.com/"),
            new LauncherItem("bad", "Bad", "cmd /c whoami")
        ]
    });
    var launched = await new SettingsStore().LoadAsync();
    Check(launched.Launchers is [{ Id: "a", Name: "Tools" }, { Id: "a-2", Name: "example.com" }] &&
          launched.DashboardUrl == upgraded.DashboardUrl,
          "Launcher items survive restart; bad entries are dropped and IDs and names repaired");
    Check(SettingsStore.Normalize(upgraded with
          {
              Launchers = Enumerable.Range(0, 40).Select(index => new LauncherItem($"i{index}", "x", "https://example.com/")).ToArray()
          }).Launchers!.Count == SettingsStore.MaximumLaunchers,
          "The launcher is limited to 24 items");

    Check(AudioNaming.DisplayName("Spotify Premium", "Spotify", "spotify", false) == "Spotify Premium" &&
          AudioNaming.DisplayName(@"@%SystemRoot%\System32\AudioSrv.Dll,-202", "Desktop Window Manager", "dwm", false) == "Desktop Window Manager" &&
          AudioNaming.DisplayName(null, null, "discord", false) == "Discord" &&
          AudioNaming.DisplayName("anything", null, null, true) == "System sounds" &&
          AudioNaming.DisplayName(null, " ", null, false) == "App",
          "App audio rows get readable names, never resource references");
    AudioSessionInfo Session(uint pid, string name, bool active, bool system = false) =>
        new(AudioNaming.KeyFor(pid, system), pid, name, null, 50, false, system, active);
    var arranged = AudioNaming.Arrange([
        Session(0, "System sounds", false, system: true),
        Session(10, "Zoom", false),
        Session(20, "Chrome", false),
        Session(20, "Chrome", true),
        Session(30, "Spotify", true)]);
    Check(arranged.Select(session => session.Name).SequenceEqual(["Chrome", "Spotify", "Zoom", "System sounds"]) &&
          arranged[0].IsActive,
          "App audio rows: one per app, playing first, System sounds last");
    var partlyMuted = AudioNaming.Arrange([
        Session(20, "Chrome", true) with { IsMuted = true },
        Session(20, "Chrome", false)]);
    var allMuted = AudioNaming.Arrange([
        Session(20, "Chrome", true) with { IsMuted = true },
        Session(20, "Chrome", false) with { IsMuted = true }]);
    Check(!partlyMuted[0].IsMuted && allMuted[0].IsMuted,
          "An app reads as muted only when all its sessions are muted");
    const string player = @"C:\Apps\Player\player.exe";
    Check(AudioNaming.KeyFor(40, false, player) == AudioNaming.KeyFor(41, false, player.ToLowerInvariant()) &&
          AudioNaming.KeyFor(40, false, null) != AudioNaming.KeyFor(41, false, null) &&
          AudioNaming.KeyFor(40, true, player) == "system",
          "One audio row per program, even across processes; process ID only as a fallback");

    Check(MediaNaming.FallbackName("chrome.exe") == "Chrome" && MediaNaming.FallbackName(@"C:\Apps\Foo\musicbee.exe") == "MusicBee" &&
          MediaNaming.FallbackName("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify") == "Spotify" &&
          MediaNaming.FallbackName("Microsoft.ZuneMusic_8wekyb3d8bbwe!App") == "ZuneMusic" &&
          MediaNaming.FallbackName("somePlayer") == "SomePlayer" && MediaNaming.FallbackName("") == "Media app",
          "Media apps get readable names from desktop and packaged app IDs");
    // Idle +50, kernel (including idle) +200, user +100: 250 of 300 ticks busy.
    Check(StatsMath.CpuPercent(100, 200, 100, 150, 400, 200) is { } cpu && Math.Abs(cpu - 250.0 / 3) < 0.001,
          "Processor use counts kernel and user time minus idle");
    Check(StatsMath.CpuPercent(10, 10, 10, 10, 10, 10) is null && StatsMath.CpuPercent(10, 20, 20, 5, 30, 30) is null,
          "Processor use is unavailable for an empty or reset interval");
    Check(StatsMath.Rate(1000, 3000, 2) == 1000 && StatsMath.Rate(5000, 10, 1) == 0 && StatsMath.Rate(0, 10, 0) == 0,
          "Byte rates handle normal, reset and zero-length intervals");
    Check(StatsMath.GpuPercent([
              ("pid_1_luid_0x0_0xA_phys_0_eng_0_engtype_3D", 30),
              ("pid_2_luid_0x0_0xA_phys_0_eng_0_engtype_3D", 25),
              ("pid_2_luid_0x0_0xA_phys_0_eng_4_engtype_VideoDecode", 70),
              ("pid_3_luid_0x0_0xB_phys_0_eng_0_engtype_3D", 10),
              ("not-a-gpu-instance", 99)]) == 70 &&
          StatsMath.GpuPercent([("pid_1_luid_0x0_0xA_phys_0_eng_0_engtype_3D", 80), ("pid_2_luid_0x0_0xA_phys_0_eng_0_engtype_3D", 60)]) == 100 &&
          StatsMath.GpuPercent([]) is null,
          "Graphics use adds every process's share of an engine, capped at 100%");
    Check(StatsMath.GpuPercent([
              ("pid_1_luid_0x0_0xA_phys_0_eng_0_engtype_3D", 60),
              ("pid_1_luid_0x0_0xA_phys_0_eng_1_engtype_3D", 60)]) == 60,
          "Two engines of the same type are not added together");
    Check(StatsMath.FormatBytes(512) == "512 B" && StatsMath.FormatBytes(1536) == "1.5 KB" &&
          StatsMath.FormatBytes(17179869184) == "16.0 GB" && StatsMath.FormatRate(2_621_440) == "2.5 MB/s",
          "Sizes and rates are written in readable units");
    var line = StatsMath.Sparkline([0, 50, 100], 60, 590, 30, 100);
    Check(line.Count == 3 && line[^1] == (590.0, 0.0) && line[0] == (570.0, 30.0) && line[1].Y == 15 &&
          StatsMath.Sparkline([], 60, 100, 30, 100).Count == 0,
          "Graphs put the newest sample on the right and scale to the panel");
    var before = new Dictionary<string, (long Down, long Up)> { ["wifi"] = (1000, 500), ["gone"] = (9000, 9000) };
    var after = new Dictionary<string, (long Down, long Up)> { ["wifi"] = (3000, 1500), ["vpn"] = (50_000_000, 40_000_000) };
    Check(StatsMath.NetworkRates(before, after, 2) == (1000.0, 500.0) &&
          StatsMath.NetworkRates(after, new Dictionary<string, (long, long)> { ["wifi"] = (10, 10) }, 1) == (0.0, 0.0),
          "A newly connected or removed network adapter causes no spike or drop");
    var history = new SampleHistory(3);
    foreach (var sample in new[] { 1.0, 2, double.NaN, 4 }) history.Add(sample);
    Check(history.Values.SequenceEqual([2.0, 0, 4]),
          "Graph history keeps the newest samples and ignores invalid ones");
    Check(SettingsStore.NormalizeZoom(0.8) == 0.8 && SettingsStore.NormalizeZoom(0.83) == 0.85 &&
          SettingsStore.NormalizeZoom(5) == SettingsStore.MaximumZoom && SettingsStore.NormalizeZoom(0.1) == SettingsStore.MinimumZoom &&
          SettingsStore.NormalizeZoom(double.NaN) == 1.0,
          "Card zoom is clamped to 50-150% in 5% steps");
    store = new SettingsStore();
    await store.SaveAsync(twoCards with
    {
        WebCards = [twoCards.WebCards![0] with { Zoom = 0.75 }, twoCards.WebCards[1] with { Zoom = 9 }]
    });
    var zoomed = await new SettingsStore().LoadAsync();
    Check(zoomed.WebCards?[0].Zoom == 0.75 && zoomed.WebCards[1].Zoom == SettingsStore.MaximumZoom,
          "Each card keeps its own zoom across restart");
    await File.WriteAllTextAsync(file, """{"WebCards":[{"Id":"old","Name":"Old","Url":"https://example.com/"}]}""");
    Check((await new SettingsStore().LoadAsync()).WebCards?[0].Zoom == 1.0,
          "Cards saved before zoom existed open at 100%");
    var workLayout = LayoutProfiles.Capture(twoCards);
    var gamingLayout = workLayout with
    {
        IsWebVisible = false,
        WidgetSlots = [new WidgetSlotSettings(["stats"], "stats"), new WidgetSlotSettings(["media"], "media")],
        WebPanelCount = 1
    };
    var withProfiles = SettingsStore.Normalize(twoCards with
    {
        Profiles =
        [
            new LayoutProfile("work", " Work ", null, workLayout),
            new LayoutProfile("game", "Gaming", "cs2.exe", gamingLayout),
            new LayoutProfile("bad", "  ", null, workLayout),
            new LayoutProfile("game", "Copy", @"C:\Games\x.exe --wipe", workLayout)
        ]
    });
    Check(withProfiles.Profiles is [{ Name: "Work", WhenRunning: null }, { Name: "Gaming", WhenRunning: "cs2" }, { Id: "game-2", WhenRunning: null }],
          "Layout profiles keep clean names and single program names, and repair IDs");
    var applied = LayoutProfiles.Apply(withProfiles, gamingLayout);
    Check(!applied.IsWebVisible && applied.WidgetSlots.Count == 2 && applied.WidgetSlots[0].SelectedWidgetId == "stats" &&
          applied.WebCards!.Count == 2 && applied.Profiles!.Count == 3,
          "Applying a profile changes the layout and keeps web cards and the other profiles");
    var running = new HashSet<string>(["explorer", "cs2"], StringComparer.OrdinalIgnoreCase);
    Check(LayoutProfiles.Automatic(withProfiles.Profiles!, running)?.Name == "Gaming" &&
          LayoutProfiles.Automatic(withProfiles.Profiles!, new HashSet<string>(["explorer"])) is null,
          "A profile switches on while its program is running");
    store = new SettingsStore();
    await store.SaveAsync(withProfiles);
    var profilesSaved = await new SettingsStore().LoadAsync();
    Check(profilesSaved.Profiles?.Count == 3 && profilesSaved.Profiles[1].Layout.WidgetSlots[0].SelectedWidgetId == "stats" &&
          !profilesSaved.Profiles[1].Layout.IsWebVisible && profilesSaved.Profiles[1].WhenRunning == "cs2",
          "Layout profiles survive restart");
    await store.SaveAsync(withProfiles with { AutoSwitch = new AutoLayoutSwitch("game", workLayout) });
    var autoSaved = await new SettingsStore().LoadAsync();
    Check(autoSaved.AutoSwitch?.ProfileId == "game" && autoSaved.AutoSwitch.Before.IsWebVisible == workLayout.IsWebVisible &&
          autoSaved.AutoSwitch.Before.WebPanelCount == workLayout.WebPanelCount,
          "The layout to return to after a program closes survives restart");
    var overriddenAt = new DateTime(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);
    await store.SaveAsync(withProfiles with { SuppressedProfileIds = ["game", "chat"], SuppressedSince = new Dictionary<string, DateTime> { ["game"] = overriddenAt } });
    var suppressedLoaded = await new SettingsStore().LoadAsync();
    Check(suppressedLoaded.SuppressedProfileIds is ["game", "chat"] && suppressedLoaded.SuppressedSince?["game"] == overriddenAt,
          "Layouts switched off by hand, and when, stay off across restart");
    var gameAndChat = new HashSet<string>(["game", "chat"], StringComparer.OrdinalIgnoreCase);
    LayoutProfile[] watched = [new("a", "A", "game", workLayout), new("b", "B", "chat", workLayout)];
    Check(LayoutProfiles.StillSuppressed(["a"], watched, gameAndChat) is ["a"] &&
          LayoutProfiles.StillSuppressed(["a"], watched, new HashSet<string>(["chat"])).Count == 0 &&
          LayoutProfiles.Automatic(watched, gameAndChat, ["a"])?.Id == "b" &&
          LayoutProfiles.StillSuppressed(["a", "b"], watched, gameAndChat) is ["a", "b"] &&
          LayoutProfiles.Automatic(watched, gameAndChat, ["a", "b"]) is null,
          "A hand override ends when its own program stops, even while another layout's program runs");
    var since = new Dictionary<string, DateTime> { ["a"] = overriddenAt };
    Check(LayoutProfiles.StillSuppressed(["a"], watched, gameAndChat, since, new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase) { ["game"] = overriddenAt.AddMinutes(-5) }) is ["a"] &&
          LayoutProfiles.StillSuppressed(["a"], watched, gameAndChat, since, new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase) { ["game"] = overriddenAt.AddMinutes(5) }).Count == 0 &&
          LayoutProfiles.StillSuppressed(["a"], watched, gameAndChat, since, new Dictionary<string, DateTime>()) is ["a"],
          "A hand override ends when its program has restarted, even while EdgeDock was closed");
    Check(LayoutProfiles.NormalizeProcessName("Game.EXE") == "Game" && LayoutProfiles.NormalizeProcessName("cmd /c x") is null &&
          LayoutProfiles.NormalizeProcessName(@"C:\x.exe") is null && LayoutProfiles.NormalizeProcessName(" ") is null,
          "Program triggers accept a name only");
    Check(LayoutProfiles.NormalizeProcessName("notepad++.exe") == "notepad++" && LayoutProfiles.NormalizeProcessName("Some Game") == "Some Game" &&
          LayoutProfiles.NormalizeProcessName("game -windowed") is null &&
          LayoutProfiles.NormalizeProcessName(new string('a', 100) + ".exe") == new string('a', 100) && LayoutProfiles.NormalizeProcessName("\"game\"") is null,
          "Program triggers accept any file name, but not options or quotes");
    var shown = LayoutProfiles.Capture(withProfiles);
    Check(LayoutProfiles.Matches(shown, shown) &&
          !LayoutProfiles.Matches(shown with { PanelWidth = shown.PanelWidth + 40 }, shown) &&
          !LayoutProfiles.Matches(shown with { WidgetSlots = [new(["stats"], "stats")] }, shown) &&
          !LayoutProfiles.Matches(shown with { IsWebVisible = false, WebPanelCount = shown.WebPanelCount + 1 }, shown with { IsWebVisible = false }),
          "A saved layout reads as current only while the dashboard still matches it");
    var night = new TimeSpan(22, 30, 0);
    var morning = new TimeSpan(7, 0, 0);
    Check(ScreenSchedule.IsNight(new TimeSpan(23, 0, 0), night, morning) && ScreenSchedule.IsNight(new TimeSpan(3, 0, 0), night, morning) &&
          !ScreenSchedule.IsNight(new TimeSpan(7, 0, 0), night, morning) && !ScreenSchedule.IsNight(new TimeSpan(12, 0, 0), night, morning) &&
          ScreenSchedule.IsNight(new TimeSpan(14, 0, 0), new TimeSpan(13, 0, 0), new TimeSpan(15, 0, 0)) &&
          !ScreenSchedule.IsNight(new TimeSpan(14, 0, 0), night, night),
          "Night runs past midnight, works within a day, and is off when start equals end");
    var dimming = ScreenScheduleSettings.Default with { DimAtNight = true, DayBrightness = 80, NightBrightness = 15, BlankAfterMinutes = 10 };
    Check(ScreenSchedule.Evaluate(dimming, new DateTime(2026, 10, 10, 23, 0, 0), TimeSpan.FromMinutes(2)) == new ScreenState(15, false) &&
          ScreenSchedule.Evaluate(dimming, new DateTime(2026, 10, 10, 12, 0, 0), TimeSpan.Zero) == new ScreenState(80, false) &&
          ScreenSchedule.Evaluate(dimming, new DateTime(2026, 10, 10, 12, 0, 0), TimeSpan.FromMinutes(11)) == new ScreenState(0, true) &&
          ScreenSchedule.Evaluate(ScreenScheduleSettings.Default, new DateTime(2026, 10, 10, 23, 0, 0), TimeSpan.FromHours(5)) == new ScreenState(null, false),
          "The schedule picks night or day brightness, blanks after the idle time, and does nothing when off");
    Check(ScreenSchedule.Normalize(new ScreenScheduleSettings(true, 140, -5, TimeSpan.FromHours(30), new TimeSpan(6, 30, 45), 7)) is
          { DayBrightness: 100, NightBrightness: 0, BlankAfterMinutes: 0 } screenFixed &&
          screenFixed.NightStart == ScreenScheduleSettings.Default.NightStart && screenFixed.NightEnd == new TimeSpan(6, 30, 0),
          "Screen settings recover to usable values");
    store = new SettingsStore();
    await store.SaveAsync(upgraded with { Screen = dimming with { NightStart = new TimeSpan(21, 45, 0) } });
    var screenSaved = await new SettingsStore().LoadAsync();
    Check(screenSaved.Screen == ScreenSchedule.Normalize(dimming with { NightStart = new TimeSpan(21, 45, 0) }) &&
          (await new SettingsStore().LoadAsync()).DashboardUrl == upgraded.DashboardUrl,
          "The screen schedule survives restart");
    var restorePoint = new BrightnessRestore(edgePreference!, 95);
    store = new SettingsStore();
    await store.SaveAsync(upgraded with { PendingBrightness = restorePoint });
    var restoreSaved = await new SettingsStore().LoadAsync();
    Check(restoreSaved.PendingBrightness == restorePoint &&
          SettingsStore.Normalize(upgraded with { PendingBrightness = restorePoint with { Brightness = 140 } }).PendingBrightness?.Brightness == 100 &&
          SettingsStore.Normalize(upgraded with { PendingBrightness = new BrightnessRestore(new DisplayPreference(null, null, 0, 0), 50) }).PendingBrightness is null,
          "A monitor's original brightness survives restart so it can be put back later");
    const string forecastJson = """
        {"utc_offset_seconds":3600,
         "current":{"time":"2026-10-10T20:00","temperature_2m":11.04,"weather_code":3,"is_day":0},
         "hourly":{"time":["2026-10-10T19:00","2026-10-10T20:00","2026-10-10T21:00","2026-10-10T22:00"],
                   "temperature_2m":[11.5,11.0,-0.4,10.1],"weather_code":[2,3,61,0],"is_day":[1,0,0,0]},
         "daily":{"time":["2026-10-10"],"temperature_2m_max":[12.6],"temperature_2m_min":[9.9]}}
        """;
    var forecast = WeatherForecast.Parse(forecastJson, new DateTime(2026, 10, 10, 20, 25, 0));
    Check(forecast is { Code: 3, IsDay: false, High: 12.6, Low: 9.9 } &&
          forecast.NextHours.Select(hour => hour.Time.Hour).SequenceEqual([21, 22]) && forecast.NextHours.All(hour => !hour.IsDay) &&
          WeatherForecast.Degrees(forecast.Temperature) == "11°" && WeatherForecast.Degrees(-0.4) == "0°",
          "Weather reads now, today's range and only the hours still to come");
    Check(WeatherForecast.Parse("{}", DateTime.Now) is null && WeatherForecast.Parse("not json", DateTime.Now) is null,
          "An unusable weather response is ignored");
    Check(WeatherForecast.Describe(0, true) == ("Clear", "☀️") && WeatherForecast.Describe(0, false).Text == "Clear" &&
          WeatherForecast.Describe(0, false).Symbol == "🌙" && WeatherForecast.Describe(95).Text == "Thunderstorm" &&
          WeatherForecast.Describe(1234).Text == "Unknown",
          "Weather codes are described in plain words, by day and night");
    var places = WeatherForecast.ParseSearch("""
        {"results":[{"name":"Manchester","admin1":"England","country":"United Kingdom","latitude":53.48095,"longitude":-2.23743},
                    {"name":"Monaco","admin1":"Monaco","country":"Monaco","latitude":43.73,"longitude":7.42}]}
        """);
    Check(places.Count == 2 && places[0].Name == "Manchester, England, United Kingdom" && places[1].Name == "Monaco" &&
          WeatherForecast.ParseSearch("""{"generationtime_ms":0.2}""").Count == 0,
          "Place search gives readable names and copes with no results");
    Check(WeatherForecast.ForecastUrl(new WeatherLocation("x", 53.48095, -2.23743), true)
              .Contains("latitude=53.481&longitude=-2.2374") &&
          WeatherForecast.ForecastUrl(new WeatherLocation("x", 1, 2), true).Contains("temperature_unit=fahrenheit") &&
          WeatherForecast.SearchUrl(" St Ives & co ").EndsWith("name=St%20Ives%20%26%20co&count=5&language=en&format=json"),
          "Weather requests send only rounded coordinates or the search text");
    store = new SettingsStore();
    await store.SaveAsync(upgraded with { Weather = new WeatherLocation(" Manchester ", 53.48, -2.24), Fahrenheit = true });
    var weatherSaved = await new SettingsStore().LoadAsync();
    Check(weatherSaved.Weather == new WeatherLocation("Manchester", 53.48, -2.24) && weatherSaved.Fahrenheit &&
          SettingsStore.Normalize(upgraded with { Weather = new WeatherLocation("Nowhere", 120, 0) }).Weather is null,
          "The weather place and units survive restart; impossible places are dropped");

    // ---------- Waveform: synthetic sample buffers only, nothing is played ----------
    static float[] Sine(double frequency, double amplitude, int sampleRate, int channels, double seconds, double phase = 0)
    {
        var frames = (int)(sampleRate * seconds);
        var samples = new float[frames * channels];
        for (var frame = 0; frame < frames; frame++)
            for (var channel = 0; channel < channels; channel++)
                samples[frame * channels + channel] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * frame / sampleRate + phase));
        return samples;
    }
    static List<WaveformColumn> Analyse(float[] samples, int sampleRate, int channels)
    {
        var ring = new WaveformRing(4096);
        new WaveformAnalyzer(sampleRate, channels).Process(samples, ring);
        var columns = new List<WaveformColumn>();
        while (ring.TryRead(out var column)) columns.Add(column);
        return columns;
    }
    static bool Near(double value, double expected, double tolerance) => Math.Abs(value - expected) <= tolerance;

    var bass = Analyse(Sine(100, 0.5, 48000, 2, 0.5), 48000, 2);
    var settled = bass.Skip(20).ToArray();
    Check(bass.Count == 100 && settled.All(c => Near(c.Peak, 0.5, 0.01) && Near(c.Rms, 0.5 / Math.Sqrt(2), 0.03)),
          "Waveform columns are 5 ms blocks with the right peak and RMS (100 Hz, stereo, 48 kHz)");
    Check(settled.Average(c => c.Low) > 0.3 && settled.Average(c => c.High) < 0.01 && settled.Average(c => c.Mid) < 0.15,
          "A bass tone lands in the low band");
    var highs = Analyse(Sine(9000, 0.5, 48000, 2, 0.5), 48000, 2).Skip(20).ToArray();
    Check(highs.Average(c => c.High) > 0.3 && highs.Average(c => c.Low) < 0.01,
          "A 9 kHz tone lands in the high band");
    var mids = Analyse(Sine(1200, 0.5, 44100, 1, 0.5), 44100, 1).Skip(20).ToArray();
    Check(Near(mids.Average(c => c.Mid), 0.5 / Math.Sqrt(2), 0.03) && mids.Average(c => c.Low) < 0.05 && mids.Average(c => c.High) < 0.05,
          "A 1.2 kHz tone passes the mid band at full level (mono, 44.1 kHz)");

    var partialRing = new WaveformRing(16);
    var blocks = new WaveformAnalyzer(48000, 2);
    blocks.Process(new float[100 * 2], partialRing);
    var afterFirst = partialRing.Count;
    blocks.Process(new float[140 * 2 + 1], partialRing); // The odd sample is half a frame.
    blocks.ProcessSilence(240, partialRing);
    Check(blocks.BlockFrames == 240 && afterFirst == 0 && partialRing.Count == 2 && blocks.ColumnsWritten == 2,
          "Waveform blocks carry over between packets and count reported silence");
    Check(Analyse([float.NaN, float.PositiveInfinity, .. new float[238]], 48000, 1) is [{ Peak: 0, Rms: 0, Low: 0 }],
          "Unusable samples count as silence");

    var smallRing = new WaveformRing(4);
    var accepted = Enumerable.Range(1, 5).Count(i => smallRing.TryWrite(new WaveformColumn(i, 0, 0, 0, 0)));
    smallRing.TryRead(out var firstOut);
    smallRing.Clear();
    Check(accepted == 4 && firstOut.Peak == 1 && smallRing.Count == 0 && !smallRing.TryRead(out _),
          "The waveform ring keeps order, drops when full and can be cleared by the reader");
    var sharedRing = new WaveformRing(256);
    const int handedOver = 200_000;
    var producer = Task.Run(() =>
    {
        for (var i = 1; i <= handedOver;)
            if (sharedRing.TryWrite(new WaveformColumn(i, 0, 0, 0, 0))) i++;
            else Thread.Yield();
    });
    var expectedNext = 1;
    var inOrder = true;
    while (expectedNext <= handedOver)
    {
        if (sharedRing.TryRead(out var item)) { inOrder &= item.Peak == expectedNext; expectedNext++; }
        else Thread.Yield();
    }
    await producer;
    Check(inOrder, "The waveform ring hands 200,000 columns between threads without loss or reordering");

    var tap = new WaveformSampleTap();
    var early = new float[4];
    tap.Write(0.25f, 0.5f, 0f);
    tap.CopyLatest(early);
    new WaveformAnalyzer(44100, 2, tap).Process(Enumerable.Range(1, 6000).Select(i => (float)(i % 3)).ToArray(), new WaveformRing(64));
    var latest = new float[3];
    var latestLeft = new float[3];
    var latestRight = new float[3];
    tap.CopyLatest(latest, latestLeft, latestRight);
    Check(early.SequenceEqual([0f, 0f, 0f, 0.25f]) && tap.Written == 3001 && tap.SampleRate == 44100 && latest.SequenceEqual([1.5f, 0.5f, 1f]) &&
          latestLeft.SequenceEqual([1f, 0f, 2f]) && latestRight.SequenceEqual([2f, 1f, 0f]),
          "The sample tap keeps the newest mono samples, oldest first, padded with silence at the start");

    foreach (var look in Enum.GetValues<WaveformLook>())
    {
        var palette = WaveformPalette.For(look);
        var mixes = new[] { palette.Mix(1, 0, 0), palette.Mix(0, 1, 0), palette.Mix(0, 0, 1), palette.Mix(0.3f, 0.2f, 0.5f) };
        if (!mixes.All(c => Near(Math.Max(c.R, Math.Max(c.G, c.B)), 1, 0.0001) && c.R >= 0 && c.G >= 0 && c.B >= 0))
            throw new InvalidOperationException($"{look} colours are not brightened to full");
    }
    Check(true, "Every look's mixed colour is brightened so its strongest channel is full");
    var spectrum = WaveformPalette.For(WaveformLook.Spectrum);
    Check(WaveformPalette.Weights(1, 0, 0) == (1, 0, 0) && spectrum.Mix(1, 0, 0) == new WaveformRgb(1, 50 / 255f, 95 / 255f) &&
          WaveformPalette.Weights(0, 0, 1) == (0, 0, 1) && spectrum.Mix(0, 0, 1) == new WaveformRgb(80 / 255f, 165 / 255f, 1),
          "Pure bass shows the low colour; pure highs show the high colour");
    // Weights 1 : 1.8 : 4.5, so 0.45 of bass balances 0.1 of highs: an even mix, then brightened.
    var (evenLow, evenMid, evenHigh) = WaveformPalette.Weights(0.45f, 0, 0.1f);
    var even = spectrum.Mix(evenLow, evenMid, evenHigh);
    Check(Near(evenLow, 0.5, 1e-6) && Near(evenHigh, 0.5, 1e-6) &&
          Near(even.R, 167.5 / 175, 1e-5) && Near(even.G, 107.5 / 175, 1e-5) && Near(even.B, 1, 1e-5),
          "Band weights balance bass against highs before mixing");
    Check(WaveformPalette.Weights(0, 0, 0) == (0, 1, 0) && WaveformPalette.Weights(float.NaN, -1, float.PositiveInfinity) == (0, 1, 0),
          "Silence and unusable band values show the mid colour");
    var whitened = new WaveformRgb(0.4f, 0, 0.8f).Whiten(0.5f);
    Check(Near(whitened.R, 0.7, 1e-6) && Near(whitened.G, 0.5, 1e-6) && Near(whitened.B, 0.9, 1e-6),
          "The RMS bar's colour leans towards white");

    static WaveformBar Feed(WaveformLevels levels, WaveformColumn column, double seconds)
    {
        WaveformBar bar = default;
        for (var i = 0; i < (int)Math.Round(seconds / WaveformAnalyzer.BlockSeconds); i++) bar = levels.Next(column);
        return bar;
    }
    var quiet = new WaveformColumn(0.1f, 0.05f, 0.05f, 0, 0);
    var lifted = new WaveformLevels(true);
    Check(Near(Feed(lifted, quiet, 6).Peak, WaveformLevels.Target, 0.001) && Near(lifted.Gain, 9.2, 0.01) &&
          Near(Feed(new WaveformLevels(false), quiet, 6).Peak, 0.1, 0.0001) && new WaveformLevels(false).Gain == 1,
          "Auto level lifts a steady quiet sound to 92% height; without it the level is kept");
    var afterLoud = new WaveformLevels(true);
    Feed(afterLoud, new WaveformColumn(1, 0.5f, 0.5f, 0, 0), 1);
    var quietAfterLoud = Feed(afterLoud, quiet, 5);
    Check(Near(afterLoud.Envelope, 0.5, 0.01) && Near(quietAfterLoud.Peak, 0.1 * 0.92 / 0.5, 0.01),
          "Auto level falls by half over five seconds after a loud moment");
    var fixedLevel = new WaveformLevels(false).Next(new WaveformColumn(0.95f, 0.9f, 0, 0, 0));
    Check(fixedLevel.Peak == 0.95f && fixedLevel.Rms == 1, "RMS is drawn 1.45 times larger, never past full height");
    var colourShift = new WaveformLevels(false);
    Feed(colourShift, new WaveformColumn(0.5f, 0.3f, 0.3f, 0, 0), 1);
    var justAfter = colourShift.Next(new WaveformColumn(0.5f, 0.3f, 0, 0, 0.3f));
    var later = Feed(colourShift, new WaveformColumn(0.5f, 0.3f, 0, 0, 0.3f), 0.2);
    Check(justAfter.LowWeight > 0.4 && later.HighWeight > 0.95, "Band colour changes smoothly rather than flickering");

    var fastHistory = new WaveformHistory(240);
    var slowHistory = new WaveformHistory(50);
    WaveformBar lastSlow = default;
    for (var i = 0; i < 400; i++)
    {
        var bar = new WaveformBar(i % 4 == 1 ? 0.9f : 0.1f, 0.05f, 0.2f, 0.4f);
        fastHistory.Add(bar, out _);
        if (slowHistory.Add(bar, out var merged)) lastSlow = merged;
    }
    Check(fastHistory.ColumnsPerEntry == 1 && Near(fastHistory.PixelsPerEntry, 1.2, 1e-9) && fastHistory.Written == 400 &&
          slowHistory.ColumnsPerEntry == 4 && slowHistory.PixelsPerEntry == 1 && slowHistory.Written == 100 &&
          lastSlow.Peak == 0.9f && lastSlow.Rms == 0.05f && Near(lastSlow.LowWeight, 0.2, 1e-6) && Near(lastSlow.HighWeight, 0.4, 1e-6) &&
          new WaveformHistory(1000).PixelsPerEntry == 5,
          "Scroll history keeps an entry per pixel or more, merging columns at slow speeds by their loudest moment");

    // Windows delivers 10 ms packets (2 columns); frames come every 1/60 s.
    var clock = new WaveformClock();
    long received = 0;
    var previousPosition = double.NaN;
    var steps = new List<double>();
    var neverAhead = true;
    for (var frame = 1; frame <= 600; frame++)
    {
        received = (long)(frame / 60.0 * 100) * 2;
        var position = clock.Advance(1 / 60.0, received);
        neverAhead &= position <= received - 1;
        if (frame > 120) steps.Add(position - previousPosition);
        previousPosition = position;
    }
    Check(neverAhead && steps.All(step => Near(step, 200 / 60.0, 0.2)) && Near(received - previousPosition, WaveformClock.Latency, 3),
          "The scrolling picture moves at an even speed about 40 ms behind the newest sound, never ahead of it");
    var stalled = new WaveformClock();
    stalled.Advance(0, 100);
    for (var frame = 0; frame < 12; frame++) stalled.Advance(1 / 60.0, 100);
    var duringStall = stalled.Advance(1 / 60.0, 100);
    var afterGap = stalled.Advance(1 / 60.0, 400);
    Check(duringStall == 99 && Near(afterGap, 400 - WaveformClock.Latency + 200 / 60.0, 1e-6),
          "A stall waits at the newest column; a long gap jumps back in line");

    var fftReal = new float[64];
    var fftImaginary = new float[64];
    for (var i = 0; i < 64; i++) fftReal[i] = (float)Math.Cos(2 * Math.PI * 5 * i / 64);
    WaveformFft.Forward(fftReal, fftImaginary);
    var magnitudes = Enumerable.Range(0, 64).Select(i => Math.Sqrt(fftReal[i] * fftReal[i] + fftImaginary[i] * fftImaginary[i])).ToArray();
    Check(Near(magnitudes[5], 32, 1e-3) && Near(magnitudes[59], 32, 1e-3) &&
          magnitudes.Where((_, i) => i != 5 && i != 59).All(m => m < 1e-3),
          "The FFT puts a pure tone in its own bin at the right size");

    var analyser = new WaveformSpectrum(autoLevel: false);
    var tone = Sine(1000, 0.5, 48000, 1, 0.1).AsSpan(0, WaveformSpectrum.Size).ToArray();
    for (var frame = 0; frame < 15; frame++) analyser.Update(tone, 48000, 1 / 60.0);
    var loudestBand = analyser.Levels.ToArray().Select((level, band) => (level, band)).MaxBy(x => x.level).band;
    var expectedBand = (int)(Math.Log(1000 / WaveformSpectrum.LowestHz) / Math.Log(WaveformSpectrum.HighestHz / WaveformSpectrum.LowestHz) * WaveformSpectrum.Bands);
    Check(Math.Abs(loudestBand - expectedBand) <= 1 && Near(analyser.BandAmplitude(loudestBand), 0.5, 0.08) &&
          analyser.Levels[loudestBand] > 0.95 && analyser.Levels[5] < 0.3 && analyser.Levels[WaveformSpectrum.Bands - 2] < 0.3,
          "A 1 kHz tone at half scale fills its own spectrum band, and not the bass or top bands");
    var silence = new float[WaveformSpectrum.Size];
    for (var frame = 0; frame < 12; frame++) analyser.Update(silence, 48000, 1 / 60.0);
    var peakHeld = analyser.Peaks[loudestBand];
    for (var frame = 0; frame < 60; frame++) analyser.Update(silence, 48000, 1 / 60.0);
    Check(analyser.Levels[loudestBand] < 0.01 && peakHeld > 0.95 && analyser.Peaks[loudestBand] < 0.5,
          "After the sound stops the bars fall within a second; the peak marker holds, then follows");
    var quietTone = Sine(1000, 0.01, 44100, 1, 0.1).AsSpan(0, WaveformSpectrum.Size).ToArray();
    var autoSpectrum = new WaveformSpectrum(autoLevel: true);
    var fixedSpectrum = new WaveformSpectrum(autoLevel: false);
    // Auto level starts at -20 dB and falls 4 dB a second towards the loudest band.
    for (var frame = 0; frame < 400; frame++)
    {
        autoSpectrum.Update(quietTone, 44100, 1 / 60.0);
        fixedSpectrum.Update(quietTone, 44100, 1 / 60.0);
    }
    Check(autoSpectrum.Levels.ToArray().Max() > 0.95 && Near(fixedSpectrum.Levels.ToArray().Max(), (-40 + 66) / 60.0, 0.04),
          "Auto level fills the spectrum for a quiet tone; without it a -40 dB tone reaches about 43%");

    var scopeOutput = new float[WaveformScope.Points];
    var recentSamples = Sine(220, 0.5, 48000, 1, 0.1, phase: 1.1).AsSpan(0, 4096).ToArray();
    WaveformScope.Prepare(recentSamples, scopeOutput, 1.5f);
    var shiftedOutput = new float[WaveformScope.Points];
    WaveformScope.Prepare(Sine(220, 0.5, 48000, 1, 0.1, phase: 1.1).AsSpan(37, 4096).ToArray(), shiftedOutput, 1.5f);
    Check(Near(scopeOutput[0], 0, 0.05) && scopeOutput[3] > scopeOutput[0] && Near(scopeOutput.Max(), 0.75, 0.01) &&
          scopeOutput.Zip(shiftedOutput).All(pair => Near(pair.First, pair.Second, 0.05)),
          "The oscilloscope starts on a rising zero crossing, so a steady note holds still as time moves on");
    var loudOutput = new float[WaveformScope.Points];
    WaveformScope.Prepare(Enumerable.Repeat(0.001f, 4096).ToArray(), loudOutput, 2000);
    Check(loudOutput.All(value => value == 1), "Oscilloscope gain is kept within full height, and a flat signal still draws");
    // A slow bass note changes by under 0.004 between samples: the trigger must still find it.
    var bassScope = new float[WaveformScope.Points];
    var bassShifted = new float[WaveformScope.Points];
    var bassWave = Sine(60, 0.5, 48000, 1, 0.2, phase: 0.7);
    WaveformScope.Prepare(bassWave.AsSpan(0, 4096).ToArray(), bassScope, 1);
    WaveformScope.Prepare(bassWave.AsSpan(211, 4096).ToArray(), bassShifted, 1);
    var bassStart = WaveformScope.FindStart(bassWave.AsSpan(0, 4096).ToArray(), WaveformScope.Points, 1);
    var noiseStart = WaveformScope.FindStart(Enumerable.Range(0, 4096).Select(i => (float)(Math.Sin(i * 1.7) * 0.004)).ToArray(), WaveformScope.Points, 1);
    Check(bassStart != 4096 - WaveformScope.Points && Near(bassScope[0], 0, 0.005) && bassScope[10] > bassScope[0] &&
          bassScope.Zip(bassShifted).All(pair => Near(pair.First, pair.Second, 0.01)) && noiseStart == 4096 - WaveformScope.Points,
          "The scope locks onto a slow 60 Hz bass note, but not onto faint noise around zero");

    var pulse = new WaveformPulse();
    var burst = new float[WaveformSpectrum.Size];
    for (var i = 0; i < 32; i++) burst[i] = i % 2 == 0 ? 0.4f : -0.4f;
    pulse.Update(burst, 2, 1 / 60.0);
    var firstBar = pulse.Levels[0];
    var quietBar = pulse.Levels[10];
    pulse.Update(new float[WaveformSpectrum.Size], 2, 0.1);
    Check(Near(firstBar, 0.8, 1e-5) && quietBar == 0 && Near(pulse.Levels[0], 0.55, 1e-5),
          "Pulse bars show each slice's loudest sample with gain, then fall smoothly");

    var spectrumLook = WaveformPalette.For(WaveformLook.Spectrum);
    var turnedOnce = spectrumLook.Rotate(360);
    var turnedRed = new WaveformPalette(new WaveformRgb(1, 0, 0), new WaveformRgb(1, 0, 0), new WaveformRgb(1, 0, 0)).Rotate(120).Low;
    Check(Near(turnedOnce.Low.R, spectrumLook.Low.R, 1e-4) && Near(turnedOnce.High.B, spectrumLook.High.B, 1e-4) &&
          Near(spectrumLook.Rotate(0).Mid.G, spectrumLook.Mid.G, 1e-5) && turnedRed.B > turnedRed.R && turnedRed.B > turnedRed.G &&
          WaveformSettings.CycleDegreesPerSecond(WaveformColourCycle.Off) == 0 &&
          WaveformSettings.CycleDegreesPerSecond(WaveformColourCycle.Fast) > WaveformSettings.CycleDegreesPerSecond(WaveformColourCycle.Slow),
          "Colour cycle turns a look around the colour wheel (red turns blue at 120 degrees) and back at 360");

    var beat = new WaveformBeat();
    for (var i = 0; i < 400; i++) beat.Add(0.05f, WaveformAnalyzer.BlockSeconds);
    var steadyBeats = beat.Beats;
    beat.Add(0.4f, WaveformAnalyzer.BlockSeconds);
    var kicked = beat.Flash;
    for (var i = 0; i < 10; i++) beat.Add(0.4f, WaveformAnalyzer.BlockSeconds);
    var heldOff = beat.Beats;
    for (var i = 0; i < 60; i++) beat.Add(0.05f, WaveformAnalyzer.BlockSeconds);
    Check(steadyBeats == 0 && kicked == 1 && heldOff == steadyBeats + 1 && beat.Flash < 0.05f,
          "A bass kick well above its average flashes once, then fades");

    // A loud four-on-the-floor track: 100 ms kicks twice a second over a steady bed.
    var drive = new WaveformDrive();
    float highest = 0, lowest = 1;
    for (var i = 0; i < 2000; i++)
    {
        var kick = i % 100 < 20;
        drive.Add(new WaveformColumn(0.9f, kick ? 0.5f : 0.3f, kick ? 0.6f : 0.08f, 0.1f, 0.05f), WaveformAnalyzer.BlockSeconds);
        if (i >= 1600 && i % 100 == 19) highest = Math.Max(highest, drive.Bass);
        if (i >= 1600 && i % 100 == 95) lowest = Math.Min(lowest, drive.Bass);
    }
    var silentDrive = new WaveformDrive();
    for (var i = 0; i < 200; i++) silentDrive.Add(default, WaveformAnalyzer.BlockSeconds);
    Check(highest > 0.85f && lowest < 0.2f && drive.Mid is > 0.3f and < 0.6f && silentDrive.Bass == 0 && silentDrive.Energy == 0,
          "The drive signal swings from near 0 between kicks to near 1 on them, even in a loud track; a steady sound sits near the middle; silence stays still");

    var everyView = WaveformSettings.Default with { CycleSeconds = 30 };
    var chosen = WaveformSettings.Default with { CycleViews = [WaveformView.Fire, WaveformView.CrtScope], CycleSeconds = 10 };
    Check(everyView.NextInCycle(WaveformView.Scroll) == WaveformView.CentreOut &&
          everyView.NextInCycle(Enum.GetValues<WaveformView>()[^1]) == WaveformView.Scroll &&
          chosen.NextInCycle(WaveformView.Fire) == WaveformView.CrtScope && chosen.NextInCycle(WaveformView.CrtScope) == WaveformView.Fire &&
          chosen.NextInCycle(WaveformView.Scroll) == WaveformView.Fire,
          "Cycle mode moves through the chosen views in order (all views when none are chosen)");

    var quietScope = new float[4096];
    var bandsLow = new float[4096];
    var bandsMid = new float[4096];
    var bandsHigh = new float[4096];
    var mixed = Sine(100, 0.4, 48000, 1, 0.1).Zip(Sine(9000, 0.4, 48000, 1, 0.1), (a, b) => a + b).Take(4096).ToArray();
    WaveformScope.SplitBands(mixed, 48000, bandsLow, bandsMid, bandsHigh);
    static double Rms(float[] values) => Math.Sqrt(values.Skip(2048).Average(v => v * v));
    Check(Near(Rms(bandsLow), 0.4 / Math.Sqrt(2), 0.05) && Near(Rms(bandsHigh), 0.4 / Math.Sqrt(2), 0.06) && Rms(bandsMid) < 0.12 &&
          WaveformScope.FindStart(quietScope, 1024, 1) == 4096 - 1024,
          "Band scopes split bass and highs apart; with no crossing the scope shows the newest window");

    Check(WaveformSettings.Normalize(null).SameAs(WaveformSettings.Default) &&
          WaveformSettings.Normalize(new WaveformSettings("  ", (WaveformView)99, (WaveformLook)99, double.NaN, false, (WaveformColourCycle)9,
              [(WaveformView)(-1), WaveformView.Fire, WaveformView.Fire], -5))
              .SameAs(WaveformSettings.Default with { AutoLevel = false, CycleViews = [WaveformView.Fire] }) &&
          WaveformSettings.Normalize(WaveformSettings.Default with { Speed = 5000 }).Speed == WaveformSettings.MaximumSpeed &&
          WaveformSettings.Normalize(WaveformSettings.Default with { Speed = 1 }).Speed == WaveformSettings.MinimumSpeed &&
          WaveformSettings.Normalize(WaveformSettings.Default with { Speed = 164 }).Speed == 160 &&
          WaveformSettings.Normalize(WaveformSettings.Default with { CycleSeconds = 45 }).CycleSeconds == 30 &&
          Enum.GetValues<WaveformView>().Count(WaveformSettings.Scrolls) == 4,
          "Waveform settings recover from bad values; speed is 30 to 600 in steps of 10; cycle times snap to the offered ones");
    store = new SettingsStore();
    var waveformChoice = new WaveformSettings(@"{0.0.1.00000000}.{abc}", WaveformView.CrtScope, WaveformLook.Vapor, 320, false,
        WaveformColourCycle.Slow, [WaveformView.Fractal, WaveformView.Tunnel], 60);
    await store.SaveAsync(weatherSaved with { Waveform = waveformChoice });
    var waveformSaved = await new SettingsStore().LoadAsync();
    Check(waveformChoice.SameAs(waveformSaved.Waveform) && WaveformSettings.Default.SameAs(weatherSaved.Waveform),
          "Waveform source, view, look, colour cycle, speed, auto level and cycle set survive restart; older settings get the defaults");
    Console.WriteLine($"{passed} checks passed.");
}
finally
{
    Environment.SetEnvironmentVariable("EDGEDOCK_DATA_DIR", previousRoot);
    // This is the unique directory created above, never the user's app profile.
    Directory.Delete(root, recursive: true);
}
