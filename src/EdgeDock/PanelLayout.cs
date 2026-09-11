namespace EdgeDock;

internal static class PanelLayout
{
    internal const double MinimumWidgetWidth = 240;
    internal const double MaximumWidgetWidth = 440;
    internal const double MinimumWebPanelWidth = 240;
    internal const double MinimumWebSplitRatio = 0.1;
    internal const double MaximumWebSplitRatio = 0.9;

    internal static double NormalizeWebSplitRatio(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, MinimumWebSplitRatio, MaximumWebSplitRatio) : 0.5;

    internal static double EffectiveWebSplitRatio(double preferredRatio, double availableWidth)
    {
        var preferred = NormalizeWebSplitRatio(preferredRatio);
        if (!double.IsFinite(availableWidth) || availableWidth <= 0) return preferred;

        var minimumRatio = Math.Min(0.5, MinimumWebPanelWidth / availableWidth);
        return Math.Clamp(preferred, minimumRatio, 1 - minimumRatio);
    }

    internal static double ResizeSharedWidgetWidth(
        double startingWidth,
        double dividerDelta,
        int visibleWidgetCount,
        MediaPanelPlacement placement)
    {
        if (!double.IsFinite(startingWidth)) startingWidth = 340;
        if (!double.IsFinite(dividerDelta)) dividerDelta = 0;
        var count = Math.Max(1, visibleWidgetCount);
        var direction = placement == MediaPanelPlacement.Left ? 1 : -1;
        return Math.Clamp(startingWidth + direction * dividerDelta / count,
            MinimumWidgetWidth, MaximumWidgetWidth);
    }

    internal static double EffectiveWidgetGroupWidth(
        double availableWidth,
        double preferredWidgetWidth,
        int visibleWidgetCount,
        int visibleWebPanelCount)
    {
        if (!double.IsFinite(availableWidth) || availableWidth <= 0) return 0;
        var count = Math.Max(1, visibleWidgetCount);
        var preferred = Math.Clamp(
            double.IsFinite(preferredWidgetWidth) ? preferredWidgetWidth : 340,
            MinimumWidgetWidth, MaximumWidgetWidth) * count + 10 * (count - 1);
        var widgetMinimum = MinimumWidgetWidth * count + 10 * (count - 1);
        var webMinimum = MinimumWebPanelWidth * Math.Max(1, visibleWebPanelCount) +
                         10 * Math.Max(0, visibleWebPanelCount - 1);
        var contentWidth = Math.Max(0, availableWidth - 10); // web/widget divider

        if (contentWidth >= preferred + webMinimum) return preferred;
        if (contentWidth >= widgetMinimum + webMinimum) return contentWidth - webMinimum;

        var totalMinimum = widgetMinimum + webMinimum;
        return totalMinimum <= 0 ? 0 : contentWidth * widgetMinimum / totalMinimum;
    }
}
