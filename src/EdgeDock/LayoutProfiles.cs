namespace EdgeDock;

/// <summary>The parts of the dashboard a layout profile restores: panels, widgets and web cards.</summary>
internal sealed record ProfileLayout(
    MediaPanelPlacement Placement,
    MediaPanelPlacement LastVisiblePlacement,
    double PanelWidth,
    bool IsWebVisible,
    IReadOnlyList<WidgetSlotSettings> WidgetSlots,
    int WebPanelCount,
    IReadOnlyList<string> WebPanelCardIds,
    double WebSplitRatio);

/// <summary>
/// A layout switched on because its program is running, and the layout to return to.
/// Saved with the settings so a restart mid-game still returns to the right layout.
/// </summary>
internal sealed record AutoLayoutSwitch(string ProfileId, ProfileLayout Before);

/// <summary>A named layout; optionally used automatically while a given app is running.</summary>
internal sealed record LayoutProfile(string Id, string Name, string? WhenRunning, ProfileLayout Layout);

/// <summary>Capturing, applying and choosing layout profiles; free of WinUI for testing.</summary>
internal static class LayoutProfiles
{
    public const int Maximum = 12;

    public static ProfileLayout Capture(EdgeDockSettings settings) => new(
        settings.MediaPanelPlacement,
        settings.LastVisibleMediaPanelPlacement,
        settings.MediaPanelWidth,
        settings.IsWebVisible,
        settings.WidgetSlots,
        settings.WebPanelCount,
        settings.WebPanelCardIds ?? [],
        settings.WebSplitRatio);

    /// <summary>
    /// The settings with a profile's layout applied, then normalised, so a profile naming a
    /// web card that was since deleted still produces a usable dashboard.
    /// </summary>
    public static EdgeDockSettings Apply(EdgeDockSettings settings, ProfileLayout layout)
    {
        // Profiles are set aside while normalising: normalising settings normalises their
        // profiles, which applies layouts, which would recurse.
        var applied = SettingsStore.Normalize(settings with
        {
            MediaPanelPlacement = layout.Placement,
            LastVisibleMediaPanelPlacement = layout.LastVisiblePlacement,
            MediaPanelWidth = layout.PanelWidth,
            IsWebVisible = layout.IsWebVisible,
            WidgetSlots = layout.WidgetSlots,
            WebPanelCount = layout.WebPanelCount,
            WebPanelCardIds = layout.WebPanelCardIds,
            WebSplitRatio = layout.WebSplitRatio,
            Profiles = []
        });
        return applied with { Profiles = settings.Profiles };
    }

    /// <summary>
    /// A process name as Task Manager's Details tab shows it, without ".exe"; anything else
    /// (paths, arguments) is rejected so a trigger only ever names one program.
    /// </summary>
    public static string? NormalizeProcessName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var name = value.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Length is > 0 and <= 64 && name.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or ' ')
            ? name
            : null;
    }

    public static IReadOnlyList<LayoutProfile> Normalize(IReadOnlyList<LayoutProfile>? source, EdgeDockSettings baseline)
    {
        var profiles = new List<LayoutProfile>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in (source ?? []).Where(profile => profile is not null && profile.Layout is not null))
        {
            if (profiles.Count == Maximum) break;
            if (string.IsNullOrWhiteSpace(profile.Name)) continue;
            var baseId = string.IsNullOrWhiteSpace(profile.Id) ? $"layout-{profiles.Count + 1}" : profile.Id.Trim();
            var id = baseId;
            for (var suffix = 2; !ids.Add(id); suffix++) id = $"{baseId}-{suffix}";
            var layout = Capture(Apply(baseline, profile.Layout));
            profiles.Add(new LayoutProfile(id, profile.Name.Trim(), NormalizeProcessName(profile.WhenRunning), layout));
        }
        return profiles;
    }

    /// <summary>The first profile whose app is running, or null.</summary>
    public static LayoutProfile? Automatic(IReadOnlyList<LayoutProfile> profiles, IReadOnlySet<string> runningProcesses) =>
        profiles.FirstOrDefault(profile => profile.WhenRunning is { } app && runningProcesses.Contains(app));
}
