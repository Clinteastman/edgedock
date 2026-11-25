using System.Runtime.InteropServices;
using ComputeSharp;
using ComputeSharp.D2D1;
using ComputeSharp.D2D1.Interop;
using ComputeSharp.D2D1.WinUI;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace EdgeDock.Controls;

/// <summary>
/// Pictures the PC's sound (or a chosen input) in one of many views: waves, spectrum, CRT
/// oscilloscopes, flashy, demo-scene and crazy ones. Every pixel is worked out by a GPU shader
/// (WaveformShaders.cs); each frame the processor only reads the new sound, updates a few small
/// textures and issues a draw or two. Frames follow the compositor's own clock, so nothing spins
/// between frames, and capture and drawing both stop whenever the widget is not on screen.
/// Tap the widget for a touch bar: previous/next view and look, cycle, a grid of all views and
/// a Tweak panel of sliders saved per view (speed, reactivity, brightness, colour, trails, a
/// second view blended over the top, and camera movement).
/// </summary>
internal sealed class WaveformWidget : UserControl
{
    private static readonly Windows.UI.Color PanelColour = Windows.UI.Color.FromArgb(255, 6, 7, 12);
    private const int DataWidth = 1024;
    private const int DataRows = 8;
    private const int SpectrogramRate = 60;
    private const double FadeSeconds = 0.8;
    private const double CycleFadeSeconds = 2.5;
    private const int Trails = 6;

    private enum Renderer { Scroll, Spectrogram, Ridges, Bars, Scope, Crt, Lissajous, Ring, Field, Demo, Crazy, Borrowed, Wild, Scene, Orbs, Cosmos, Mania, Mashup }

    [Flags]
    private enum Needs { None = 0, History = 1, SpectrogramRows = 2, Spectrum = 4, Scope = 8, Trails = 16, Stereo = 32, Bands = 64, Pulse = 128 }

    private readonly Func<WaveformSettings> _source;
    private readonly Action<WaveformSettings>? _save;
    private WaveformSettings _settings;
    private readonly Grid _root = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly TextBlock _status = new()
    {
        Margin = new Thickness(18, 14, 18, 0),
        VerticalAlignment = VerticalAlignment.Top,
        TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 200, 194, 212)),
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false
    };
    private readonly Border _bar = new();
    private readonly Grid _picker = new();
    private readonly TextBlock _viewName = new();
    private readonly TextBlock _lookName = new();
    private readonly ToggleButton _cycleButton = new();
    private readonly Dictionary<Button, WaveformView> _tiles = [];
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
    private CanvasControl? _canvas;
    private WaveformCapture? _capture;
    private bool _rendering;
    private DateTime _barShownAt;

    // Per-frame state. Everything runs on the UI thread; drawing is a call or two per frame.
    private readonly Dictionary<Renderer, ICanvasImage> _effects = [];
    private D2D1ResourceTextureManager? _historyTexture, _spectrogramTexture, _dataTexture;
    private readonly WaveformLevels _levels;
    private readonly WaveformSpectrum _spectrum;
    private readonly WaveformPulse _pulse = new();
    private readonly WaveformBeat _beat = new();
    private readonly WaveformDrive _drive = new();
    private readonly float[] _recent = new float[WaveformSpectrum.Size * 2];
    private readonly float[] _recentLeft = new float[DataWidth];
    private readonly float[] _recentRight = new float[DataWidth];
    private readonly float[] _bandLow = new float[WaveformSpectrum.Size * 2];
    private readonly float[] _bandMid = new float[WaveformSpectrum.Size * 2];
    private readonly float[] _bandHigh = new float[WaveformSpectrum.Size * 2];
    private readonly float[] _scope = new float[DataWidth];
    private readonly float[][] _trails = Enumerable.Range(0, Trails).Select(_ => new float[DataWidth]).ToArray();
    private int _trailHead;
    private readonly float[] _data = new float[DataWidth * DataRows * 4];
    private readonly byte[] _spectrogramRow = new byte[WaveformSpectrum.Bands];
    private readonly List<WaveformBar> _newEntries = [];
    private readonly System.Diagnostics.Stopwatch _frameClock = new();
    private WaveformHistory? _history;
    private WaveformClock _clock = new();
    private double _scrollPosition;
    private TimeSpan _lastFrame;
    private double _frameSeconds;
    private long _received;
    private long _spectrogramRows;
    private double _spectrogramCarry;
    private bool _spectrogramLive;
    private float _historyScale;
    private double _historySpeed;
    private double _time, _hue;
    // The views' own clocks: they run at each view's Speed slider.
    private double _shaderTime, _shaderTravel;
    private double _cameraPhase, _cameraMix;
    private float _cameraYaw, _cameraPitch;

    // Tweak panel.
    private readonly Border _tweakPanel = new();
    private readonly TextBlock _tweakTitle = new();
    private readonly List<(Slider Slider, TextBlock Value, Func<double, string> Format)> _sliders = [];
    private Slider _speedSlider = null!, _reactSlider = null!, _brightSlider = null!, _hueSlider = null!, _trailSlider = null!, _blendSlider = null!;
    private readonly ToggleButton _cameraButton = new();
    private Control? _barFirst, _pickerFirst;
    private readonly List<(Grid Row, ColumnDefinition Label, ColumnDefinition Value, Slider Slider)> _sliderRows = [];
    private bool? _tweaksNarrow;
    private WaveformView _tweakView;
    private bool _loadingTweaks;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _saveTimer;

    // Trails: each frame is drawn off screen, then kept brighter of itself and the fading last one.
    private CanvasRenderTarget? _frame, _trailPrevious, _trailNext;
    private readonly ColorMatrixEffect _decay = new();
    private readonly BlendEffect _lighten = new() { Mode = BlendEffectMode.Lighten };
    private readonly Dictionary<Renderer, (ExposureEffect Bright, Transform2DEffect Move)> _finishes = [];
    private float _scopeBoost = 1;
    private WaveformView? _fadingFrom;
    private double _fadeStarted, _viewSince;
    private double _fadeLength = FadeSeconds;

    public WaveformWidget(Func<WaveformSettings> settings, Action<WaveformSettings>? save = null)
    {
        _source = settings;
        _settings = WaveformSettings.Normalize(settings());
        _save = save;
        _levels = new WaveformLevels(_settings.AutoLevel);
        _spectrum = new WaveformSpectrum(_settings.AutoLevel);

        _root.Children.Add(_status);
        BuildBar();
        BuildPicker();
        BuildTweaks();
        // Keyboard users reach the widget with Tab; Enter or Space opens the touch bar and Escape
        // closes it. Taps never move keyboard focus here, so typing elsewhere is not disturbed.
        IsTabStop = true;
        AllowFocusOnInteraction = false;
        UseSystemFocusVisuals = true;
        KeyDown += Widget_KeyDown;
        Content = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(PanelColour),
            Child = _root
        };
        AutomationProperties.SetName(this, "Waveform");
        AutomationProperties.SetHelpText(this, "A moving picture of the sound. Tap to change the view.");
        Tapped += Widget_Tapped;
        Loaded += (_, _) => Start();
        SizeChanged += (_, _) => FitTweaks();
        Unloaded += (_, _) => Stop();
    }

    // ---------- Lifetime ----------

    private void Start()
    {
        if (_timer is not null) return;
        _canvas = new CanvasControl
        {
            ClearColor = Windows.UI.Color.FromArgb(0, 0, 0, 0),
            IsTabStop = false,
            IsHitTestVisible = false // Taps go to the widget (touch bar) and swipes to the panel.
        };
        _canvas.Draw += Canvas_Draw;
        _root.Children.Insert(0, _canvas);
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    private void Stop()
    {
        _timer?.Stop();
        _timer = null;
        Pause();
        if (_canvas is not null)
        {
            _canvas.Draw -= Canvas_Draw;
            _root.Children.Remove(_canvas);
            // Win2D's guidance for controls created in code: releases the device and surfaces.
            _canvas.RemoveFromVisualTree();
            _canvas = null;
        }
        DropTrails();
        if (_saveTimer is { IsRunning: true }) SaveTweaks();
    }

    /// <summary>Twice a second: follow visibility and hide an idle touch bar.</summary>
    private void Tick()
    {
        if (_canvas is null) return;
        FollowSaved();
        if (_bar.Visibility == Visibility.Visible && _picker.Visibility != Visibility.Visible && _tweakPanel.Visibility != Visibility.Visible &&
            !FocusWithin(_bar) && DateTime.UtcNow - _barShownAt > TimeSpan.FromSeconds(6))
            _bar.Visibility = Visibility.Collapsed;

        // Listens and draws only while seen; see WidgetVisibility.
        if (!WidgetVisibility.IsOnScreen(this))
        {
            Pause();
            return;
        }
        if (_capture is null)
        {
            _capture = new WaveformCapture(_settings.InputDeviceId);
            // A fresh start: the picture begins again from now.
            _received = 0;
            _clock = new WaveformClock();
            _history = null;
            _spectrogramLive = false;
            _newEntries.Clear();
            _frameClock.Restart();
            _lastFrame = TimeSpan.Zero;
            _viewSince = _time;
            _fadingFrom = null;
        }
        if (!_rendering)
        {
            CompositionTarget.Rendering += OnRendering;
            _rendering = true;
        }
        ShowStatus(_capture.State);
    }

    private void Pause()
    {
        if (_rendering)
        {
            CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
        }
        _capture?.Dispose();
        _capture = null;
    }

    /// <summary>Once per compositor frame (the display's refresh), while on screen.</summary>
    private void OnRendering(object? sender, object args) => _canvas?.Invalidate();

    private void ShowStatus(WaveformCaptureState state)
    {
        var text = state switch
        {
            WaveformCaptureState.NoDevice => _settings.InputDeviceId is null
                ? "No playback device is available. The waveform starts when one is."
                : "The chosen input is not connected. The waveform starts when it is.",
            WaveformCaptureState.DeviceBusy => "Another app is using this device in exclusive mode, so its sound cannot be shown.",
            WaveformCaptureState.Unsupported => "This device's sound format is not supported.",
            WaveformCaptureState.Failed => "Windows audio is not available right now. Trying again.",
            _ => null
        };
        _status.Text = text ?? string.Empty;
        _status.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------- Choosing views (touch bar, grid, cycle) ----------

    /// <param name="save">False for the cycle's own steps, which are not choices to keep.</param>
    private void ChangeSettings(WaveformSettings next, bool save = true)
    {
        next = WaveformSettings.Normalize(next);
        if (next.View != _settings.View)
        {
            _fadingFrom = _settings.View;
            _fadeStarted = _time;
            // The cycle melts slowly from one view into the next; a tap answers quickly.
            _fadeLength = save ? FadeSeconds : CycleFadeSeconds;
        }
        // The cycle's next step is timed from now, even when the view stays the same.
        _viewSince = _time;
        var changed = !next.SameAs(_settings);
        if (save && changed)
        {
            // Saves only what changed here onto the latest saved settings (another Waveform
            // widget may have saved meanwhile); this widget keeps showing its own view.
            var merged = WaveformSettings.Merge(_settings, next, WaveformSettings.Normalize(_source()));
            _settings = merged with { View = next.View };
            UpdateBar();
            _save?.Invoke(merged);
            return;
        }
        _settings = next;
        UpdateBar();
    }

    /// <summary>
    /// Takes up what another Waveform widget (or the Settings page) saved, keeping this widget's
    /// own view. Not while this widget has slider changes still waiting to be saved.
    /// </summary>
    private void FollowSaved()
    {
        if (_saveTimer is { IsRunning: true }) return;
        var latest = WaveformSettings.Normalize(_source()) with { View = _settings.View };
        if (latest.SameAs(_settings)) return;
        _settings = latest;
        UpdateBar();
    }

    /// <summary>Saves the slider changes for the view being tweaked onto the latest saved settings.</summary>
    private void SaveTweaks()
    {
        _saveTimer?.Stop();
        var latest = WaveformSettings.Normalize(_source());
        var merged = WaveformSettings.Normalize(latest.WithTweaks(_tweakView, _settings.TweaksFor(_tweakView)));
        if (!merged.SameAs(latest)) _save?.Invoke(merged);
    }

    private void StepView(int direction)
    {
        var all = WaveformNames.Groups.SelectMany(group => group.Views).ToArray();
        var index = Array.IndexOf(all, _settings.View);
        ChangeSettings(_settings with { View = all[(index + direction + all.Length) % all.Length] });
    }

    private void StepLook(int direction)
    {
        var looks = Enum.GetValues<WaveformLook>();
        var index = Array.IndexOf(looks, _settings.Look);
        ChangeSettings(_settings with { Look = looks[(index + direction + looks.Length) % looks.Length] });
    }

    private void Widget_Tapped(object sender, TappedRoutedEventArgs args)
    {
        // Taps on the bar's or grid's own controls are theirs.
        for (var element = args.OriginalSource as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
            if (element == _bar || element == _picker || element == _tweakPanel) return;
        if (_tweakPanel.Visibility == Visibility.Visible) CloseTweaks();
        else if (_picker.Visibility == Visibility.Visible) _picker.Visibility = Visibility.Collapsed;
        else if (_bar.Visibility == Visibility.Visible) _bar.Visibility = Visibility.Collapsed;
        else
        {
            UpdateBar();
            _bar.Visibility = Visibility.Visible;
        }
    }

    // ---------- Keyboard ----------

    private void Widget_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if ((args.Key == VirtualKey.Enter || args.Key == VirtualKey.Space) && args.OriginalSource == this)
        {
            UpdateBar();
            _bar.Visibility = Visibility.Visible;
            FocusInto(_barFirst);
            args.Handled = true;
        }
        else if (args.Key == VirtualKey.Escape)
        {
            if (_tweakPanel.Visibility == Visibility.Visible) CloseTweaks();
            else if (_picker.Visibility == Visibility.Visible) HidePicker();
            else if (_bar.Visibility == Visibility.Visible)
            {
                _bar.Visibility = Visibility.Collapsed;
                Focus(FocusState.Keyboard);
            }
            else return;
            args.Handled = true;
        }
    }

    /// <summary>Whether keyboard focus is on the element or inside it.</summary>
    private bool FocusWithin(DependencyObject element)
    {
        if (XamlRoot is null) return false;
        for (var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; focused is not null; focused = VisualTreeHelper.GetParent(focused))
            if (focused == element) return true;
        return false;
    }

    /// <summary>Moves keyboard focus to a panel's first control once the panel has been laid out.</summary>
    private void FocusInto(Control? first)
    {
        if (first is null) return;
        // Lays the newly shown panel out first: a control that has not been measured cannot take focus.
        UpdateLayout();
        if (!first.Focus(FocusState.Keyboard))
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => first.Focus(FocusState.Keyboard));
    }

    /// <summary>Closes the view grid; keyboard focus inside it returns to the widget.</summary>
    private void HidePicker()
    {
        var keyboard = FocusWithin(_picker);
        _picker.Visibility = Visibility.Collapsed;
        if (keyboard) Focus(FocusState.Keyboard);
    }

    private void UpdateBar()
    {
        _viewName.Text = WaveformNames.View(_settings.View);
        _lookName.Text = WaveformNames.Look(_settings.Look);
        _cycleButton.IsChecked = _settings.CycleSeconds > 0;
        _barShownAt = DateTime.UtcNow;
        foreach (var (tile, view) in _tiles) tile.Background = view == _settings.View ? SelectedTile : NormalTile;
    }

    private static readonly Brush BarBackground = new SolidColorBrush(Windows.UI.Color.FromArgb(230, 22, 20, 32));
    private static readonly Brush NormalTile = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 40, 36, 56));
    private static readonly Brush SelectedTile = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 96, 70, 184));

    private static Button TouchButton(string text, string name, RoutedEventHandler click, double width = 64)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 20 },
            MinWidth = width,
            MinHeight = 56,
            // Taps must not move keyboard focus away from whatever the user is typing into.
            AllowFocusOnInteraction = false,
            Background = NormalTile,
            CornerRadius = new CornerRadius(10)
        };
        AutomationProperties.SetName(button, name);
        button.Click += click;
        return button;
    }

    private void BuildBar()
    {
        _viewName.FontSize = 20;
        _viewName.FontWeight = FontWeights.SemiBold;
        _viewName.MinWidth = 220;
        _viewName.TextAlignment = TextAlignment.Center;
        _viewName.VerticalAlignment = VerticalAlignment.Center;
        _lookName.FontSize = 18;
        _lookName.MinWidth = 110;
        _lookName.TextAlignment = TextAlignment.Center;
        _lookName.VerticalAlignment = VerticalAlignment.Center;
        _cycleButton.Content = new TextBlock { Text = "Cycle", FontSize = 20 };
        _cycleButton.MinHeight = 56;
        _cycleButton.MinWidth = 96;
        _cycleButton.AllowFocusOnInteraction = false;
        _cycleButton.CornerRadius = new CornerRadius(10);
        AutomationProperties.SetName(_cycleButton, "Cycle through views");
        _cycleButton.Click += (_, _) => ChangeSettings(_settings with { CycleSeconds = _settings.CycleSeconds > 0 ? 0 : 30 });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _barFirst = TouchButton("◀", "Previous view", (_, _) => StepView(-1));
        row.Children.Add(_barFirst);
        row.Children.Add(_viewName);
        row.Children.Add(TouchButton("▶", "Next view", (_, _) => StepView(1)));
        row.Children.Add(new Border { Width = 12 });
        row.Children.Add(TouchButton("◀", "Previous look", (_, _) => StepLook(-1)));
        row.Children.Add(_lookName);
        row.Children.Add(TouchButton("▶", "Next look", (_, _) => StepLook(1)));
        row.Children.Add(new Border { Width = 12 });
        row.Children.Add(_cycleButton);
        row.Children.Add(TouchButton("All views", "Show all views and looks", (_, _) =>
        {
            UpdateBar();
            var keyboard = FocusWithin(this);
            _picker.Visibility = Visibility.Visible;
            if (keyboard) FocusInto(_pickerFirst);
        }, 120));
        row.Children.Add(TouchButton("Tweak", "Tweak this view: sliders for speed, colour and more", (_, _) => OpenTweaks(), 96));

        _bar.Child = new ScrollViewer
        {
            Content = row,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled
        };
        _bar.Background = BarBackground;
        _bar.CornerRadius = new CornerRadius(16);
        _bar.Padding = new Thickness(10);
        _bar.Margin = new Thickness(12);
        _bar.HorizontalAlignment = HorizontalAlignment.Center;
        _bar.VerticalAlignment = VerticalAlignment.Bottom;
        _bar.Visibility = Visibility.Collapsed;
        _root.Children.Add(_bar);
    }

    private void BuildPicker()
    {
        var content = new StackPanel { Spacing = 8, Padding = new Thickness(20) };
        var header = new Grid();
        header.Children.Add(new TextBlock { Text = "Choose a view", FontSize = 22, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var close = TouchButton("✕", "Close", (_, _) => HidePicker());
        close.HorizontalAlignment = HorizontalAlignment.Right;
        // Clear of EdgeDock's own controls handle in the top-right corner.
        close.Margin = new Thickness(0, 0, 72, 0);
        header.Children.Add(close);
        _pickerFirst = close;
        content.Children.Add(header);
        foreach (var (group, views) in WaveformNames.Groups)
        {
            content.Children.Add(Heading(group));
            var tiles = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 196, ItemHeight = 64 };
            foreach (var view in views)
            {
                var tile = TouchButton(WaveformNames.View(view), WaveformNames.View(view), (_, _) =>
                {
                    ChangeSettings(_settings with { View = view });
                    HidePicker();
                }, 188);
                tile.HorizontalAlignment = HorizontalAlignment.Stretch;
                tile.Margin = new Thickness(0, 0, 8, 8);
                _tiles[tile] = view;
                tiles.Children.Add(tile);
            }
            content.Children.Add(tiles);
        }

        content.Children.Add(Heading("Playlists: cycle through a set"));
        var playlists = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 196, ItemHeight = 64 };
        foreach (var (name, views) in WaveformNames.Playlists)
        {
            var set = views;
            var tile = TouchButton(name, "Playlist: " + name, (_, _) =>
            {
                ChangeSettings(_settings with
                {
                    CycleViews = set,
                    CycleSeconds = _settings.CycleSeconds > 0 ? _settings.CycleSeconds : 30,
                    View = set.Length > 0 ? set[0] : _settings.View
                });
                HidePicker();
            }, 188);
            tile.HorizontalAlignment = HorizontalAlignment.Stretch;
            tile.Margin = new Thickness(0, 0, 8, 8);
            playlists.Children.Add(tile);
        }
        content.Children.Add(playlists);

        content.Children.Add(Heading("Looks"));
        var looks = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 146, ItemHeight = 64 };
        foreach (var look in Enum.GetValues<WaveformLook>())
        {
            var palette = WaveformPalette.For(look);
            var swatch = new Button
            {
                Content = new TextBlock { Text = WaveformNames.Look(look), FontSize = 18, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) },
                AllowFocusOnInteraction = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(0, 0, 8, 8),
                CornerRadius = new CornerRadius(10),
                Background = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(1, 0),
                    GradientStops =
                    {
                        new GradientStop { Color = Dim(palette.Low), Offset = 0 },
                        new GradientStop { Color = Dim(palette.Mid), Offset = 0.5 },
                        new GradientStop { Color = Dim(palette.High), Offset = 1 }
                    }
                }
            };
            AutomationProperties.SetName(swatch, "Look: " + WaveformNames.Look(look));
            swatch.Click += (_, _) => ChangeSettings(_settings with { Look = look });
            looks.Children.Add(swatch);
        }
        content.Children.Add(looks);

        content.Children.Add(Heading("Colour cycle"));
        // Wraps like the looks, so every choice stays reachable in a narrow widget.
        var cycles = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 128, ItemHeight = 64 };
        foreach (var cycle in Enum.GetValues<WaveformColourCycle>())
        {
            var choice = TouchButton(WaveformNames.ColourCycle(cycle), "Colour cycle: " + WaveformNames.ColourCycle(cycle),
                (_, _) => ChangeSettings(_settings with { ColourCycle = cycle }), 0);
            choice.HorizontalAlignment = HorizontalAlignment.Stretch;
            choice.Margin = new Thickness(0, 0, 8, 8);
            cycles.Children.Add(choice);
        }
        content.Children.Add(cycles);

        _picker.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(238, 12, 11, 20));
        _picker.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _picker.Visibility = Visibility.Collapsed;
        _root.Children.Add(_picker);

        static TextBlock Heading(string text) => new() { Text = text, FontSize = 16, Opacity = 0.75, Margin = new Thickness(0, 6, 0, 0) };

        static Windows.UI.Color Dim(WaveformRgb c) =>
            Windows.UI.Color.FromArgb(255, (byte)(c.R * 200), (byte)(c.G * 200), (byte)(c.B * 200));
    }

    // ---------- Tweak panel ----------

    private void BuildTweaks()
    {
        var content = new StackPanel { Spacing = 6, Padding = new Thickness(18, 12, 18, 12) };
        _tweakTitle.FontSize = 20;
        _tweakTitle.FontWeight = FontWeights.SemiBold;
        _tweakTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        content.Children.Add(_tweakTitle);

        _speedSlider = AddSlider(content, "Speed", WaveformTweaks.MinimumSpeed, WaveformTweaks.MaximumSpeed, 0.05, value => $"{value:0.00}×");
        _reactSlider = AddSlider(content, "React", 0, WaveformTweaks.MaximumReactivity, 0.05, value => $"{value * 100:0}%");
        _brightSlider = AddSlider(content, "Bright", WaveformTweaks.MinimumBrightness, WaveformTweaks.MaximumBrightness, 0.05, value => $"{value * 100:0}%");
        _hueSlider = AddSlider(content, "Colour", 0, 355, 5, value => $"{value:0}°");
        _trailSlider = AddSlider(content, "Trails", 0, WaveformTweaks.MaximumTrails, 0.02, value => $"{value * 100:0}%");
        _blendSlider = AddSlider(content, "Blend over", 0, 1, 0.05, value => $"{value * 100:0}%");

        _cameraButton.Content = new TextBlock { Text = "Camera", FontSize = 18 };
        _cameraButton.MinHeight = 52;
        _cameraButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        _cameraButton.AllowFocusOnInteraction = false;
        _cameraButton.CornerRadius = new CornerRadius(10);
        AutomationProperties.SetName(_cameraButton, "Camera move");
        _cameraButton.Click += (_, _) => TweaksChanged();

        // Three equal buttons that share whatever width the panel has.
        var buttons = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        for (var i = 0; i < 3; i++) buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var reset = TouchButton("Reset", "Reset this view's sliders", (_, _) =>
        {
            LoadTweaks(WaveformTweaks.Default);
            TweaksChanged();
        }, 0);
        var done = TouchButton("Done", "Close the sliders", (_, _) => CloseTweaks(), 0);
        reset.HorizontalAlignment = done.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(reset, 1);
        Grid.SetColumn(done, 2);
        buttons.Children.Add(_cameraButton);
        buttons.Children.Add(reset);
        buttons.Children.Add(done);
        content.Children.Add(buttons);

        _tweakPanel.Child = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _tweakPanel.Background = BarBackground;
        _tweakPanel.CornerRadius = new CornerRadius(16);
        _tweakPanel.Margin = new Thickness(12);
        // At most 520 wide; FitTweaks narrows it (and stacks each slider under its name) to fit
        // the widget, which can be as narrow as 240 beside a web panel.
        _tweakPanel.Width = 520;
        _tweakPanel.HorizontalAlignment = HorizontalAlignment.Left;
        _tweakPanel.VerticalAlignment = VerticalAlignment.Stretch;
        _tweakPanel.Visibility = Visibility.Collapsed;
        _root.Children.Add(_tweakPanel);

        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(700);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => SaveTweaks();
    }

    private Slider AddSlider(Panel parent, string label, double minimum, double maximum, double step, Func<double, string> format)
    {
        var row = new Grid { ColumnSpacing = 10 };
        var labelColumn = new ColumnDefinition { Width = new GridLength(110) };
        var valueColumn = new ColumnDefinition { Width = new GridLength(70) };
        row.ColumnDefinitions.Add(labelColumn);
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(valueColumn);
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var name = new TextBlock { Text = label, FontSize = 17, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider
        {
            Minimum = minimum,
            Maximum = maximum,
            StepFrequency = step,
            SmallChange = step,
            MinHeight = 44,
            VerticalAlignment = VerticalAlignment.Center,
            IsThumbToolTipEnabled = false,
            // Dragging must not move keyboard focus away from whatever the user is typing into;
            // the sliders stay in the Tab order so they can still be set from the keyboard.
            AllowFocusOnInteraction = false
        };
        AutomationProperties.SetName(slider, label);
        var value = new TextBlock { FontSize = 16, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(value, 2);
        row.Children.Add(name);
        row.Children.Add(slider);
        row.Children.Add(value);
        parent.Children.Add(row);
        _sliders.Add((slider, value, format));
        _sliderRows.Add((row, labelColumn, valueColumn, slider));
        slider.ValueChanged += (_, _) =>
        {
            value.Text = format(slider.Value);
            if (!_loadingTweaks) TweaksChanged();
        };
        return slider;
    }

    /// <summary>
    /// Fits the Tweak panel to the widget: up to 520 wide, and below 420 each slider moves onto
    /// its own line under its name and value so it keeps a usable length.
    /// </summary>
    private void FitTweaks()
    {
        var available = ActualWidth - 24;
        if (available <= 0) return;
        _tweakPanel.Width = Math.Min(520, available);
        var narrow = _tweakPanel.Width < 420;
        if (_tweaksNarrow == narrow) return;
        _tweaksNarrow = narrow;
        // A narrow panel spans the widget: start it below EdgeDock's controls handle (top right).
        _tweakPanel.Margin = new Thickness(12, narrow ? 64 : 12, 12, 12);
        foreach (var (row, label, value, slider) in _sliderRows)
        {
            // Narrow: name left and value right on the first line, the slider across the second.
            label.Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(110);
            value.Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(70);
            Grid.SetRow(slider, narrow ? 1 : 0);
            Grid.SetColumn(slider, narrow ? 0 : 1);
            Grid.SetColumnSpan(slider, narrow ? 3 : 1);
            var number = row.Children.OfType<TextBlock>().Last();
            Grid.SetColumn(number, narrow ? 1 : 2);
            Grid.SetColumnSpan(number, narrow ? 2 : 1);
        }
    }

    private void OpenTweaks()
    {
        FitTweaks();
        _tweakView = _settings.View;
        _tweakTitle.Text = "Tweak: " + WaveformNames.View(_tweakView);
        LoadTweaks(_settings.TweaksFor(_tweakView));
        var keyboard = FocusWithin(this);
        _bar.Visibility = Visibility.Collapsed;
        _tweakPanel.Visibility = Visibility.Visible;
        if (keyboard) FocusInto(_speedSlider);
    }

    private void CloseTweaks()
    {
        var keyboard = FocusWithin(_tweakPanel);
        _tweakPanel.Visibility = Visibility.Collapsed;
        if (keyboard) Focus(FocusState.Keyboard);
        // Saves straight away rather than waiting for the pause after the last move.
        if (_saveTimer is { IsRunning: true }) SaveTweaks();
        // The cycle's next step is timed from now.
        _viewSince = _time;
    }

    private void LoadTweaks(WaveformTweaks tweaks)
    {
        _loadingTweaks = true;
        _speedSlider.Value = tweaks.Speed;
        _reactSlider.Value = tweaks.Reactivity;
        _brightSlider.Value = tweaks.Brightness;
        _hueSlider.Value = tweaks.Hue;
        _trailSlider.Value = tweaks.Trails;
        _blendSlider.Value = tweaks.Blend;
        _cameraButton.IsChecked = tweaks.Camera;
        foreach (var (slider, value, format) in _sliders) value.Text = format(slider.Value);
        _loadingTweaks = false;
    }

    /// <summary>A slider moved: the view changes at once and the choice is saved after a short pause.</summary>
    private void TweaksChanged()
    {
        var tweaks = new WaveformTweaks(_speedSlider.Value, _reactSlider.Value, _brightSlider.Value, _hueSlider.Value,
            _trailSlider.Value, _blendSlider.Value, _cameraButton.IsChecked == true);
        var next = WaveformSettings.Normalize(_settings.WithTweaks(_tweakView, tweaks));
        if (next.SameAs(_settings)) return;
        _settings = next;
        _saveTimer?.Stop();
        _saveTimer?.Start();
    }

    // ---------- Views ----------

    private static (Renderer Renderer, int Style) Map(WaveformView view) => view switch
    {
        WaveformView.Scroll => (Renderer.Scroll, 0),
        WaveformView.CentreOut => (Renderer.Scroll, 1),
        WaveformView.SpectrumBars => (Renderer.Bars, 0),
        WaveformView.MirrorSpectrum => (Renderer.Bars, 1),
        WaveformView.SpectrumLine => (Renderer.Bars, 2),
        WaveformView.Pulse => (Renderer.Bars, 3),
        WaveformView.LedBars => (Renderer.Bars, 4),
        WaveformView.Club => (Renderer.Bars, 5),
        WaveformView.Spectrogram => (Renderer.Spectrogram, 0),
        WaveformView.Ridges => (Renderer.Ridges, 0),
        WaveformView.Ring => (Renderer.Ring, 0),
        WaveformView.Oscilloscope => (Renderer.Scope, 0),
        WaveformView.StereoScope => (Renderer.Scope, 1),
        WaveformView.BandScopes => (Renderer.Scope, 2),
        WaveformView.CrtScope => (Renderer.Crt, 0),
        WaveformView.CrtVectorscope => (Renderer.Crt, 1),
        WaveformView.Lissajous => (Renderer.Lissajous, 0),
        WaveformView.Warp => (Renderer.Field, 0),
        WaveformView.Aurora => (Renderer.Field, 1),
        WaveformView.Plasma => (Renderer.Field, 2),
        WaveformView.Tunnel => (Renderer.Demo, 0),
        WaveformView.Kaleidoscope => (Renderer.Crazy, 0),
        WaveformView.Vortex => (Renderer.Crazy, 1),
        WaveformView.Glitch => (Renderer.Crazy, 2),
        WaveformView.Fire => (Renderer.Crazy, 3),
        WaveformView.Metaballs => (Renderer.Crazy, 4),
        WaveformView.Lightning => (Renderer.Crazy, 5),
        WaveformView.Fractal => (Renderer.Crazy, 6),
        WaveformView.StarNest => (Renderer.Borrowed, 0),
        WaveformView.HexPlasma => (Renderer.Borrowed, 1),
        WaveformView.Clouds => (Renderer.Borrowed, 2),
        WaveformView.HypnoCircles => (Renderer.Borrowed, 3),
        WaveformView.Cloudscape => (Renderer.Wild, 0),
        WaveformView.Lasers => (Renderer.Wild, 1),
        WaveformView.Voronoi => (Renderer.Wild, 2),
        WaveformView.Blob => (Renderer.Wild, 3),
        WaveformView.Truchet => (Renderer.Wild, 4),
        WaveformView.Spheres => (Renderer.Wild, 5),
        WaveformView.Mandelbulb => (Renderer.Wild, 6),
        WaveformView.Lattice => (Renderer.Scene, 0),
        WaveformView.FractalCubes => (Renderer.Scene, 1),
        WaveformView.Mandelbox => (Renderer.Scene, 2),
        WaveformView.OrbField => (Renderer.Orbs, 0),
        WaveformView.BlackHole => (Renderer.Cosmos, 0),
        WaveformView.Mountains => (Renderer.Cosmos, 1),
        WaveformView.NeonRings => (Renderer.Cosmos, 2),
        WaveformView.FractalForest => (Renderer.Cosmos, 3),
        WaveformView.Ocean => (Renderer.Borrowed, 4),
        WaveformView.Hyperspace => (Renderer.Mania, 0),
        WaveformView.SynthGrid => (Renderer.Mania, 1),
        WaveformView.KaleidoTunnel => (Renderer.Mania, 2),
        WaveformView.MandelbrotZoom => (Renderer.Mania, 3),
        WaveformView.City => (Renderer.Mania, 4),
        WaveformView.Helix => (Renderer.Mania, 5),
        WaveformView.LiquidColour => (Renderer.Mania, 6),
        WaveformView.Shards => (Renderer.Mania, 7),
        WaveformView.Mashup => (Renderer.Mashup, 0),
        WaveformView.GlassCubes => (Renderer.Mania, 8),
        WaveformView.FractalLand => (Renderer.Mania, 9),
        _ => (Renderer.Wild, 6)
    };

    private static Needs NeedsOf(WaveformView view) => Map(view) switch
    {
        (Renderer.Scroll, _) => Needs.History,
        (Renderer.Spectrogram or Renderer.Ridges, _) => Needs.Spectrum | Needs.SpectrogramRows,
        (Renderer.Bars, 3) => Needs.Pulse,
        (Renderer.Bars, _) => Needs.Spectrum,
        (Renderer.Ring, _) => Needs.Spectrum | Needs.Scope,
        (Renderer.Scope, 0) => Needs.Scope,
        (Renderer.Scope, 1) => Needs.Stereo,
        (Renderer.Scope, _) => Needs.Bands,
        (Renderer.Crt, 0) => Needs.Scope | Needs.Trails,
        (Renderer.Crt, _) or (Renderer.Lissajous, _) => Needs.Stereo,
        (Renderer.Crazy, _) => Needs.Spectrum | Needs.Scope,
        (Renderer.Wild, _) or (Renderer.Scene, _) or (Renderer.Cosmos, _) or (Renderer.Mania, _) => Needs.Spectrum,
        (Renderer.Mashup, _) => Needs.Spectrum | Needs.Scope,
        (Renderer.Orbs, _) => Needs.Spectrum | Needs.SpectrogramRows,
        _ => Needs.None
    };

    private ICanvasImage Effect(Renderer renderer)
    {
        if (_effects.TryGetValue(renderer, out var existing)) return existing;
        ICanvasImage effect = renderer switch
        {
            Renderer.Scroll => WithTexture(new PixelShaderEffect<WaveformScrollShader>(), HistoryTexture()),
            Renderer.Spectrogram => WithTexture(new PixelShaderEffect<WaveformSpectrogramShader>(), SpectrogramTexture()),
            Renderer.Ridges => WithTexture(new PixelShaderEffect<WaveformRidgesShader>(), SpectrogramTexture()),
            Renderer.Bars => WithTexture(new PixelShaderEffect<WaveformBarsShader>(), DataTexture()),
            Renderer.Scope => WithTexture(new PixelShaderEffect<WaveformScopeShader>(), DataTexture()),
            Renderer.Crt => WithTexture(new PixelShaderEffect<WaveformCrtShader>(), DataTexture()),
            Renderer.Lissajous => WithTexture(new PixelShaderEffect<WaveformLissajousShader>(), DataTexture()),
            Renderer.Ring => WithTexture(new PixelShaderEffect<WaveformRingShader>(), DataTexture()),
            Renderer.Crazy => WithTexture(new PixelShaderEffect<WaveformCrazyShader>(), DataTexture()),
            Renderer.Wild => WithTexture(new PixelShaderEffect<WaveformWildShader>(), DataTexture()),
            Renderer.Scene => WithTexture(new PixelShaderEffect<WaveformSceneShader>(), DataTexture()),
            Renderer.Cosmos => WithTexture(new PixelShaderEffect<WaveformCosmosShader>(), DataTexture()),
            Renderer.Mania => WithTexture(new PixelShaderEffect<WaveformManiaShader>(), DataTexture()),
            Renderer.Orbs => WithTexture(new PixelShaderEffect<WaveformOrbShader>(), SpectrogramTexture()),
            Renderer.Field => new PixelShaderEffect<WaveformFieldShader>(),
            Renderer.Borrowed => new PixelShaderEffect<WaveformBorrowedShader>(),
            _ => new PixelShaderEffect<WaveformDemoShader>()
        };
        _effects[renderer] = effect;
        return effect;

        static ICanvasImage WithTexture<T>(PixelShaderEffect<T> effect, D2D1ResourceTextureManager texture)
            where T : unmanaged, ID2D1PixelShader, ComputeSharp.D2D1.Descriptors.ID2D1PixelShaderDescriptor<T>
        {
            effect.ResourceTextureManagers[0] = texture;
            return effect;
        }
    }

    private D2D1ResourceTextureManager HistoryTexture() => _historyTexture ??= new(
        [WaveformHistory.Length], D2D1BufferPrecision.Float32, D2D1ChannelDepth.Four, D2D1Filter.MinMagMipLinear,
        [D2D1ExtendMode.Wrap], new byte[WaveformHistory.Length * 16], []);

    private D2D1ResourceTextureManager SpectrogramTexture() => _spectrogramTexture ??= new(
        [(uint)WaveformSpectrum.Bands, WaveformHistory.Length], D2D1BufferPrecision.UInt8Normalized, D2D1ChannelDepth.One,
        D2D1Filter.MinMagMipLinear, [D2D1ExtendMode.Clamp, D2D1ExtendMode.Wrap],
        new byte[WaveformSpectrum.Bands * WaveformHistory.Length], [(uint)WaveformSpectrum.Bands]);

    private D2D1ResourceTextureManager DataTexture() => _dataTexture ??= new(
        [DataWidth, DataRows], D2D1BufferPrecision.Float32, D2D1ChannelDepth.Four, D2D1Filter.MinMagMipLinear,
        [D2D1ExtendMode.Clamp, D2D1ExtendMode.Clamp], new byte[_data.Length * 4], [DataWidth * 16]);

    // ---------- Each frame ----------

    private void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var capture = _capture;
        if (capture is null) return;
        var scale = sender.Dpi / 96f;
        var width = (float)Math.Round(sender.Size.Width * scale);
        var height = (float)Math.Round(sender.Size.Height * scale);
        if (width < 2 || height < 2) return;

        var now = _frameClock.Elapsed;
        var seconds = Math.Clamp((now - _lastFrame).TotalSeconds, 0, 0.1);
        _lastFrame = now;
        _frameSeconds = seconds;
        _time += seconds;

        // Cycle mode: move on once this view has had its time.
        if (_settings.CycleSeconds > 0 && _time - _viewSince >= _settings.CycleSeconds && _tweakPanel.Visibility != Visibility.Visible)
            ChangeSettings(_settings with { View = _settings.NextInCycle(_settings.View) }, save: false);

        var fade = _fadingFrom is null ? 1 : Math.Clamp((_time - _fadeStarted) / _fadeLength, 0, 1);
        if (fade >= 1) _fadingFrom = null;
        var needs = NeedsOf(_settings.View) | (_fadingFrom is { } from ? NeedsOf(from) : Needs.None);
        // Mashup and Blend over layers: this frame's pair is chosen first, so their data is ready too.
        var layered = _settings.View == WaveformView.Mashup || _fadingFrom == WaveformView.Mashup ||
            _settings.TweaksFor(_settings.View).Blend > 0.01 || (_fadingFrom is { } faded && _settings.TweaksFor(faded).Blend > 0.01);
        if (layered)
        {
            AdvanceMashup();
            needs |= NeedsOf(MashupPool[_mashBase]) | NeedsOf(MashupPool[_mashTop]);
        }

        // Read the new 5 ms columns: level, colour, beat and the scrolling history. The history
        // scrolls at the speed setting times the scrolling view's own Speed slider; a new speed
        // starts it afresh.
        var scrolling = needs.HasFlag(Needs.History);
        var scrollView = NeedsOf(_settings.View).HasFlag(Needs.History) || _fadingFrom is not { } previous ? _settings.View : previous;
        var scrollSpeed = _settings.Speed * _settings.TweaksFor(scrollView).Speed;
        if (scrolling && (_history is null || _historyScale != scale || _historySpeed != scrollSpeed))
        {
            // The history counts from now: the scroll clock is measured from this column on.
            _history = new WaveformHistory(scrollSpeed * scale, _received);
            _historyScale = scale;
            _historySpeed = scrollSpeed;
            _newEntries.Clear();
        }
        if (!scrolling) _history = null;
        while (capture.Ring.TryRead(out var column))
        {
            _received++;
            var bar = _levels.Next(column);
            _beat.Add(column.Low, WaveformAnalyzer.BlockSeconds);
            _drive.Add(column, WaveformAnalyzer.BlockSeconds);
            if (_history is not null && _history.Add(bar, out var entry)) _newEntries.Add(entry);
        }
        _scrollPosition = _clock.Advance(seconds, _received);
        // Motion surges with each hit: travel speeds up with the music's own dynamics. The views'
        // clocks run at the shown view's Speed slider, and its Reactivity scales the surge.
        var tweaks = _settings.TweaksFor(_settings.View);
        _shaderTime += seconds * tweaks.Speed;
        _shaderTravel += seconds * tweaks.Speed * (0.2 + tweaks.Reactivity * (2.6 * _drive.Energy + 1.2 * _drive.Bass));
        _hue = (_hue + seconds * WaveformSettings.CycleDegreesPerSecond(_settings.ColourCycle)) % 360;
        var palette = WaveformPalette.For(_settings.Look);
        if (_hue != 0) palette = palette.Rotate(_hue);
        MoveCamera(tweaks, seconds);

        PrepareData(capture, needs, seconds);
        if (_history is not null) UploadHistory(_history.Written);
        if (needs.HasFlag(Needs.SpectrogramRows)) AddSpectrogramRows(seconds);
        else _spectrogramLive = false;

        var size = new Float2(width, height);
        var ds = args.DrawingSession;
        ds.Units = CanvasUnits.Pixels;
        var area = new Windows.Foundation.Rect(0, 0, width, height);
        var trails = _settings.TweaksFor(_settings.View).Trails * fade + (_fadingFrom is { } was ? _settings.TweaksFor(was).Trails * (1 - fade) : 0);
        if (trails < 0.01)
        {
            DropTrails();
            DrawViews(ds, size, area, palette, fade, scale);
            return;
        }
        // Trails: draw the frame off screen, keep the brighter of it and the fading last frame.
        EnsureTrails(sender, width, height);
        using (var frame = _frame!.CreateDrawingSession())
        {
            frame.Clear(PanelColour);
            DrawViews(frame, size, area, palette, fade, scale);
        }
        var keep = (float)trails;
        _decay.Source = _trailPrevious;
        _decay.ColorMatrix = new Matrix5x4 { M11 = keep, M22 = keep, M33 = keep, M44 = 1 };
        _lighten.Background = _frame;
        _lighten.Foreground = _decay;
        using (var next = _trailNext!.CreateDrawingSession())
        {
            next.Blend = CanvasBlend.Copy;
            next.DrawImage(_lighten);
        }
        ds.DrawImage(_trailNext);
        (_trailPrevious, _trailNext) = (_trailNext, _trailPrevious);
    }

    private void DrawViews(CanvasDrawingSession ds, Float2 size, Windows.Foundation.Rect area, WaveformPalette palette, double fade, float scale)
    {
        if (_fadingFrom is { } old) DrawView(ds, old, size, area, palette, (float)(1 - fade), scale);
        DrawView(ds, _settings.View, size, area, palette, (float)fade, scale);
    }

    private void EnsureTrails(CanvasControl sender, float width, float height)
    {
        if (_frame is not null && _frame.Device == sender.Device && _frame.SizeInPixels.Width == (uint)width && _frame.SizeInPixels.Height == (uint)height)
            return;
        DropTrails();
        _frame = new CanvasRenderTarget(sender, width, height, 96);
        _trailPrevious = new CanvasRenderTarget(sender, width, height, 96);
        _trailNext = new CanvasRenderTarget(sender, width, height, 96);
        using var clear = _trailPrevious.CreateDrawingSession();
        clear.Clear(PanelColour);
    }

    private void DropTrails()
    {
        if (_frame is null) return;
        _decay.Source = null;
        _lighten.Background = null;
        _lighten.Foreground = null;
        _frame.Dispose();
        _trailPrevious?.Dispose();
        _trailNext?.Dispose();
        _frame = _trailPrevious = _trailNext = null;
    }

    /// <summary>
    /// Camera movement (when the view's Camera button is on): a slow look-around that 3D views
    /// turn their rays by, plus a gentle drift, zoom and roll of the whole picture. It eases in
    /// and out over a second or so rather than jumping.
    /// </summary>
    private void MoveCamera(WaveformTweaks tweaks, double seconds)
    {
        _cameraMix = Math.Clamp(_cameraMix + (tweaks.Camera ? seconds : -seconds) * 0.8, 0, 1);
        _cameraPhase += seconds * tweaks.Speed * (0.25 + 0.6 * _drive.Energy);
        var mix = (float)_cameraMix;
        var phase = (float)_cameraPhase;
        _cameraYaw = mix * (0.34f * MathF.Sin(phase * 0.9f) + 0.1f * MathF.Sin(phase * 2.3f + 1f));
        _cameraPitch = mix * (0.12f * MathF.Sin(phase * 0.7f + 2f) + 0.05f * _drive.Bass);
    }

    /// <summary>The picture's drift, zoom and roll for the camera, about the middle of the area.</summary>
    private Matrix3x2 CameraMove(Float2 size)
    {
        var mix = (float)_cameraMix;
        var phase = (float)_cameraPhase;
        var zoom = 1 + mix * (0.1f + 0.06f * _drive.Bass);
        var roll = mix * 0.022f * MathF.Sin(phase * 0.6f);
        var drift = new Vector2(mix * 0.02f * size.X * MathF.Sin(phase * 0.5f), 0);
        var middle = new Vector2(size.X / 2, size.Y / 2);
        return Matrix3x2.CreateTranslation(-middle) * Matrix3x2.CreateScale(zoom) * Matrix3x2.CreateRotation(roll) *
            Matrix3x2.CreateTranslation(middle + drift);
    }

    /// <param name="layer">The sliders of the view this one sits in (mashup, blend), or null for its own.</param>
    private void DrawView(CanvasDrawingSession ds, WaveformView view, Float2 size, Windows.Foundation.Rect area,
        WaveformPalette palette, float opacity, float scale, WaveformTweaks? layer = null)
    {
        if (opacity <= 0.001f) return;
        var tweaks = layer ?? _settings.TweaksFor(view);
        if (view == WaveformView.Mashup)
        {
            DrawMashup(ds, size, area, palette, opacity, scale, tweaks);
            return;
        }
        // Reactivity scales how hard the music drives the view, colours included.
        var react = (float)tweaks.Reactivity;
        var (bass, mids, highs) = (MathF.Min(_drive.Bass * react, 1.5f), MathF.Min(_drive.Mid * react, 1.5f), MathF.Min(_drive.High * react, 1.5f));
        var energy = MathF.Min(_drive.Energy * react, 1.5f);
        var flash = MathF.Min(_beat.Flash * react, 1f);
        if (tweaks.Hue != 0) palette = palette.Rotate(tweaks.Hue);
        // Colours follow the music in every view: each look colour swings with its own band.
        palette = palette.React(MathF.Min(bass, 1), MathF.Min(mids, 1), MathF.Min(highs, 1));
        var (renderer, style) = Map(view);
        var (low, mid, high) = (ToFloat3(palette.Low), ToFloat3(palette.Mid), ToFloat3(palette.High));
        var (lowWeight, midWeight, highWeight) = _levels.Weights;
        var current = ToFloat3(palette.Mix(lowWeight, midWeight, highWeight));
        var effect = Effect(renderer);
        switch (renderer)
        {
            case Renderer.Scroll:
            {
                var history = _history!;
                // One entry behind the newest, so the picture never reads an entry still being filled.
                var head = history.Head(_scrollPosition);
                ((PixelShaderEffect<WaveformScrollShader>)effect).ConstantBuffer = new WaveformScrollShader(
                    size, low, mid, high, (float)(head % WaveformHistory.Length), (float)history.PixelsPerEntry,
                    (float)Math.Min(head + 1, WaveformHistory.Length - 1), style);
                break;
            }
            case Renderer.Spectrogram:
                ((PixelShaderEffect<WaveformSpectrogramShader>)effect).ConstantBuffer = new WaveformSpectrogramShader(
                    size, low, mid, high, SpectrogramHead(), (float)(_settings.Speed * tweaks.Speed * scale / SpectrogramRate), SpectrogramAvailable());
                break;
            case Renderer.Ridges:
                // Ridges sit a fixed time apart: the faster the speed, the closer in time.
                ((PixelShaderEffect<WaveformRidgesShader>)effect).ConstantBuffer = new WaveformRidgesShader(
                    size, low, mid, high, SpectrogramHead(), (float)Math.Clamp(1200 / (_settings.Speed * tweaks.Speed), 2, 40), SpectrogramAvailable());
                break;
            case Renderer.Bars:
                ((PixelShaderEffect<WaveformBarsShader>)effect).ConstantBuffer = new WaveformBarsShader(
                    size, low, mid, high, current, style,
                    style == 3 ? WaveformPulse.Bars : style == 4 ? 48 : WaveformSpectrum.Bands, flash);
                break;
            case Renderer.Scope:
                ((PixelShaderEffect<WaveformScopeShader>)effect).ConstantBuffer = new WaveformScopeShader(size, current, low, mid, high, style);
                break;
            case Renderer.Crt:
                ((PixelShaderEffect<WaveformCrtShader>)effect).ConstantBuffer = new WaveformCrtShader(size, mid, style, (float)_shaderTime);
                break;
            case Renderer.Lissajous:
                ((PixelShaderEffect<WaveformLissajousShader>)effect).ConstantBuffer = new WaveformLissajousShader(size, current, low);
                break;
            case Renderer.Ring:
                ((PixelShaderEffect<WaveformRingShader>)effect).ConstantBuffer = new WaveformRingShader(
                    size, low, mid, high, current, bass, flash, (float)_shaderTime);
                break;
            case Renderer.Field:
                ((PixelShaderEffect<WaveformFieldShader>)effect).ConstantBuffer = new WaveformFieldShader(
                    size, low, mid, high, (float)_shaderTime, (float)_shaderTravel, bass, mids, highs, energy, flash, style);
                break;
            case Renderer.Demo:
                ((PixelShaderEffect<WaveformDemoShader>)effect).ConstantBuffer = new WaveformDemoShader(
                    size, low, mid, high, (float)_shaderTime, (float)_shaderTravel, bass, mids, highs, energy, flash, MathF.Max(2, MathF.Round(3 * scale)));
                break;
            case Renderer.Borrowed:
                ((PixelShaderEffect<WaveformBorrowedShader>)effect).ConstantBuffer = new WaveformBorrowedShader(
                    size, low, mid, high, (float)_shaderTime, (float)_shaderTravel, bass, energy, flash, style);
                break;
            case Renderer.Mania:
                ((PixelShaderEffect<WaveformManiaShader>)effect).ConstantBuffer = new WaveformManiaShader(
                    size, low, mid, high, (float)_shaderTime, (float)_shaderTravel, bass, mids, highs, energy, flash, style);
                break;
            case Renderer.Scene:
                ((PixelShaderEffect<WaveformSceneShader>)effect).ConstantBuffer = new WaveformSceneShader(
                    size, low, mid, high, (float)_shaderTime, (float)_shaderTravel, bass, mids, highs, energy, flash, style);
                break;
            case Renderer.Cosmos:
                ((PixelShaderEffect<WaveformCosmosShader>)effect).ConstantBuffer = new WaveformCosmosShader(
                    size, low, mid, high, (float)_shaderTime, (float)_shaderTravel, bass, mids, highs, energy, flash, style);
                break;
            case Renderer.Orbs:
                // Rows sit a fixed time apart: the faster the speed, the closer in time.
                ((PixelShaderEffect<WaveformOrbShader>)effect).ConstantBuffer = new WaveformOrbShader(
                    size, low, mid, high, SpectrogramHead(), (float)Math.Clamp(900 / (_settings.Speed * tweaks.Speed), 2, 30), SpectrogramAvailable(),
                    energy, flash);
                break;
            case Renderer.Wild:
                ((PixelShaderEffect<WaveformWildShader>)effect).ConstantBuffer = new WaveformWildShader(
                    size, low, mid, high, (float)_shaderTime, (float)_shaderTravel, bass, mids, highs, energy, flash, style);
                break;
            default:
                ((PixelShaderEffect<WaveformCrazyShader>)effect).ConstantBuffer = new WaveformCrazyShader(
                    size, low, mid, high, (float)_shaderTime, (float)_shaderTravel, bass, mids, highs, energy, flash, style);
                break;
        }
        ds.DrawImage(Finish(renderer, effect, size, tweaks), 0, 0, area, opacity);

        // Blend over: a second view from the mashup set added on top as light, pulsing with the bass.
        if (layer is null && tweaks.Blend > 0.01)
        {
            AdvanceMashup();
            var over = MashupPool[_mashTop] == view ? MashupPool[_mashBase] : MashupPool[_mashTop];
            var rise = (float)Math.Clamp((_time - _mashSince) / 2.5, 0, 1);
            ds.Blend = CanvasBlend.Add;
            DrawView(ds, over, size, area, palette, opacity * rise * (float)tweaks.Blend * (0.45f + 0.4f * bass + 0.15f * flash), scale,
                tweaks with { Blend = 0, Hue = 0 });
            ds.Blend = CanvasBlend.SourceOver;
        }
    }

    /// <summary>Brightness and camera movement on top of a view's shader, when either is in use.</summary>
    private ICanvasImage Finish(Renderer renderer, ICanvasImage effect, Float2 size, WaveformTweaks tweaks)
    {
        var bright = Math.Abs(tweaks.Brightness - 1) > 0.01;
        var moving = _cameraMix > 0.001;
        if (!bright && !moving) return effect;
        if (!_finishes.TryGetValue(renderer, out var finish))
        {
            var exposure = new ExposureEffect { Source = effect };
            finish = (exposure, new Transform2DEffect { Source = exposure });
            _finishes[renderer] = finish;
        }
        finish.Bright.Exposure = (float)Math.Log2(tweaks.Brightness);
        finish.Move.TransformMatrix = moving ? CameraMove(size) : Matrix3x2.Identity;
        return finish.Move;
    }

    /// <summary>Views that mix well; the mashup layers two of them with additive light.</summary>
    private static readonly WaveformView[] MashupPool =
    [
        WaveformView.Hyperspace, WaveformView.KaleidoTunnel, WaveformView.LiquidColour, WaveformView.NeonRings, WaveformView.Lasers,
        WaveformView.Kaleidoscope, WaveformView.Vortex, WaveformView.Warp, WaveformView.Aurora, WaveformView.StarNest,
        WaveformView.Plasma, WaveformView.HexPlasma, WaveformView.Voronoi, WaveformView.Helix, WaveformView.Lightning,
        WaveformView.Fire, WaveformView.Metaballs, WaveformView.OrbField, WaveformView.SynthGrid, WaveformView.Truchet
    ];
    private int _mashBase, _mashTop = 1;
    private double _mashSince;

    /// <summary>
    /// Two views at once: a base and a top layer added as light, the top pulsing with the bass.
    /// Every eight seconds the top becomes the base and a new top fades in.
    /// </summary>
    private void DrawMashup(CanvasDrawingSession ds, Float2 size, Windows.Foundation.Rect area, WaveformPalette palette, float opacity, float scale,
        WaveformTweaks tweaks)
    {
        AdvanceMashup();
        var rise = (float)Math.Clamp((_time - _mashSince) / 2.5, 0, 1);
        var layer = tweaks with { Blend = 0 };
        DrawView(ds, MashupPool[_mashBase], size, area, palette, opacity, scale, layer);
        ds.Blend = CanvasBlend.Add;
        // The Blend slider makes the top layer stronger still.
        var strength = 0.35f + 0.45f * _drive.Bass + 0.2f * _beat.Flash;
        DrawView(ds, MashupPool[_mashTop], size, area, palette, opacity * rise * strength * (1 + (float)tweaks.Blend), scale, layer);
        ds.Blend = CanvasBlend.SourceOver;
    }

    /// <summary>Every eight seconds the top layer becomes the base and a new top fades in.</summary>
    private void AdvanceMashup()
    {
        if (_time - _mashSince < 8) return;
        _mashSince = _time;
        _mashBase = _mashTop;
        _mashTop = (_mashTop + 1 + (int)(_beat.Flash * 7) + (int)(_time % 5)) % MashupPool.Length;
        if (_mashTop == _mashBase) _mashTop = (_mashTop + 1) % MashupPool.Length;
    }

    /// <summary>Fills the live data texture with what this frame's views read.</summary>
    private void PrepareData(WaveformCapture capture, Needs needs, double seconds)
    {
        const Needs live = Needs.Spectrum | Needs.Scope | Needs.Trails | Needs.Stereo | Needs.Bands | Needs.Pulse;
        if ((needs & live) == Needs.None) return;
        var gain = _levels.Gain;
        var stereo = needs.HasFlag(Needs.Stereo);
        capture.Samples.CopyLatest(_recent, stereo ? _recentLeft : [], stereo ? _recentRight : []);
        var newest = _recent.AsSpan(_recent.Length - WaveformSpectrum.Size);
        Array.Clear(_data);
        // The camera's look-around for 3D views (row 1, first entry, Z and W).
        _data[Index(1, 0) + 2] = _cameraYaw;
        _data[Index(1, 0) + 3] = _cameraPitch;
        // Scopes fill their height: on top of the level gain, the shown stretch of wave is scaled
        // so its loudest point reaches about nine tenths (quick to shrink, slower to grow).
        if (needs.HasFlag(Needs.Scope) || needs.HasFlag(Needs.Bands) || stereo)
        {
            var loudest = 0f;
            for (var i = Math.Max(0, _recent.Length - DataWidth * 2); i < _recent.Length; i++) loudest = MathF.Max(loudest, MathF.Abs(_recent[i]));
            var target = Math.Clamp(0.9f / MathF.Max(loudest * gain, 0.02f), 1, 6);
            _scopeBoost += (target - _scopeBoost) * (float)Math.Min(1, seconds * (target < _scopeBoost ? 12 : 1.5));
            gain *= _scopeBoost;
        }

        if (needs.HasFlag(Needs.Scope) || needs.HasFlag(Needs.Bands))
        {
            var start = WaveformScope.FindStart(_recent, DataWidth, gain);
            WaveformScope.Copy(_recent, start, _scope, gain);
            for (var i = 0; i < DataWidth; i++) _data[Index(0, i)] = _scope[i];
            if (needs.HasFlag(Needs.Bands))
            {
                WaveformScope.SplitBands(_recent, capture.Samples.SampleRate, _bandLow, _bandMid, _bandHigh);
                for (var i = 0; i < DataWidth; i++)
                {
                    _data[Index(2, i) + 1] = Math.Clamp(_bandLow[start + i] * gain, -1, 1);
                    _data[Index(2, i) + 2] = Math.Clamp(_bandMid[start + i] * gain * 1.6f, -1, 1);
                    _data[Index(2, i) + 3] = Math.Clamp(_bandHigh[start + i] * gain * 3f, -1, 1);
                }
            }
        }
        if (needs.HasFlag(Needs.Trails))
        {
            // Rows 2-7 hold the last six frames for the CRT's phosphor afterglow.
            for (var k = 0; k < Trails; k++)
            {
                var old = _trails[(_trailHead - k - 1 + Trails * 2) % Trails];
                for (var i = 0; i < DataWidth; i++) _data[Index(2 + k, i)] = old[i];
            }
            _scope.CopyTo(_trails[_trailHead], 0);
            _trailHead = (_trailHead + 1) % Trails;
        }
        if (stereo)
        {
            for (var i = 0; i < DataWidth; i++)
            {
                _data[Index(1, i)] = Math.Clamp(_recentLeft[i] * gain, -1, 1);
                _data[Index(1, i) + 1] = Math.Clamp(_recentRight[i] * gain, -1, 1);
            }
        }
        if (needs.HasFlag(Needs.Spectrum))
        {
            _spectrum.Update(newest, capture.Samples.SampleRate, seconds);
            for (var band = 0; band < WaveformSpectrum.Bands; band++)
            {
                _data[Index(0, band) + 1] = _spectrum.Levels[band];
                _data[Index(0, band) + 2] = _spectrum.Peaks[band];
            }
        }
        if (needs.HasFlag(Needs.Pulse))
        {
            _pulse.Update(newest, gain, seconds);
            for (var bar = 0; bar < WaveformPulse.Bars; bar++) _data[Index(0, bar) + 3] = _pulse.Levels[bar];
        }
        DataTexture().Update([0, 0], [DataWidth, DataRows], [DataWidth * 16], MemoryMarshal.AsBytes(_data.AsSpan()));

        static int Index(int row, int entry) => (row * DataWidth + entry) * 4;
    }

    /// <summary>Writes the history entries completed since the last frame into the history texture.</summary>
    private void UploadHistory(long written)
    {
        var entries = _newEntries.Count > WaveformHistory.Length
            ? _newEntries.GetRange(_newEntries.Count - WaveformHistory.Length, WaveformHistory.Length)
            : _newEntries;
        var first = written - entries.Count;
        var index = 0;
        while (index < entries.Count)
        {
            var slot = (int)((first + index) % WaveformHistory.Length);
            var run = Math.Min(entries.Count - index, WaveformHistory.Length - slot);
            var values = new float[run * 4];
            for (var i = 0; i < run; i++)
            {
                var entry = entries[index + i];
                values[i * 4] = entry.Peak;
                values[i * 4 + 1] = entry.Rms;
                values[i * 4 + 2] = entry.LowWeight;
                values[i * 4 + 3] = entry.HighWeight;
            }
            HistoryTexture().Update([(uint)slot], [(uint)(slot + run)], [], MemoryMarshal.AsBytes(values.AsSpan()));
            index += run;
        }
        _newEntries.Clear();
    }

    private void AddSpectrogramRows(double seconds)
    {
        if (!_spectrogramLive)
        {
            // Starting (or coming back to) a spectrogram view: begin its history from now.
            _spectrogramLive = true;
            _spectrogramRows = 0;
            _spectrogramCarry = 0;
        }
        // Rows are added at a steady 60 a second whatever the frame rate, so speed stays true.
        _spectrogramCarry += seconds * SpectrogramRate;
        var rows = Math.Min(8, (int)Math.Floor(_spectrogramCarry));
        _spectrogramCarry -= Math.Floor(_spectrogramCarry);
        var levels = _spectrum.Levels;
        for (var band = 0; band < levels.Length; band++) _spectrogramRow[band] = (byte)Math.Round(levels[band] * 255);
        for (var row = 0; row < rows; row++)
        {
            var slot = (uint)(_spectrogramRows % WaveformHistory.Length);
            SpectrogramTexture().Update([0, slot], [(uint)WaveformSpectrum.Bands, slot + 1], [(uint)WaveformSpectrum.Bands], _spectrogramRow);
            _spectrogramRows++;
        }
    }

    // One row behind the newest, moving smoothly between row updates.
    private float SpectrogramHead() => (float)(Math.Max(0, _spectrogramRows - 2 + _spectrogramCarry) % WaveformHistory.Length);

    private float SpectrogramAvailable() => (float)Math.Min(Math.Max(0, _spectrogramRows - 1 + _spectrogramCarry), WaveformHistory.Length - 1);

    private static Float3 ToFloat3(WaveformRgb colour) => new(colour.R, colour.G, colour.B);

    private static Windows.UI.Color ToColor(WaveformRgb colour, float opacity) => Windows.UI.Color.FromArgb(
        (byte)Math.Clamp(opacity * 255, 0, 255),
        (byte)Math.Clamp(colour.R * 255, 0, 255),
        (byte)Math.Clamp(colour.G * 255, 0, 255),
        (byte)Math.Clamp(colour.B * 255, 0, 255));
}
