using System.Runtime.InteropServices;
using ComputeSharp;
using ComputeSharp.D2D1;
using ComputeSharp.D2D1.Interop;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace EdgeDock.Controls;

/// <summary>
/// Pictures the PC's sound (or a chosen input) in one of many views: waves, spectrum, CRT
/// oscilloscopes, flashy, demo-scene and crazy ones. Every pixel is worked out by a GPU shader
/// (WaveformShaders.cs); each frame the processor only reads the new sound, updates a few small
/// textures and issues a draw or two. Frames follow the compositor's own clock, so nothing spins
/// between frames, and capture and drawing both stop whenever the widget is not on screen.
/// Tap the widget for a touch bar: previous/next view and look, cycle, and a grid of all views.
/// </summary>
internal sealed class WaveformWidget : UserControl
{
    private static readonly Windows.UI.Color PanelColour = Windows.UI.Color.FromArgb(255, 6, 7, 12);
    private const int DataWidth = 1024;
    private const int DataRows = 8;
    private const int SpectrogramRate = 60;
    private const double FadeSeconds = 0.8;
    private const int Trails = 6;

    private enum Renderer { Scroll, Spectrogram, Ridges, Bars, Scope, Crt, Lissajous, Ring, Field, Demo, Crazy, Borrowed, Wild }

    [Flags]
    private enum Needs { None = 0, History = 1, SpectrogramRows = 2, Spectrum = 4, Scope = 8, Trails = 16, Stereo = 32, Bands = 64, Pulse = 128 }

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
    private double _time, _travel, _hue, _scrollerOffset;
    private WaveformView? _fadingFrom;
    private double _fadeStarted, _viewSince;
    private string _scroller = "EDGEDOCK";

    public WaveformWidget(Func<WaveformSettings> settings, Action<WaveformSettings>? save = null)
    {
        _settings = WaveformSettings.Normalize(settings());
        _save = save;
        _levels = new WaveformLevels(_settings.AutoLevel);
        _spectrum = new WaveformSpectrum(_settings.AutoLevel);

        _root.Children.Add(_status);
        BuildBar();
        BuildPicker();
        IsTabStop = false;
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
        Unloaded += (_, _) => Stop();
    }

    /// <summary>Set by the panel; the demo scrollers show what is playing.</summary>
    internal MediaSessionService? Media { get; set; }

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
    }

    /// <summary>Twice a second: follow visibility, hide an idle touch bar, refresh the scroller text.</summary>
    private void Tick()
    {
        if (_canvas is null) return;
        if (_bar.Visibility == Visibility.Visible && _picker.Visibility != Visibility.Visible &&
            DateTime.UtcNow - _barShownAt > TimeSpan.FromSeconds(6))
            _bar.Visibility = Visibility.Collapsed;
        var playing = Media?.CurrentSnapshot is { HasSession: true } media && !string.IsNullOrWhiteSpace(media.Title)
            ? (string.IsNullOrWhiteSpace(media.Artist) ? media.Title : $"{media.Artist} - {media.Title}")
            : "";
        // The scroller's font has Latin letters and punctuation only; emoji and other scripts would show as boxes.
        playing = new string(playing.Where(c => c is >= ' ' and <= '~' or >= ' ' and <= 'ſ').ToArray()).Trim();
        _scroller = playing.Length > 0 ? playing : "EDGEDOCK";

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

    private void ChangeSettings(WaveformSettings next)
    {
        next = WaveformSettings.Normalize(next);
        if (next.View != _settings.View)
        {
            _fadingFrom = _settings.View;
            _fadeStarted = _time;
            _viewSince = _time;
        }
        _settings = next;
        UpdateBar();
        _save?.Invoke(next);
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
            if (element == _bar || element == _picker) return;
        if (_picker.Visibility == Visibility.Visible) _picker.Visibility = Visibility.Collapsed;
        else if (_bar.Visibility == Visibility.Visible) _bar.Visibility = Visibility.Collapsed;
        else
        {
            UpdateBar();
            _bar.Visibility = Visibility.Visible;
        }
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
        row.Children.Add(TouchButton("◀", "Previous view", (_, _) => StepView(-1)));
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
            _picker.Visibility = Visibility.Visible;
        }, 120));

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
        var close = TouchButton("✕", "Close", (_, _) => _picker.Visibility = Visibility.Collapsed);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        header.Children.Add(close);
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
                    _picker.Visibility = Visibility.Collapsed;
                }, 188);
                tile.HorizontalAlignment = HorizontalAlignment.Stretch;
                tile.Margin = new Thickness(0, 0, 8, 8);
                _tiles[tile] = view;
                tiles.Children.Add(tile);
            }
            content.Children.Add(tiles);
        }

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
        var cycles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var cycle in Enum.GetValues<WaveformColourCycle>())
            cycles.Children.Add(TouchButton(WaveformNames.ColourCycle(cycle), "Colour cycle: " + WaveformNames.ColourCycle(cycle),
                (_, _) => ChangeSettings(_settings with { ColourCycle = cycle }), 120));
        content.Children.Add(cycles);

        _picker.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(238, 12, 11, 20));
        _picker.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _picker.Visibility = Visibility.Collapsed;
        _root.Children.Add(_picker);

        static TextBlock Heading(string text) => new() { Text = text, FontSize = 16, Opacity = 0.75, Margin = new Thickness(0, 6, 0, 0) };

        static Windows.UI.Color Dim(WaveformRgb c) =>
            Windows.UI.Color.FromArgb(255, (byte)(c.R * 200), (byte)(c.G * 200), (byte)(c.B * 200));
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
        WaveformView.Copper => (Renderer.Demo, 0),
        WaveformView.Tunnel => (Renderer.Demo, 1),
        WaveformView.Checkerboard => (Renderer.Demo, 2),
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
        _ => (Renderer.Wild, 4)
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
        (Renderer.Wild, _) => Needs.Spectrum,
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
        if (_settings.CycleSeconds > 0 && _time - _viewSince >= _settings.CycleSeconds)
            ChangeSettings(_settings with { View = _settings.NextInCycle(_settings.View) });

        var fade = _fadingFrom is null ? 1 : Math.Clamp((_time - _fadeStarted) / FadeSeconds, 0, 1);
        if (fade >= 1) _fadingFrom = null;
        var needs = NeedsOf(_settings.View) | (_fadingFrom is { } from ? NeedsOf(from) : Needs.None);

        // Read the new 5 ms columns: level, colour, beat and the scrolling history.
        var scrolling = needs.HasFlag(Needs.History);
        if (scrolling && (_history is null || _historyScale != scale))
        {
            _history = new WaveformHistory(_settings.Speed * scale);
            _historyScale = scale;
            _newEntries.Clear();
        }
        if (!scrolling) _history = null;
        while (capture.Ring.TryRead(out var column))
        {
            _received++;
            var bar = _levels.Next(column);
            _beat.Add(column.Low, bar.Peak, WaveformAnalyzer.BlockSeconds);
            if (_history is not null && _history.Add(bar, out var entry)) _newEntries.Add(entry);
        }
        _scrollPosition = _clock.Advance(seconds, _received);
        _travel += seconds * (0.25 + 2.2 * _beat.Energy);
        _hue = (_hue + seconds * WaveformSettings.CycleDegreesPerSecond(_settings.ColourCycle)) % 360;
        var palette = WaveformPalette.For(_settings.Look);
        if (_hue != 0) palette = palette.Rotate(_hue);

        PrepareData(capture, needs, seconds);
        if (_history is not null) UploadHistory(_history.Written);
        if (needs.HasFlag(Needs.SpectrogramRows)) AddSpectrogramRows(seconds);
        else _spectrogramLive = false;

        var size = new Float2(width, height);
        var ds = args.DrawingSession;
        ds.Units = CanvasUnits.Pixels;
        var area = new Windows.Foundation.Rect(0, 0, width, height);
        if (_fadingFrom is { } old) DrawView(ds, old, size, area, palette, (float)(1 - fade), scale);
        DrawView(ds, _settings.View, size, area, palette, (float)fade, scale);
    }

    private void DrawView(CanvasDrawingSession ds, WaveformView view, Float2 size, Windows.Foundation.Rect area,
        WaveformPalette palette, float opacity, float scale)
    {
        if (opacity <= 0.001f) return;
        var (renderer, style) = Map(view);
        var (low, mid, high) = (ToFloat3(palette.Low), ToFloat3(palette.Mid), ToFloat3(palette.High));
        var (lowWeight, midWeight, highWeight) = _levels.Weights;
        var current = ToFloat3(palette.Mix(lowWeight, midWeight, highWeight));
        var (bass, mids, highs) = _levels.Bands;
        var effect = Effect(renderer);
        switch (renderer)
        {
            case Renderer.Scroll:
            {
                var history = _history!;
                // One entry behind the newest, so the picture never reads an entry still being filled.
                var head = Math.Max(0, Math.Min(_scrollPosition / history.ColumnsPerEntry - 1, history.Written - 1));
                ((PixelShaderEffect<WaveformScrollShader>)effect).ConstantBuffer = new WaveformScrollShader(
                    size, low, mid, high, (float)(head % WaveformHistory.Length), (float)history.PixelsPerEntry,
                    (float)Math.Min(head + 1, WaveformHistory.Length - 1), style);
                break;
            }
            case Renderer.Spectrogram:
                ((PixelShaderEffect<WaveformSpectrogramShader>)effect).ConstantBuffer = new WaveformSpectrogramShader(
                    size, low, mid, high, SpectrogramHead(), (float)(_settings.Speed * scale / SpectrogramRate), SpectrogramAvailable());
                break;
            case Renderer.Ridges:
                // Ridges sit a fixed time apart: the faster the speed, the closer in time.
                ((PixelShaderEffect<WaveformRidgesShader>)effect).ConstantBuffer = new WaveformRidgesShader(
                    size, low, mid, high, SpectrogramHead(), (float)Math.Clamp(1200 / _settings.Speed, 2, 40), SpectrogramAvailable());
                break;
            case Renderer.Bars:
                ((PixelShaderEffect<WaveformBarsShader>)effect).ConstantBuffer = new WaveformBarsShader(
                    size, low, mid, high, current, style,
                    style == 3 ? WaveformPulse.Bars : style == 4 ? 48 : WaveformSpectrum.Bands, _beat.Flash);
                break;
            case Renderer.Scope:
                ((PixelShaderEffect<WaveformScopeShader>)effect).ConstantBuffer = new WaveformScopeShader(size, current, low, mid, high, style);
                break;
            case Renderer.Crt:
                ((PixelShaderEffect<WaveformCrtShader>)effect).ConstantBuffer = new WaveformCrtShader(size, mid, style, (float)_time);
                break;
            case Renderer.Lissajous:
                ((PixelShaderEffect<WaveformLissajousShader>)effect).ConstantBuffer = new WaveformLissajousShader(size, current, low);
                break;
            case Renderer.Ring:
                ((PixelShaderEffect<WaveformRingShader>)effect).ConstantBuffer = new WaveformRingShader(
                    size, low, mid, high, current, bass, _beat.Flash, (float)_time);
                break;
            case Renderer.Field:
                ((PixelShaderEffect<WaveformFieldShader>)effect).ConstantBuffer = new WaveformFieldShader(
                    size, low, mid, high, (float)_time, (float)_travel, bass, mids, highs, _beat.Energy, _beat.Flash, style);
                break;
            case Renderer.Demo:
                ((PixelShaderEffect<WaveformDemoShader>)effect).ConstantBuffer = new WaveformDemoShader(
                    size, low, mid, high, (float)_time, (float)_travel, bass, _beat.Energy, _beat.Flash, style,
                    MathF.Max(2, MathF.Round(3 * scale)));
                break;
            case Renderer.Borrowed:
                ((PixelShaderEffect<WaveformBorrowedShader>)effect).ConstantBuffer = new WaveformBorrowedShader(
                    size, low, mid, high, (float)_time, (float)_travel, bass, _beat.Energy, _beat.Flash, style);
                break;
            case Renderer.Wild:
                ((PixelShaderEffect<WaveformWildShader>)effect).ConstantBuffer = new WaveformWildShader(
                    size, low, mid, high, (float)_time, (float)_travel, bass, mids, highs, _beat.Energy, _beat.Flash, style);
                break;
            default:
                ((PixelShaderEffect<WaveformCrazyShader>)effect).ConstantBuffer = new WaveformCrazyShader(
                    size, low, mid, high, (float)_time, (float)_travel, bass, mids, highs, _beat.Energy, _beat.Flash, style);
                break;
        }
        ds.DrawImage(effect, 0, 0, area, opacity);
        if (view is WaveformView.Copper or WaveformView.Checkerboard) DrawScroller(ds, size, palette, opacity, view == WaveformView.Copper);
    }

    /// <summary>The demo scroller: big letters riding a sine wave right to left, coloured by the look.</summary>
    private void DrawScroller(CanvasDrawingSession ds, Float2 size, WaveformPalette palette, float opacity, bool middle)
    {
        var fontSize = size.Y * 0.13f;
        using var format = new CanvasTextFormat { FontFamily = "Consolas", FontSize = fontSize, FontWeight = FontWeights.Bold };
        var text = $"  {_scroller.ToUpperInvariant()}  •  ";
        var advance = fontSize * 0.62f;
        var loop = text.Length * advance;
        _scrollerOffset = (_scrollerOffset + size.Y * 0.35 * (1 + _beat.Energy) * _frameSeconds) % loop;
        var baseline = middle ? size.Y * 0.42f : size.Y * 0.62f;
        var amplitude = size.Y * (0.12f + 0.06f * _beat.Energy);
        var count = (int)(size.X / advance) + 2;
        var first = (int)Math.Floor(_scrollerOffset / advance);
        var shift = (float)(_scrollerOffset - first * advance);
        for (var i = 0; i < count; i++)
        {
            var letter = text[(first + i) % text.Length];
            if (letter == ' ') continue;
            var x = i * advance - shift;
            var y = baseline + MathF.Sin(x * 0.006f + (float)_time * 3f) * amplitude - fontSize * 0.5f;
            var t = Math.Clamp(x / size.X, 0, 1);
            var colour = t < 0.5f ? Lerp(palette.Low, palette.Mid, t * 2) : Lerp(palette.Mid, palette.High, t * 2 - 1);
            ds.DrawText(letter.ToString(), x + 4, y + 4, Windows.UI.Color.FromArgb((byte)(160 * opacity), 0, 0, 0), format);
            ds.DrawText(letter.ToString(), x, y, ToColor(colour, opacity), format);
        }

        static WaveformRgb Lerp(WaveformRgb a, WaveformRgb b, float t) => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);
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
