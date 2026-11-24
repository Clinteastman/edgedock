using ComputeSharp;
using ComputeSharp.D2D1;
using ComputeSharp.D2D1.Interop;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EdgeDock.Controls;

/// <summary>
/// Pictures the PC's sound (or a chosen input) in one of several views: scrolling wave, centre
/// out, spectrogram, spectrum bars, mirror spectrum, spectrum line, oscilloscope or pulse.
/// Every pixel is worked out by a GPU shader (see WaveformShaders.cs); each frame the processor
/// only reads the new sound, updates one small texture and issues one draw. Frames follow the
/// compositor's own frame clock, so nothing spins between frames. Capture and drawing both stop
/// whenever the widget is not really on screen.
/// </summary>
internal sealed class WaveformWidget : UserControl
{
    private static readonly Windows.UI.Color PanelColour = Windows.UI.Color.FromArgb(255, 6, 7, 12);
    private const int DataLength = 1024;
    private const int SpectrogramRate = 60;

    private readonly WaveformSettings _settings;
    private readonly WaveformPalette _palette;
    private readonly Grid _root = new();
    private readonly TextBlock _status = new()
    {
        Margin = new Thickness(18, 0, 18, 14),
        VerticalAlignment = VerticalAlignment.Bottom,
        TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 200, 194, 212)),
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false
    };
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
    private CanvasControl? _canvas;
    private WaveformCapture? _capture;
    private bool _rendering;

    // Per-frame state. Everything runs on the UI thread: drawing is one call per frame.
    private readonly ICanvasImage _effect;
    private readonly D2D1ResourceTextureManager _texture;
    private readonly WaveformLevels _levels;
    private readonly WaveformSpectrum _spectrum;
    private readonly WaveformPulse _pulse = new();
    private readonly float[] _recent = new float[WaveformSpectrum.Size * 2];
    private readonly float[] _scope = new float[WaveformScope.Points];
    private readonly float[] _data = new float[DataLength * 4];
    private readonly byte[] _spectrogramRow = new byte[WaveformSpectrum.Bands];
    private readonly List<WaveformBar> _newEntries = [];
    private readonly System.Diagnostics.Stopwatch _frameClock = new();
    private WaveformHistory? _history;
    private WaveformClock _clock = new();
    private TimeSpan _lastFrame;
    private long _received;
    private long _spectrogramRows;
    private double _spectrogramCarry;
    private float _historyScale;

    public WaveformWidget(Func<WaveformSettings> settings)
    {
        _settings = WaveformSettings.Normalize(settings());
        _palette = WaveformPalette.For(_settings.Look);
        _levels = new WaveformLevels(_settings.AutoLevel);
        _spectrum = new WaveformSpectrum(_settings.AutoLevel);
        (_effect, _texture) = CreateView(_settings.View);

        _root.Children.Add(_status);
        IsTabStop = false;
        Content = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(PanelColour),
            Child = _root
        };
        AutomationProperties.SetName(this, "Waveform");
        AutomationProperties.SetHelpText(this, _settings.InputDeviceId is null
            ? "A moving picture of the sound playing on this PC."
            : "A moving picture of the sound from the chosen input.");
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
    }

    /// <summary>The view's shader and the texture it reads.</summary>
    private static (ICanvasImage Effect, D2D1ResourceTextureManager Texture) CreateView(WaveformView view)
    {
        switch (view)
        {
            case WaveformView.Scroll or WaveformView.CentreOut:
            {
                var texture = new D2D1ResourceTextureManager(
                    [WaveformHistory.Length], D2D1BufferPrecision.Float32, D2D1ChannelDepth.Four, D2D1Filter.MinMagMipLinear,
                    [D2D1ExtendMode.Wrap], new byte[WaveformHistory.Length * 16], []);
                var effect = new PixelShaderEffect<WaveformScrollShader>();
                effect.ResourceTextureManagers[0] = texture;
                return (effect, texture);
            }
            case WaveformView.Spectrogram:
            {
                var texture = new D2D1ResourceTextureManager(
                    [(uint)WaveformSpectrum.Bands, WaveformHistory.Length], D2D1BufferPrecision.UInt8Normalized, D2D1ChannelDepth.One,
                    D2D1Filter.MinMagMipLinear, [D2D1ExtendMode.Clamp, D2D1ExtendMode.Wrap],
                    new byte[WaveformSpectrum.Bands * WaveformHistory.Length], [(uint)WaveformSpectrum.Bands]);
                var effect = new PixelShaderEffect<WaveformSpectrogramShader>();
                effect.ResourceTextureManagers[0] = texture;
                return (effect, texture);
            }
            case WaveformView.Oscilloscope:
            {
                var texture = DataTexture();
                var effect = new PixelShaderEffect<WaveformScopeShader>();
                effect.ResourceTextureManagers[0] = texture;
                return (effect, texture);
            }
            default:
            {
                var texture = DataTexture();
                var effect = new PixelShaderEffect<WaveformBarsShader>();
                effect.ResourceTextureManagers[0] = texture;
                return (effect, texture);
            }
        }

        static D2D1ResourceTextureManager DataTexture() => new(
            [DataLength], D2D1BufferPrecision.Float32, D2D1ChannelDepth.Four, D2D1Filter.MinMagMipLinear,
            [D2D1ExtendMode.Clamp], new byte[DataLength * 16], []);
    }

    private void Start()
    {
        if (_timer is not null) return;
        _canvas = new CanvasControl
        {
            ClearColor = Windows.UI.Color.FromArgb(0, 0, 0, 0),
            IsTabStop = false,
            IsHitTestVisible = false // Swipes and taps go to the panel, never to the drawing.
        };
        _canvas.Draw += Canvas_Draw;
        _root.Children.Insert(0, _canvas);
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.Tick += (_, _) => FollowVisibility();
        _timer.Start();
        FollowVisibility();
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

    /// <summary>Listens and draws only while seen; see <see cref="WidgetVisibility"/>.</summary>
    private void FollowVisibility()
    {
        if (_canvas is null) return;
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
            _spectrogramRows = 0;
            _spectrogramCarry = 0;
            _newEntries.Clear();
            _frameClock.Restart();
            _lastFrame = TimeSpan.Zero;
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

        // Read the new 5 ms columns: they set the level, the colour and the scrolling history.
        var scrolling = _settings.View is WaveformView.Scroll or WaveformView.CentreOut;
        if (scrolling && (_history is null || _historyScale != scale))
        {
            _history = new WaveformHistory(_settings.Speed * scale);
            _historyScale = scale;
            _newEntries.Clear();
        }
        while (capture.Ring.TryRead(out var column))
        {
            _received++;
            var bar = _levels.Next(column);
            if (scrolling && _history!.Add(bar, out var entry)) _newEntries.Add(entry);
        }

        var size = new Float2(width, height);
        var (low, mid, high) = (ToFloat3(_palette.Low), ToFloat3(_palette.Mid), ToFloat3(_palette.High));
        switch (_settings.View)
        {
            case WaveformView.Scroll or WaveformView.CentreOut:
                DrawScroll(size, low, mid, high, seconds);
                break;
            case WaveformView.Spectrogram:
                DrawSpectrogram(capture, size, low, mid, high, seconds, scale);
                break;
            case WaveformView.Oscilloscope:
                FillLiveData(capture, seconds, spectrum: false);
                ((PixelShaderEffect<WaveformScopeShader>)_effect).ConstantBuffer = new WaveformScopeShader(size, CurrentColour());
                break;
            default:
                FillLiveData(capture, seconds, spectrum: _settings.View != WaveformView.Pulse);
                var (style, count) = _settings.View switch
                {
                    WaveformView.MirrorSpectrum => (1, WaveformSpectrum.Bands),
                    WaveformView.SpectrumLine => (2, WaveformSpectrum.Bands),
                    WaveformView.Pulse => (3, WaveformPulse.Bars),
                    _ => (0, WaveformSpectrum.Bands)
                };
                ((PixelShaderEffect<WaveformBarsShader>)_effect).ConstantBuffer =
                    new WaveformBarsShader(size, low, mid, high, CurrentColour(), style, count);
                break;
        }

        var ds = args.DrawingSession;
        ds.Units = CanvasUnits.Pixels;
        ds.DrawImage(_effect, 0, 0, new Windows.Foundation.Rect(0, 0, width, height));
    }

    private void DrawScroll(Float2 size, Float3 low, Float3 mid, Float3 high, double seconds)
    {
        var history = _history!;
        UploadHistory(history.Written);
        var position = _clock.Advance(seconds, _received);
        // One entry behind the newest, so the picture never reads an entry still being filled.
        var head = Math.Min(position / history.ColumnsPerEntry - 1, history.Written - 1);
        if (head < 0) head = 0;
        var available = (float)Math.Min(head + 1, WaveformHistory.Length - 1);
        ((PixelShaderEffect<WaveformScrollShader>)_effect).ConstantBuffer = new WaveformScrollShader(
            size, low, mid, high, (float)(head % WaveformHistory.Length), (float)history.PixelsPerEntry, available,
            _settings.View == WaveformView.CentreOut ? 1 : 0);
    }

    /// <summary>Writes the entries completed since the last frame into the history texture.</summary>
    private void UploadHistory(long written)
    {
        var entries = _newEntries.Count > WaveformHistory.Length ? _newEntries.GetRange(_newEntries.Count - WaveformHistory.Length, WaveformHistory.Length) : _newEntries;
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
            _texture.Update([(uint)slot], [(uint)(slot + run)], [], System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()));
            index += run;
        }
        _newEntries.Clear();
    }

    private void DrawSpectrogram(WaveformCapture capture, Float2 size, Float3 low, Float3 mid, Float3 high, double seconds, float scale)
    {
        capture.Samples.CopyLatest(_recent.AsSpan(0, WaveformSpectrum.Size));
        _spectrum.Update(_recent.AsSpan(0, WaveformSpectrum.Size), capture.Samples.SampleRate, seconds);
        // Rows are added at a steady 60 a second whatever the frame rate, so speed stays true.
        _spectrogramCarry += seconds * SpectrogramRate;
        var rows = Math.Min(8, (int)Math.Floor(_spectrogramCarry));
        _spectrogramCarry -= Math.Floor(_spectrogramCarry);
        var levels = _spectrum.Levels;
        for (var band = 0; band < levels.Length; band++) _spectrogramRow[band] = (byte)Math.Round(levels[band] * 255);
        for (var row = 0; row < rows; row++)
        {
            var slot = (uint)(_spectrogramRows % WaveformHistory.Length);
            _texture.Update([0, slot], [(uint)WaveformSpectrum.Bands, slot + 1], [(uint)WaveformSpectrum.Bands], _spectrogramRow);
            _spectrogramRows++;
        }
        // One row behind the newest, moving smoothly between row updates.
        var head = Math.Max(0, _spectrogramRows - 2 + _spectrogramCarry);
        ((PixelShaderEffect<WaveformSpectrogramShader>)_effect).ConstantBuffer = new WaveformSpectrogramShader(
            size, low, mid, high, (float)(head % WaveformHistory.Length), (float)(_settings.Speed * scale / SpectrogramRate),
            (float)Math.Min(head + 1, WaveformHistory.Length - 1));
    }

    /// <summary>Fills the live data texture: scope samples, spectrum levels and peaks, pulse bars.</summary>
    private void FillLiveData(WaveformCapture capture, double seconds, bool spectrum)
    {
        capture.Samples.CopyLatest(_recent);
        Array.Clear(_data);
        var gain = _levels.Gain;
        if (_settings.View == WaveformView.Oscilloscope)
        {
            WaveformScope.Prepare(_recent, _scope, gain);
            for (var i = 0; i < _scope.Length; i++) _data[i * 4] = _scope[i];
        }
        if (spectrum)
        {
            _spectrum.Update(_recent.AsSpan(_recent.Length - WaveformSpectrum.Size), capture.Samples.SampleRate, seconds);
            for (var band = 0; band < WaveformSpectrum.Bands; band++)
            {
                _data[band * 4 + 1] = _spectrum.Levels[band];
                _data[band * 4 + 2] = _spectrum.Peaks[band];
            }
        }
        if (_settings.View == WaveformView.Pulse)
        {
            _pulse.Update(_recent.AsSpan(_recent.Length - WaveformSpectrum.Size), gain, seconds);
            for (var bar = 0; bar < WaveformPulse.Bars; bar++) _data[bar * 4 + 3] = _pulse.Levels[bar];
        }
        _texture.Update([0], [DataLength], [], System.Runtime.InteropServices.MemoryMarshal.AsBytes(_data.AsSpan()));
    }

    private Float3 CurrentColour()
    {
        var (low, mid, high) = _levels.Weights;
        return ToFloat3(_palette.Mix(low, mid, high));
    }

    private static Float3 ToFloat3(WaveformRgb colour) => new(colour.R, colour.G, colour.B);
}
