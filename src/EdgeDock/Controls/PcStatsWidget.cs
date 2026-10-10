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
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
    private SystemStatsService? _stats;
    private bool _sampleRunning;
    // Starts true so the first visible tick primes fresh baselines rather than showing a gap.
    private bool _needsPrime = true;

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
        if (_timer is not null) return;
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => SampleIfOnScreen();
        _timer.Start();
        SampleIfOnScreen();
    }

    private void StopSampling()
    {
        _timer?.Stop();
        _timer = null;
        // A sample still running on the thread pool disposes the service when it finishes.
        if (!_sampleRunning) DisposeStats();
    }

    /// <summary>Checks real visibility before doing any work; see <see cref="WidgetVisibility"/>.</summary>
    private void SampleIfOnScreen()
    {
        if (_sampleRunning || _timer is null) return;
        if (!IsOnScreen())
        {
            _needsPrime = true;
            return;
        }
        _sampleRunning = true;
        var stats = _stats;
        var prime = _needsPrime;
        _needsPrime = false;
        if (prime) foreach (var row in Rows) row.Clear();
        // Opening and reading performance counters can take tens of milliseconds, so both
        // happen on the thread pool. Only one sample runs at a time.
        _ = Task.Run(() =>
        {
            stats ??= new SystemStatsService();
            if (prime)
            {
                // Coming back into view: reset baselines now and show the next full second.
                stats.Prime();
                return (Stats: stats, Snapshot: (SystemStatsSnapshot?)null);
            }
            return (Stats: stats, Snapshot: stats.Sample());
        }).ContinueWith(task =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _sampleRunning = false;
                if (task.Status == TaskStatus.RanToCompletion)
                {
                    _stats = task.Result.Stats;
                    if (_timer is not null && task.Result.Snapshot is { } snapshot) Show(snapshot);
                }
                if (_timer is null) DisposeStats();
            });
        }, TaskScheduler.Default);
    }

    private bool IsOnScreen() => WidgetVisibility.IsOnScreen(this);

    private void DisposeStats()
    {
        _stats?.Dispose();
        _stats = null;
    }

    private IEnumerable<StatRow> Rows => [_cpu, _memory, _gpu, _disk, _network];

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
            $"Read {RateOrUnavailable(snapshot.DiskReadBytesPerSecond)}  ·  Write {RateOrUnavailable(snapshot.DiskWriteBytesPerSecond)}");

        _network.Show(snapshot.NetworkDownBytesPerSecond, value => "↓ " + StatsMath.FormatRate(value),
            "↑ " + RateOrUnavailable(snapshot.NetworkUpBytesPerSecond), snapshot.NetworkUpBytesPerSecond);
    }

    private static string RateOrUnavailable(double? bytesPerSecond) =>
        bytesPerSecond is { } rate ? StatsMath.FormatRate(rate) : "not available";

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

        internal void Clear()
        {
            _history.Clear();
            _secondHistory?.Clear();
            Draw();
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
