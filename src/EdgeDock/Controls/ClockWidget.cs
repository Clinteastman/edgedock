using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EdgeDock.Controls;

/// <summary>What the clock widget needs from Settings.</summary>
internal sealed record ClockOptions(WeatherLocation? Weather, bool Fahrenheit);

/// <summary>
/// Large local time and date, with optional weather for a place chosen in Settings: now,
/// today's range and the next six hours. Ticks only while visible; weather refreshes every
/// 15 minutes and keeps its last reading when offline.
/// </summary>
internal sealed class ClockWidget : UserControl
{
    private static readonly Brush MutedText = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 200, 194, 212));

    private readonly Func<ClockOptions> _options;
    private readonly TextBlock _time = new() { FontSize = 64, FontWeight = FontWeights.SemiBold, TextLineBounds = TextLineBounds.Tight };
    private readonly TextBlock _date = new() { FontSize = 18, Foreground = MutedText };
    private readonly StackPanel _weather = new() { Spacing = 10, Visibility = Visibility.Collapsed };
    private readonly TextBlock _weatherSymbol = new() { FontSize = 40, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _weatherNow = new() { FontSize = 32, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _weatherText = new() { FontSize = 15, Foreground = MutedText, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _place = new() { FontSize = 13, Foreground = MutedText, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Grid _hours = new() { ColumnSpacing = 4 };
    private readonly TextBlock _hint = new()
    {
        Text = "Add a place in Settings, under Clock and weather, to see the weather here.",
        FontSize = 14,
        Foreground = MutedText,
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed
    };
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
    // When to ask for weather next (UTC): 15 minutes after the forecast was fetched, or
    // sooner after a failure.
    private DateTime _nextWeatherAt = DateTime.MinValue;
    private bool _loadingWeather;
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(2);

    public ClockWidget(Func<ClockOptions> options)
    {
        _options = options;
        var now = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        now.Children.Add(_weatherSymbol);
        var nowText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        nowText.Children.Add(_weatherNow);
        nowText.Children.Add(_weatherText);
        now.Children.Add(nowText);
        _weather.Children.Add(now);
        _weather.Children.Add(_hours);
        _weather.Children.Add(_place);

        var content = new StackPanel { Spacing = 18 };
        var clock = new StackPanel { Spacing = 6 };
        clock.Children.Add(_time);
        clock.Children.Add(_date);
        content.Children.Add(clock);
        content.Children.Add(_weather);
        content.Children.Add(_hint);

        Content = new Border
        {
            Padding = new Thickness(20, 16, 20, 16),
            CornerRadius = new CornerRadius(14),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = content
            }
        };
        AutomationProperties.SetName(this, "Clock and weather");
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => _timer?.Stop();
    }

    private void Start()
    {
        if (_timer is null)
        {
            _timer = DispatcherQueue.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += (_, _) => Tick();
        }
        _timer.Start();
        Tick();
    }

    private void Tick()
    {
        if (!WidgetVisibility.IsOnScreen(this)) return;
        var now = DateTime.Now;
        // Short time follows the Windows 12/24-hour setting.
        _time.Text = now.ToString("t");
        _date.Text = now.ToString("dddd d MMMM");
        AutomationProperties.SetHelpText(this, $"{_time.Text}, {_date.Text}");
        if (DateTime.UtcNow >= _nextWeatherAt) _ = RefreshWeatherAsync();
    }

    private async Task RefreshWeatherAsync()
    {
        if (_loadingWeather) return;
        var options = _options();
        if (options.Weather is not { } location)
        {
            _weather.Visibility = Visibility.Collapsed;
            _hint.Visibility = Visibility.Visible;
            _nextWeatherAt = DateTime.UtcNow + WeatherService.RefreshInterval;
            return;
        }
        _hint.Visibility = Visibility.Collapsed;
        _loadingWeather = true;
        _nextWeatherAt = DateTime.UtcNow + RetryAfterFailure;
        try
        {
            var (snapshot, fetchedAt) = await WeatherService.GetAsync(location, options.Fahrenheit);
            if (snapshot is null)
            {
                // Keep showing an earlier reading if there was one; otherwise say why it is empty.
                if (_weather.Visibility != Visibility.Visible)
                {
                    _hint.Text = "Weather is unavailable right now. EdgeDock will try again shortly.";
                    _hint.Visibility = Visibility.Visible;
                }
                return;
            }
            // A cached forecast may already be minutes old, so refresh relative to when it was
            // fetched; but when offline the service returns the last one, so never retry sooner
            // than the failure back-off.
            var dueFromFetch = fetchedAt + WeatherService.RefreshInterval;
            var earliest = DateTime.UtcNow + RetryAfterFailure;
            _nextWeatherAt = dueFromFetch > earliest ? dueFromFetch : earliest;
            Show(snapshot, location, fetchedAt);
        }
        finally { _loadingWeather = false; }
    }

    private void Show(WeatherSnapshot snapshot, WeatherLocation location, DateTime fetchedAtUtc)
    {
        var (text, symbol) = WeatherForecast.Describe(snapshot.Code, snapshot.IsDay);
        _weatherSymbol.Text = symbol;
        _weatherNow.Text = WeatherForecast.Degrees(snapshot.Temperature);
        _weatherText.Text = $"{text} · H {WeatherForecast.Degrees(snapshot.High)}  L {WeatherForecast.Degrees(snapshot.Low)}";
        _place.Text = $"{location.Name} · updated {fetchedAtUtc.ToLocalTime():t}";
        AutomationProperties.SetName(_weather, $"Weather: {text}, {_weatherNow.Text}, high {WeatherForecast.Degrees(snapshot.High)}, low {WeatherForecast.Degrees(snapshot.Low)}");

        _hours.Children.Clear();
        _hours.ColumnDefinitions.Clear();
        for (var index = 0; index < snapshot.NextHours.Count; index++)
        {
            var hour = snapshot.NextHours[index];
            _hours.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var cell = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
            cell.Children.Add(new TextBlock { Text = hour.Time.ToString("HH"), FontSize = 13, Foreground = MutedText, HorizontalAlignment = HorizontalAlignment.Center });
            cell.Children.Add(new TextBlock { Text = WeatherForecast.Describe(hour.Code, hour.Time.Hour is >= 7 and < 19).Symbol, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center });
            cell.Children.Add(new TextBlock { Text = WeatherForecast.Degrees(hour.Temperature), FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center });
            Grid.SetColumn(cell, index);
            _hours.Children.Add(cell);
        }
        _weather.Visibility = Visibility.Visible;
        _hint.Visibility = Visibility.Collapsed;
    }
}
