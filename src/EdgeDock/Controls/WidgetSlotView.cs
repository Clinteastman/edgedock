using EdgeDock.Widgets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EdgeDock.Controls;

internal sealed class WidgetSlotView : Grid
{
    private readonly FlipView _pages = new();
    private readonly TextBlock _title = new() { FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _position = new() { VerticalAlignment = VerticalAlignment.Center };
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
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(150, 26, 24, 33));
        CornerRadius = new CornerRadius(14);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid { Margin = new Thickness(10, 8, 10, 4), ColumnSpacing = 4 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var previous = new Button { Content = new SymbolIcon(Symbol.Back), Padding = new Thickness(0), Width = 52 };
        var next = new Button { Content = new SymbolIcon(Symbol.Forward), Padding = new Thickness(0), Width = 52 };
        AutomationProperties.SetName(previous, "Previous widget");
        AutomationProperties.SetName(next, "Next widget");
        ToolTipService.SetToolTip(previous, "Previous widget");
        ToolTipService.SetToolTip(next, "Next widget");
        previous.Click += (_, _) => Move(-1);
        next.Click += (_, _) => Move(1);
        previous.IsEnabled = next.IsEnabled = _ids.Length > 1;
        var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        heading.Children.Add(_title);
        _position.FontSize = 12;
        _position.HorizontalAlignment = HorizontalAlignment.Center;
        _position.Foreground = (Brush)Application.Current.Resources["MutedTextBrush"];
        heading.Children.Add(_position);
        header.Children.Add(previous);
        Grid.SetColumn(heading, 1); header.Children.Add(heading);
        Grid.SetColumn(next, 2); header.Children.Add(next);
        Children.Add(header);
        Grid.SetRow(_pages, 1);
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
            _title.Text = "Choose widgets";
            _pages.Items.Add(new TextBlock { Text = "Open Settings to add widgets to this panel.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20) });
            return;
        }
        _pages.SelectedIndex = Math.Max(0, Array.FindIndex(_ids, id => string.Equals(id, settings.SelectedWidgetId, StringComparison.OrdinalIgnoreCase)));
        _pages.SelectionChanged += (_, _) => ActivateSelection();
        ActivateSelection();
        _ready = true;
    }

    public event EventHandler<string>? SelectionChanged;

    private void ActivateSelection()
    {
        var index = _pages.SelectedIndex;
        if (index < 0 || index >= _ids.Length) return;
        for (var i = 0; i < _containers.Count; i++)
            if (i != index) _containers[i].Content = null;
        var id = _ids[index];
        if (_registry.TryGet(id, out var descriptor) && descriptor is not null)
        {
            _title.Text = descriptor.DisplayName;
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
            _title.Text = "Unavailable";
            _containers[index].Content = new TextBlock { Text = $"{id} is not installed. Open Settings to choose another widget.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20) };
        }
        _position.Text = $"{index + 1} of {_ids.Length}";
        if (_ready) SelectionChanged?.Invoke(this, id);
    }

    private void Move(int direction)
    {
        if (_ids.Length > 1) _pages.SelectedIndex = (_pages.SelectedIndex + direction + _ids.Length) % _ids.Length;
    }
}
