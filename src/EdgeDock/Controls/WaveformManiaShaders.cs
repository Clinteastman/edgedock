using ComputeSharp;
using ComputeSharp.D2D1;

namespace EdgeDock.Controls;

/// <summary>
/// The fast, mental ones, written for EdgeDock. Every part answers to its own band.
/// Style 0: hyperspace, streaks rushing past in a tunnel of light. 1: synth grid, a neon grid
/// racing towards a striped sun under mountains shaped by the spectrum. 2: kaleido tunnel, a
/// spinning mirrored tunnel. 3: Mandelbrot zoom, diving into the set again and again. 4: spectrum
/// city, a fast flight between towers whose heights follow the spectrum. 5: DNA helix, a double
/// strand of orbs across the screen, one pair per band. 6: liquid colour, domain-warped noise
/// flowing faster with the music. 7: crystal shards, a spinning folded crystal. 8: glass cubes,
/// three neon-edged glass cubes across the screen, one per band. 9: fractal land, a flight over a
/// landscape grown from the Kali set.
/// </summary>
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader40)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct WaveformManiaShader(
    Float2 size, Float3 low, Float3 mid, Float3 high,
    float time, float travel, float bass, float mids, float highs, float energy, float flash, int style) : ID2D1PixelShader
{
    /// <summary>The live data texture (spectrum levels in row 0, channel Y).</summary>
    [D2DResourceTextureIndex(0)]
    private readonly D2D1ResourceTexture2D<Float4> data;

    /// <summary>
    /// Turns a view ray by the camera's look-around (yaw in Z, pitch in W of the data texture's
    /// row 1, first entry; both zero unless the view's camera is switched on).
    /// </summary>
    private Float3 Look(Float3 dir)
    {
        Float4 camera = data.Sample(new Float2(0.5f / 1024f, 0.1875f));
        float cy = Hlsl.Cos(camera.Z), sy = Hlsl.Sin(camera.Z);
        Float3 d = new Float3(dir.X * cy + dir.Z * sy, dir.Y, dir.Z * cy - dir.X * sy);
        float cp = Hlsl.Cos(camera.W), sp = Hlsl.Sin(camera.W);
        return new Float3(d.X, d.Y * cp + d.Z * sp, d.Z * cp - d.Y * sp);
    }

    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        Float4 first = new Float4(Band(0f), Band(1f / 7f), Band(2f / 7f), Band(3f / 7f));
        Float4 second = new Float4(Band(4f / 7f), Band(5f / 7f), Band(6f / 7f), Band(1f));
        Float3 rgb = new Float3(0f, 0f, 0f);
        if (style == 0) rgb = Hyperspace(p, first, second);
        else if (style == 1) rgb = SynthGrid(p, first, second);
        else if (style == 2) rgb = KaleidoTunnel(p, first, second);
        else if (style == 3) rgb = MandelbrotZoom(p, first);
        else if (style == 4) rgb = City(p, first, second);
        else if (style == 5) rgb = Helix(p);
        else if (style == 6) rgb = LiquidColour(p, first);
        else if (style == 7) rgb = Shards(p, first);
        else if (style == 8) rgb = GlassCubes(p, first, second);
        else rgb = FractalLand(p, first, second);
        return new Float4(Hlsl.Saturate(rgb), 1f);
    }

    private static float Spectrum(float position, Float4 first, Float4 second)
    {
        float slot = Hlsl.Saturate(position) * 7f;
        float i = Hlsl.Floor(Hlsl.Min(slot, 6.999f));
        return Hlsl.Lerp(Pick(i, first, second), Pick(i + 1f, first, second), slot - i);
    }

    private static float Pick(float i, Float4 first, Float4 second) =>
        i < 0.5f ? first.X : (i < 1.5f ? first.Y : (i < 2.5f ? first.Z : (i < 3.5f ? first.W :
        (i < 4.5f ? second.X : (i < 5.5f ? second.Y : (i < 6.5f ? second.Z : second.W))))));

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

    private static Float2 Rotate(Float2 v, float a) =>
        new Float2(v.X * Hlsl.Cos(a) - v.Y * Hlsl.Sin(a), v.X * Hlsl.Sin(a) + v.Y * Hlsl.Cos(a));

    // ---------- Hyperspace ----------

    private Float3 Hyperspace(Float2 fragment, Float4 first, Float4 second)
    {
        Float2 uv = (fragment - size * 0.5f) / size.Y;
        float r = Hlsl.Length(uv) + 0.0001f;
        float around = Hlsl.Atan2(uv.Y, uv.X) / 6.2831853f + 0.5f;
        float depth = 0.25f / r;
        Float3 rgb = new Float3(0f, 0f, 0f);
        for (int layer = 0; layer < 3; layer++)
        {
            float fl = layer;
            float lanes = 90f + fl * 70f;
            float lane = Hlsl.Floor(around * lanes);
            float h = Hash(lane * 1.7f + fl * 31f);
            float band = Hlsl.Frac(h * 3.1f);
            float level = Spectrum(band, first, second);
            float speed = 2.5f + 3f * h;
            float position = Hlsl.Frac(depth * 0.35f - travel * speed * 0.35f + h);
            float streak = Hlsl.SmoothStep(0f, 0.04f, position) * Hlsl.SmoothStep(0.25f + 0.4f * energy, 0.05f, position);
            float width = Hlsl.Exp(-Hlsl.Pow(Hlsl.Frac(around * lanes) - 0.5f, 2f) * 60f);
            rgb += Gradient(band) * streak * width * (0.3f + 1.4f * level) * Hlsl.Saturate(r * 4f);
        }
        // Rings rushing past on the beat, and a bright core ahead.
        float rings = Hlsl.Pow(Hlsl.Abs(Hlsl.Sin(depth * 3f - travel * 8f)), 30f) * (0.15f + 0.8f * bass) * Hlsl.Saturate(r * 3f);
        rgb += Gradient(0f) * rings + new Float3(1f, 1f, 1f) * Hlsl.Exp(-r * 9f) * (0.4f + flash);
        return rgb;
    }

    // ---------- Synth grid ----------

    private Float3 SynthGrid(Float2 fragment, Float4 first, Float4 second)
    {
        Float2 uv = (fragment - size * 0.5f) / size.Y;
        uv.Y = -uv.Y;
        float horizon = -0.02f;
        Float3 rgb = new Float3(0f, 0f, 0f);
        if (uv.Y < horizon)
        {
            // The floor: perspective grid racing towards us with the music.
            float below = horizon - uv.Y;
            float z = 0.35f / below;
            float x = uv.X * z;
            float zMove = z + travel * 3f;
            Float2 cell = Hlsl.Abs(Hlsl.Frac(new Float2(x, zMove) * 0.8f) - 0.5f);
            float line = Hlsl.Max(Hlsl.Exp(-cell.X * 30f / (1f + z * 0.08f)), Hlsl.Exp(-cell.Y * 30f / (1f + z * 0.08f)));
            float fade = Hlsl.Exp(-z * 0.05f);
            float band = Hlsl.Saturate(uv.X / (size.X / size.Y) + 0.5f);
            rgb = Gradient(band) * line * fade * (0.5f + 1.2f * bass) + Gradient(0.1f) * 0.06f * fade;
        }
        else
        {
            float up = uv.Y - horizon;
            rgb = Hlsl.Lerp(Gradient(0.9f) * 0.35f, new Float3(0.02f, 0f, 0.06f), Hlsl.Saturate(up * 2.5f));
            // A striped sun that swells with the bass.
            float sunRadius = 0.33f + 0.05f * bass;
            Float2 sunCentre = new Float2(0f, horizon + 0.18f);
            float inSun = Hlsl.Step(Hlsl.Length(uv - sunCentre), sunRadius);
            float stripes = Hlsl.Step(0.3f * Hlsl.Saturate((sunCentre.Y - uv.Y) * 3f + 0.5f), Hlsl.Frac(uv.Y * 18f - time * 0.6f));
            rgb = Hlsl.Lerp(rgb, Hlsl.Lerp(Gradient(0.15f), Gradient(0.85f), Hlsl.Saturate((uv.Y - sunCentre.Y) / sunRadius + 0.5f)), inSun * stripes);
            // Mountains shaped by the spectrum across the screen.
            float band = Hlsl.Saturate(uv.X / (size.X / size.Y) + 0.5f);
            float ridge = horizon + 0.04f + Spectrum(1f - Hlsl.Abs(band * 2f - 1f), first, second) * 0.22f
                        + Noise(new Float2(uv.X * 6f, 1f)) * 0.04f;
            float mountain = Hlsl.Step(uv.Y, ridge);
            float edge = Hlsl.Exp(-Hlsl.Abs(uv.Y - ridge) * 120f);
            rgb = Hlsl.Lerp(rgb, new Float3(0.01f, 0f, 0.03f), mountain) + Gradient(band) * edge * (0.4f + highs);
        }
        float glow = Hlsl.Exp(-Hlsl.Abs(uv.Y - horizon) * 30f) * (0.3f + 0.7f * energy);
        return rgb + Gradient(0.5f) * glow + new Float3(flash, flash, flash) * 0.05f;
    }

    // ---------- Kaleido tunnel ----------

    private Float3 KaleidoTunnel(Float2 fragment, Float4 first, Float4 second)
    {
        Float2 uv = (fragment - size * 0.5f) / size.Y;
        uv = Rotate(uv, time * 0.4f + travel * 0.2f);
        float r = Hlsl.Length(uv) + 0.0001f;
        float a = Hlsl.Atan2(uv.Y, uv.X);
        float slices = 6f + Hlsl.Floor(mids * 3f) * 2f;
        float wedge = 6.2831853f / slices;
        a = Hlsl.Abs(a - wedge * Hlsl.Floor(a / wedge) - wedge * 0.5f);
        float depth = 0.3f / r + travel * 1.6f;
        Float2 q = new Float2(a * 3f, depth);
        float pattern = Hlsl.Sin(q.X * 6f + Hlsl.Sin(q.Y * 2f) * 2f) * Hlsl.Sin(q.Y * 5f + time) + Hlsl.Sin((q.X + q.Y) * 4f - time * 2f);
        float ring = Hlsl.Frac(depth * 0.25f);
        float level = Spectrum(ring, first, second);
        float glow = Hlsl.Pow(Hlsl.Saturate(pattern * 0.5f + 0.5f), 2f) * (0.35f + 1.3f * level);
        return Gradient(ring) * glow * Hlsl.Saturate(r * 3f) + Gradient(0f) * Hlsl.Exp(-r * 6f) * (0.4f + flash);
    }

    // ---------- Mandelbrot zoom ----------

    private Float3 MandelbrotZoom(Float2 fragment, Float4 levels)
    {
        Float2 uv = (fragment - size * 0.5f) / size.Y;
        // Dive towards a point in the seahorse valley; every 24 seconds the dive starts again.
        float cycle = Hlsl.Frac((time + travel * 2f) / 24f);
        float zoom = Hlsl.Exp(-cycle * 9f);
        Float2 c = new Float2(-0.743643887f, 0.131825904f) + Rotate(uv, time * 0.05f) * zoom * 1.6f;
        Float2 z = new Float2(0f, 0f);
        float steps = 0f;
        for (int i = 0; i < 120; i++)
        {
            if (Hlsl.Dot(z, z) < 256f)
            {
                z = new Float2(z.X * z.X - z.Y * z.Y, 2f * z.X * z.Y) + c;
                steps += 1f;
            }
        }
        float escaped = Hlsl.Step(steps, 119f);
        float smooth = steps - Hlsl.Log2(Hlsl.Log2(Hlsl.Max(Hlsl.Dot(z, z), 1.0001f))) + 4f;
        float t = Hlsl.Frac(smooth * 0.02f - time * 0.1f);
        float band = Hlsl.Floor(t * 3.99f);
        float level = band < 0.5f ? levels.X : (band < 1.5f ? levels.Y : (band < 2.5f ? levels.Z : levels.W));
        float fade = Hlsl.SmoothStep(0f, 0.04f, cycle) * Hlsl.SmoothStep(1f, 0.94f, cycle);
        return Gradient(1f - Hlsl.Abs(t * 2f - 1f)) * escaped * (0.35f + 1.1f * level) * fade;
    }

    // ---------- Spectrum city ----------

    private float Tower(Float3 p, Float4 first, Float4 second)
    {
        Float2 cell = Hlsl.Floor(p.XZ);
        // Keep a street clear down the middle to fly along.
        if (Hlsl.Abs(cell.X) < 0.5f) return 0.5f;
        Float2 local = p.XZ - cell - 0.5f;
        float band = Hlsl.Saturate(cell.X / 16f + 0.5f);
        float h = 0.3f + Hash(cell.X * 17.3f + cell.Y * 3.1f) * 1.2f + Spectrum(band, first, second) * 2.5f;
        Float2 d = Hlsl.Abs(local) - 0.32f;
        float side = Hlsl.Length(Hlsl.Max(d, new Float2(0f, 0f))) + Hlsl.Min(Hlsl.Max(d.X, d.Y), 0f);
        return Hlsl.Max(side, p.Y - h);
    }

    private Float3 City(Float2 fragment, Float4 first, Float4 second)
    {
        Float2 uv = (fragment - size * 0.5f) / size.Y;
        uv.Y = -uv.Y;
        Float3 origin = new Float3(0.5f + Hlsl.Sin(time * 0.3f) * 0.3f, 3.4f + 0.4f * Hlsl.Sin(time * 0.2f), travel * 3f);
        Float3 dir = Look(Hlsl.Normalize(new Float3(uv.X, uv.Y - 0.45f, 1.1f)));
        float t = 0f;
        float hit = 0f;
        for (int i = 0; i < 110; i++)
        {
            if (hit < 0.5f && t < 30f)
            {
                Float3 p = origin + dir * t;
                float d = Hlsl.Min(Tower(p, first, second), p.Y);
                if (d < 0.002f) hit = 1f;
                else t += Hlsl.Min(d, 0.35f);
            }
        }
        Float3 skyColour = Hlsl.Lerp(Gradient(0.8f) * 0.15f, new Float3(0.01f, 0f, 0.03f), Hlsl.Saturate(uv.Y * 2f));
        if (hit < 0.5f) return skyColour;
        Float3 position = origin + dir * t;
        float band = Hlsl.Saturate(Hlsl.Floor(position.X) / 16f + 0.5f);
        float level = Spectrum(band, first, second);
        // Windows: a grid of lights on the towers, lit by their band; the street glows.
        Float2 window = Hlsl.Frac(new Float2(position.X + position.Z, position.Y) * new Float2(6f, 8f));
        float lit = Hlsl.Step(0.35f, window.X) * Hlsl.Step(0.4f, window.Y) * Hlsl.Step(0.45f - 0.3f * level, Hash(Hlsl.Floor(position.Y * 8f) + Hlsl.Floor((position.X + position.Z) * 6f) * 7.1f));
        float street = Hlsl.Step(position.Y, 0.01f);
        Float3 colour = Gradient(band) * (lit * (0.5f + 1.2f * level) + 0.04f) + Gradient(0.5f) * street * 0.15f * (0.5f + bass);
        float fog = Hlsl.Exp(-t * 0.07f);
        return Hlsl.Lerp(skyColour, colour, fog) + new Float3(flash, flash, flash) * 0.04f;
    }

    // ---------- DNA helix ----------

    private Float3 Helix(Float2 fragment)
    {
        Float2 uv = (fragment - size * 0.5f) / size.Y;
        float spacing = 0.075f;
        float columns = Hlsl.Floor(size.X / size.Y / spacing);
        Float3 rgb = new Float3(0f, 0f, 0f);
        for (int j = -1; j <= 1; j++)
        {
            float k = Hlsl.Round(uv.X / spacing) + j;
            float band = Hlsl.Saturate(k / columns + 0.5f);
            float level = data.Sample(new Float2((band * 95f + 0.5f) / 1024f, 0.0625f)).Y;
            float phase = k * 0.32f + travel * 2f;
            float x = k * spacing;
            float amplitude = 0.22f + 0.1f * bass;
            for (int s = 0; s < 2; s++)
            {
                float side = s == 0 ? 1f : -1f;
                float y = Hlsl.Sin(phase) * amplitude * side;
                float near = Hlsl.Cos(phase) * side;
                float radius = (0.016f + 0.03f * level) * (1f + 0.35f * near);
                float d = Hlsl.Length(uv - new Float2(x, y));
                float orb = Hlsl.SmoothStep(radius, radius * 0.6f, d) + Hlsl.Exp(-d / (radius * 1.5f)) * 0.4f;
                rgb += Gradient(band) * orb * (0.35f + 1.2f * level) * (0.6f + 0.4f * near);
            }
            // The rung between the two strands.
            float yA = Hlsl.Sin(phase) * amplitude;
            float rung = Hlsl.Exp(-Hlsl.Abs(uv.X - x) * 900f) * Hlsl.Step(Hlsl.Abs(uv.Y), Hlsl.Abs(yA)) * 0.25f * (0.3f + level);
            rgb += Gradient(band) * rung;
        }
        return rgb + Gradient(0.5f) * flash * 0.05f;
    }

    // ---------- Liquid colour ----------

    private float Warp(Float2 q)
    {
        return Noise(q) * 0.5f + Noise(q * 2.1f) * 0.25f + Noise(q * 4.3f) * 0.125f;
    }

    private Float3 LiquidColour(Float2 fragment, Float4 levels)
    {
        Float2 q = fragment / size.Y * 2.2f;
        float t = travel * 0.4f;
        Float2 a = new Float2(Warp(q + t * 0.3f), Warp(q + new Float2(5.2f, 1.3f) - t * 0.2f));
        Float2 b = new Float2(Warp(q + 4f * a * (1f + levels.X) + new Float2(1.7f, 9.2f) + t * 0.15f),
                              Warp(q + 4f * a * (1f + levels.Y) + new Float2(8.3f, 2.8f) - t * 0.1f));
        float f = Warp(q + 4f * b * (1f + levels.Z));
        float shade = Hlsl.Saturate(f * f * 2.2f);
        Float3 colour = Hlsl.Lerp(Gradient(Hlsl.Saturate(Hlsl.Length(a))), Gradient(Hlsl.Saturate(b.X)), Hlsl.Saturate(b.Y * 1.5f));
        return colour * (0.25f + shade * (0.9f + 0.8f * energy)) + Gradient(1f) * Hlsl.Pow(shade, 6f) * levels.W;
    }

    // ---------- Crystal shards ----------

    private float Crystal(Float3 p, Float4 levels)
    {
        float scale = 1f;
        for (int i = 0; i < 7; i++)
        {
            float level = i < 2 ? levels.X : (i < 4 ? levels.Y : (i < 6 ? levels.Z : levels.W));
            p = Hlsl.Abs(p);
            if (p.X < p.Y) p = new Float3(p.Y, p.X, p.Z);
            if (p.X < p.Z) p = new Float3(p.Z, p.Y, p.X);
            if (p.Y < p.Z) p = new Float3(p.X, p.Z, p.Y);
            // The standard octahedral fold: scale 2 about the corner, nudged by the band.
            p = p * 2f - new Float3(1f, 1f, 1f) * (1f + 0.25f * level);
            scale *= 2f;
            Float2 turned = Rotate(new Float2(p.X, p.Z), 0.12f + time * 0.07f);
            p = new Float3(turned.X, p.Y, turned.Y);
        }
        return (Hlsl.Length(p) - 1.6f) / scale;
    }

    private Float3 Shards(Float2 fragment, Float4 levels)
    {
        Float2 uv = (fragment - size * 0.5f) / size.Y;
        uv.Y = -uv.Y;
        float spin = travel * 0.25f;
        Float3 origin = new Float3(Hlsl.Sin(spin) * 4.6f, 0.8f * Hlsl.Sin(time * 0.2f), Hlsl.Cos(spin) * 4.6f);
        Float3 forward = Hlsl.Normalize(-origin);
        Float3 right = Hlsl.Normalize(new Float3(forward.Z, 0f, -forward.X));
        Float3 upward = Hlsl.Cross(right, forward);
        Float3 dir = Look(Hlsl.Normalize(forward * 1.4f + uv.X * right + uv.Y * upward));
        float t = 0f;
        float hit = 0f;
        float glow = 0f;
        for (int i = 0; i < 80; i++)
        {
            if (hit < 0.5f && t < 10f)
            {
                float d = Crystal(origin + dir * t, levels);
                glow += Hlsl.Exp(-Hlsl.Abs(d) * 30f) * 0.025f;
                if (d < 0.001f) hit = 1f;
                else t += d * 0.8f;
            }
        }
        Float3 rgb = Gradient(0.3f) * glow * (0.6f + 1.5f * energy);
        if (hit > 0.5f)
        {
            Float3 position = origin + dir * t;
            float band = Hlsl.Saturate(Hlsl.Length(position) / 2f);
            rgb += Gradient(band) * (0.4f + 0.9f * Hlsl.Lerp(levels.X, levels.W, band)) + new Float3(flash, flash, flash) * 0.2f;
        }
        return rgb;
    }

    // ---------- Glass cubes ----------

    private const float GlassSpacing = 3.4f;

    /// <summary>Distance to the edges of a cube frame (X) and to its faces (Y).</summary>
    private static Float2 CubeFrame(Float3 p, float size, float edge)
    {
        Float3 q = Hlsl.Abs(p) - size;
        float box = Hlsl.Length(Hlsl.Max(q, new Float3(0f, 0f, 0f))) + Hlsl.Min(Hlsl.Max(q.X, Hlsl.Max(q.Y, q.Z)), 0f);
        Float3 w = Hlsl.Abs(q + edge) - edge;
        float a = Hlsl.Length(Hlsl.Max(new Float3(q.X, w.Y, w.Z), new Float3(0f, 0f, 0f))) + Hlsl.Min(Hlsl.Max(q.X, Hlsl.Max(w.Y, w.Z)), 0f);
        float b = Hlsl.Length(Hlsl.Max(new Float3(w.X, q.Y, w.Z), new Float3(0f, 0f, 0f))) + Hlsl.Min(Hlsl.Max(w.X, Hlsl.Max(q.Y, w.Z)), 0f);
        float c = Hlsl.Length(Hlsl.Max(new Float3(w.X, w.Y, q.Z), new Float3(0f, 0f, 0f))) + Hlsl.Min(Hlsl.Max(w.X, Hlsl.Max(w.Y, q.Z)), 0f);
        return new Float2(Hlsl.Min(a, Hlsl.Min(b, c)), box);
    }

    private Float3 GlassCubes(Float2 fragment, Float4 first, Float4 second)
    {
        // Three glass cubes across the screen (bass, mids, highs). Their neon edges glow with
        // their band; light gathers as the ray passes through the glass.
        Float2 uv = (fragment - size * 0.5f) / size.Y;
        uv.Y = -uv.Y;
        Float3 origin = new Float3(0f, 0.3f, -6f);
        Float3 dir = Look(Hlsl.Normalize(new Float3(uv.X, uv.Y - 0.05f, 1.5f)));
        Float3 levels = new Float3(Spectrum(0.1f, first, second), Spectrum(0.5f, first, second), Spectrum(0.9f, first, second));
        Float3 rgb = new Float3(0f, 0f, 0f);
        float t = 0f;
        for (int i = 0; i < 90; i++)
        {
            Float3 p = origin + dir * t;
            float cell = Hlsl.Clamp(Hlsl.Round(p.X / GlassSpacing), -1f, 1f);
            float level = cell < -0.5f ? levels.X : (cell < 0.5f ? levels.Y : levels.Z);
            Float3 local = p - new Float3(cell * GlassSpacing, 0f, 0f);
            Float2 xz = Rotate(new Float2(local.X, local.Z), travel * 0.25f + cell);
            Float2 yz = Rotate(new Float2(local.Y, xz.Y), time * 0.3f + cell * 0.7f);
            local = new Float3(xz.X, yz.X, yz.Y);
            Float2 d = CubeFrame(local, 0.95f + 0.25f * level, 0.04f);
            Float3 colour = Gradient(cell * 0.5f + 0.5f);
            // Edges: bright neon glow. Inside the glass: a soft haze of the band's colour.
            rgb += colour * (0.012f / (0.004f + d.X * d.X * 40f)) * (0.2f + 1.2f * level) * 0.02f;
            rgb += colour * Hlsl.Step(d.Y, 0f) * 0.012f * (0.3f + level);
            t += Hlsl.Clamp(Hlsl.Min(Hlsl.Abs(d.X), Hlsl.Abs(d.Y) + 0.05f) * 0.7f, 0.02f, 0.4f);
        }
        float floorGlow = Hlsl.Exp(-Hlsl.Abs(uv.Y + 0.32f) * 25f) * 0.08f * (0.5f + energy);
        return rgb + Gradient(0.5f) * floorGlow + new Float3(flash, flash, flash) * 0.04f;
    }

    // ---------- Fractal land ----------

    private float Kaliset(Float2 q)
    {
        Float3 p = new Float3(q.X, q.Y, 0.65f);
        float accumulated = 0f;
        float previous = 0f;
        for (int i = 0; i < 10; i++)
        {
            p = Hlsl.Abs(p) / Hlsl.Dot(p, p) - new Float3(0.55f + 0.16f * bass, 0.6f + 0.05f * highs, 0.56f + 0.12f * mids);
            float length = Hlsl.Length(p);
            accumulated += Hlsl.Abs(length - previous);
            previous = length;
        }
        return accumulated;
    }

    private Float3 FractalLand(Float2 fragment, Float4 first, Float4 second)
    {
        // A flight over a landscape made from the Kali set: heights and colours come from the
        // fractal, and the ground glows with the band beneath it.
        Float2 uv = (fragment - size * 0.5f) / size.Y;
        uv.Y = -uv.Y;
        Float3 origin = new Float3(Hlsl.Sin(time * 0.1f) * 0.5f, 1.15f + 0.15f * bass, travel * 1.6f);
        Float3 dir = Look(Hlsl.Normalize(new Float3(uv.X, uv.Y - 0.3f, 1.2f)));
        Float3 sky = Hlsl.Lerp(Gradient(0.85f) * 0.5f, Gradient(0.1f) * 0.12f, Hlsl.Saturate(uv.Y * 2f + 0.3f));
        float t = 0.2f;
        float hit = 0f;
        float height = 0f;
        for (int i = 0; i < 80; i++)
        {
            if (hit < 0.5f && t < 12f)
            {
                Float3 p = origin + dir * t;
                height = (1f - Hlsl.Exp(-Kaliset(p.XZ * 0.25f) * 0.035f)) * (1.1f + 0.7f * bass) - 0.2f;
                float gap = p.Y - height;
                if (gap < 0.004f * t) hit = 1f;
                else t += Hlsl.Max(gap * 0.5f, 0.02f);
            }
        }
        if (hit < 0.5f) return sky;
        Float3 position = origin + dir * t;
        float band = Hlsl.Saturate(position.X / 6f + 0.5f);
        float level = Spectrum(band, first, second);
        Float3 colour = Gradient(Hlsl.Saturate(height * 1.1f)) * (0.3f + 0.7f * height) * (0.5f + 0.9f * level)
            + Gradient(band) * level * level * Hlsl.Saturate(height * 1.5f) * 1.2f + new Float3(flash, flash, flash) * 0.15f;
        float fog = Hlsl.Exp(-t * 0.1f);
        return Hlsl.Lerp(sky, colour * (0.6f + 0.7f * energy), fog);
    }

    /// <summary>A spectrum level at 0 (bass) to 1 (highs). Read before any varying loop.</summary>
    private float Band(float position) => data.Sample(new Float2((Hlsl.Saturate(position) * 95f + 0.5f) / 1024f, 0.0625f)).Y;

    private Float3 Gradient(float t)
    {
        Float3 c = t < 0.5f ? Hlsl.Lerp(low, mid, t * 2f) : Hlsl.Lerp(mid, high, t * 2f - 1f);
        return c / Hlsl.Max(Hlsl.Max(c.X, c.Y), Hlsl.Max(c.Z, 0.001f));
    }
}
