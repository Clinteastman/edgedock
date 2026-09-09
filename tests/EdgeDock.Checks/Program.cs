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
    Console.WriteLine($"{passed} checks passed.");
}
finally
{
    Environment.SetEnvironmentVariable("EDGEDOCK_DATA_DIR", previousRoot);
    // This is the unique directory created above, never the user's app profile.
    Directory.Delete(root, recursive: true);
}
