namespace EdgeDock;

/// <summary>How the Waveform widget pictures the sound. Each widget shows one view. Saved by name.</summary>
internal enum WaveformView
{
    // Waves over time
    Scroll, CentreOut,
    // Spectrum
    SpectrumBars, MirrorSpectrum, SpectrumLine, LedBars, Spectrogram, Ridges, Ring,
    // Oscilloscopes
    Oscilloscope, CrtScope, CrtVectorscope, StereoScope, BandScopes, Lissajous,
    // Flashy
    Pulse, Club, Warp, Aurora,
    // Demo scene
    Plasma, Tunnel,
    // Crazy
    Kaleidoscope, Vortex, Glitch, Fire, Metaballs, Lightning, Fractal,
    // Space and sky
    StarNest, Clouds, Cloudscape,
    // More crazy
    HypnoCircles, HexPlasma, Lasers, Voronoi, Blob, Truchet, Spheres, Mandelbulb,
    // 3D scenes and more space
    Lattice, FractalCubes, Mandelbox, OrbField, FractalForest, BlackHole, Mountains, Ocean, NeonRings,
    // Mania
    Hyperspace, SynthGrid, KaleidoTunnel, MandelbrotZoom, City, Helix, LiquidColour, Shards, Mashup
}

/// <summary>The three-colour palettes every view draws with. Saved by name.</summary>
internal enum WaveformLook
{
    Spectrum, Neon, Ember, Ice, Sunset, Ocean, Borealis, Candy,
    Vapor, Toxic, Gold, Blood, Mono, Matrix, Prism, Lava
}

/// <summary>Slowly turns the look's colours around the colour wheel.</summary>
internal enum WaveformColourCycle { Off, Slow, Fast }

/// <summary>
/// Waveform widget choices. A null input means the sound playing on the PC. With
/// <see cref="CycleSeconds"/> above zero the widget moves through <see cref="CycleViews"/> (all
/// views when empty), one every so many seconds, starting from <see cref="View"/>.
/// </summary>
internal sealed record WaveformSettings(string? InputDeviceId, WaveformView View, WaveformLook Look, double Speed, bool AutoLevel,
    WaveformColourCycle ColourCycle = WaveformColourCycle.Off, IReadOnlyList<WaveformView>? CycleViews = null, int CycleSeconds = 0)
{
    public const double MinimumSpeed = 30;
    public const double MaximumSpeed = 600;
    /// <summary>The cycle intervals Settings offers; 0 is off.</summary>
    public static readonly int[] CycleChoices = [0, 10, 20, 30, 60, 120, 300];
    public static WaveformSettings Default { get; } = new(null, WaveformView.Scroll, WaveformLook.Spectrum, 160, true, CycleViews: []);

    /// <summary>The views the cycle moves through, in order: the chosen set, or every view.</summary>
    public IReadOnlyList<WaveformView> CycleSet => CycleViews is { Count: > 0 } chosen ? chosen : Enum.GetValues<WaveformView>();

    /// <summary>The view after <paramref name="view"/> in the cycle (the first, if it is not in the set).</summary>
    public WaveformView NextInCycle(WaveformView view)
    {
        var set = CycleSet;
        var index = -1;
        for (var i = 0; i < set.Count; i++) if (set[i] == view) index = i;
        return set[(index + 1) % set.Count];
    }

    /// <summary>Same choices, comparing the cycle set by its contents.</summary>
    public bool SameAs(WaveformSettings? other) =>
        other is not null && this with { CycleViews = null } == other with { CycleViews = null } &&
        (CycleViews ?? []).SequenceEqual(other.CycleViews ?? []);

    public static WaveformSettings Normalize(WaveformSettings? value)
    {
        if (value is null) return Default;
        return value with
        {
            InputDeviceId = string.IsNullOrWhiteSpace(value.InputDeviceId) ? null : value.InputDeviceId.Trim(),
            View = Enum.IsDefined(value.View) ? value.View : Default.View,
            Look = Enum.IsDefined(value.Look) ? value.Look : Default.Look,
            ColourCycle = Enum.IsDefined(value.ColourCycle) ? value.ColourCycle : Default.ColourCycle,
            CycleViews = (value.CycleViews ?? []).Where(Enum.IsDefined).Distinct().ToArray(),
            CycleSeconds = CycleChoices.Contains(value.CycleSeconds) ? value.CycleSeconds : CycleChoices.Where(choice => choice <= value.CycleSeconds).DefaultIfEmpty(0).Max(),
            // Whole steps of 10 logical pixels a second, as the Settings slider offers.
            Speed = double.IsFinite(value.Speed) ? Math.Round(Math.Clamp(value.Speed, MinimumSpeed, MaximumSpeed) / 10) * 10 : Default.Speed
        };
    }

    /// <summary>Views that show sound over time, so Speed applies to them.</summary>
    public static bool Scrolls(WaveformView view) =>
        view is WaveformView.Scroll or WaveformView.CentreOut or WaveformView.Spectrogram or WaveformView.Ridges or WaveformView.OrbField;

    /// <summary>Degrees a second the colours turn for each colour cycle setting.</summary>
    public static double CycleDegreesPerSecond(WaveformColourCycle cycle) => cycle switch
    {
        WaveformColourCycle.Slow => 12,
        WaveformColourCycle.Fast => 60,
        _ => 0
    };
}

/// <summary>One ~5 ms block of sound: loudest sample, RMS, and RMS of three frequency bands.</summary>
internal readonly record struct WaveformColumn(float Peak, float Rms, float Low, float Mid, float High);

/// <summary>A colour with 0-1 channels.</summary>
internal readonly record struct WaveformRgb(float R, float G, float B)
{
    /// <summary>Moves the colour towards white by <paramref name="amount"/> (0 to 1).</summary>
    public WaveformRgb Whiten(float amount) => new(R + (1 - R) * amount, G + (1 - G) * amount, B + (1 - B) * amount);
}

/// <summary>
/// A drawn moment: heights as a fraction of half the panel (0 to 1), and how much of the low
/// and high palette colours to mix in (the mid colour takes the rest).
/// </summary>
internal readonly record struct WaveformBar(float Peak, float Rms, float LowWeight, float HighWeight)
{
    public float MidWeight => Math.Max(0, 1 - LowWeight - HighWeight);
}

/// <summary>
/// A second-order IIR filter (RBJ audio EQ cookbook), transposed direct form II.
/// Coefficients are normalised so a0 is 1.
/// </summary>
internal struct Biquad
{
    private readonly float _b0, _b1, _b2, _a1, _a2;
    private float _z1, _z2;

    private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = (float)(b0 / a0);
        _b1 = (float)(b1 / a0);
        _b2 = (float)(b2 / a0);
        _a1 = (float)(a1 / a0);
        _a2 = (float)(a2 / a0);
        _z1 = _z2 = 0;
    }

    public static Biquad LowPass(double sampleRate, double frequency, double q)
    {
        var (cos, alpha) = Prepare(sampleRate, frequency, q);
        return new((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public static Biquad HighPass(double sampleRate, double frequency, double q)
    {
        var (cos, alpha) = Prepare(sampleRate, frequency, q);
        return new((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    /// <summary>Band-pass with 0 dB gain at the centre frequency.</summary>
    public static Biquad BandPass(double sampleRate, double frequency, double q)
    {
        var (cos, alpha) = Prepare(sampleRate, frequency, q);
        return new(alpha, 0, -alpha, 1 + alpha, -2 * cos, 1 - alpha);
    }

    private static (double Cos, double Alpha) Prepare(double sampleRate, double frequency, double q)
    {
        // Keep the corner below Nyquist so very low sample rates still give a stable filter.
        var f = Math.Min(frequency, sampleRate * 0.45);
        var w0 = 2 * Math.PI * f / sampleRate;
        return (Math.Cos(w0), Math.Sin(w0) / (2 * q));
    }

    public float Process(float x)
    {
        var y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        // Flush denormals after long silence; they are very slow on some processors.
        if (Math.Abs(_z1) < 1e-20f) _z1 = 0;
        if (Math.Abs(_z2) < 1e-20f) _z2 = 0;
        return y;
    }
}

/// <summary>
/// Turns interleaved float samples into <see cref="WaveformColumn"/>s, one per ~5 ms block, and
/// passes the mono mix to a <see cref="WaveformSampleTap"/> for the live views. Runs on the
/// capture thread only. Peak and RMS use every channel; the three bands use the mono mix
/// (bass under 200 Hz, mids around 1.2 kHz, highs over 5 kHz).
/// </summary>
internal sealed class WaveformAnalyzer
{
    public const double BlockSeconds = 0.005;
    public const double ColumnsPerSecond = 1 / BlockSeconds;
    private readonly int _channels;
    private readonly int _blockFrames;
    private readonly WaveformSampleTap? _tap;
    private Biquad _low, _mid, _high;
    private int _frames;
    private float _peak;
    private double _sumSquares, _lowSquares, _midSquares, _highSquares;

    public WaveformAnalyzer(int sampleRate, int channels, WaveformSampleTap? tap = null)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        _channels = channels;
        _tap = tap;
        if (tap is not null) tap.SampleRate = sampleRate;
        _blockFrames = Math.Max(1, (int)Math.Round(sampleRate * BlockSeconds));
        _low = Biquad.LowPass(sampleRate, 200, 0.707);
        _mid = Biquad.BandPass(sampleRate, 1200, 0.6);
        _high = Biquad.HighPass(sampleRate, 5000, 0.707);
    }

    public int BlockFrames => _blockFrames;
    public long ColumnsWritten { get; private set; }

    /// <summary>Analyses whole frames of interleaved samples; a trailing partial frame is ignored.</summary>
    public void Process(ReadOnlySpan<float> interleaved, WaveformRing ring)
    {
        var frames = interleaved.Length / _channels;
        var scale = 1f / _channels;
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = frame * _channels;
            float mono = 0f, left = 0f, right = 0f;
            for (var channel = 0; channel < _channels; channel++)
            {
                var sample = interleaved[offset + channel];
                if (!float.IsFinite(sample)) sample = 0;
                var magnitude = Math.Abs(sample);
                if (magnitude > _peak) _peak = magnitude;
                _sumSquares += sample * sample;
                mono += sample;
                if (channel == 0) left = sample;
                else if (channel == 1) right = sample;
            }
            AddMono(mono * scale, left, _channels > 1 ? right : left, ring);
        }
    }

    /// <summary>Counts frames that Windows reported as silent, keeping the filters' memory.</summary>
    public void ProcessSilence(int frames, WaveformRing ring)
    {
        for (var frame = 0; frame < frames; frame++) AddMono(0, 0, 0, ring);
    }

    private void AddMono(float mono, float left, float right, WaveformRing ring)
    {
        _tap?.Write(mono, left, right);
        var low = _low.Process(mono);
        var mid = _mid.Process(mono);
        var high = _high.Process(mono);
        _lowSquares += low * low;
        _midSquares += mid * mid;
        _highSquares += high * high;
        if (++_frames < _blockFrames) return;
        var count = (double)_frames;
        ring.TryWrite(new WaveformColumn(
            Math.Min(1f, _peak),
            (float)Math.Min(1, Math.Sqrt(_sumSquares / (count * _channels))),
            (float)Math.Sqrt(_lowSquares / count),
            (float)Math.Sqrt(_midSquares / count),
            (float)Math.Sqrt(_highSquares / count)));
        ColumnsWritten++;
        _frames = 0;
        _peak = 0;
        _sumSquares = _lowSquares = _midSquares = _highSquares = 0;
    }
}

/// <summary>
/// Lock-free single-producer, single-consumer queue of columns: the capture thread writes,
/// the drawing code reads. When full (the reader stopped), new columns are dropped.
/// </summary>
internal sealed class WaveformRing
{
    private readonly WaveformColumn[] _items;
    private readonly long _mask;
    private long _head; // Next slot to write; only the producer changes it.
    private long _tail; // Next slot to read; only the consumer changes it.

    public WaveformRing(int capacity)
    {
        if (capacity < 2 || (capacity & (capacity - 1)) != 0) throw new ArgumentException("Capacity must be a power of two.", nameof(capacity));
        _items = new WaveformColumn[capacity];
        _mask = capacity - 1;
    }

    public int Count => (int)(Volatile.Read(ref _head) - Volatile.Read(ref _tail));

    public bool TryWrite(in WaveformColumn column)
    {
        var head = _head;
        if (head - Volatile.Read(ref _tail) >= _items.Length) return false;
        _items[head & _mask] = column;
        Volatile.Write(ref _head, head + 1); // Publishes the slot after it is written.
        return true;
    }

    public bool TryRead(out WaveformColumn column)
    {
        var tail = _tail;
        if (tail == Volatile.Read(ref _head))
        {
            column = default;
            return false;
        }
        column = _items[tail & _mask];
        Volatile.Write(ref _tail, tail + 1);
        return true;
    }

    /// <summary>Consumer only: drops everything waiting, for example after a pause.</summary>
    public void Clear() => Volatile.Write(ref _tail, Volatile.Read(ref _head));
}

/// <summary>
/// The newest samples (mono mix, left and right), for the live views. The capture thread
/// writes one frame at a time; the drawing code copies the newest few thousand. Old samples
/// are simply overwritten, so a slow reader never holds the writer up.
/// </summary>
internal sealed class WaveformSampleTap
{
    public const int Capacity = 8192;
    private readonly float[] _samples = new float[Capacity];
    private readonly float[] _left = new float[Capacity];
    private readonly float[] _right = new float[Capacity];
    private long _written;
    private int _sampleRate = 48000;

    public int SampleRate
    {
        get => Volatile.Read(ref _sampleRate);
        set => Volatile.Write(ref _sampleRate, value);
    }

    public long Written => Volatile.Read(ref _written);

    public void Write(float mono, float left, float right)
    {
        var written = _written;
        var slot = written & (Capacity - 1);
        _samples[slot] = mono;
        _left[slot] = left;
        _right[slot] = right;
        Volatile.Write(ref _written, written + 1); // Publishes the frame after it is written.
    }

    /// <summary>Copies the newest mono samples, oldest first. Before enough have arrived, pads with silence.</summary>
    public void CopyLatest(Span<float> mono) => CopyLatest(mono, [], []);

    /// <summary>Copies the newest mono, left and right samples; pass an empty span to skip one.</summary>
    public void CopyLatest(Span<float> mono, Span<float> left, Span<float> right)
    {
        if (Math.Max(mono.Length, Math.Max(left.Length, right.Length)) > Capacity / 2)
            throw new ArgumentException("Ask for at most half the capacity.");
        var end = Written;
        Copy(_samples, mono, end);
        Copy(_left, left, end);
        Copy(_right, right, end);
    }

    private static void Copy(float[] source, Span<float> destination, long end)
    {
        var start = end - destination.Length;
        for (var i = 0; i < destination.Length; i++)
        {
            var index = start + i;
            destination[i] = index < 0 ? 0 : source[index & (Capacity - 1)];
        }
    }
}

/// <summary>The three palette colours a look mixes by band energy.</summary>
internal readonly record struct WaveformPalette(WaveformRgb Low, WaveformRgb Mid, WaveformRgb High)
{
    // Band weights even out typical music, where bass carries far more energy than highs.
    public const float LowWeight = 1f, MidWeight = 1.8f, HighWeight = 4.5f;

    public static WaveformPalette For(WaveformLook look) => look switch
    {
        WaveformLook.Neon => From((150, 50, 255), (255, 50, 190), (40, 235, 255)),
        WaveformLook.Ember => From((255, 55, 10), (255, 165, 25), (255, 240, 190)),
        WaveformLook.Ice => From((110, 180, 255), (185, 228, 255), (255, 255, 255)),
        WaveformLook.Sunset => From((120, 40, 200), (255, 70, 110), (255, 190, 60)),
        WaveformLook.Ocean => From((0, 70, 200), (0, 190, 210), (170, 255, 240)),
        WaveformLook.Borealis => From((40, 255, 140), (40, 200, 255), (200, 90, 255)),
        WaveformLook.Candy => From((255, 90, 180), (180, 120, 255), (90, 220, 255)),
        WaveformLook.Vapor => From((255, 60, 200), (120, 80, 255), (0, 240, 255)),
        WaveformLook.Toxic => From((60, 255, 0), (200, 255, 0), (240, 255, 200)),
        WaveformLook.Gold => From((180, 90, 10), (255, 190, 40), (255, 245, 200)),
        WaveformLook.Blood => From((120, 0, 10), (230, 20, 40), (255, 140, 140)),
        WaveformLook.Mono => From((150, 150, 160), (215, 215, 225), (255, 255, 255)),
        WaveformLook.Matrix => From((0, 120, 30), (0, 230, 70), (190, 255, 190)),
        WaveformLook.Prism => From((255, 0, 80), (255, 220, 0), (0, 200, 255)),
        WaveformLook.Lava => From((255, 30, 0), (255, 0, 120), (255, 200, 0)),
        _ => From((255, 50, 95), (70, 255, 150), (80, 165, 255))
    };

    /// <summary>
    /// The same palette turned around the colour wheel by <paramref name="degrees"/>, keeping
    /// each colour's brightness (the usual luma-preserving hue rotation), clamped to 0-1.
    /// </summary>
    public WaveformPalette Rotate(double degrees) => new(Turn(Low, degrees), Turn(Mid, degrees), Turn(High, degrees));

    /// <summary>
    /// Colours that follow the music: each colour belongs to a band (low to bass, mid to mids,
    /// high to highs). When its band hits (0 to 1) the colour swings up to 30 degrees round the
    /// colour wheel and lifts towards white, so the whole picture changes with what is playing.
    /// </summary>
    public WaveformPalette React(float bass, float mid, float high) =>
        new(Lift(Low, bass, 1), Lift(Mid, mid, -1), Lift(High, high, 1));

    private static WaveformRgb Lift(WaveformRgb colour, float drive, int direction)
    {
        drive = float.IsFinite(drive) ? Math.Clamp(drive, 0, 1) : 0;
        return Turn(colour, 30 * drive * direction).Whiten(0.3f * drive * drive);
    }

    private static WaveformRgb Turn(WaveformRgb x, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var c = (float)Math.Cos(radians);
        var s = (float)Math.Sin(radians);
        return new(
            Math.Clamp((0.299f + 0.701f * c + 0.168f * s) * x.R + (0.587f - 0.587f * c + 0.330f * s) * x.G + (0.114f - 0.114f * c - 0.497f * s) * x.B, 0, 1),
            Math.Clamp((0.299f - 0.299f * c - 0.328f * s) * x.R + (0.587f + 0.413f * c + 0.035f * s) * x.G + (0.114f - 0.114f * c + 0.292f * s) * x.B, 0, 1),
            Math.Clamp((0.299f - 0.299f * c + 1.250f * s) * x.R + (0.587f - 0.587f * c - 1.050f * s) * x.G + (0.114f + 0.886f * c - 0.203f * s) * x.B, 0, 1));
    }

    private static WaveformPalette From((int R, int G, int B) low, (int R, int G, int B) mid, (int R, int G, int B) high) =>
        new(Rgb(low), Rgb(mid), Rgb(high));

    private static WaveformRgb Rgb((int R, int G, int B) c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

    /// <summary>
    /// How much of each colour to use for the given band energies, after weighting. Silence
    /// (or unusable values) is all mid colour. The three weights add up to 1.
    /// </summary>
    public static (float Low, float Mid, float High) Weights(float low, float mid, float high)
    {
        var a = Usable(low) * LowWeight;
        var b = Usable(mid) * MidWeight;
        var c = Usable(high) * HighWeight;
        var sum = a + b + c;
        return sum < 1e-9f ? (0, 1, 0) : (a / sum, b / sum, c / sum);
    }

    /// <summary>The weighted mix, brightened so the strongest channel is full. The shaders do the same.</summary>
    public WaveformRgb Mix(float lowWeight, float midWeight, float highWeight) => Brighten(new WaveformRgb(
        Low.R * lowWeight + Mid.R * midWeight + High.R * highWeight,
        Low.G * lowWeight + Mid.G * midWeight + High.G * highWeight,
        Low.B * lowWeight + Mid.B * midWeight + High.B * highWeight));

    public WaveformRgb Mix(WaveformBar bar) => Mix(bar.LowWeight, bar.MidWeight, bar.HighWeight);

    private static float Usable(float value) => float.IsFinite(value) && value > 0 ? value : 0;

    private static WaveformRgb Brighten(WaveformRgb colour)
    {
        var k = 1f / Math.Max(1e-3f, Math.Max(colour.R, Math.Max(colour.G, colour.B)));
        return new(colour.R * k, colour.G * k, colour.B * k);
    }
}

/// <summary>
/// Turns columns into bars: smooths the band colour so it does not flicker, and (optionally)
/// scales to the recent loudest peak.
/// </summary>
internal sealed class WaveformLevels
{
    /// <summary>Auto level falls by half every five seconds, so quiet passages stay quiet.</summary>
    public const double HalfLifeSeconds = 5;
    public const float Floor = 0.02f;
    public const float Target = 0.92f;
    /// <summary>RMS is drawn larger than its value; for music it sits well below the peak.</summary>
    public const float RmsBoost = 1.45f;
    private const double ColourSeconds = 0.028;

    private readonly bool _autoLevel;
    private readonly float _decay;
    private readonly float _colourStep;
    private float _envelope = 0.15f;
    private float _low, _mid, _high;

    public WaveformLevels(bool autoLevel)
    {
        _autoLevel = autoLevel;
        _decay = (float)Math.Pow(0.5, WaveformAnalyzer.BlockSeconds / HalfLifeSeconds);
        _colourStep = (float)(1 - Math.Exp(-WaveformAnalyzer.BlockSeconds / ColourSeconds));
    }

    public float Envelope => _envelope;

    /// <summary>The gain the live views use: 0.92 of the recent loudest peak, or 1.</summary>
    public float Gain => _autoLevel ? Target / _envelope : 1;

    /// <summary>The current, smoothed colour weights.</summary>
    public (float Low, float Mid, float High) Weights => WaveformPalette.Weights(_low, _mid, _high);

    public WaveformBar Next(in WaveformColumn column)
    {
        _low += (column.Low - _low) * _colourStep;
        _mid += (column.Mid - _mid) * _colourStep;
        _high += (column.High - _high) * _colourStep;
        if (_autoLevel) _envelope = Math.Max(column.Peak, Math.Max(_envelope * _decay, Floor));
        var scale = Gain;
        var (low, _, high) = Weights;
        return new WaveformBar(
            Math.Clamp(column.Peak * scale, 0, 1),
            Math.Clamp(column.Rms * scale * RmsBoost, 0, 1),
            low,
            high);
    }
}

/// <summary>
/// The scrolling views' history: one entry per pixel or more, so the GPU texture always covers
/// the full width. At high speeds an entry is one 5 ms column; at low speeds several columns
/// are merged (loudest peak and RMS, average colour).
/// </summary>
internal sealed class WaveformHistory
{
    public const int Length = 4096;
    private double _carry;
    private float _peak, _rms, _low, _high;
    private int _merged;

    /// <param name="pixelsPerSecond">Physical pixels the picture moves each second.</param>
    public WaveformHistory(double pixelsPerSecond)
    {
        ColumnsPerEntry = Math.Max(1, WaveformAnalyzer.ColumnsPerSecond / Math.Max(1, pixelsPerSecond));
        PixelsPerEntry = pixelsPerSecond * ColumnsPerEntry / WaveformAnalyzer.ColumnsPerSecond;
    }

    public double ColumnsPerEntry { get; }
    public double PixelsPerEntry { get; }
    public long Written { get; private set; }

    /// <summary>Adds one column's bar; returns true when that completed an entry.</summary>
    public bool Add(in WaveformBar bar, out WaveformBar entry)
    {
        _peak = Math.Max(_peak, bar.Peak);
        _rms = Math.Max(_rms, bar.Rms);
        _low += bar.LowWeight;
        _high += bar.HighWeight;
        _merged++;
        _carry += 1;
        if (_carry < ColumnsPerEntry)
        {
            entry = default;
            return false;
        }
        _carry -= ColumnsPerEntry;
        entry = new WaveformBar(_peak, _rms, _low / _merged, _high / _merged);
        _peak = _rms = _low = _high = 0;
        _merged = 0;
        Written++;
        return true;
    }
}

/// <summary>
/// Where the scrolling picture's newest edge is, in columns, moving smoothly at the audio rate.
/// Columns arrive in 10 ms packets, so the picture stays a little behind the newest one and
/// gently speeds up or slows down to keep that distance, instead of jumping with each packet.
/// </summary>
internal sealed class WaveformClock
{
    /// <summary>How far behind the newest column the picture runs (40 ms).</summary>
    public const double Latency = 8;
    /// <summary>Further behind than this (a stall, a pause) and the picture jumps back in line.</summary>
    public const double MaximumLag = 48;
    private double _position = double.NaN;

    /// <param name="seconds">Time since the last frame.</param>
    /// <param name="received">Columns received so far.</param>
    public double Advance(double seconds, long received)
    {
        var target = received - Latency;
        if (double.IsNaN(_position) || target - _position > MaximumLag) _position = target;
        // Up to 5% faster or slower, so packet timing and clock drift never show as jumps.
        var error = Math.Clamp((target - _position) / Latency, -1, 1);
        _position += Math.Max(0, seconds) * WaveformAnalyzer.ColumnsPerSecond * (1 + 0.05 * error);
        // Never run past what has arrived; wait for the next packet instead.
        _position = Math.Min(_position, received - 1);
        return _position;
    }
}

/// <summary>A radix-2 complex FFT, in place.</summary>
internal static class WaveformFft
{
    public static void Forward(Span<float> real, Span<float> imaginary)
    {
        var n = real.Length;
        if (n != imaginary.Length || n < 2 || (n & (n - 1)) != 0) throw new ArgumentException("Length must be a power of two.");
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j |= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }
        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2 * Math.PI / length;
            var stepReal = Math.Cos(angle);
            var stepImaginary = Math.Sin(angle);
            for (var start = 0; start < n; start += length)
            {
                double wReal = 1, wImaginary = 0;
                for (var k = 0; k < length / 2; k++)
                {
                    var even = start + k;
                    var odd = even + length / 2;
                    var tReal = (float)(wReal * real[odd] - wImaginary * imaginary[odd]);
                    var tImaginary = (float)(wReal * imaginary[odd] + wImaginary * real[odd]);
                    real[odd] = real[even] - tReal;
                    imaginary[odd] = imaginary[even] - tImaginary;
                    real[even] += tReal;
                    imaginary[even] += tImaginary;
                    var next = wReal * stepReal - wImaginary * stepImaginary;
                    wImaginary = wReal * stepImaginary + wImaginary * stepReal;
                    wReal = next;
                }
            }
        }
    }
}

/// <summary>
/// Frequency levels for the spectrum views: a 2048-point FFT of the newest samples, grouped into
/// log-spaced bands from 30 Hz to 16 kHz, shown over a 60 dB range. Bars rise at once and fall
/// smoothly; each band's peak marker holds briefly, then drops.
/// </summary>
internal sealed class WaveformSpectrum
{
    public const int Size = 2048;
    public const int Bands = 96;
    public const double LowestHz = 30;
    public const double HighestHz = 16000;
    public const float RangeDb = 60;
    /// <summary>Without auto level, 0 dB (full scale) reaches the top less this headroom.</summary>
    public const float FixedTopDb = -6;
    private const float FallPerSecond = 1.4f;
    private const float PeakFallPerSecond = 0.7f;
    private const float PeakHoldSeconds = 0.35f;
    private const float TopFallDbPerSecond = 4;

    private readonly bool _autoLevel;
    private readonly float[] _window = new float[Size];
    private readonly float[] _real = new float[Size];
    private readonly float[] _imaginary = new float[Size];
    private readonly float[] _levels = new float[Bands];
    private readonly float[] _peaks = new float[Bands];
    private readonly float[] _hold = new float[Bands];
    private readonly double[] _bandLow = new double[Bands];
    private readonly double[] _bandHigh = new double[Bands];
    private float _topDb = -20;

    public WaveformSpectrum(bool autoLevel)
    {
        _autoLevel = autoLevel;
        for (var i = 0; i < Size; i++) _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Size - 1)));
        for (var band = 0; band < Bands; band++)
        {
            _bandLow[band] = LowestHz * Math.Pow(HighestHz / LowestHz, band / (double)Bands);
            _bandHigh[band] = LowestHz * Math.Pow(HighestHz / LowestHz, (band + 1) / (double)Bands);
        }
    }

    public ReadOnlySpan<float> Levels => _levels;
    public ReadOnlySpan<float> Peaks => _peaks;

    /// <summary>Amplitude (0 to 1, as a sine's peak) of one band in the last analysed block.</summary>
    public float BandAmplitude(int band) => _amplitudes[band];
    private readonly float[] _amplitudes = new float[Bands];

    public void Update(ReadOnlySpan<float> samples, int sampleRate, double seconds)
    {
        if (samples.Length != Size) throw new ArgumentException($"Pass exactly {Size} samples.", nameof(samples));
        var s = (float)Math.Clamp(seconds, 0, 0.25);
        for (var i = 0; i < Size; i++)
        {
            var sample = samples[i];
            _real[i] = float.IsFinite(sample) ? sample * _window[i] : 0;
            _imaginary[i] = 0;
        }
        WaveformFft.Forward(_real, _imaginary);

        // A Hann-windowed sine of amplitude A peaks at A * N / 4.
        var scale = 4f / Size;
        var binHz = sampleRate / (double)Size;
        var nyquist = sampleRate / 2.0;
        var frameTop = float.MinValue;
        Span<float> decibels = stackalloc float[Bands];
        for (var band = 0; band < Bands; band++)
        {
            var first = (int)Math.Ceiling(_bandLow[band] / binHz);
            var last = Math.Min(Size / 2 - 1, (int)Math.Floor(_bandHigh[band] / binHz));
            float amplitude;
            // A band above what this input can carry (a 16 kHz microphone stops at 8 kHz) is silent.
            if (_bandLow[band] >= nyquist) amplitude = 0;
            else if (last >= first)
            {
                amplitude = 0;
                for (var bin = first; bin <= last; bin++) amplitude = Math.Max(amplitude, Magnitude(bin));
            }
            else
            {
                // Narrower than one bin (the bass): read between the two nearest bins.
                var position = Math.Sqrt(_bandLow[band] * _bandHigh[band]) / binHz;
                var below = Math.Clamp((int)Math.Floor(position), 0, Size / 2 - 2);
                var t = (float)Math.Clamp(position - below, 0, 1);
                amplitude = Magnitude(below) + (Magnitude(below + 1) - Magnitude(below)) * t;
            }
            amplitude *= scale;
            _amplitudes[band] = amplitude;
            decibels[band] = 20 * MathF.Log10(amplitude + 1e-7f);
            frameTop = Math.Max(frameTop, decibels[band]);
        }

        _topDb = _autoLevel ? Math.Clamp(Math.Max(frameTop, _topDb - TopFallDbPerSecond * s), -50, 0) : FixedTopDb;
        var floor = _topDb - RangeDb;
        for (var band = 0; band < Bands; band++)
        {
            var target = Math.Clamp((decibels[band] - floor) / RangeDb, 0, 1);
            var level = _levels[band];
            level = target >= level ? level + (target - level) * Math.Min(1, s * 40) : Math.Max(target, level - FallPerSecond * s);
            _levels[band] = level;
            if (level >= _peaks[band])
            {
                _peaks[band] = level;
                _hold[band] = PeakHoldSeconds;
            }
            else if ((_hold[band] -= s) <= 0)
            {
                _peaks[band] = Math.Max(level, _peaks[band] - PeakFallPerSecond * s);
            }
        }
    }

    private float Magnitude(int bin) => MathF.Sqrt(_real[bin] * _real[bin] + _imaginary[bin] * _imaginary[bin]);
}

/// <summary>The oscilloscope's line: a steady window of the newest wave.</summary>
internal static class WaveformScope
{
    public const int Points = 1024;

    /// <summary>
    /// Copies <see cref="Points"/> samples starting at the newest rising zero crossing that still
    /// leaves a full window, so a steady note holds still instead of sliding. Gain is applied
    /// and the result kept within -1 to 1.
    /// </summary>
    public static void Prepare(ReadOnlySpan<float> recent, Span<float> output, float gain) =>
        Copy(recent, FindStart(recent, output.Length, gain), output, gain);

    /// <summary>Where the window starts: the newest rising zero crossing that leaves room for it.</summary>
    public static int FindStart(ReadOnlySpan<float> recent, int points, float gain)
    {
        if (recent.Length < points * 2) throw new ArgumentException("Pass at least twice as many recent samples as points.", nameof(recent));
        var latestStart = recent.Length - points;
        // Hysteresis: a rising zero crossing counts only if the negative swing just before it went
        // below the threshold, so noise wobbling around zero is ignored but slow bass notes are not.
        var threshold = 0.01f / Math.Max(1, gain);
        for (var i = latestStart; i > latestStart - points && i > 0; i--)
        {
            if (!(recent[i - 1] < 0 && recent[i] >= 0)) continue;
            for (var j = i - 1; j >= 0 && recent[j] < 0; j--)
                if (recent[j] < -threshold) return i;
        }
        return latestStart;
    }

    /// <summary>Copies a window with gain, kept within -1 to 1.</summary>
    public static void Copy(ReadOnlySpan<float> source, int start, Span<float> output, float gain)
    {
        for (var i = 0; i < output.Length; i++) output[i] = Math.Clamp(source[start + i] * gain, -1, 1);
    }

    /// <summary>
    /// Splits recent samples into bass (under 200 Hz), mids (around 1.2 kHz) and highs (over
    /// 5 kHz) with the same filters as the columns, starting fresh each call. The filters
    /// settle within a few milliseconds, so read windows from the newer half.
    /// </summary>
    public static void SplitBands(ReadOnlySpan<float> recent, int sampleRate, Span<float> low, Span<float> mid, Span<float> high)
    {
        var lowPass = Biquad.LowPass(sampleRate, 200, 0.707);
        var bandPass = Biquad.BandPass(sampleRate, 1200, 0.6);
        var highPass = Biquad.HighPass(sampleRate, 5000, 0.707);
        for (var i = 0; i < recent.Length; i++)
        {
            low[i] = lowPass.Process(recent[i]);
            mid[i] = bandPass.Process(recent[i]);
            high[i] = highPass.Process(recent[i]);
        }
    }
}

/// <summary>
/// Beat flash for the flashy views. A beat is the bass jumping well above its recent average;
/// the flash then fades over about a quarter of a second.
/// </summary>
internal sealed class WaveformBeat
{
    public const float Threshold = 1.45f;
    private const float FlashSeconds = 0.25f;
    private const float AverageSeconds = 0.6f;
    private const double GapSeconds = 0.12;
    private float _average;
    private double _sinceBeat = double.MaxValue;
    private bool _started;

    public float Flash { get; private set; }
    public int Beats { get; private set; }

    /// <param name="bass">This moment's bass level (any scale; compared with its own average).</param>
    /// <param name="seconds">How long this moment lasts.</param>
    public void Add(float bass, double seconds)
    {
        var s = (float)Math.Max(0, seconds);
        if (!float.IsFinite(bass) || bass < 0) bass = 0;
        if (!_started)
        {
            // Start from the first reading, so steady sound does not flash while the average catches up.
            _started = true;
            _average = bass;
        }
        _sinceBeat += s;
        if (bass > _average * Threshold && bass > 0.01f && _sinceBeat > GapSeconds)
        {
            Flash = 1;
            Beats++;
            _sinceBeat = 0;
        }
        else Flash *= MathF.Exp(-s * 3 / FlashSeconds);
        _average += (bass - _average) * (1 - MathF.Exp(-s / AverageSeconds));
    }
}

/// <summary>The Pulse view's bars: the loudest sample in each slice of the newest sound, falling smoothly.</summary>
internal sealed class WaveformPulse
{
    public const int Bars = 64;
    private const float FallPerSecond = 2.5f;
    private readonly float[] _levels = new float[Bars];

    public ReadOnlySpan<float> Levels => _levels;

    public void Update(ReadOnlySpan<float> recent, float gain, double seconds)
    {
        var slice = recent.Length / Bars;
        if (slice == 0) return;
        var fall = (float)(FallPerSecond * Math.Clamp(seconds, 0, 0.25));
        for (var bar = 0; bar < Bars; bar++)
        {
            var loudest = 0f;
            for (var i = bar * slice; i < (bar + 1) * slice; i++) loudest = Math.Max(loudest, Math.Abs(recent[i]));
            var target = Math.Clamp(loudest * gain, 0, 1);
            _levels[bar] = target >= _levels[bar] ? target : Math.Max(target, _levels[bar] - fall);
        }
    }
}

/// <summary>
/// What makes the flashy views move. Each band (and the overall level) is followed quickly,
/// then placed within its own recent range: the quiet between hits reads near 0, a kick, snare
/// or hi-hat near 1. Without this, a loud track reads "loud" all the time and nothing moves.
/// </summary>
internal sealed class WaveformDrive
{
    private const float AttackSeconds = 0.01f;
    private const float ReleaseSeconds = 0.12f;
    private const float AverageSeconds = 1.5f;
    private const float PeakHalfLifeSeconds = 1.5f;
    private readonly Follower _low = new(), _mid = new(), _high = new(), _level = new();

    public float Bass => _low.Value;
    public float Mid => _mid.Value;
    public float High => _high.Value;
    public float Energy => _level.Value;

    public void Add(in WaveformColumn column, double seconds)
    {
        var s = (float)Math.Max(0, seconds);
        _low.Add(column.Low, s);
        _mid.Add(column.Mid, s);
        _high.Add(column.High, s);
        _level.Add(column.Rms, s);
    }

    private sealed class Follower
    {
        private float _envelope, _average, _peak;

        public float Value { get; private set; }

        public void Add(float input, float seconds)
        {
            if (!float.IsFinite(input) || input < 0) input = 0;
            var rate = input > _envelope ? AttackSeconds : ReleaseSeconds;
            _envelope += (input - _envelope) * (1 - MathF.Exp(-seconds / rate));
            _average += (_envelope - _average) * (1 - MathF.Exp(-seconds / AverageSeconds));
            _peak = Math.Max(_envelope, _peak * MathF.Pow(0.5f, seconds / PeakHalfLifeSeconds));
            // Half the average is the floor; the ceiling is the recent peak, but at least 1.6 times
            // the average, so a steady sound sits near the middle and only real hits reach the top.
            var floor = _average * 0.5f;
            var span = Math.Max(_peak, _average * 1.6f) - floor;
            Value = span < 1e-4f ? 0 : Math.Clamp((_envelope - floor) / span, 0, 1);
        }
    }
}
