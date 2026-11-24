using ComputeSharp;
using ComputeSharp.D2D1;

namespace EdgeDock.Controls;

// The Waveform widget's views, as Direct2D pixel shaders. ComputeSharp turns this C# into HLSL
// and compiles it when EdgeDock is built, so no shader compiler runs on the user's PC. Each
// shader works out every pixel on its own from small textures the widget updates once a frame;
// the processor's only per-frame work is those updates and one draw call.
//
// All sizes are physical pixels. Colours are 0-1; output is premultiplied alpha. Helpers such as
// Gradient repeat WaveformPalette's maths, because shader code cannot call ordinary C#, and each
// shader carries its own copy because shader types cannot share methods.
//
// The live data texture (1024 x 8, four channels) holds:
//   row 0: mono scope sample, spectrum band level, band peak marker, pulse bar level
//   row 1: left and right samples (stereo scope, Lissajous)
//   rows 2-7: the mono scope 1 to 6 frames ago in X (CRT afterglow); row 2 also bass, mid and high samples
// A row's centre is at (row + 0.5) / 8, so row 0 is sampled at 0.0625 and row 1 at 0.1875.
// Spectrum bands use the first 96 entries, pulse bars the first 64.

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

        float midWeight = Hlsl.Saturate(1f - entry.Z - entry.W);
        Float3 colour = Brighten(low * entry.Z + mid * midWeight + high * entry.W);
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

    private static Float3 Brighten(Float3 c) => c / Hlsl.Max(Hlsl.Max(c.X, c.Y), Hlsl.Max(c.Z, 0.001f));
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
        Float3 rgb = Hlsl.Lerp(Gradient(band), new Float3(1f, 1f, 1f), Hlsl.Saturate((level - 0.8f) * 3f) * 0.6f);
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
/// Ridges: the recent spectrum as stacked mountain lines, newest at the front (bottom), older
/// ones rising behind it. Each ridge hides the ones behind it.
/// </summary>
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader40)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct WaveformRidgesShader(
    Float2 size, Float3 low, Float3 mid, Float3 high,
    float head, float rowsPerRidge, float available) : ID2D1PixelShader
{
    [D2DResourceTextureIndex(0)]
    private readonly D2D1ResourceTexture2D<float> history;

    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        const int ridges = 26;
        float spacing = size.Y * 0.8f / ridges;
        float amplitude = spacing * 5f;
        // Bass in the middle, highs towards both edges, tallest in the centre.
        float t = Hlsl.Abs(p.X - size.X * 0.5f) / (size.X * 0.5f);
        float band = 1f - t;
        float shape = 0.3f + 0.7f * (1f - t * t);
        Float3 rgb = new Float3(0f, 0f, 0f);
        float alpha = 0f;
        float hit = 0f;
        for (int k = 0; k < ridges; k++)
        {
            float age = k * rowsPerRidge;
            float baseline = size.Y * 0.95f - k * spacing;
            float level = history.Sample(new Float2(band * 0.98f, (head - age + 0.5f) / 4096f)) * Hlsl.Step(age, available - 1f);
            float curve = baseline - level * amplitude * shape;
            float covered = Hlsl.Step(curve - 1.5f, p.Y) * (1f - hit);
            float d = Hlsl.Abs(p.Y - curve);
            float line = Hlsl.Saturate(1.6f - d);
            float glow = 0.35f * Hlsl.Exp(-d / 6f) * Hlsl.Saturate(level * 2f);
            float depth = 1f - k / (float)ridges;
            Float3 colour = Gradient(Hlsl.Saturate(level * 1.2f));
            rgb += covered * (Hlsl.Lerp(colour, new Float3(1f, 1f, 1f), 0.3f) * line + colour * glow * (1f - line)) * depth;
            alpha += covered * Hlsl.Saturate(line + glow) * depth;
            // Below its line, a ridge is solid and hides everything behind it.
            hit = Hlsl.Max(hit, covered);
        }
        // Ridges hide what is behind them with the panel's own dark colour.
        return new Float4(rgb, Hlsl.Max(Hlsl.Saturate(alpha), hit * 0.92f));
    }

    private Float3 Gradient(float t)
    {
        Float3 c = t < 0.5f ? Hlsl.Lerp(low, mid, t * 2f) : Hlsl.Lerp(mid, high, t * 2f - 1f);
        return c / Hlsl.Max(Hlsl.Max(c.X, c.Y), Hlsl.Max(c.Z, 0.001f));
    }
}

/// <summary>
/// The bar family. Style 0: spectrum bars from the bottom with peak markers. 1: mirror spectrum,
/// bass in the middle. 2: spectrum line with a soft fill. 3: pulse, loudness bars around the
/// centre line. 4: LED segments like a hi-fi analyser. 5: club, mirror bars over a beat flash.
/// </summary>
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader40)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct WaveformBarsShader(
    Float2 size, Float3 low, Float3 mid, Float3 high, Float3 current, int style, int count, float flash) : ID2D1PixelShader
{
    [D2DResourceTextureIndex(0)]
    private readonly D2D1ResourceTexture2D<Float4> data;

    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        Float4 result = style == 2 ? Line(p) : (style == 4 ? Led(p) : Bars(p));
        if (style == 5)
        {
            // The whole panel flashes on each beat, strongest in the middle.
            Float2 d = (p - size * 0.5f) / size.Y;
            float wash = flash * 0.55f * Hlsl.Exp(-Hlsl.Dot(d, d) * 1.5f) + flash * 0.12f;
            Float3 back = Gradient(0.15f) * wash;
            result = new Float4(result.XYZ + back * (1f - result.W), result.W + wash * (1f - result.W));
        }
        return result;
    }

    private Float4 Bars(Float2 p)
    {
        Float3 white = new Float3(1f, 1f, 1f);
        bool mirrored = style == 1 || style == 5;
        float half = mirrored ? size.X * 0.5f : size.X;
        float axis = mirrored ? Hlsl.Abs(p.X - half) / half : p.X / size.X;
        float slot = Hlsl.Min(axis * count, count - 0.001f);
        float index = Hlsl.Floor(slot);
        float within = slot - index;
        float slotWidth = half / count;
        Float4 entry = Band(index);
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
            if (style != 3)
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
        return new Float4(rgb, Hlsl.Saturate(body + glow + cap));
    }

    private Float4 Led(Float2 p)
    {
        const float segments = 24f;
        float slot = Hlsl.Min(p.X / size.X * count, count - 0.001f);
        float index = Hlsl.Floor(slot);
        float within = slot - index;
        // Each LED bar covers its share of all 96 bands (two each at 48 bars), showing the louder.
        float firstBand = Hlsl.Floor(index * 96f / count);
        Float4 entry = Hlsl.Max(Band(firstBand), Band(Hlsl.Min(firstBand + 1f, 95f)));
        float rise = (size.Y - p.Y) / size.Y / 0.94f;
        float segment = Hlsl.Floor(rise * segments);
        float inSegment = Hlsl.Frac(rise * segments);
        float cell = Hlsl.Step(Hlsl.Abs(within - 0.5f), 0.4f) * Hlsl.Step(0.22f, inSegment) * Hlsl.Step(rise, 1f) * Hlsl.Step(0f, rise);
        float lit = Hlsl.Step(segment + 0.5f, entry.Y * segments);
        float peak = Hlsl.Step(Hlsl.Abs(segment - Hlsl.Floor(entry.Z * segments - 0.01f)), 0.5f) * Hlsl.Step(0.04f, entry.Z);
        Float3 colour = Gradient(segment / segments);
        float on = Hlsl.Max(lit, peak);
        float brightness = cell * (0.07f + 0.93f * on);
        return new Float4(colour * brightness, brightness);
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

    private float Height(float position) => size.Y - data.Sample(new Float2(position / 1024f, 0.0625f)).Y * size.Y * 0.9f;

    private Float4 Band(float index) => data.Sample(new Float2((index + 0.5f) / 1024f, 0.0625f));

    private Float3 Gradient(float t)
    {
        Float3 c = t < 0.5f ? Hlsl.Lerp(low, mid, t * 2f) : Hlsl.Lerp(mid, high, t * 2f - 1f);
        return c / Hlsl.Max(Hlsl.Max(c.X, c.Y), Hlsl.Max(c.Z, 0.001f));
    }
}

/// <summary>
/// The oscilloscope family. Style 0: the live wave as a glowing line. 1: stereo, left above
/// right. 2: band scopes, bass, mids and highs as three lines in their own colours.
/// </summary>
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader40)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct WaveformScopeShader(
    Float2 size, Float3 current, Float3 low, Float3 mid, Float3 high, int style) : ID2D1PixelShader
{
    [D2DResourceTextureIndex(0)]
    private readonly D2D1ResourceTexture2D<Float4> data;

    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        Float3 rgb = new Float3(0f, 0f, 0f);
        float alpha = 0f;
        if (style == 1)
        {
            Float4 left = Trace(p, 1.5f, 0, size.Y * 0.27f, size.Y * 0.22f, Brighten(low));
            Float4 right = Trace(p, 1.5f, 1, size.Y * 0.73f, size.Y * 0.22f, Brighten(high));
            rgb = left.XYZ + right.XYZ;
            alpha = Hlsl.Saturate(left.W + right.W);
        }
        else if (style == 2)
        {
            Float4 bass = Trace(p, 2.5f, 1, size.Y * 0.5f, size.Y * 0.42f, Brighten(low));
            Float4 mids = Trace(p, 2.5f, 2, size.Y * 0.5f, size.Y * 0.42f, Brighten(mid));
            Float4 highs = Trace(p, 2.5f, 3, size.Y * 0.5f, size.Y * 0.42f, Brighten(high));
            rgb = bass.XYZ + mids.XYZ + highs.XYZ;
            alpha = Hlsl.Saturate(bass.W + mids.W + highs.W);
        }
        else
        {
            Float4 wave = Trace(p, 0.5f, 0, size.Y * 0.5f, size.Y * 0.45f, current);
            rgb = wave.XYZ;
            alpha = wave.W;
        }
        float centre = 0.08f * Hlsl.Saturate(1f - Hlsl.Abs(p.Y - size.Y * 0.5f)) * (1f - alpha);
        return new Float4(rgb + new Float3(centre, centre, centre), Hlsl.Saturate(alpha + centre));
    }

    /// <summary>A glowing line through one channel of a data row (row given as its centre, 0.5 = first).</summary>
    private Float4 Trace(Float2 p, float row, int channel, float centreY, float amplitude, Float3 colour)
    {
        float perPixel = 1024f / size.X;
        float x = p.X * perPixel;
        float y0 = centreY - Value(x - perPixel, row, channel) * amplitude;
        float y1 = centreY - Value(x, row, channel) * amplitude;
        float y2 = centreY - Value(x + perPixel, row, channel) * amplitude;
        float lowest = Hlsl.Min(y1, Hlsl.Min((y0 + y1) * 0.5f, (y1 + y2) * 0.5f));
        float highest = Hlsl.Max(y1, Hlsl.Max((y0 + y1) * 0.5f, (y1 + y2) * 0.5f));
        float d = Hlsl.Max(Hlsl.Max(lowest - p.Y, p.Y - highest), 0f);
        float core = Hlsl.Saturate(2f - d);
        float glow = (0.5f * Hlsl.Exp(-d / 14f) + 0.15f * Hlsl.Exp(-d / 56f)) * (1f - core);
        return new Float4(Hlsl.Lerp(colour, new Float3(1f, 1f, 1f), 0.55f) * core + colour * glow, core + glow);
    }

    private float Value(float position, float row, int channel)
    {
        Float4 v = data.Sample(new Float2(Hlsl.Clamp(position, 0.5f, 1023.5f) / 1024f, row / 8f));
        return channel == 0 ? v.X : (channel == 1 ? v.Y : (channel == 2 ? v.Z : v.W));
    }

    private static Float3 Brighten(Float3 c) => c / Hlsl.Max(Hlsl.Max(c.X, c.Y), Hlsl.Max(c.Z, 0.001f));
}

/// <summary>Lissajous: left against right as a glowing figure (mid up, side across), like a vectorscope.</summary>
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader40)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct WaveformLissajousShader(Float2 size, Float3 current, Float3 low) : ID2D1PixelShader
{
    [D2DResourceTextureIndex(0)]
    private readonly D2D1ResourceTexture2D<Float4> data;

    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        Float2 centre = size * 0.5f;
        float up = size.Y * 0.46f;
        float across = Hlsl.Min(size.X * 0.46f, up * 2.4f);
        float nearest = 1e6f;
        float density = 0f;
        Float2 previous = Point(0, centre, up, across);
        for (int i = 1; i < 128; i++)
        {
            Float2 next = Point(i, centre, up, across);
            Float2 segment = next - previous;
            float t = Hlsl.Saturate(Hlsl.Dot(p - previous, segment) / Hlsl.Max(Hlsl.Dot(segment, segment), 0.0001f));
            float d = Hlsl.Length(p - previous - segment * t);
            nearest = Hlsl.Min(nearest, d);
            density += Hlsl.Exp(-d / 5f);
            previous = next;
        }
        float core = Hlsl.Saturate(1.6f - nearest);
        float glow = Hlsl.Saturate(density * 0.09f) * (1f - core);
        // Faint guide lines: the centre cross and the mono diagonal.
        Float2 q = p - centre;
        float guide = 0.07f * Hlsl.Saturate(1f - Hlsl.Min(Hlsl.Abs(q.X), Hlsl.Abs(q.Y))) * (1f - core - glow);
        Float3 rgb = Hlsl.Lerp(current, new Float3(1f, 1f, 1f), 0.5f) * core + current * glow + Brighten(low) * guide;
        return new Float4(rgb, Hlsl.Saturate(core + glow + guide));
    }

    private Float2 Point(int i, Float2 centre, float up, float across)
    {
        Float4 v = data.Sample(new Float2((i * 8f + 0.5f) / 1024f, 0.1875f));
        float side = (v.X - v.Y) * 0.7071f;
        float middle = (v.X + v.Y) * 0.7071f;
        return centre + new Float2(side * across, -middle * up);
    }

    private static Float3 Brighten(Float3 c) => c / Hlsl.Max(Hlsl.Max(c.X, c.Y), Hlsl.Max(c.Z, 0.001f));
}

/// <summary>Ring: spectrum bars radiating from a circle that swells with the bass, a circular scope inside.</summary>
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader40)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct WaveformRingShader(
    Float2 size, Float3 low, Float3 mid, Float3 high, Float3 current, float bass, float flash, float time) : ID2D1PixelShader
{
    [D2DResourceTextureIndex(0)]
    private readonly D2D1ResourceTexture2D<Float4> data;

    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        Float2 d = p - size * 0.5f;
        float r = Hlsl.Length(d);
        float angle = Hlsl.Atan2(d.X, -d.Y);
        float t = Hlsl.Abs(angle) / 3.14159265f;
        float radius = size.Y * (0.17f + 0.11f * bass + 0.04f * flash);
        const float count = 72f;
        float slot = Hlsl.Min(t * count, count - 0.001f);
        float index = Hlsl.Floor(slot);
        float within = slot - index;
        Float4 entry = data.Sample(new Float2((index * 96f / count + 0.5f) / 1024f, 0.0625f));
        float length = entry.Y * size.Y * 0.27f;
        float slotWidth = 3.14159265f / count * r;
        float dx = Hlsl.Max(Hlsl.Abs(within - 0.5f) * slotWidth - slotWidth * 0.34f, 0f);
        float dr = Hlsl.Max(Hlsl.Max(radius - r, r - radius - length), 0f);
        float dist = Hlsl.Length(new Float2(dx, dr));
        float body = Hlsl.Saturate(1f - dist) * Hlsl.Step(0.002f, entry.Y);
        float glow = 0.35f * Hlsl.Exp(-dist / 12f) * Hlsl.Saturate(entry.Y * 3f) * (1f - body);
        Float3 colour = Gradient(t);
        Float3 rgb = Hlsl.Lerp(colour * 0.6f, colour, Hlsl.Saturate((r - radius) / Hlsl.Max(length, 1f))) * body + colour * glow;
        float alpha = body + glow;

        // A circular oscilloscope just inside the ring.
        float wave = data.Sample(new Float2((t * 1022f + 0.5f) / 1024f, 0.0625f)).X;
        float scopeRadius = radius * 0.78f + wave * radius * 0.22f;
        float sd = Hlsl.Abs(r - scopeRadius);
        float scope = Hlsl.Saturate(1.6f - sd) + 0.4f * Hlsl.Exp(-sd / 8f);
        rgb += Hlsl.Lerp(current, new Float3(1f, 1f, 1f), 0.4f) * scope * (1f - Hlsl.Saturate(alpha));
        alpha += scope;

        // Soft centre glow with the bass, and faint rays turning slowly.
        float centre = Hlsl.Exp(-r / (radius * 0.6f)) * (0.15f + 0.45f * bass + 0.3f * flash);
        float rays = Hlsl.Pow(Hlsl.Abs(Hlsl.Sin(angle * 12f + time * 0.3f)), 24f) * Hlsl.Exp(-r / (size.X * 0.25f)) * 0.12f * (0.3f + bass);
        rgb += Brighten(low) * (centre + rays) * (1f - Hlsl.Saturate(alpha));
        alpha += centre + rays;
        return new Float4(rgb, Hlsl.Saturate(alpha));
    }

    private Float3 Gradient(float t)
    {
        Float3 c = t < 0.5f ? Hlsl.Lerp(low, mid, t * 2f) : Hlsl.Lerp(mid, high, t * 2f - 1f);
        return c / Hlsl.Max(Hlsl.Max(c.X, c.Y), Hlsl.Max(c.Z, 0.001f));
    }

    private static Float3 Brighten(Float3 c) => c / Hlsl.Max(Hlsl.Max(c.X, c.Y), Hlsl.Max(c.Z, 0.001f));
}

/// <summary>
/// The flashy fields, drawn from maths alone. Style 0: warp, streaks flying out from the centre
/// faster with the music. 1: aurora, swaying curtains of light. 2: plasma, flowing colour.
/// </summary>
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader40)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct WaveformFieldShader(
    Float2 size, Float3 low, Float3 mid, Float3 high,
    float time, float travel, float bass, float mids, float highs, float energy, float flash, int style) : ID2D1PixelShader
{
    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        Float4 result = style == 0 ? Warp(p) : (style == 1 ? Aurora(p) : Plasma(p));
        return result;
    }

    private Float4 Warp(Float2 p)
    {
        Float2 d = (p - size * 0.5f) / size.Y;
        float r = Hlsl.Length(d);
        float around = Hlsl.Atan2(d.Y, d.X) / 6.2831853f + 0.5f;
        Float3 rgb = new Float3(0f, 0f, 0f);
        float alpha = 0f;
        for (int layer = 0; layer < 2; layer++)
        {
            float cells = layer == 0 ? 150f : 233f;
            float lane = around * cells;
            float cell = Hlsl.Floor(lane);
            float h = Hash(cell + layer * 97f);
            float speed = 0.35f + h * 1.1f;
            float along = Hlsl.Frac(h * 7.31f + travel * speed * 0.3f);
            float tip = along * along * 1.7f;
            float length = 0.03f + 0.3f * energy * along;
            float onStreak = Hlsl.Step(r, tip) * Hlsl.Saturate(1f - (tip - r) / length);
            float sideways = Hlsl.Abs(Hlsl.Frac(lane) - 0.5f) * 2f;
            float width = Hlsl.Exp(-sideways * sideways * 18f);
            float brightness = onStreak * width * (0.25f + 0.9f * energy) * Hlsl.Saturate(r * 12f);
            rgb += Gradient(h) * brightness;
            alpha += brightness;
        }
        float core = Hlsl.Exp(-r * 5f) * (0.2f + 0.6f * bass) + flash * 0.25f * Hlsl.Exp(-r * 1.5f);
        rgb += Gradient(0.1f) * core;
        alpha += core;
        return new Float4(rgb, Hlsl.Saturate(alpha));
    }

    private Float4 Aurora(Float2 p)
    {
        float x = p.X / size.Y;
        float y = p.Y / size.Y;
        Float3 rgb = new Float3(0f, 0f, 0f);
        for (int i = 0; i < 4; i++)
        {
            float fi = i;
            float edge = 0.5f + fi * 0.08f
                + 0.07f * Hlsl.Sin(x * (1.1f + fi * 0.6f) + time * (0.22f + fi * 0.1f) + fi)
                + 0.06f * Hlsl.Sin(x * 3.3f - time * 0.37f + fi * 2f) * (0.3f + 1.6f * bass);
            float above = Hlsl.Max(edge - y, 0f);
            float below = Hlsl.Max(y - edge, 0f);
            float curtain = Hlsl.Exp(-above / (0.12f + 0.25f * (mids * 0.6f + energy * 0.4f))) * Hlsl.Exp(-below * 45f);
            float rays = 0.55f + 0.45f * Hlsl.Sin(x * (38f + fi * 11f) + time * (1.3f + fi * 0.4f) + highs * 3f);
            rgb += Gradient(fi / 3f) * curtain * rays * (0.3f + 0.7f * energy);
        }
        rgb += Gradient(0.2f) * flash * 0.15f;
        float alpha = Hlsl.Saturate(Hlsl.Max(rgb.X, Hlsl.Max(rgb.Y, rgb.Z)));
        return new Float4(Hlsl.Min(rgb, new Float3(alpha, alpha, alpha)), alpha);
    }

    private Float4 Plasma(Float2 p)
    {
        Float2 q = p / size.Y;
        Float2 orbit = new Float2(size.X / size.Y * 0.5f + 0.5f * Hlsl.Sin(time * 0.31f), 0.5f + 0.3f * Hlsl.Cos(time * 0.43f));
        float v = Hlsl.Sin(q.X * 3.1f + time * 0.7f)
            + Hlsl.Sin(q.Y * 4.3f + time * 0.9f)
            + Hlsl.Sin((q.X + q.Y) * 2.6f + travel * 0.6f)
            + Hlsl.Sin(Hlsl.Length(q - orbit) * (6.5f + 5f * energy) - travel * 1.2f);
        float t = Hlsl.Frac(v * 0.125f + 0.5f + travel * 0.04f);
        float brightness = 0.22f + 0.6f * energy + 0.25f * flash;
        Float3 rgb = Gradient(1f - Hlsl.Abs(t * 2f - 1f)) * brightness;
        float alpha = Hlsl.Saturate(brightness);
        return new Float4(Hlsl.Min(rgb, new Float3(alpha, alpha, alpha)), alpha);
    }

    private static float Hash(float n) => Hlsl.Frac(Hlsl.Sin(n * 12.9898f) * 43758.5453f);

    private Float3 Gradient(float t)
    {
        Float3 c = t < 0.5f ? Hlsl.Lerp(low, mid, t * 2f) : Hlsl.Lerp(mid, high, t * 2f - 1f);
        return c / Hlsl.Max(Hlsl.Max(c.X, c.Y), Hlsl.Max(c.Z, 0.001f));
    }
}

/// <summary>
/// An old oscilloscope on a curved CRT: glass that bends at the edges, a bezel with rounded
/// corners, a graticule with tick marks, scan lines, beam bloom, phosphor afterglow from the last
/// six frames and a faint reflection. Style 0 draws the wave against time; 1 is an X-Y
/// vectorscope of left against right. The phosphor takes the look's mid colour (green for
/// Spectrum, amber for Ember).
/// </summary>
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader40)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct WaveformCrtShader(Float2 size, Float3 phosphor, int vectorscope, float time) : ID2D1PixelShader
{
    [D2DResourceTextureIndex(0)]
    private readonly D2D1ResourceTexture2D<Float4> data;

    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        Float2 c = p / size * 2f - 1f;
        // Curved glass: the picture bends away towards the edges, more along the short side.
        Float2 bent = new Float2(c.X * (1f + c.Y * c.Y * 0.035f), c.Y * (1f + c.X * c.X * 0.09f));
        Float2 uv = bent * 0.5f + 0.5f;
        Float2 screenPoint = uv * size;

        // Rounded screen inside the bezel.
        Float2 halfScreen = size * 0.5f - new Float2(18f, 18f);
        float radius = size.Y * 0.08f;
        Float2 q = Hlsl.Abs(screenPoint - size * 0.5f) - halfScreen + new Float2(radius, radius);
        float edge = Hlsl.Length(Hlsl.Max(q, new Float2(0f, 0f))) + Hlsl.Min(Hlsl.Max(q.X, q.Y), 0f) - radius;
        float onScreen = Hlsl.Saturate(0.5f - edge);
        float rim = Hlsl.Exp(-Hlsl.Abs(edge) / 10f);
        Float3 bezel = new Float3(0.045f, 0.045f, 0.05f) * (0.7f + 0.3f * (1f - uv.Y)) + new Float3(0.05f, 0.05f, 0.055f) * rim * Hlsl.Step(0f, edge);

        Float3 white = new Float3(1f, 1f, 1f);
        Float3 rgb = phosphor * 0.022f;
        // Graticule: divisions an eighth of the height, with fine ticks along the centre lines.
        float cell = size.Y / 8f;
        Float2 centre = size * 0.5f;
        Float2 g = Hlsl.Abs(Hlsl.Frac((screenPoint - centre) / cell + 0.5f) - 0.5f) * cell;
        float grid = Hlsl.Saturate(1f - Hlsl.Min(g.X, g.Y)) * 0.11f;
        Float2 tickGap = Hlsl.Abs(Hlsl.Frac((screenPoint - centre) / (cell / 5f) + 0.5f) - 0.5f) * (cell / 5f);
        float ticks = (Hlsl.Saturate(1f - tickGap.X) * Hlsl.Step(Hlsl.Abs(screenPoint.Y - centre.Y), 5f) +
                       Hlsl.Saturate(1f - tickGap.Y) * Hlsl.Step(Hlsl.Abs(screenPoint.X - centre.X), 5f)) * 0.11f;
        rgb += Hlsl.Lerp(phosphor, white, 0.5f) * (grid + ticks);

        Float3 beam = new Float3(0f, 0f, 0f);
        if (vectorscope != 0)
        {
            beam = Vector(screenPoint);
        }
        else
        {
            for (int k = 0; k < 7; k++)
            {
                float row = k == 0 ? 0.5f : k + 1.5f;
                float strength = Hlsl.Pow(0.55f, k);
                beam += Trace(screenPoint, row) * strength;
            }
        }
        rgb += beam;

        float scan = 0.78f + 0.22f * Hlsl.Cos(screenPoint.Y * 2.0944f);
        float grain = (Hash(Hlsl.Floor(screenPoint.X) + Hlsl.Floor(screenPoint.Y) * 1789f + Hlsl.Floor(time * 30f) * 97f) - 0.5f) * 0.025f;
        float vignette = 1f - Hlsl.Dot(c, c) * 0.18f;
        Float2 shine = uv - new Float2(0.22f, 0.14f);
        float reflection = 0.045f * Hlsl.Exp(-(shine.X * shine.X * 6f + shine.Y * shine.Y * 40f));
        Float3 tube = rgb * scan * vignette + new Float3(reflection + grain, reflection + grain, reflection + grain);
        return new Float4(Hlsl.Lerp(bezel, Hlsl.Saturate(tube), onScreen), 1f);
    }

    /// <summary>The beam drawing one stored frame of the wave (row given as its centre).</summary>
    private Float3 Trace(Float2 p, float row)
    {
        float perPixel = 1024f / size.X;
        float x = p.X * perPixel;
        float amplitude = size.Y * 0.38f;
        float middle = size.Y * 0.5f;
        float y0 = middle - Sample(x - perPixel, row) * amplitude;
        float y1 = middle - Sample(x, row) * amplitude;
        float y2 = middle - Sample(x + perPixel, row) * amplitude;
        float lowest = Hlsl.Min(y1, Hlsl.Min((y0 + y1) * 0.5f, (y1 + y2) * 0.5f));
        float highest = Hlsl.Max(y1, Hlsl.Max((y0 + y1) * 0.5f, (y1 + y2) * 0.5f));
        float d = Hlsl.Max(Hlsl.Max(lowest - p.Y, p.Y - highest), 0f);
        return Beam(d);
    }

    private float Sample(float position, float row) =>
        data.Sample(new Float2(Hlsl.Clamp(position, 0.5f, 1023.5f) / 1024f, row / 8f)).X;

    private Float3 Vector(Float2 p)
    {
        Float2 middle = size * 0.5f;
        float up = size.Y * 0.4f;
        float across = Hlsl.Min(size.X * 0.42f, up * 2.2f);
        float nearest = 1e6f;
        Float2 previous = Point(0, middle, up, across);
        for (int i = 1; i < 128; i++)
        {
            Float2 next = Point(i, middle, up, across);
            Float2 segment = next - previous;
            float t = Hlsl.Saturate(Hlsl.Dot(p - previous, segment) / Hlsl.Max(Hlsl.Dot(segment, segment), 0.0001f));
            nearest = Hlsl.Min(nearest, Hlsl.Length(p - previous - segment * t));
            previous = next;
        }
        return Beam(nearest);
    }

    private Float2 Point(int i, Float2 middle, float up, float across)
    {
        Float4 v = data.Sample(new Float2((i * 8f + 0.5f) / 1024f, 0.1875f));
        return middle + new Float2((v.X - v.Y) * 0.7071f * across, -(v.X + v.Y) * 0.7071f * up);
    }

    /// <summary>Bright core, phosphor glow and a wide bloom at distance d from the beam.</summary>
    private Float3 Beam(float d)
    {
        float core = Hlsl.Saturate(1.8f - d);
        float glow = 0.5f * Hlsl.Exp(-d / 9f) + 0.16f * Hlsl.Exp(-d / 45f);
        return Hlsl.Lerp(phosphor, new Float3(1f, 1f, 1f), 0.6f) * core + phosphor * glow * (1f - core);
    }

    private static float Hash(float n) => Hlsl.Frac(Hlsl.Sin(n * 12.9898f) * 43758.5453f);
}

/// <summary>
/// Demo-scene tunnel: a checker tunnel in chunky pixels and stepped colours, flying faster
/// with the music and flashing on the beat.
/// </summary>
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader40)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct WaveformDemoShader(
    Float2 size, Float3 low, Float3 mid, Float3 high,
    float time, float travel, float energy, float flash, float pixel) : ID2D1PixelShader
{
    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        Float2 q = Hlsl.Floor(p / pixel) * pixel + pixel * 0.5f;
        Float3 rgb = Tunnel(q);
        // A limited palette: colours come in steps, as on old hardware.
        rgb = Hlsl.Floor(Hlsl.Saturate(rgb) * 10f + 0.5f) / 10f;
        return new Float4(rgb, 1f);
    }

    private Float3 Tunnel(Float2 q)
    {
        Float2 centre = size * 0.5f + new Float2(Hlsl.Sin(time * 0.7f) * size.X * 0.12f, Hlsl.Cos(time * 0.9f) * size.Y * 0.1f);
        Float2 d = (q - centre) / size.Y;
        float r = Hlsl.Length(d) + 0.001f;
        float u = Hlsl.Atan2(d.Y, d.X) / 3.14159265f * 8f + time * 0.15f;
        float v = 0.35f / r + travel * 1.2f;
        float check = Hlsl.Frac((Hlsl.Floor(u) + Hlsl.Floor(v)) * 0.5f) * 2f;
        // Rings step along the look's three colours; checks alternate bright and dark.
        float ring = Hlsl.Frac(Hlsl.Floor(v) / 3f);
        Float3 tint = ring < 0.3f ? Brighten(low) : (ring < 0.6f ? Brighten(mid) : Brighten(high));
        Float3 colour = tint * (0.2f + 0.8f * check);
        float fog = Hlsl.Saturate(r * 2.4f);
        return colour * fog * (0.4f + 0.9f * energy) + new Float3(1f, 1f, 1f) * flash * 0.35f * fog;
    }

    private static Float3 Brighten(Float3 c) => c / Hlsl.Max(Hlsl.Max(c.X, c.Y), Hlsl.Max(c.Z, 0.001f));
}

/// <summary>
/// The crazy ones. Style 0: kaleidoscope. 1: vortex. 2: glitch scope. 3: fire. 4: metaballs.
/// 5: lightning. 6: a Julia-set fractal steered by the music.
/// </summary>
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader40)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct WaveformCrazyShader(
    Float2 size, Float3 low, Float3 mid, Float3 high,
    float time, float travel, float bass, float mids, float highs, float energy, float flash, int style) : ID2D1PixelShader
{
    [D2DResourceTextureIndex(0)]
    private readonly D2D1ResourceTexture2D<Float4> data;

    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        Float3 rgb = new Float3(0f, 0f, 0f);
        if (style == 0) rgb = Kaleidoscope(p);
        else if (style == 1) rgb = Vortex(p);
        else if (style == 2) rgb = Glitch(p);
        else if (style == 3) rgb = Fire(p);
        else if (style == 4) rgb = Metaballs(p);
        else if (style == 5) rgb = Lightning(p);
        else rgb = Julia(p);
        rgb = Hlsl.Saturate(rgb);
        float alpha = Hlsl.Max(rgb.X, Hlsl.Max(rgb.Y, rgb.Z));
        return new Float4(rgb, alpha);
    }

    private Float3 Kaleidoscope(Float2 p)
    {
        Float2 d = (p - size * 0.5f) / (size.Y * (1f + 0.35f * bass));
        float r = Hlsl.Length(d);
        float slices = 6f + Hlsl.Floor(bass * 4f) * 2f;
        float wedge = 6.2831853f / slices;
        float a = Hlsl.Atan2(d.Y, d.X) + time * 0.1f;
        a = Hlsl.Abs(Hlsl.Fmod(Hlsl.Abs(a), wedge) - wedge * 0.5f);
        Float2 f = new Float2(Hlsl.Cos(a), Hlsl.Sin(a)) * r;
        float level = Band(Hlsl.Saturate(r * 1.2f));
        float v = Hlsl.Sin(f.X * 14f - travel * 2.5f) + Hlsl.Sin(f.Y * 22f + time) + Hlsl.Sin((f.X + f.Y) * 9f + travel);
        float t = Hlsl.Frac(v * 0.17f + r * 0.6f - travel * 0.05f);
        float brightness = Hlsl.Saturate(0.12f + level * 1.1f + energy * 0.6f) * Hlsl.Saturate(1.6f - r);
        return Gradient(1f - Hlsl.Abs(t * 2f - 1f)) * brightness + new Float3(1f, 1f, 1f) * flash * 0.2f * Hlsl.Exp(-r * 3f);
    }

    private Float3 Vortex(Float2 p)
    {
        Float2 d = (p - size * 0.5f) / size.Y;
        float r = Hlsl.Length(d) + 0.0001f;
        float a = Hlsl.Atan2(d.Y, d.X) + (1f / (r + 0.15f)) * (0.4f + bass * 2f) + time * 0.4f;
        float stripes = Hlsl.Sin(a * (10f + Hlsl.Floor(highs * 4f) * 2f) + Hlsl.Log(r) * 9f - travel * 4f);
        float glow = Hlsl.Pow(Hlsl.Saturate(stripes * 0.5f + 0.5f), 3f) * (0.35f + energy * 0.9f);
        return Gradient(Hlsl.Frac(r * 0.8f + travel * 0.03f)) * glow * Hlsl.Saturate(r * 6f) + Gradient(0f) * Hlsl.Exp(-r * 8f) * (0.3f + flash);
    }

    private Float3 Glitch(Float2 p)
    {
        float band = Hlsl.Floor(p.Y / (size.Y / 14f));
        float tick = Hlsl.Floor(time * 9f);
        float jump = Hlsl.Step(0.72f - flash * 0.3f, Hash(band * 3.7f + tick)) * (Hash(band + tick * 1.3f) - 0.5f) * size.X * 0.12f * (0.3f + energy);
        float split = (3f + 18f * flash + 6f * energy);
        float x = p.X + jump;
        float red = Scope(new Float2(x - split, p.Y));
        float green = Scope(new Float2(x, p.Y));
        float blue = Scope(new Float2(x + split, p.Y));
        Float3 rgb = new Float3(red, green, blue) * Hlsl.Lerp(new Float3(1f, 1f, 1f), Gradient(0.5f), 0.4f);
        float noise = Hash(Hlsl.Floor(p.X / 4f) + Hlsl.Floor(p.Y / 2f) * 911f + tick * 37f) * 0.08f * (0.4f + energy);
        float scan = 0.85f + 0.15f * Hlsl.Sin(p.Y * 1.7f);
        return (rgb + new Float3(noise, noise, noise)) * scan;
    }

    private float Scope(Float2 p)
    {
        float perPixel = 1024f / size.X;
        float y = size.Y * 0.5f - data.Sample(new Float2(Hlsl.Clamp(p.X * perPixel, 0.5f, 1023.5f) / 1024f, 0.0625f)).X * size.Y * 0.42f;
        float d = Hlsl.Abs(p.Y - y);
        return Hlsl.Saturate(2.2f - d) + 0.45f * Hlsl.Exp(-d / 12f);
    }

    private Float3 Fire(Float2 p)
    {
        Float2 uv = p / size.Y;
        float height = 1f - p.Y / size.Y;
        float n = Noise(uv * new Float2(4f, 3f) + new Float2(0f, time * 1.6f)) * 0.55f
                + Noise(uv * new Float2(9f, 7f) + new Float2(0f, time * 2.7f)) * 0.3f
                + Noise(uv * new Float2(19f, 15f) + new Float2(0f, time * 4.1f)) * 0.15f;
        float column = Band(Hlsl.Saturate(Hlsl.Abs(p.X / size.X - 0.5f) * 2f));
        float heat = Hlsl.Saturate(n * 1.5f - height * (1.6f - column * 0.9f - bass * 0.9f) + 0.15f);
        Float3 rgb = heat < 0.5f ? Hlsl.Lerp(new Float3(0f, 0f, 0f), Gradient(0f), heat * 2f)
                                 : Hlsl.Lerp(Gradient(0f), Hlsl.Lerp(Gradient(1f), new Float3(1f, 1f, 1f), 0.5f), heat * 2f - 1f);
        return rgb * Hlsl.Saturate(heat * 3f);
    }

    private Float3 Metaballs(Float2 p)
    {
        float field = 0f;
        float hue = 0f;
        for (int i = 0; i < 7; i++)
        {
            float fi = i;
            Float2 centre = size * 0.5f + new Float2(
                Hlsl.Sin(time * (0.31f + fi * 0.07f) + fi * 1.7f) * size.X * 0.4f,
                Hlsl.Cos(time * (0.43f + fi * 0.05f) + fi * 2.3f) * size.Y * 0.33f);
            float radius = size.Y * (0.06f + 0.13f * Band(fi / 6f) + 0.1f * bass);
            Float2 d = p - centre;
            float contribution = radius * radius / Hlsl.Max(Hlsl.Dot(d, d), 1f);
            field += contribution;
            hue += contribution * fi / 6f;
        }
        float t = hue / Hlsl.Max(field, 0.0001f);
        float inside = Hlsl.SmoothStep(0.9f, 1.1f, field);
        float rim = Hlsl.Exp(-Hlsl.Abs(field - 1f) * 6f);
        return Gradient(t) * (inside * 0.75f + rim * 0.6f) + new Float3(1f, 1f, 1f) * rim * flash * 0.4f;
    }

    private Float3 Lightning(Float2 p)
    {
        Float3 rgb = new Float3(0f, 0f, 0f);
        float perPixel = 1024f / size.X;
        float wave = data.Sample(new Float2(Hlsl.Clamp(p.X * perPixel, 0.5f, 1023.5f) / 1024f, 0.0625f)).X;
        for (int i = 0; i < 3; i++)
        {
            float fi = i;
            float jitter = (Noise(new Float2(p.X / 38f + fi * 17f, time * 14f + fi * 5f)) - 0.5f) * size.Y * (0.08f + 0.4f * energy)
                         + (Noise(new Float2(p.X / 9f + fi * 3f, time * 30f)) - 0.5f) * size.Y * 0.04f;
            float y = size.Y * (0.5f + (fi - 1f) * 0.12f) - wave * size.Y * 0.32f + jitter;
            float d = Hlsl.Abs(p.Y - y);
            float strength = (i == 1 ? 1f : 0.55f) * (0.4f + 0.6f * energy + flash * 0.5f);
            rgb += (new Float3(1f, 1f, 1f) * Hlsl.Saturate(1.6f - d) + Gradient(fi / 2f) * (0.6f * Hlsl.Exp(-d / 8f) + 0.2f * Hlsl.Exp(-d / 40f))) * strength;
        }
        return rgb + Gradient(0.5f) * flash * 0.1f;
    }

    private Float3 Julia(Float2 p)
    {
        Float2 z = (p - size * 0.5f) / (size.Y * 0.42f);
        Float2 c = new Float2(-0.75f + 0.12f * Hlsl.Sin(travel * 0.23f) + 0.16f * bass, 0.18f + 0.08f * Hlsl.Cos(time * 0.17f) + 0.12f * mids);
        float steps = 0f;
        for (int i = 0; i < 48; i++)
        {
            if (Hlsl.Dot(z, z) < 16f)
            {
                z = new Float2(z.X * z.X - z.Y * z.Y, 2f * z.X * z.Y) + c;
                steps += 1f;
            }
        }
        float smooth = steps < 48f ? steps - Hlsl.Log2(Hlsl.Log2(Hlsl.Max(Hlsl.Dot(z, z), 1.0001f))) + 4f : 0f;
        float t = Hlsl.Frac(smooth * 0.035f + travel * 0.05f);
        float brightness = steps < 48f ? Hlsl.Saturate(smooth / 20f) * (0.45f + 0.75f * energy) : 0f;
        return Gradient(1f - Hlsl.Abs(t * 2f - 1f)) * brightness + new Float3(1f, 1f, 1f) * flash * 0.08f;
    }

    /// <summary>A spectrum level at 0 (bass) to 1 (highs), read between bands.</summary>
    private float Band(float position) => data.Sample(new Float2((position * 95f + 0.5f) / 1024f, 0.0625f)).Y;

    private static float Hash(float n) => Hlsl.Frac(Hlsl.Sin(n * 12.9898f) * 43758.5453f);

    private static float Noise(Float2 x)
    {
        Float2 i = Hlsl.Floor(x);
        Float2 f = Hlsl.Frac(x);
        f = f * f * (3f - 2f * f);
        float a = Hash(i.X + i.Y * 57f);
        float b = Hash(i.X + 1f + i.Y * 57f);
        float c = Hash(i.X + (i.Y + 1f) * 57f);
        float d = Hash(i.X + 1f + (i.Y + 1f) * 57f);
        return Hlsl.Lerp(Hlsl.Lerp(a, b, f.X), Hlsl.Lerp(c, d, f.X), f.Y);
    }

    private Float3 Gradient(float t)
    {
        Float3 c = t < 0.5f ? Hlsl.Lerp(low, mid, t * 2f) : Hlsl.Lerp(mid, high, t * 2f - 1f);
        return c / Hlsl.Max(Hlsl.Max(c.X, c.Y), Hlsl.Max(c.Z, 0.001f));
    }
}
