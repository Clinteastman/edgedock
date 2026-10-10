using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace EdgeDock.Controls;

/// <summary>
/// Live CPU, memory, GPU, disk and network activity with a one-minute graph per row.
/// Samples once a second on a background thread, only while the widget is on screen.
/// </summary>
internal sealed class PcStatsWidget : UserControl
{
    private const int HistoryLength = 60;
    private static readonly Windows.UI.Color Accent = Windows.UI.Color.FromArgb(255, 155, 124, 255);
    private static readonly Windows.UI.Color Muted = Windows.UI.Color.FromArgb(255, 200, 194, 212);

    private readonly StatRow _cpu = new("Processor", 100);
    private readonly StatRow _memory = new("Memory", 100);
    private readonly StatRow _gpu = new("Graphics", 100);
    private readonly StatRow _disk = new("Disk", 100);
    private readonly StatRow _network = new("Network", 0, secondLine: true);
    private CancellationTokenSource? _sampling;

    public PcStatsWidget()
    {
        var rows = new StackPanel { Spacing = 12 };
        rows.Children.Add(new TextBlock { Text = "PC activity", FontSize = 20, FontWeight = FontWeights.SemiBold });
        foreach (var row in new[] { _cpu, _memory, _gpu, _disk, _network }) rows.Children.Add(row.Root);

        Content = new Border
        {
            Padding = new Thickness(20, 16, 20, 16),
            CornerRadius = new CornerRadius(14),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = rows
            }
        };
        AutomationProperties.SetName(this, "PC activity");
        Loaded += (_, _) => StartSampling();
        Unloaded += (_, _) => StopSampling();
    }

    private void StartSampling()
    {
        if (_sampling is not null) return;
        var sampling = new CancellationTokenSource();
        _sampling = sampling;
        var dispatcher = DispatcherQueue;
        _ = Task.Run(async () =>
        {
            // Performance counters can take tens of milliseconds; keep them off the UI thread.
            using var stats = new SystemStatsService();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                do
                {
                    var snapshot = stats.Sample();
                    dispatcher.TryEnqueue(() => { if (!sampling.IsCancellationRequested) Show(snapshot); });
                }
                while (await timer.WaitForNextTickAsync(sampling.Token));
            }
            catch (OperationCanceledException) { }
        });
    }

    private void StopSampling()
    {
        _sampling?.Cancel();
        _sampling?.Dispose();
        _sampling = null;
    }

    private void Show(SystemStatsSnapshot snapshot)
    {
        _cpu.Show(snapshot.CpuPercent, value => $"{value:0}%", null);

        double? memoryPercent = snapshot.MemoryTotalBytes > 0 ? snapshot.MemoryUsedBytes * 100.0 / snapshot.MemoryTotalBytes : null;
        _memory.Show(memoryPercent, value => $"{value:0}%",
            snapshot.MemoryTotalBytes > 0
                ? $"{StatsMath.FormatBytes(snapshot.MemoryUsedBytes)} of {StatsMath.FormatBytes(snapshot.MemoryTotalBytes)}"
                : null);

        _gpu.Show(snapshot.GpuPercent, value => $"{value:0}%",
            snapshot.GpuMemoryBytes is { } gpuMemory ? $"{StatsMath.FormatBytes(gpuMemory)} video memory in use" : null);

        _disk.Show(snapshot.DiskBusyPercent, value => $"{value:0}%",
            $"Read {StatsMath.FormatRate(snapshot.DiskReadBytesPerSecond)}  ·  Write {StatsMath.FormatRate(snapshot.DiskWriteBytesPerSecond)}");

        _network.Show(snapshot.NetworkDownBytesPerSecond, value => "↓ " + StatsMath.FormatRate(value),
            "↑ " + StatsMath.FormatRate(snapshot.NetworkUpBytesPerSecond), snapshot.NetworkUpBytesPerSecond);
    }

    /// <summary>A labelled value with a one-minute sparkline (two lines for network).</summary>
    private sealed class StatRow
    {
        private readonly double _maximum;
        private readonly SampleHistory _history = new(HistoryLength);
        private readonly SampleHistory? _secondHistory;
        private readonly TextBlock _value = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly TextBlock _detail = new() { FontSize = 13, Foreground = new SolidColorBrush(Muted), TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly Canvas _graph = new() { Height = 22, Margin = new Thickness(0, 2, 0, 0) };
        private readonly Polyline _line = new() { Stroke = new SolidColorBrush(Accent), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
        private readonly Polygon _fill = new() { Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(48, 155, 124, 255)) };
        private readonly Polyline _secondLine = new() { Stroke = new SolidColorBrush(Muted), StrokeThickness = 1.5, Opacity = 0.8 };

        internal StackPanel Root { get; } = new() { Spacing = 0 };

        /// <param name="maximum">Graph ceiling; 0 scales to the busiest recent sample.</param>
        internal StatRow(string label, double maximum, bool secondLine = false)
        {
            _maximum = maximum;
            if (secondLine) _secondHistory = new SampleHistory(HistoryLength);
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock { Text = label, FontSize = 15, Foreground = new SolidColorBrush(Muted), VerticalAlignment = VerticalAlignment.Bottom });
            Grid.SetColumn(_value, 1);
            header.Children.Add(_value);
            _graph.Children.Add(_fill);
            _graph.Children.Add(_line);
            if (secondLine) _graph.Children.Add(_secondLine);
            _graph.SizeChanged += (_, _) => Draw();
            Root.Children.Add(header);
            Root.Children.Add(_detail);
            Root.Children.Add(_graph);
            AutomationProperties.SetName(Root, label);
            _value.Text = "…";
        }

        internal void Show(double? value, Func<double, string> format, string? detail, double? second = null)
        {
            _value.Text = value is { } number ? format(number) : "Not available";
            _detail.Text = detail ?? string.Empty;
            _detail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetHelpText(Root, _value.Text + (string.IsNullOrEmpty(detail) ? string.Empty : ", " + detail));
            if (value is { } sample) _history.Add(sample);
            if (second is { } secondSample) _secondHistory?.Add(secondSample);
            Draw();
        }

        private void Draw()
        {
            var width = _graph.ActualWidth;
            var height = _graph.Height;
            var samples = _history.Values;
            // Network shares one scale for both directions so the two lines compare honestly.
            var maximum = _maximum > 0 ? _maximum : Math.Max(1, Math.Max(
                samples.DefaultIfEmpty(0).Max(), _secondHistory?.Values.DefaultIfEmpty(0).Max() ?? 0));

            var points = StatsMath.Sparkline(samples, HistoryLength, width, height, maximum);
            _line.Points = ToCollection(points);
            var fill = ToCollection(points);
            if (points.Count > 0)
            {
                fill.Add(new Windows.Foundation.Point(points[^1].X, height));
                fill.Add(new Windows.Foundation.Point(points[0].X, height));
            }
            _fill.Points = fill;
            if (_secondHistory is not null)
                _secondLine.Points = ToCollection(StatsMath.Sparkline(_secondHistory.Values, HistoryLength, width, height, maximum));
        }

        private static PointCollection ToCollection(IReadOnlyList<(double X, double Y)> points)
        {
            var collection = new PointCollection();
            foreach (var (x, y) in points) collection.Add(new Windows.Foundation.Point(x, y));
            return collection;
        }
    }
}
