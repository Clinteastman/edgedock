namespace EdgeDock;

/// <summary>A connected monitor, described without any Windows handles so it can be matched in tests.</summary>
internal sealed record DisplayInfo(string DeviceId, int Width, int Height);

/// <summary>The monitor EdgeDock should return to, saved with the layout.</summary>
internal sealed record DisplayPreference(string? DeviceId, string? HardwareId, int Width, int Height);

internal static class DisplayPlacement
{
    /// <summary>
    /// Extracts the monitor model code from a device interface path such as
    /// <c>\\?\DISPLAY#CRX1234#5&amp;abc&amp;0&amp;UID4352#{e6f07b5f-...}</c>, which gives <c>CRX1234</c>.
    /// The model code survives a change of cable or port; the full path does not.
    /// </summary>
    public static string? HardwareIdFrom(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;
        var parts = deviceId.Split('#');
        return parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : null;
    }

    public static DisplayPreference? PreferenceFor(DisplayInfo? display) =>
        display is null || string.IsNullOrWhiteSpace(display.DeviceId)
            ? null
            : new DisplayPreference(display.DeviceId, HardwareIdFrom(display.DeviceId), display.Width, display.Height);

    /// <summary>
    /// Finds the saved monitor: the same device path first, then the only connected monitor of the
    /// same model, then the only monitor with the same resolution. Returns -1 rather than guessing
    /// between several equally good candidates, so EdgeDock never jumps onto the wrong screen.
    /// </summary>
    public static int FindPreferred(IReadOnlyList<DisplayInfo> displays, DisplayPreference? preference)
    {
        if (preference is null || displays.Count == 0) return -1;

        if (!string.IsNullOrWhiteSpace(preference.DeviceId))
        {
            for (var index = 0; index < displays.Count; index++)
                if (string.Equals(displays[index].DeviceId, preference.DeviceId, StringComparison.OrdinalIgnoreCase))
                    return index;
        }

        if (!string.IsNullOrWhiteSpace(preference.HardwareId))
        {
            var sameModel = Matching(displays, display =>
                string.Equals(HardwareIdFrom(display.DeviceId), preference.HardwareId, StringComparison.OrdinalIgnoreCase));
            if (sameModel.Count == 1) return sameModel[0];
            if (sameModel.Count > 1)
            {
                var sameModelAndSize = sameModel.Where(index =>
                    displays[index].Width == preference.Width && displays[index].Height == preference.Height).ToArray();
                return sameModelAndSize.Length == 1 ? sameModelAndSize[0] : -1;
            }
        }

        if (preference.Width <= 0 || preference.Height <= 0) return -1;
        var sameSize = Matching(displays, display => display.Width == preference.Width && display.Height == preference.Height);
        return sameSize.Count == 1 ? sameSize[0] : -1;
    }

    private static List<int> Matching(IReadOnlyList<DisplayInfo> displays, Func<DisplayInfo, bool> predicate)
    {
        var matches = new List<int>();
        for (var index = 0; index < displays.Count; index++)
            if (predicate(displays[index])) matches.Add(index);
        return matches;
    }
}
