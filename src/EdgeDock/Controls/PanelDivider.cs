using Microsoft.UI.Xaml.Controls;

namespace EdgeDock.Controls;

internal sealed class PanelDivider : ContentControl
{
    internal PanelDivider()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch;
        VerticalContentAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch;
    }
}
