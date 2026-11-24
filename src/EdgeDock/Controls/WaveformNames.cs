namespace EdgeDock.Controls;

/// <summary>Readable names for the Waveform widget's views and looks, grouped as the pickers show them.</summary>
internal static class WaveformNames
{
    public static readonly (string Group, WaveformView[] Views)[] Groups =
    [
        ("Waves", [WaveformView.Scroll, WaveformView.CentreOut]),
        ("Spectrum", [WaveformView.SpectrumBars, WaveformView.MirrorSpectrum, WaveformView.SpectrumLine, WaveformView.LedBars,
            WaveformView.Spectrogram, WaveformView.Ridges, WaveformView.Ring]),
        ("Oscilloscopes", [WaveformView.Oscilloscope, WaveformView.CrtScope, WaveformView.CrtVectorscope, WaveformView.StereoScope,
            WaveformView.BandScopes, WaveformView.Lissajous]),
        ("Flashy", [WaveformView.Pulse, WaveformView.Club, WaveformView.Warp, WaveformView.Aurora]),
        ("Demo scene", [WaveformView.Plasma, WaveformView.Tunnel]),
        ("3D scenes", [WaveformView.Spheres, WaveformView.Mandelbulb, WaveformView.Lattice, WaveformView.FractalCubes,
            WaveformView.Mandelbox, WaveformView.OrbField, WaveformView.FractalForest]),
        ("Space and sky", [WaveformView.StarNest, WaveformView.BlackHole, WaveformView.Clouds, WaveformView.Cloudscape,
            WaveformView.Ocean, WaveformView.Mountains]),
        ("Crazy", [WaveformView.Kaleidoscope, WaveformView.Vortex, WaveformView.Glitch, WaveformView.Fire, WaveformView.Metaballs,
            WaveformView.Lightning, WaveformView.Fractal, WaveformView.HypnoCircles, WaveformView.HexPlasma, WaveformView.Lasers,
            WaveformView.Voronoi, WaveformView.Blob, WaveformView.Truchet, WaveformView.NeonRings])
    ];

    public static string View(WaveformView view) => view switch
    {
        WaveformView.Scroll => "Scrolling wave",
        WaveformView.CentreOut => "Centre out",
        WaveformView.SpectrumBars => "Spectrum bars",
        WaveformView.MirrorSpectrum => "Mirror spectrum",
        WaveformView.SpectrumLine => "Spectrum line",
        WaveformView.LedBars => "LED bars",
        WaveformView.Spectrogram => "Spectrogram",
        WaveformView.Ridges => "Ridges",
        WaveformView.Ring => "Ring",
        WaveformView.Oscilloscope => "Oscilloscope",
        WaveformView.CrtScope => "CRT oscilloscope",
        WaveformView.CrtVectorscope => "CRT vectorscope",
        WaveformView.StereoScope => "Stereo scope",
        WaveformView.BandScopes => "Band scopes",
        WaveformView.Lissajous => "Lissajous",
        WaveformView.Pulse => "Pulse",
        WaveformView.Club => "Club",
        WaveformView.Warp => "Warp",
        WaveformView.Aurora => "Aurora",
        WaveformView.Plasma => "Plasma",
        WaveformView.Tunnel => "Tunnel",
        WaveformView.Kaleidoscope => "Kaleidoscope",
        WaveformView.Vortex => "Vortex",
        WaveformView.Glitch => "Glitch",
        WaveformView.Fire => "Fire",
        WaveformView.Metaballs => "Metaballs",
        WaveformView.Lightning => "Lightning",
        WaveformView.Fractal => "Fractal",
        WaveformView.StarNest => "Star nest",
        WaveformView.Clouds => "Clouds",
        WaveformView.Cloudscape => "Cloudscape",
        WaveformView.HypnoCircles => "Hypno circles",
        WaveformView.HexPlasma => "Hex plasma",
        WaveformView.Lasers => "Lasers",
        WaveformView.Voronoi => "Voronoi cells",
        WaveformView.Blob => "Liquid blob",
        WaveformView.Truchet => "Truchet",
        WaveformView.Spheres => "Liquid spheres",
        WaveformView.Mandelbulb => "Mandelbulb",
        WaveformView.Lattice => "Escher lattice",
        WaveformView.FractalCubes => "Fractal cubes",
        WaveformView.Mandelbox => "Mandelbox flight",
        WaveformView.OrbField => "Orb field",
        WaveformView.FractalForest => "Fractal forest",
        WaveformView.BlackHole => "Black hole",
        WaveformView.Mountains => "Spectrum mountains",
        WaveformView.Ocean => "Ocean",
        WaveformView.NeonRings => "Neon rings",
        _ => view.ToString()
    };

    public static string Look(WaveformLook look) => look.ToString();

    public static string Cycle(int seconds) => seconds switch
    {
        0 => "Off",
        < 60 => $"Every {seconds} seconds",
        60 => "Every minute",
        _ => $"Every {seconds / 60} minutes"
    };

    public static string ColourCycle(WaveformColourCycle cycle) => cycle switch
    {
        WaveformColourCycle.Slow => "Slowly",
        WaveformColourCycle.Fast => "Quickly",
        _ => "Off"
    };
}
