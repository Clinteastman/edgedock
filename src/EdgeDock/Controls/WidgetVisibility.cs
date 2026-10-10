using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace EdgeDock.Controls;

/// <summary>
/// Whether a widget is really on screen, so live widgets only do work while seen. Loaded is
/// not enough: hiding widgets collapses their host, a minimised window keeps everything
/// loaded, and Widget view keeps scrolled-away cards loaded.
/// </summary>
internal static class WidgetVisibility
{
    public static bool IsOnScreen(FrameworkElement element)
    {
        if (element.XamlRoot is not { IsHostVisible: true } root) return false;
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: Visibility.Collapsed }) return false;
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        try
        {
            var bounds = element.TransformToVisual(null).TransformBounds(
                new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            var window = new Windows.Foundation.Rect(0, 0, root.Size.Width, root.Size.Height);
            bounds.Intersect(window);
            return !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0;
        }
        catch (ArgumentException)
        {
            // Not connected to the window's visual tree at the moment.
            return false;
        }
    }
}
