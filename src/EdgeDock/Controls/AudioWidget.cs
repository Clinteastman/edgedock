using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace EdgeDock.Controls;

/// <summary>
/// PC audio: output device, master volume and mute, microphone mute, and a volume slider
/// for each app making sound. Refreshes once a second while on screen and only changes
/// Windows audio when the user moves a control.
/// </summary>
internal sealed class AudioWidget : UserControl
{
    private static readonly Brush MutedText = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 200, 194, 212));
    private static readonly Brush Warning = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 194, 199));
    private static readonly Brush MicMutedBackground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 122, 36, 52));

    private readonly ComboBox _outputs = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 52 };
    private readonly Slider _volume = NewSlider("System volume");
    private readonly TextBlock _volumeText = new() { FontSize = 16, VerticalAlignment = VerticalAlignment.Center, MinWidth = 44, TextAlignment = TextAlignment.Right };
    private readonly Button _mute = new() { MinWidth = 52, Padding = new Thickness(0) };
    private readonly FontIcon _muteIcon = new() { Glyph = "" };
    private readonly Button _microphone = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 52 };
    private readonly FontIcon _microphoneIcon = new() { Glyph = "" };
    private readonly TextBlock _microphoneText = new() { FontSize = 16 };
    private readonly StackPanel _apps = new() { Spacing = 12 };
    private readonly TextBlock _noApps = new()
    {
        Text = "No apps are playing sound right now.",
        Foreground = MutedText,
        TextWrapping = TextWrapping.Wrap
    };
    private readonly TextBlock _status = new() { Foreground = Warning, FontSize = 14, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Dictionary<string, AppRow> _appRows = [];
    private readonly Dictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
    private SystemAudioService? _audio;
    private bool _updating;
    private bool _draggingVolume;
    private bool _masterMuted;
    private bool _microphoneMuted;
    private int _tick;

    public AudioWidget()
    {
        var outputRow = new StackPanel { Spacing = 4 };
        outputRow.Children.Add(new TextBlock { Text = "Output", Foreground = MutedText, FontSize = 14 });
        outputRow.Children.Add(_outputs);
        AutomationProperties.SetName(_outputs, "Sound output device");
        _outputs.SelectionChanged += Outputs_SelectionChanged;

        _mute.Content = _muteIcon;
        _mute.Click += (_, _) => Apply(() => _audio?.SetMute(!_masterMuted));
        var volumeRow = new Grid { ColumnSpacing = 10 };
        volumeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        volumeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        volumeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        volumeRow.Children.Add(_mute);
        Grid.SetColumn(_volume, 1);
        volumeRow.Children.Add(_volume);
        Grid.SetColumn(_volumeText, 2);
        volumeRow.Children.Add(_volumeText);
        TrackDrag(_volume, dragging => _draggingVolume = dragging);
        _volume.ValueChanged += (_, args) => { if (!_updating) Apply(() => _audio?.SetVolume(args.NewValue)); };

        var microphoneContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        microphoneContent.Children.Add(_microphoneIcon);
        microphoneContent.Children.Add(_microphoneText);
        _microphone.Content = microphoneContent;
        _microphone.Click += (_, _) =>
        {
            if (_audio is null) return;
            ShowMicrophone(_audio.SetMicrophoneMute(!_microphoneMuted));
        };

        var appsHeader = new TextBlock { Text = "Apps", Foreground = MutedText, FontSize = 14 };
        var appsArea = new StackPanel { Spacing = 8 };
        appsArea.Children.Add(appsHeader);
        appsArea.Children.Add(_noApps);
        appsArea.Children.Add(_apps);

        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(new TextBlock { Text = "PC audio", FontSize = 20, FontWeight = FontWeights.SemiBold });
        content.Children.Add(outputRow);
        content.Children.Add(volumeRow);
        content.Children.Add(_microphone);
        content.Children.Add(appsArea);
        content.Children.Add(_status);

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
        AutomationProperties.SetName(this, "PC audio");
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
    }

    private static Slider NewSlider(string name)
    {
        var slider = new Slider { Minimum = 0, Maximum = 100, StepFrequency = 1, SmallChange = 1, LargeChange = 5, MinHeight = 44, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(slider, name);
        return slider;
    }

    /// <summary>Slider handles pointer input itself, so listen to handled events as well.</summary>
    private static void TrackDrag(Slider slider, Action<bool> dragging)
    {
        slider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => dragging(true)), true);
        slider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => dragging(false)), true);
        slider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => dragging(false)), true);
        slider.AddHandler(PointerCanceledEvent, new PointerEventHandler((_, _) => dragging(false)), true);
    }

    private void Start()
    {
        _audio ??= new SystemAudioService();
        if (_timer is null)
        {
            _timer = DispatcherQueue.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += (_, _) => Refresh();
        }
        _timer.Start();
        _tick = 0;
        Refresh();
    }

    private void Stop()
    {
        _timer?.Stop();
        _audio?.Dispose();
        _audio = null;
    }

    private bool IsOnScreen()
    {
        if (XamlRoot is not { IsHostVisible: true }) return false;
        for (DependencyObject? element = this; element is not null; element = VisualTreeHelper.GetParent(element))
            if (element is UIElement { Visibility: Visibility.Collapsed }) return false;
        return true;
    }

    private void Refresh()
    {
        if (_audio is null || !IsOnScreen()) return;
        if (!_draggingVolume) ShowMaster(_audio.GetSnapshot());
        // Devices change rarely; enumerate them every few seconds.
        if (_tick++ % 3 == 0) ShowOutputs(_audio.GetOutputs());
        ShowMicrophone(_audio.GetMicrophone());
        ShowApps(_audio.GetSessions());
    }

    private void Apply(Func<SystemAudioSnapshot?> change)
    {
        if (change() is { } snapshot) ShowMaster(snapshot);
    }

    private void ShowMaster(SystemAudioSnapshot snapshot)
    {
        _updating = true;
        _masterMuted = snapshot.IsMuted;
        _volume.Value = snapshot.VolumePercent;
        _volume.IsEnabled = snapshot.IsAvailable;
        _mute.IsEnabled = snapshot.IsAvailable;
        _volumeText.Text = snapshot.IsAvailable ? $"{snapshot.VolumePercent:0}%" : "--";
        _muteIcon.Glyph = snapshot.IsMuted ? "" : "";
        var label = snapshot.IsMuted ? "Unmute sound" : "Mute sound";
        AutomationProperties.SetName(_mute, label);
        ToolTipService.SetToolTip(_mute, label);
        _updating = false;
        ShowStatus(snapshot.StatusMessage);
    }

    private void ShowOutputs(IReadOnlyList<AudioDevice> devices)
    {
        _updating = true;
        var current = _outputs.Items.Cast<ComboBoxItem>().Select(item => item.Tag as string).ToArray();
        if (!current.SequenceEqual(devices.Select(device => device.Id)))
        {
            _outputs.Items.Clear();
            foreach (var device in devices) _outputs.Items.Add(new ComboBoxItem { Content = device.Name, Tag = device.Id });
        }
        _outputs.SelectedItem = _outputs.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => devices.Any(device => device.IsDefault && device.Id == (string?)item.Tag));
        _outputs.IsEnabled = devices.Count > 1;
        _updating = false;
    }

    private void Outputs_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updating || _audio is null || _outputs.SelectedItem is not ComboBoxItem { Tag: string id }) return;
        if (!_audio.SetDefaultOutput(id))
        {
            ShowStatus("Windows did not switch the output. Use Sound settings to choose it instead.");
            ShowOutputs(_audio.GetOutputs());
            return;
        }
        ShowStatus(null);
        ShowMaster(_audio.GetSnapshot());
    }

    private void ShowMicrophone(MicrophoneSnapshot microphone)
    {
        _microphoneMuted = microphone.IsMuted;
        _microphone.IsEnabled = microphone.IsAvailable;
        _microphoneIcon.Glyph = microphone.IsMuted ? "" : "";
        _microphoneText.Text = !microphone.IsAvailable ? "No microphone"
            : microphone.IsMuted ? "Microphone muted" : "Microphone on";
        // Muted is the state people need to notice, so it gets the strong colour.
        if (microphone.IsMuted) _microphone.Background = MicMutedBackground;
        else _microphone.ClearValue(Control.BackgroundProperty);
        var label = !microphone.IsAvailable ? "No microphone"
            : microphone.IsMuted ? $"Unmute {microphone.Name}" : $"Mute {microphone.Name}";
        AutomationProperties.SetName(_microphone, label);
        ToolTipService.SetToolTip(_microphone, microphone.Name);
    }

    private void ShowApps(IReadOnlyList<AudioSessionInfo> sessions)
    {
        var keys = sessions.Select(session => session.Key).ToHashSet();
        foreach (var gone in _appRows.Keys.Where(key => !keys.Contains(key)).ToArray())
        {
            _apps.Children.Remove(_appRows[gone].Root);
            _appRows.Remove(gone);
        }
        for (var index = 0; index < sessions.Count; index++)
        {
            var session = sessions[index];
            if (!_appRows.TryGetValue(session.Key, out var row))
            {
                row = new AppRow(this, session.Key);
                _appRows[session.Key] = row;
            }
            // Keep the arranged order (playing first) without rebuilding rows being touched.
            var position = _apps.Children.IndexOf(row.Root);
            if (position != index)
            {
                if (position >= 0) _apps.Children.RemoveAt(position);
                _apps.Children.Insert(Math.Min(index, _apps.Children.Count), row.Root);
            }
            row.Show(session, IconFor(session));
        }
        _noApps.Visibility = sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private ImageSource? IconFor(AudioSessionInfo session)
    {
        if (session.ExecutablePath is not { } path) return null;
        if (!_icons.TryGetValue(path, out var icon)) _icons[path] = icon = ShellIcons.Load(path, 32);
        return icon;
    }

    private void ShowStatus(string? message)
    {
        _status.Text = message ?? string.Empty;
        _status.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>One app: icon, name and level on top; volume slider and mute below.</summary>
    private sealed class AppRow
    {
        private readonly AudioWidget _owner;
        private readonly string _key;
        private readonly Image _icon = new() { Width = 20, Height = 20 };
        private readonly FontIcon _fallbackIcon = new() { Glyph = "", FontSize = 18 };
        private readonly TextBlock _name = new() { FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _level = new() { FontSize = 14, Foreground = MutedText };
        private readonly Slider _slider = NewSlider("App volume");
        private readonly Button _mute = new() { MinWidth = 44, MinHeight = 44, Padding = new Thickness(0) };
        private readonly FontIcon _muteIcon = new() { Glyph = "", FontSize = 16 };
        private bool _dragging;
        private bool _muted;
        private bool _updating;

        internal Grid Root { get; } = new() { RowSpacing = 2, ColumnSpacing = 8 };

        internal AppRow(AudioWidget owner, string key)
        {
            _owner = owner;
            _key = key;
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var iconHost = new Grid { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
            iconHost.Children.Add(_fallbackIcon);
            iconHost.Children.Add(_icon);
            Root.Children.Add(iconHost);
            Grid.SetColumn(_name, 1);
            Root.Children.Add(_name);
            Grid.SetColumn(_level, 2);
            Root.Children.Add(_level);

            _mute.Content = _muteIcon;
            Grid.SetRow(_mute, 1);
            Root.Children.Add(_mute);
            Grid.SetRow(_slider, 1);
            Grid.SetColumn(_slider, 1);
            Grid.SetColumnSpan(_slider, 2);
            Root.Children.Add(_slider);

            TrackDrag(_slider, dragging => _dragging = dragging);
            _slider.ValueChanged += (_, args) =>
            {
                if (_updating) return;
                _owner._audio?.SetSessionVolume(_key, args.NewValue);
                _level.Text = $"{args.NewValue:0}%";
            };
            _mute.Click += (_, _) =>
            {
                _owner._audio?.SetSessionMute(_key, !_muted);
                SetMuted(!_muted);
            };
        }

        internal void Show(AudioSessionInfo session, ImageSource? icon)
        {
            _name.Text = session.Name;
            _icon.Source = icon;
            _icon.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
            _fallbackIcon.Visibility = icon is null ? Visibility.Visible : Visibility.Collapsed;
            _name.Opacity = session.IsActive ? 1 : 0.7;
            AutomationProperties.SetName(_slider, $"{session.Name} volume");
            if (!_dragging)
            {
                _updating = true;
                _slider.Value = session.VolumePercent;
                _updating = false;
                _level.Text = $"{session.VolumePercent:0}%";
            }
            SetMuted(session.IsMuted);
        }

        private void SetMuted(bool muted)
        {
            _muted = muted;
            _muteIcon.Glyph = muted ? "" : "";
            var label = muted ? $"Unmute {_name.Text}" : $"Mute {_name.Text}";
            AutomationProperties.SetName(_mute, label);
            ToolTipService.SetToolTip(_mute, label);
        }
    }
}
