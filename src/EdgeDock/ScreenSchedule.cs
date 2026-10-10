namespace EdgeDock;

/// <summary>Night dimming and idle blanking for the screen EdgeDock is on.</summary>
internal sealed record ScreenScheduleSettings(
    bool DimAtNight = false,
    int DayBrightness = 80,
    int NightBrightness = 20,
    TimeSpan NightStart = default,
    TimeSpan NightEnd = default,
    int BlankAfterMinutes = 0)
{
    public static ScreenScheduleSettings Default { get; } = new(NightStart: new TimeSpan(22, 30, 0), NightEnd: new TimeSpan(7, 0, 0));
}

/// <summary>What the screen should look like now.</summary>
internal sealed record ScreenState(int? Brightness, bool Blank);

/// <summary>Schedule rules, kept free of Windows types for testing.</summary>
internal static class ScreenSchedule
{
    public static readonly int[] BlankChoices = [0, 5, 10, 15, 30, 60];

    public static ScreenScheduleSettings Normalize(ScreenScheduleSettings? value)
    {
        var source = value ?? ScreenScheduleSettings.Default;
        static TimeSpan TimeOfDay(TimeSpan time, TimeSpan fallback) =>
            time >= TimeSpan.Zero && time < TimeSpan.FromDays(1) ? new TimeSpan(time.Hours, time.Minutes, 0) : fallback;
        return source with
        {
            DayBrightness = Math.Clamp(source.DayBrightness, 0, 100),
            NightBrightness = Math.Clamp(source.NightBrightness, 0, 100),
            NightStart = TimeOfDay(source.NightStart, ScreenScheduleSettings.Default.NightStart),
            NightEnd = TimeOfDay(source.NightEnd, ScreenScheduleSettings.Default.NightEnd),
            BlankAfterMinutes = BlankChoices.Contains(source.BlankAfterMinutes) ? source.BlankAfterMinutes : 0
        };
    }

    /// <summary>True inside the night window, which may run past midnight (22:30 to 07:00).</summary>
    public static bool IsNight(TimeSpan timeOfDay, TimeSpan start, TimeSpan end) =>
        start == end ? false
        : start < end ? timeOfDay >= start && timeOfDay < end
        : timeOfDay >= start || timeOfDay < end;

    /// <summary>
    /// The brightness EdgeDock should set (null: leave the monitor alone) and whether the
    /// screen should be blank, from the time of day and how long since the last input.
    /// </summary>
    public static ScreenState Evaluate(ScreenScheduleSettings settings, DateTime now, TimeSpan idle)
    {
        var blank = settings.BlankAfterMinutes > 0 && idle >= TimeSpan.FromMinutes(settings.BlankAfterMinutes);
        int? brightness = settings.DimAtNight
            ? IsNight(now.TimeOfDay, settings.NightStart, settings.NightEnd) ? settings.NightBrightness : settings.DayBrightness
            : null;
        return new ScreenState(blank ? 0 : brightness, blank);
    }
}
