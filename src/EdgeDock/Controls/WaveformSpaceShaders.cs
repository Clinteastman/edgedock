using ComputeSharp;
using ComputeSharp.D2D1;

namespace EdgeDock.Controls;

/// <summary>
/// Orb field: the spectrum in 3D. Rows of glowing orbs stretch across the whole width and back
/// into the distance: frequency runs left (bass) to right (highs), time runs back into depth.
/// Each orb rises and lights with its band at that moment, softly out of focus with distance,
/// and is mirrored in a dark glossy floor.
/// </summary>
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader40)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct WaveformOrbShader(
    Float2 size, Float3 low, Float3 mid, Float3 high,
    float head, float rowsPerStep, float available, float energy, float flash) : ID2D1PixelShader
{
    /// <summary>Band levels: one row per moment (wrapping every 4096 rows), one column per band.</summary>
    [D2DResourceTextureIndex(0)]
    private readonly D2D1ResourceTexture2D<float> history;

    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        Float2 uv = (p - size * 0.5f) / size.Y;
        uv.Y = -uv.Y;
        const float focal = 1.3f;
        const float eye = 0.55f;
        const float columns = 48f;
        const float spacing = 0.16f;
        float horizon = -0.08f;
        Float3 rgb = new Float3(0f, 0f, 0f);
        for (int k = 0; k < 22; k++)
        {
            float z = 1.1f + k * 0.55f;
            float age = k * rowsPerStep;
            float known = Hlsl.Step(age, available - 1f);
            // The orb in this row nearest the pixel, left to right.
            float worldX = uv.X * z / focal;
            float column = Hlsl.Clamp(Hlsl.Round(worldX / spacing + (columns - 1f) * 0.5f), 0f, columns - 1f);
            float band = column / (columns - 1f);
            float level = history.Sample(new Float2(band, (head - age + 0.5f) / 4096f)) * known;
            float x = (column - (columns - 1f) * 0.5f) * spacing;
            float y = 0.08f + level * 0.75f;
            Float2 centre = new Float2(x, y - eye) * focal / z;
            centre.Y += horizon;
            Float2 mirror = new Float2(x, -y - eye) * focal / z;
            mirror.Y += horizon;
            float radius = (0.035f + 0.05f * level) * focal / z;
            float blur = Hlsl.Abs(z - 2.2f) * 0.0035f + 0.0015f;
            float d = Hlsl.Length(uv - centre);
            float disk = Hlsl.SmoothStep(radius + blur, radius - blur, d);
            float glow = Hlsl.Exp(-d / (radius * 2.2f)) * 0.35f;
            float reflected = Hlsl.SmoothStep(radius + blur * 3f, radius - blur, Hlsl.Length(uv - mirror)) * 0.25f;
            float fade = Hlsl.Exp(-k * 0.09f);
            float strength = (0.08f + level * level * 1.3f) * fade;
            Float3 colour = Gradient(band);
            rgb += colour * (disk + glow + reflected) * strength;
        }
        // A faint glossy floor towards the horizon, warmer on beats.
        float floorGlow = Hlsl.Saturate(1f - Hlsl.Abs(uv.Y - horizon + 0.02f) * 12f) * 0.05f * (0.5f + energy);
        rgb += Gradient(0.5f) * floorGlow + Gradient(0.2f) * flash * 0.06f;
        rgb = rgb / (1f + rgb);
        return new Float4(rgb, 1f);
    }

    private Float3 Gradient(float t)
    {
        Float3 c = t < 0.5f ? Hlsl.Lerp(low, mid, t * 2f) : Hlsl.Lerp(mid, high, t * 2f - 1f);
        return c / Hlsl.Max(Hlsl.Max(c.X, c.Y), Hlsl.Max(c.Z, 0.001f));
    }
}

/// <summary>
/// Space and landscapes, written for EdgeDock. Style 0: black hole, rays bent round it (a simple
/// photon-orbit approximation) so a starfield and a glowing accretion disk lens into a ring; the
/// disk's rings light with their bands, highs on the inside, bass on the outside. 1: spectrum
/// mountains, a flight over ridges whose height follows the spectrum across the screen, under a
/// sunset sky. 2: neon rings, repeating glowing rings, one layer per band. 3: fractal forest, a
/// branching fractal that twists with depth as you fly through it.
/// </summary>
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader40)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct WaveformCosmosShader(
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
        Float3 rgb = style == 0 ? BlackHole(p, first, second)
            : (style == 1 ? Mountains(p, first, second) : (style == 2 ? NeonRings(p, first) : Forest(p, first)));
        return new Float4(Hlsl.Saturate(rgb), 1f);
    }

    /// <summary>The spectrum level at 0 (bass) to 1 (highs), between the eight bands read for this pixel.</summary>
    private static float Spectrum(float position, Float4 first, Float4 second)
    {
        float slot = Hlsl.Saturate(position) * 7f;
        float i = Hlsl.Floor(Hlsl.Min(slot, 6.999f));
        float t = slot - i;
        float a = Pick(i, first, second);
        float b = Pick(i + 1f, first, second);
        return Hlsl.Lerp(a, b, t);
    }

    private static float Pick(float i, Float4 first, Float4 second) =>
        i < 0.5f ? first.X : (i < 1.5f ? first.Y : (i < 2.5f ? first.Z : (i < 3.5f ? first.W :
        (i < 4.5f ? second.X : (i < 5.5f ? second.Y : (i < 6.5f ? second.Z : second.W))))));

    // ---------- Black hole ----------

    private Float3 BlackHole(Float2 fragment, Float4 first, Float4 second)
    {
        Float2 uv = (fragment - size * 0.5f) / size.Y;
        uv.Y = -uv.Y;
        const float horizonRadius = 1f;
        float tilt = 0.18f + 0.05f * Hlsl.Sin(time * 0.05f);
        Float3 position = new Float3(0f, 0f, -14f);
        Float3 velocity = Hlsl.Normalize(new Float3(uv.X * 0.9f, uv.Y * 0.9f, 1f));
        // Tilt the scene so the disk is seen slightly from above.
        position = new Float3(position.X, position.Y * Hlsl.Cos(tilt) - position.Z * Hlsl.Sin(tilt), position.Y * Hlsl.Sin(tilt) + position.Z * Hlsl.Cos(tilt));
        velocity = new Float3(velocity.X, velocity.Y * Hlsl.Cos(tilt) - velocity.Z * Hlsl.Sin(tilt), velocity.Y * Hlsl.Sin(tilt) + velocity.Z * Hlsl.Cos(tilt));
        float angular = Hlsl.Length(Hlsl.Cross(position, velocity));
        float h2 = angular * angular;
        Float3 colour = new Float3(0f, 0f, 0f);
        float remaining = 1f;
        float captured = 0f;
        for (int i = 0; i < 110; i++)
        {
            if (captured < 0.5f && remaining > 0.02f)
            {
                float r = Hlsl.Length(position);
                float step = Hlsl.Clamp(r * 0.06f, 0.02f, 0.6f);
                Float3 before = position;
                // Light bends towards the hole: the photon-orbit term -1.5 h^2 r / |r|^5.
                velocity += -1.5f * h2 * position / Hlsl.Pow(r, 5f) * step;
                position += velocity * step;
                float rNow = Hlsl.Length(position);
                if (rNow < horizonRadius) captured = 1f;
                else if (before.Y * position.Y < 0f)
                {
                    // Crossed the disk plane: inside 2.6 to 10 radii, the disk glows.
                    Float3 crossing = Hlsl.Lerp(before, position, before.Y / (before.Y - position.Y));
                    float radius = Hlsl.Length(new Float2(crossing.X, crossing.Z));
                    if (radius > 2.6f && radius < 10f)
                    {
                        float ring = Hlsl.Saturate((radius - 2.6f) / 7.4f);
                        float level = Spectrum(1f - ring, first, second);
                        float angle = Hlsl.Atan2(crossing.Z, crossing.X);
                        float swirl = 0.55f + 0.45f * Hlsl.Sin(angle * 3f + radius * 2.2f - travel * 1.5f / Hlsl.Sqrt(radius))
                                    * Hlsl.Sin(radius * 9f - time * 0.8f);
                        // The side turning towards us is brighter (Doppler beaming).
                        float beaming = 1f + 0.55f * Hlsl.Cos(angle) * Hlsl.Sign(velocity.X + 0.0001f);
                        float heat = (1.4f - ring) * swirl * beaming * (0.35f + 1.3f * level);
                        Float3 disk = Hlsl.Lerp(Gradient(1f - ring), new Float3(1f, 1f, 1f), Hlsl.Saturate(heat - 1f) * 0.6f) * heat;
                        float alpha = Hlsl.Saturate(0.85f - ring * 0.3f);
                        colour += disk * alpha * remaining;
                        remaining *= 1f - alpha;
                    }
                }
            }
        }
        if (captured < 0.5f)
        {
            // Whatever escapes sees the stars, lensed by the bend.
            Float3 dir = Hlsl.Normalize(velocity);
            Float3 cell = Hlsl.Floor(dir * 140f);
            float star = Hlsl.Step(0.985f, Hash(cell.X * 1.3f + cell.Y * 57.1f + cell.Z * 113.7f));
            float twinkle = 0.6f + 0.4f * Hlsl.Sin(time * 3f + cell.X);
            Float3 nebula = Gradient(Hlsl.Saturate(dir.Y * 0.5f + 0.5f)) * 0.04f * (0.5f + energy);
            colour += (new Float3(star, star, star) * twinkle * (0.6f + highs) + nebula) * remaining;
        }
        return colour + Gradient(0f) * flash * 0.05f;
    }

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

    // ---------- Spectrum mountains ----------

    private float Terrain(Float2 q, Float4 first, Float4 second)
    {
        float shape = Noise(q * 0.35f) * 1.2f + Noise(q * 0.9f) * 0.45f + Noise(q * 2.3f) * 0.15f;
        // Across the screen, the spectrum lifts the ridges: bass on the left, highs on the right.
        float lift = Spectrum(q.X / 18f + 0.5f, first, second);
        return shape * (0.5f + 1.6f * lift) - 0.6f;
    }

    private Float3 Mountains(Float2 fragment, Float4 first, Float4 second)
    {
        Float2 uv = (fragment - size * 0.5f) / size.Y;
        uv.Y = -uv.Y;
        Float3 origin = new Float3(0f, 1.6f, travel * 1.2f);
        Float3 dir = Look(Hlsl.Normalize(new Float3(uv.X, uv.Y - 0.12f, 1.25f)));
        Float3 sun = Hlsl.Normalize(new Float3(0.2f, 0.18f, 1f));
        float sunAmount = Hlsl.Max(Hlsl.Dot(dir, sun), 0f);
        Float3 horizon = Hlsl.Lerp(new Float3(1f, 0.55f, 0.35f), Gradient(0.9f), 0.45f);
        Float3 zenith = Hlsl.Lerp(new Float3(0.1f, 0.12f, 0.3f), Gradient(0.1f) * 0.4f, 0.45f);
        Float3 sky = Hlsl.Lerp(horizon, zenith, Hlsl.Saturate(dir.Y * 3f));
        sky += new Float3(1f, 0.8f, 0.55f) * (Hlsl.Pow(sunAmount, 600f) * 2f + Hlsl.Pow(sunAmount, 10f) * 0.35f) * (0.8f + 0.5f * energy);
        // Thin cloud streaks high in the sky.
        if (dir.Y > 0f)
        {
            Float2 cloudUv = dir.XZ / (dir.Y + 0.05f) * 1.5f + new Float2(time * 0.03f, travel * 0.05f);
            float cloud = Hlsl.SmoothStep(0.55f, 0.95f, Noise(cloudUv) * 0.7f + Noise(cloudUv * 2.3f) * 0.3f);
            sky = Hlsl.Lerp(sky, Hlsl.Lerp(horizon, new Float3(1f, 1f, 1f), 0.4f), cloud * 0.5f * Hlsl.Saturate(dir.Y * 4f));
        }

        float t = 0.1f;
        float hit = 0f;
        for (int i = 0; i < 90; i++)
        {
            if (hit < 0.5f && t < 40f)
            {
                Float3 position = origin + dir * t;
                float gap = position.Y - Terrain(position.XZ, first, second);
                if (gap < 0.002f * t) hit = 1f;
                else t += Hlsl.Max(gap * 0.4f, 0.01f * t);
            }
        }
        if (hit < 0.5f) return sky;
        Float3 surface = origin + dir * t;
        const float e = 0.02f;
        float height = Terrain(surface.XZ, first, second);
        Float3 normal = Hlsl.Normalize(new Float3(
            height - Terrain(surface.XZ + new Float2(e, 0f), first, second), e,
            height - Terrain(surface.XZ + new Float2(0f, e), first, second)));
        float diffuse = Hlsl.Max(Hlsl.Dot(normal, sun), 0f);
        float band = Hlsl.Saturate(surface.X / 18f + 0.5f);
        float level = Spectrum(band, first, second);
        // Lower slopes take the look's colours; peaks catch snow; bands glow along the ridges.
        Float3 rock = Hlsl.Lerp(Gradient(band) * 0.35f, new Float3(0.95f, 0.95f, 1f), Hlsl.SmoothStep(0.6f, 1.4f, height));
        Float3 lit = rock * (0.18f + 0.9f * diffuse) + Gradient(band) * level * level * 0.4f * Hlsl.Saturate(height + 0.3f);
        float fog = 1f - Hlsl.Exp(-t * 0.05f);
        return Hlsl.Lerp(lit, horizon, fog);
    }

    // ---------- Neon rings ----------

    private Float3 NeonRings(Float2 fragment, Float4 levels)
    {
        Float2 uv = (fragment - size * 0.5f) / size.Y * 2f;
        Float2 start = uv;
        Float3 rgb = new Float3(0f, 0f, 0f);
        for (int i = 0; i < 4; i++)
        {
            float level = i == 0 ? levels.X : (i == 1 ? levels.Y : (i == 2 ? levels.Z : levels.W));
            uv = Hlsl.Frac(uv * (1.5f + 0.1f * bass)) - 0.5f;
            float d = Hlsl.Length(uv) * Hlsl.Exp(-Hlsl.Length(start));
            float hue = Hlsl.Frac(Hlsl.Length(start) * 0.4f + i * 0.25f + travel * 0.08f);
            d = Hlsl.Sin(d * (8f + 3f * mids) + time * 0.8f + travel * 0.3f) / 8f;
            d = Hlsl.Abs(d);
            d = Hlsl.Pow(0.01f / Hlsl.Max(d, 0.0005f), 1.2f);
            rgb += Gradient(1f - Hlsl.Abs(hue * 2f - 1f)) * d * (0.25f + 1.4f * level);
        }
        return rgb / (1f + rgb * 0.3f) + Gradient(0f) * flash * 0.06f;
    }

    // ---------- Fractal forest ----------

    private static float Capsule(Float3 p, float height, float radius)
    {
        p.Y -= Hlsl.Clamp(p.Y, 0f, height);
        return Hlsl.Length(p) - radius;
    }

    private float Branches(Float3 p, Float4 levels)
    {
        // Repeat trees along the flight; each tree twists more the further away it is.
        float cell = Hlsl.Floor(p.Z / 3f);
        p.Z = p.Z - 3f * cell - 1.5f;
        p.X = p.X - 4f * Hlsl.Round(p.X / 4f);
        float d = Capsule(p, 1f, 0.09f);
        float scale = 1f;
        for (int i = 0; i < 6; i++)
        {
            float level = i < 2 ? levels.X : (i < 3 ? levels.Y : (i < 5 ? levels.Z : levels.W));
            p.Y -= 1f;
            p.X = Hlsl.Abs(p.X);
            float angle = 0.55f + 0.25f * level + 0.1f * Hlsl.Sin(time * 0.5f + cell);
            p = new Float3(p.X * Hlsl.Cos(angle) - p.Y * Hlsl.Sin(angle), p.X * Hlsl.Sin(angle) + p.Y * Hlsl.Cos(angle), p.Z);
            float twist = 0.6f + cell * 0.07f;
            p = new Float3(p.X * Hlsl.Cos(twist) - p.Z * Hlsl.Sin(twist), p.Y, p.X * Hlsl.Sin(twist) + p.Z * Hlsl.Cos(twist));
            p *= 1.45f;
            scale *= 1.45f;
            d = Hlsl.Min(d, Capsule(p, 1f, 0.09f) / scale);
        }
        return d;
    }

    private Float3 Forest(Float2 fragment, Float4 levels)
    {
        Float2 uv = (fragment - size * 0.5f) / size.Y;
        uv.Y = -uv.Y;
        Float3 origin = new Float3(2f + Hlsl.Sin(time * 0.1f) * 0.8f, 1.3f, travel * 0.8f);
        Float3 dir = Look(Hlsl.Normalize(new Float3(uv.X, uv.Y + 0.05f, 1.1f)));
        float t = 0f;
        float hit = 0f;
        float steps = 0f;
        for (int i = 0; i < 90; i++)
        {
            if (hit < 0.5f && t < 25f)
            {
                float d = Hlsl.Min(Branches(origin + dir * t, levels), (origin + dir * t).Y + 0.05f);
                if (d < 0.002f * (1f + t)) hit = 1f;
                else
                {
                    t += d * 0.8f;
                    steps += 1f;
                }
            }
        }
        Float3 fogColour = Hlsl.Lerp(Gradient(0.2f) * 0.1f, Gradient(0.8f) * 0.25f, Hlsl.Saturate(uv.Y + 0.5f)) * (0.6f + energy);
        float glow = Hlsl.Saturate(steps / 90f);
        if (hit < 0.5f) return fogColour + Gradient(0.5f) * glow * glow * 0.6f;
        Float3 position = origin + dir * t;
        float depth = Hlsl.Saturate(position.Y / 4f);
        Float3 colour = Gradient(depth) * (0.4f + 0.9f * Spectrum(depth, levels, levels));
        float shade = Hlsl.Saturate(1f - glow * 1.2f);
        float fog = Hlsl.Exp(-t * 0.08f);
        return Hlsl.Lerp(fogColour, colour * (0.3f + 0.7f * shade), fog) + new Float3(flash, flash, flash) * 0.05f;
    }

    /// <summary>A spectrum level at 0 (bass) to 1 (highs). Read before any loop.</summary>
    private float Band(float position) => data.Sample(new Float2((Hlsl.Saturate(position) * 95f + 0.5f) / 1024f, 0.0625f)).Y;

    private Float3 Gradient(float t)
    {
        Float3 c = t < 0.5f ? Hlsl.Lerp(low, mid, t * 2f) : Hlsl.Lerp(mid, high, t * 2f - 1f);
        return c / Hlsl.Max(Hlsl.Max(c.X, c.Y), Hlsl.Max(c.Z, 0.001f));
    }
}
