using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EdgeDock.Controls;

/// <summary>
/// Touch tiles that open saved apps, files, folders and web pages, like double-clicking
/// them in Explorer. Items are configured in Settings; this view only reads them.
/// </summary>
internal sealed class LauncherWidget : UserControl
{
    private const double TileMinimumWidth = 104;
    private readonly Func<IReadOnlyList<LauncherItem>> _items;
    private readonly Grid _tiles = new() { ColumnSpacing = 10, RowSpacing = 10 };
    private readonly TextBlock _status = new()
    {
        FontSize = 14,
        TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 194, 199)),
        Visibility = Visibility.Collapsed
    };
    private readonly List<Button> _buttons = [];
    private int _columns;

    public LauncherWidget(Func<IReadOnlyList<LauncherItem>> items)
    {
        _items = items;
        var layout = new Grid { RowSpacing = 12 };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(new TextBlock { Text = "Launcher", FontSize = 20, FontWeight = FontWeights.SemiBold });
        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _tiles
        };
        Grid.SetRow(scroller, 1);
        layout.Children.Add(scroller);
        Grid.SetRow(_status, 2);
        layout.Children.Add(_status);

        Content = new Border
        {
            Padding = new Thickness(20, 16, 20, 16),
            CornerRadius = new CornerRadius(14),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            Child = layout
        };
        AutomationProperties.SetName(this, "Launcher");
        Loaded += (_, _) => Build();
        _tiles.SizeChanged += (_, args) =>
        {
            if (ColumnsFor(args.NewSize.Width) != _columns) Arrange();
        };
    }

    private static int ColumnsFor(double width) => Math.Max(1, (int)((width + 10) / (TileMinimumWidth + 10)));

    private void Build()
    {
        _buttons.Clear();
        var items = _items();
        if (items.Count == 0)
        {
            _tiles.Children.Clear();
            _tiles.ColumnDefinitions.Clear();
            _tiles.RowDefinitions.Clear();
            _tiles.Children.Add(new TextBlock
            {
                Text = "Add apps, files, folders or web pages in Settings, under Launcher. Each becomes a tile here.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 15,
                Foreground = (Brush)Application.Current.Resources["MutedTextBrush"]
            });
            return;
        }
        foreach (var item in items) _buttons.Add(CreateTile(item));
        Arrange();
    }

    /// <summary>Lays tiles out in as many equal columns as fit the panel.</summary>
    private void Arrange()
    {
        if (_buttons.Count == 0) return;
        _columns = ColumnsFor(_tiles.ActualWidth > 0 ? _tiles.ActualWidth : 2 * TileMinimumWidth + 10);
        _tiles.Children.Clear();
        _tiles.ColumnDefinitions.Clear();
        _tiles.RowDefinitions.Clear();
        for (var column = 0; column < _columns; column++)
            _tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var index = 0; index < _buttons.Count; index++)
        {
            if (index % _columns == 0) _tiles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(_buttons[index], index / _columns);
            Grid.SetColumn(_buttons[index], index % _columns);
            _tiles.Children.Add(_buttons[index]);
        }
    }

    private Button CreateTile(LauncherItem item)
    {
        var kind = LauncherTargets.Classify(item.Target);
        UIElement icon = kind switch
        {
            LauncherTargetKind.File or LauncherTargetKind.Folder when ShellIcons.Load(item.Target, 48) is { } image =>
                new Image { Source = image, Width = 40, Height = 40 },
            LauncherTargetKind.WebPage => Glyph(""),    // Globe
            LauncherTargetKind.Folder => Glyph(""),     // Folder
            LauncherTargetKind.Invalid => Glyph(""),    // Error
            _ => Glyph("")                              // Document
        };
        var content = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(icon);
        content.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontSize = 14,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var button = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            MinHeight = 104,
            Padding = new Thickness(8, 12, 8, 10)
        };
        AutomationProperties.SetName(button, $"Open {item.Name}");
        ToolTipService.SetToolTip(button, kind == LauncherTargetKind.Invalid ? $"{item.Target} was not found" : item.Target);
        button.Click += (_, _) => Launch(item);
        return button;
    }

    private static FontIcon Glyph(string glyph) => new()
    {
        Glyph = glyph,
        FontSize = 34,
        Foreground = (Brush)Application.Current.Resources["AccentBrush"]
    };

    private void Launch(LauncherItem item)
    {
        _status.Visibility = Visibility.Collapsed;
        // Re-check at the moment of use: a drive may have gone, or the file moved.
        if (LauncherTargets.Classify(item.Target) == LauncherTargetKind.Invalid)
        {
            ShowStatus($"{item.Name} could not be found. Check it in Settings, under Launcher.");
            return;
        }
        try
        {
            // EdgeDock just received the tap, so it may pass the foreground to what it opens.
            AllowSetForegroundWindow(AnyProcess);
            Process.Start(new ProcessStartInfo(item.Target.Trim()) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            ShowStatus($"Windows could not open {item.Name}.");
        }
    }

    private void ShowStatus(string message)
    {
        _status.Text = message;
        _status.Visibility = Visibility.Visible;
    }

    private const int AnyProcess = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
