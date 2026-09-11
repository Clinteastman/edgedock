using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdgeDock;

internal enum MediaPanelPlacement { Right, Left, Hidden }
internal enum BackdropMaterial { Mica, Acrylic }

internal sealed record WidgetSlotSettings(IReadOnlyList<string> EnabledWidgetIds, string? SelectedWidgetId);
internal sealed record WebCardSettings(string Id, string Name, string Url);

internal sealed record EdgeDockSettings(
    string? DashboardUrl,
    MediaPanelPlacement MediaPanelPlacement,
    MediaPanelPlacement LastVisibleMediaPanelPlacement,
    double MediaPanelWidth,
    bool ShowArtwork,
    bool IsWebVisible,
    IReadOnlyList<WidgetSlotSettings> WidgetSlots,
    BackdropMaterial Material = BackdropMaterial.Mica,
    double BackdropTransparency = 50,
    IReadOnlyList<WebCardSettings>? WebCards = null,
    int WebPanelCount = 1,
    IReadOnlyList<string>? WebPanelCardIds = null,
    double WebSplitRatio = 0.5);

internal sealed class SettingsStore
{
    private readonly string _settingsPath;
    private Dictionary<string, JsonElement>? _extensionData;

    public SettingsStore()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("EDGEDOCK_DATA_DIR");
        var root = string.IsNullOrWhiteSpace(overrideRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeDock")
            : Path.GetFullPath(overrideRoot);

        Directory.CreateDirectory(root);
        _settingsPath = Path.Combine(root, "settings.json");
        WebViewProfilePath = Path.Combine(root, "WebView2");
    }

    public string WebViewProfilePath { get; }

    public async Task<EdgeDockSettings> LoadAsync()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return Defaults();
            await using var stream = File.OpenRead(_settingsPath);
            var stored = await JsonSerializer.DeserializeAsync<StoredSettings>(stream);
            _extensionData = stored?.ExtensionData;
            var slots = stored?.WidgetSlots?
                .Where(slot => slot is not null)
                .Select(slot => new WidgetSlotSettings(slot.EnabledWidgetIds ?? [], slot.SelectedWidgetId))
                .ToArray() ?? DefaultSlots();
            var cards = stored?.WebCards is null
                ? MigrateDashboard(stored?.DashboardUrl)
                : stored.WebCards.Where(card => card is not null)
                    .Select(card => new WebCardSettings(card.Id ?? string.Empty, card.Name ?? string.Empty, card.Url ?? string.Empty))
                    .ToArray();
            return Normalize(new EdgeDockSettings(
                IsAllowedUrl(stored?.DashboardUrl, out _) ? stored!.DashboardUrl : null,
                ParsePlacement(stored?.MediaPanelPlacement, MediaPanelPlacement.Right, true),
                ParsePlacement(stored?.LastVisibleMediaPanelPlacement, MediaPanelPlacement.Right, false),
                Math.Clamp(stored?.MediaPanelWidth ?? 340, 240, 440),
                stored?.ShowArtwork ?? true,
                stored?.IsWebVisible ?? true,
                slots,
                ParseMaterial(stored?.Material), stored?.BackdropTransparency ?? 50,
                cards, stored?.WebPanelCount ?? 1, stored?.WebPanelCardIds,
                stored?.WebSplitRatio ?? 0.5));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return Defaults();
        }
    }

    public async Task SaveAsync(EdgeDockSettings settings)
    {
        settings = Normalize(settings);
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"settings-{Guid.NewGuid():N}.tmp");
        try
        {
            await using var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            var stored = new StoredSettings
            {
                DashboardUrl = settings.DashboardUrl,
                MediaPanelPlacement = settings.MediaPanelPlacement.ToString(),
                LastVisibleMediaPanelPlacement = settings.LastVisibleMediaPanelPlacement.ToString(),
                MediaPanelWidth = settings.MediaPanelWidth,
                ShowArtwork = settings.ShowArtwork,
                IsWebVisible = settings.IsWebVisible,
                Material = settings.Material.ToString(),
                BackdropTransparency = settings.BackdropTransparency,
                WebCards = settings.WebCards!.Select(card => new StoredWebCard { Id = card.Id, Name = card.Name, Url = card.Url }).ToList(),
                WebPanelCount = settings.WebPanelCount,
                WebPanelCardIds = settings.WebPanelCardIds!.ToList(),
                WebSplitRatio = settings.WebSplitRatio,
                WidgetSlots = settings.WidgetSlots.Select(slot => new StoredSlot { EnabledWidgetIds = slot.EnabledWidgetIds.ToList(), SelectedWidgetId = slot.SelectedWidgetId }).ToList(),
                ExtensionData = _extensionData
            };
            await JsonSerializer.SerializeAsync(stream, stored);
            await stream.FlushAsync();
            await stream.DisposeAsync();
            File.Move(temporaryPath, _settingsPath, true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    public static EdgeDockSettings Normalize(EdgeDockSettings value)
    {
        var slots = (value.WidgetSlots ?? []).Where(slot => slot is not null).Take(3).Select(NormalizeSlot).ToList();
        if (slots.Count == 0) slots.AddRange(DefaultSlots());
        var cards = NormalizeWebCards(value.WebCards ?? MigrateDashboard(value.DashboardUrl));
        var panelCount = cards.Count >= 2 ? Math.Clamp(value.WebPanelCount, 1, 2) : 1;
        var requestedSelections = value.WebPanelCardIds ?? [];
        // Keep both choices while only one panel is visible, so restoring the split view
        // also restores the page that was previously shown in its second panel.
        var selectionCount = cards.Count >= 2 ? 2 : 1;
        var selections = Enumerable.Range(0, selectionCount)
            .Select(index => cards.FirstOrDefault(card => string.Equals(card.Id, requestedSelections.ElementAtOrDefault(index), StringComparison.OrdinalIgnoreCase))?.Id
                ?? cards.ElementAtOrDefault(index)?.Id
                ?? cards.FirstOrDefault()?.Id
                ?? string.Empty)
            .ToArray();
        var hasWidgets = slots.Any(slot => slot.EnabledWidgetIds.Count > 0);
        var placement = value.MediaPanelPlacement;
        if (!value.IsWebVisible && !hasWidgets) slots[0] = new WidgetSlotSettings(["media"], "media");
        if (!value.IsWebVisible && placement == MediaPanelPlacement.Hidden)
            placement = value.LastVisibleMediaPanelPlacement is MediaPanelPlacement.Left or MediaPanelPlacement.Right
                ? value.LastVisibleMediaPanelPlacement : MediaPanelPlacement.Right;
        return value with
        {
            DashboardUrl = cards.FirstOrDefault()?.Url,
            MediaPanelPlacement = placement,
            LastVisibleMediaPanelPlacement = value.LastVisibleMediaPanelPlacement is MediaPanelPlacement.Left or MediaPanelPlacement.Right ? value.LastVisibleMediaPanelPlacement : MediaPanelPlacement.Right,
            MediaPanelWidth = Math.Clamp(value.MediaPanelWidth, 240, 440),
            WidgetSlots = slots,
            Material = Enum.IsDefined(value.Material) ? value.Material : BackdropMaterial.Mica,
            BackdropTransparency = double.IsFinite(value.BackdropTransparency) ? Math.Clamp(value.BackdropTransparency, 0, 100) : 50,
            WebCards = cards,
            WebPanelCount = panelCount,
            WebPanelCardIds = selections,
            WebSplitRatio = PanelLayout.NormalizeWebSplitRatio(value.WebSplitRatio)
        };
    }

    private static IReadOnlyList<WebCardSettings> NormalizeWebCards(IReadOnlyList<WebCardSettings> source)
    {
        var cards = new List<WebCardSettings>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in source.Where(card => card is not null))
        {
            if (!IsAllowedUrl(candidate.Url, out var uri) || uri is null) continue;
            var baseId = string.IsNullOrWhiteSpace(candidate.Id) ? $"card-{cards.Count + 1}" : candidate.Id.Trim();
            var id = baseId;
            for (var suffix = 2; !ids.Add(id); suffix++) id = $"{baseId}-{suffix}";
            var name = string.IsNullOrWhiteSpace(candidate.Name) ? uri.Host : candidate.Name.Trim();
            cards.Add(new WebCardSettings(id, name, uri.AbsoluteUri));
        }
        return cards;
    }

    private static IReadOnlyList<WebCardSettings> MigrateDashboard(string? dashboardUrl) =>
        IsAllowedUrl(dashboardUrl, out var uri) && uri is not null
            ? [new WebCardSettings("dashboard", "Dashboard", uri.AbsoluteUri)]
            : [];

    private static WidgetSlotSettings NormalizeSlot(WidgetSlotSettings slot)
    {
        var ids = (slot.EnabledWidgetIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new(ids, ids.FirstOrDefault(id => string.Equals(id, slot.SelectedWidgetId, StringComparison.OrdinalIgnoreCase)) ?? ids.FirstOrDefault());
    }

    public static bool IsAllowedUrl(string? value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var candidate) || candidate.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(candidate.Host) || !string.IsNullOrEmpty(candidate.UserInfo)) return false;
        uri = candidate;
        return true;
    }

    private static MediaPanelPlacement ParsePlacement(string? value, MediaPanelPlacement fallback, bool allowHidden) => Enum.TryParse<MediaPanelPlacement>(value, true, out var parsed) && Enum.IsDefined(parsed) && (allowHidden || parsed != MediaPanelPlacement.Hidden) ? parsed : fallback;
    private static BackdropMaterial ParseMaterial(string? value) => Enum.TryParse<BackdropMaterial>(value, true, out var parsed) && Enum.IsDefined(parsed) ? parsed : BackdropMaterial.Mica;
    private static WidgetSlotSettings[] DefaultSlots() => [new(["media", "audio", "pc"], "media")];
    private static EdgeDockSettings Defaults() => Normalize(new(null, MediaPanelPlacement.Right, MediaPanelPlacement.Right, 340, true, true, DefaultSlots()));

    private sealed class StoredSettings
    {
        public string? DashboardUrl { get; set; }
        public string? MediaPanelPlacement { get; set; }
        public string? LastVisibleMediaPanelPlacement { get; set; }
        public double? MediaPanelWidth { get; set; }
        public bool? ShowArtwork { get; set; }
        public bool? IsWebVisible { get; set; }
        public string? Material { get; set; }
        public double? BackdropTransparency { get; set; }
        public List<StoredWebCard>? WebCards { get; set; }
        public int? WebPanelCount { get; set; }
        public List<string>? WebPanelCardIds { get; set; }
        public double? WebSplitRatio { get; set; }
        public List<StoredSlot>? WidgetSlots { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }

    private sealed class StoredSlot
    {
        public List<string>? EnabledWidgetIds { get; set; }
        public string? SelectedWidgetId { get; set; }
    }

    private sealed class StoredWebCard
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Url { get; set; }
    }
}
