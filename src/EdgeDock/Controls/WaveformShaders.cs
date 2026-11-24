using ComputeSharp;
using ComputeSharp.D2D1;

namespace EdgeDock.Controls;

// The Waveform widget's views, as Direct2D pixel shaders. ComputeSharp turns this C# into HLSL
// and compiles it when EdgeDock is built, so no shader compiler runs on the user's PC. Each
// shader works out every pixel on its own from a small data texture the widget updates once a
// frame; the processor's only per-frame work is that update and one draw call.
//
// All sizes are physical pixels. Colours are 0-1; output is premultiplied alpha. The palette
// helpers repeat WaveformPalette's maths, because shader code cannot call ordinary C#.

/// <summary>Scroll and Centre out: mirrored peak and RMS bars moving away from the newest sound.</summary>
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader40)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct WaveformScrollShader(
    Float2 size, Float3 low, Float3 mid, Float3 high,
    float head, float pixelsPerEntry, float available, int centreOut) : ID2D1PixelShader
{
    /// <summary>History entries: peak, RMS, low weight, high weight. Wraps every 4096 entries.</summary>
    [D2DResourceTextureIndex(0)]
    private readonly D2D1ResourceTexture1D<Float4> history;

    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        float reach = centreOut != 0 ? size.X * 0.5f : size.X;
        float distance = centreOut != 0 ? Hlsl.Abs(p.X - reach) : reach - p.X;
        float age = distance / pixelsPerEntry;
        float known = Hlsl.Step(age, available - 1f);
        Float4 entry = history.Sample((head - age + 0.5f) / 4096f);

        float span = size.Y * 0.47f;
        float peak = Hlsl.Max(entry.X * span, 0.75f);
        float rms = Hlsl.Min(entry.Y * span, peak);
        float dy = Hlsl.Abs(p.Y - size.Y * 0.5f);
        float outer = Hlsl.Saturate(peak + 0.5f - dy);
        float inner = Hlsl.Saturate(rms + 0.5f - dy);
        float glow = 0.4f * Hlsl.Exp(-Hlsl.Max(dy - peak, 0f) / (size.Y * 0.035f)) * Hlsl.Saturate(entry.X * 4f);

        Float3 colour = Mix(entry.Z, entry.W);
        Float3 white = Hlsl.Lerp(colour, new Float3(1f, 1f, 1f), 0.42f);
        float shell = 0.5f * outer * (1f - inner);
        float halo = glow * (1f - outer);
        Float3 rgb = colour * (shell + halo) + white * inner;
        float alpha = Hlsl.Saturate(shell + halo + inner);

        // The oldest sound fades out towards the far edge(s).
        float fade = Hlsl.Saturate((reach - distance) / (reach * (centreOut != 0 ? 0.32f : 0.18f)));
        float k = fade * known;
        float line = 0.08f * Hlsl.Saturate(1f - dy) * (1f - alpha * k);
        return new Float4(rgb * k + new Float3(line, line, line), alpha * k + line);
    }

    private Float3 Mix(float lowWeight, float highWeight)
    {
        float midWeight = Hlsl.Saturate(1f - lowWeight - highWeight);
        Float3 c = low * lowWeight + mid * midWeight + high * highWeight;
        return c / Hlsl.Max(Hlsl.Max(c.X, c.Y), Hlsl.Max(c.Z, 0.001f));
    }
}

/// <summary>Spectrogram: frequency bottom (bass) to top (highs), time moving left, brightness by level.</summary>
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader40)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct WaveformSpectrogramShader(
    Float2 size, Float3 low, Float3 mid, Float3 high,
    float head, float pixelsPerEntry, float available) : ID2D1PixelShader
{
    /// <summary>Band levels: one row per moment (wrapping every 4096 rows), one column per band.</summary>
    [D2DResourceTextureIndex(0)]
    private readonly D2D1ResourceTexture2D<float> history;

    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        float distance = size.X - p.X;
        float age = distance / pixelsPerEntry;
        float known = Hlsl.Step(age, available - 1f);
        float band = Hlsl.Saturate(1f - p.Y / size.Y);
        float level = history.Sample(new Float2(band, (head - age + 0.5f) / 4096f));
        float intensity = Hlsl.Pow(Hlsl.Saturate(level), 1.6f);
        Float3 colour = Gradient(band);
        Float3 rgb = Hlsl.Lerp(colour, new Float3(1f, 1f, 1f), Hlsl.Saturate((level - 0.8f) * 3f) * 0.6f);
        float fade = Hlsl.Saturate((size.X - distance) / (size.X * 0.18f)) * known;
        return new Float4(rgb * intensity * fade, intensity * fade);
    }

    private Float3 Gradient(float t)
    {
        Float3 c = t < 0.5f ? Hlsl.Lerp(low, mid, t * 2f) : Hlsl.Lerp(mid, high, t * 2f - 1f);
        return c / Hlsl.Max(Hlsl.Max(c.X, c.Y), Hlsl.Max(c.Z, 0.001f));
    }
}

/// <summary>
/// The bar family. Style 0: spectrum bars rising from the bottom with peak markers. 1: mirror
/// spectrum, bass in the middle, growing up and down. 2: spectrum line with a soft fill.
/// 3: pulse, loudness bars mirrored around the centre line in the sound's current colour.
/// </summary>
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader40)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct WaveformBarsShader(
    Float2 size, Float3 low, Float3 mid, Float3 high, Float3 current, int style, int count) : ID2D1PixelShader
{
    /// <summary>Per entry: scope sample, band level, band peak marker, pulse level (1024 entries).</summary>
    [D2DResourceTextureIndex(0)]
    private readonly D2D1ResourceTexture1D<Float4> data;

    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        Float4 result = style == 2 ? Line(p) : Bars(p);
        return result;
    }

    private Float4 Bars(Float2 p)
    {
        Float3 white = new Float3(1f, 1f, 1f);
        float half = style == 1 ? size.X * 0.5f : size.X;
        float axis = style == 1 ? Hlsl.Abs(p.X - half) / half : p.X / size.X;
        float slot = Hlsl.Min(axis * count, count - 0.001f);
        float index = Hlsl.Floor(slot);
        float within = slot - index;
        float slotWidth = half / count;
        Float4 entry = data.Sample((index + 0.5f) / 1024f);
        float level = style == 3 ? entry.W : entry.Y;
        float peakLevel = entry.Z;
        Float3 colour = style == 3 ? current : Gradient((index + 0.5f) / count);

        float dx = Hlsl.Max(Hlsl.Abs(within - 0.5f) * slotWidth - slotWidth * 0.38f, 0f);
        float outside = 0f;
        float tip = 0f;
        float cap = 0f;
        if (style == 0)
        {
            float height = level * size.Y * 0.9f;
            float top = size.Y - height;
            outside = Hlsl.Max(top - p.Y, 0f);
            tip = Hlsl.Saturate(1f - (p.Y - top) / Hlsl.Max(height, 1f));
            float capY = size.Y - peakLevel * size.Y * 0.9f - 3f;
            cap = Hlsl.Saturate(2f - Hlsl.Abs(p.Y - capY)) * Hlsl.Step(dx, 0.001f) * Hlsl.Step(0.02f, peakLevel);
        }
        else
        {
            float reach = level * size.Y * 0.46f;
            float dy = Hlsl.Abs(p.Y - size.Y * 0.5f);
            outside = Hlsl.Max(dy - reach, 0f);
            tip = Hlsl.Saturate(dy / Hlsl.Max(reach, 1f));
            if (style == 1)
            {
                float capDy = peakLevel * size.Y * 0.46f + 3f;
                cap = Hlsl.Saturate(2f - Hlsl.Abs(dy - capDy)) * Hlsl.Step(dx, 0.001f) * Hlsl.Step(0.02f, peakLevel);
            }
        }

        float d = Hlsl.Length(new Float2(dx, outside));
        float body = Hlsl.Saturate(1f - d) * Hlsl.Step(0.002f, level);
        float glow = 0.35f * Hlsl.Exp(-d / 14f) * Hlsl.Saturate(level * 3f) * (1f - body);
        Float3 lit = Hlsl.Lerp(colour * 0.55f, Hlsl.Lerp(colour, white, 0.35f), tip);
        Float3 rgb = lit * body + colour * glow + white * cap;
        float alpha = Hlsl.Saturate(body + glow + cap);
        return new Float4(rgb, alpha);
    }

    private Float4 Line(Float2 p)
    {
        float stepPerPixel = count / size.X;
        float position = Hlsl.Clamp(p.X * stepPerPixel, 0.5f, count - 0.5f);
        float y = Height(position);
        float slope = (Height(Hlsl.Clamp(position + stepPerPixel, 0.5f, count - 0.5f)) -
                       Height(Hlsl.Clamp(position - stepPerPixel, 0.5f, count - 0.5f))) * 0.5f;
        float d = Hlsl.Abs(p.Y - y) / Hlsl.Sqrt(1f + slope * slope);
        float core = Hlsl.Saturate(1.8f - d);
        float glow = 0.45f * Hlsl.Exp(-d / 12f) * (1f - core);
        float fill = 0.3f * Hlsl.Step(y, p.Y) * Hlsl.Saturate(1f - (p.Y - y) / (size.Y * 0.9f)) * (1f - core);
        Float3 colour = Gradient(position / count);
        Float3 rgb = Hlsl.Lerp(colour, new Float3(1f, 1f, 1f), 0.4f) * core + colour * (glow + fill);
        return new Float4(rgb, Hlsl.Saturate(core + glow + fill));
    }

    private float Height(float position) => size.Y - data.Sample(position / 1024f).Y * size.Y * 0.9f;

    private Float3 Gradient(float t)
    {
        Float3 c = t < 0.5f ? Hlsl.Lerp(low, mid, t * 2f) : Hlsl.Lerp(mid, high, t * 2f - 1f);
        return c / Hlsl.Max(Hlsl.Max(c.X, c.Y), Hlsl.Max(c.Z, 0.001f));
    }
}

/// <summary>Oscilloscope: the live wave shape as a glowing line in the sound's current colour.</summary>
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader40)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct WaveformScopeShader(Float2 size, Float3 colour) : ID2D1PixelShader
{
    /// <summary>Per entry: scope sample in X (1024 entries).</summary>
    [D2DResourceTextureIndex(0)]
    private readonly D2D1ResourceTexture1D<Float4> data;

    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        float perPixel = 1024f / size.X;
        float x = p.X * perPixel;
        float y0 = Y(x - perPixel);
        float y1 = Y(x);
        float y2 = Y(x + perPixel);
        float lowest = Hlsl.Min(y1, Hlsl.Min((y0 + y1) * 0.5f, (y1 + y2) * 0.5f));
        float highest = Hlsl.Max(y1, Hlsl.Max((y0 + y1) * 0.5f, (y1 + y2) * 0.5f));
        float d = Hlsl.Max(Hlsl.Max(lowest - p.Y, p.Y - highest), 0f);
        float core = Hlsl.Saturate(2f - d);
        float glow = (0.5f * Hlsl.Exp(-d / 16f) + 0.15f * Hlsl.Exp(-d / 60f)) * (1f - core);
        Float3 rgb = Hlsl.Lerp(colour, new Float3(1f, 1f, 1f), 0.55f) * core + colour * glow;
        float alpha = Hlsl.Saturate(core + glow);
        float line = 0.08f * Hlsl.Saturate(1f - Hlsl.Abs(p.Y - size.Y * 0.5f)) * (1f - alpha);
        return new Float4(rgb + new Float3(line, line, line), alpha + line);
    }

    private float Y(float position) =>
        size.Y * 0.5f - data.Sample(Hlsl.Clamp(position, 0.5f, 1023.5f) / 1024f).X * size.Y * 0.45f;
}
