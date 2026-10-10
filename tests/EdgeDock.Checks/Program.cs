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
    Console.WriteLine($"{passed} checks passed.");
}
finally
{
    Environment.SetEnvironmentVariable("EDGEDOCK_DATA_DIR", previousRoot);
    // This is the unique directory created above, never the user's app profile.
    Directory.Delete(root, recursive: true);
}
