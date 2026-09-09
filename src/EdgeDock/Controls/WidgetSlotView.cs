using EdgeDock.Widgets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EdgeDock.Controls;

internal sealed class WidgetSlotView : Grid
{
    private readonly HeaderNavigationFlipView _pages = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly WidgetRegistry _registry;
    private readonly MediaSessionService _media;
    private readonly string[] _ids;
    private readonly bool _showArtwork;
    private readonly List<ContentControl> _containers = [];
    private bool _ready;

    public WidgetSlotView(WidgetRegistry registry, MediaSessionService media, WidgetSlotSettings settings, bool showArtwork)
    {
        _registry = registry;
        _media = media;
        _showArtwork = showArtwork;
        _ids = settings.EnabledWidgetIds.ToArray();
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        CornerRadius = new CornerRadius(14);
        AutomationProperties.SetName(_pages, "Widget pages");
        Children.Add(_pages);
        foreach (var id in _ids)
        {
            var container = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            _containers.Add(container);
            _pages.Items.Add(container);
        }
        if (_ids.Length == 0)
        {
            _pages.Items.Add(new TextBlock { Text = "Open Settings to add widgets to this panel.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20) });
            return;
        }
        _pages.SelectedIndex = Math.Max(0, Array.FindIndex(_ids, id => string.Equals(id, settings.SelectedWidgetId, StringComparison.OrdinalIgnoreCase)));
        _pages.SelectionChanged += (_, _) => ActivateSelection();
        ActivateSelection();
        _ready = true;
    }

    public event EventHandler<string>? SelectionChanged;

    public string? SelectedWidgetId => _pages.SelectedIndex >= 0 && _pages.SelectedIndex < _ids.Length
        ? _ids[_pages.SelectedIndex]
        : null;

    public bool SelectWidget(string id)
    {
        var index = Array.FindIndex(_ids, candidate => string.Equals(candidate, id, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        _pages.SelectedIndex = index;
        return true;
    }

    private void ActivateSelection()
    {
        var index = _pages.SelectedIndex;
        if (index < 0 || index >= _ids.Length) return;
        for (var i = 0; i < _containers.Count; i++)
            if (i != index) _containers[i].Content = null;
        var id = _ids[index];
        if (_registry.TryGet(id, out var descriptor) && descriptor is not null)
        {
            if (_containers[index].Content is null)
            {
                var control = descriptor.Create();
                if (control is MediaWidget mediaWidget)
                {
                    mediaWidget.ShowArtwork = _showArtwork;
                    mediaWidget.Bind(_media);
                }
                _containers[index].Content = control;
            }
        }
        else
        {
            _containers[index].Content = new TextBlock { Text = $"{id} is not installed. Open Settings to choose another widget.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20) };
        }
        if (_ready) SelectionChanged?.Invoke(this, id);
    }

    // Keep native touch and keyboard paging while suppressing the template's overlay arrows.
    private sealed class HeaderNavigationFlipView : FlipView
    {
        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            foreach (var name in new[] { "PreviousButtonHorizontal", "NextButtonHorizontal", "PreviousButtonVertical", "NextButtonVertical" })
            {
                if (GetTemplateChild(name) is not Button button) continue;
                button.Visibility = Visibility.Collapsed;
                button.Width = button.Height = button.MinWidth = button.MinHeight = 0;
                button.IsHitTestVisible = false;
                button.IsTabStop = false;
                AutomationProperties.SetAccessibilityView(button, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            }
        }
    }
}
