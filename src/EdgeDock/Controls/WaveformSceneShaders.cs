using ComputeSharp;
using ComputeSharp.D2D1;

namespace EdgeDock.Controls;

/// <summary>
/// Raymarched 3D scenes, written for EdgeDock, where each frequency band drives its own part.
/// Style 0: an endless lattice of beams and cubes (after Escher's Cubic Space Division) to fly
/// through; beams across follow the bass, upright beams the mids, beams in depth the highs, and
/// the cubes pulse on the beat. 1: three spinning fractal cubes (Menger sponges) across the screen
/// whose four levels of holes each open with their own band, from bass (the big holes) to highs.
/// 2: an orbit round a Mandelbox fractal (the standard box-fold and sphere-fold formula) whose
/// folds lift with the bass and whose depths light in the bands' colours.
/// </summary>
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader40)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct WaveformSceneShader(
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
        Float3 rgb = style == 0 ? Lattice(p) : (style == 1 ? MengerCube(p) : Mandelbox(p));
        return new Float4(Hlsl.Saturate(rgb), 1f);
    }

    // ---------- Lattice ----------

    private static Float3 Mod(Float3 x, float y) => x - y * Hlsl.Floor(x / y);

    private static float Box(Float3 q, Float3 b)
    {
        Float3 d = Hlsl.Abs(q) - b;
        return Hlsl.Length(Hlsl.Max(d, new Float3(0f, 0f, 0f))) + Hlsl.Min(Hlsl.Max(d.X, Hlsl.Max(d.Y, d.Z)), 0f);
    }

    /// <summary>Distance (X) and which part is nearest (Y: 0 across, 1 upright, 2 depth, 3 cube).</summary>
    private Float2 LatticeMap(Float3 position)
    {
        Float3 q = Mod(position, 2f) - 1f;
        float across = Hlsl.Max(Hlsl.Abs(q.Y), Hlsl.Abs(q.Z)) - (0.06f + 0.1f * bass);
        float upright = Hlsl.Max(Hlsl.Abs(q.X), Hlsl.Abs(q.Z)) - (0.06f + 0.1f * mids);
        float depth = Hlsl.Max(Hlsl.Abs(q.X), Hlsl.Abs(q.Y)) - (0.06f + 0.1f * highs);
        float cube = Box(q, new Float3(1f, 1f, 1f) * (0.26f + 0.1f * flash + 0.06f * energy)) - 0.03f;
        Float2 best = new Float2(across, 0f);
        if (upright < best.X) best = new Float2(upright, 1f);
        if (depth < best.X) best = new Float2(depth, 2f);
        if (cube < best.X) best = new Float2(cube, 3f);
        return best;
    }

    private Float3 Lattice(Float2 fragment)
    {
        Float2 uv = (fragment - size * 0.5f) / size.Y;
        uv.Y = -uv.Y;
        // Fly along a gentle diagonal, the view slowly turning.
        Float3 origin = new Float3(1f + Hlsl.Sin(time * 0.13f) * 0.6f, 1f + Hlsl.Cos(time * 0.11f) * 0.6f, travel * 1.4f);
        float yaw = Hlsl.Sin(time * 0.07f) * 0.6f;
        float pitch = Hlsl.Sin(time * 0.05f) * 0.3f;
        Float3 dir = Look(Hlsl.Normalize(new Float3(uv.X, uv.Y, 1.2f)));
        dir = new Float3(dir.X * Hlsl.Cos(yaw) + dir.Z * Hlsl.Sin(yaw), dir.Y, -dir.X * Hlsl.Sin(yaw) + dir.Z * Hlsl.Cos(yaw));
        dir = new Float3(dir.X, dir.Y * Hlsl.Cos(pitch) - dir.Z * Hlsl.Sin(pitch), dir.Y * Hlsl.Sin(pitch) + dir.Z * Hlsl.Cos(pitch));

        float t = 0f;
        float hit = 0f;
        float part = 0f;
        for (int i = 0; i < 80; i++)
        {
            if (hit < 0.5f && t < 30f)
            {
                Float2 d = LatticeMap(origin + dir * t);
                if (d.X < 0.002f * (1f + t))
                {
                    hit = 1f;
                    part = d.Y;
                }
                else t += d.X;
            }
        }
        Float3 fogColour = Hlsl.Lerp(Gradient(0.5f) * 0.12f, Gradient(0f) * 0.25f, flash * 0.5f);
        if (hit < 0.5f) return fogColour;

        Float3 position = origin + dir * t;
        const float e = 0.002f;
        Float3 normal = Hlsl.Normalize(new Float3(
            LatticeMap(position + new Float3(e, 0f, 0f)).X - LatticeMap(position - new Float3(e, 0f, 0f)).X,
            LatticeMap(position + new Float3(0f, e, 0f)).X - LatticeMap(position - new Float3(0f, e, 0f)).X,
            LatticeMap(position + new Float3(0f, 0f, e)).X - LatticeMap(position - new Float3(0f, 0f, e)).X));
        Float3 light = Hlsl.Normalize(new Float3(0.4f, 0.8f, -0.5f));
        float diffuse = Hlsl.Max(Hlsl.Dot(normal, light), 0f) * 0.75f + 0.25f;
        float level = part < 0.5f ? bass : (part < 1.5f ? mids : (part < 2.5f ? highs : Hlsl.Max(flash, energy)));
        Float3 colour = part < 0.5f ? Brighten(low) : (part < 1.5f ? Brighten(mid) : (part < 2.5f ? Brighten(high) : Hlsl.Lerp(Brighten(mid), new Float3(1f, 1f, 1f), 0.5f)));
        Float3 lit = colour * diffuse * (0.35f + 0.65f * level) + colour * level * level * 0.6f;
        float fog = Hlsl.Exp(-t * 0.09f);
        return Hlsl.Lerp(fogColour, lit, fog);
    }

    // ---------- Fractal cube ----------

    /// <summary>Distance (X) and which level of holes shaped the surface (Y, 0 to 4).</summary>
    private static Float2 Menger(Float3 p, Float4 openings)
    {
        float d = Box(p, new Float3(1f, 1f, 1f));
        float scale = 1f;
        float shaped = 4f;
        for (int i = 0; i < 4; i++)
        {
            float opening = i == 0 ? openings.X : (i == 1 ? openings.Y : (i == 2 ? openings.Z : openings.W));
            Float3 a = p * scale - 2f * Hlsl.Floor(p * scale / 2f) - 1f;
            scale *= 3f;
            Float3 r = Hlsl.Abs(1f - 3f * Hlsl.Abs(a));
            float da = Hlsl.Max(r.X, r.Y);
            float db = Hlsl.Max(r.Y, r.Z);
            float dc = Hlsl.Max(r.Z, r.X);
            // A louder band opens its holes wider.
            float c = (Hlsl.Min(da, Hlsl.Min(db, dc)) - (0.7f + 0.5f * opening)) / scale;
            if (c > d)
            {
                d = c;
                shaped = i;
            }
        }
        return new Float2(d, shaped);
    }

    private Float3 MengerCube(Float2 fragment)
    {
        Float2 uv = (fragment - size * 0.5f) / size.Y;
        uv.Y = -uv.Y;
        Float4 openings = new Float4(Band(0.05f), Band(0.35f), Band(0.65f), Band(0.95f));
        float spin = travel * 0.18f;
        float tilt = 0.5f + 0.25f * Hlsl.Sin(time * 0.21f);
        Float3 origin = new Float3(0f, 0f, -4.2f + 0.4f * bass);
        Float3 dir = Look(Hlsl.Normalize(new Float3(uv.X, uv.Y, 1.5f)));
        float t = 0f;
        float hit = 0f;
        float shaped = 0f;
        float steps = 0f;
        for (int i = 0; i < 90; i++)
        {
            if (hit < 0.5f && t < 8f)
            {
                Float2 d = Cubes(origin + dir * t, openings, spin, tilt);
                if (d.X < 0.0015f)
                {
                    hit = 1f;
                    shaped = d.Y;
                }
                else
                {
                    t += d.X;
                    steps += 1f;
                }
            }
        }
        float halo = Hlsl.Saturate(steps / 90f);
        Float3 background = Gradient(0.2f) * halo * halo * (0.4f + 1.2f * energy) + new Float3(0.01f, 0.01f, 0.02f);
        if (hit < 0.5f) return background;

        Float3 position = origin + dir * t;
        float cube = CubeCell(position);
        const float e = 0.0015f;
        Float3 normal = Hlsl.Normalize(new Float3(
            Cubes(position + new Float3(e, 0f, 0f), openings, spin, tilt).X - Cubes(position - new Float3(e, 0f, 0f), openings, spin, tilt).X,
            Cubes(position + new Float3(0f, e, 0f), openings, spin, tilt).X - Cubes(position - new Float3(0f, e, 0f), openings, spin, tilt).X,
            Cubes(position + new Float3(0f, 0f, e), openings, spin, tilt).X - Cubes(position - new Float3(0f, 0f, e), openings, spin, tilt).X));
        Float3 light = Hlsl.Normalize(new Float3(0.5f, 0.7f, -0.6f));
        float diffuse = Hlsl.Max(Hlsl.Dot(normal, light), 0f);
        float occlusion = Hlsl.Saturate(1f - steps / 90f * 1.4f);
        float level = shaped < 0.5f ? openings.X : (shaped < 1.5f ? openings.Y : (shaped < 2.5f ? openings.Z : (shaped < 3.5f ? openings.W : energy)));
        // Each cube leans towards its own band's colour: bass left, highs right.
        Float3 colour = Gradient(Hlsl.Saturate(shaped / 3f * 0.5f + (cube * 0.5f + 0.5f) * 0.5f));
        return colour * (0.15f + 0.85f * diffuse) * (0.45f + 0.55f * occlusion) * (0.5f + 0.8f * level)
             + colour * level * level * 0.5f + new Float3(flash, flash, flash) * 0.1f;
    }

    private const float CubeSpacing = 3.1f;

    private static float CubeCell(Float3 p) => Hlsl.Clamp(Hlsl.Round(p.X / CubeSpacing), -1f, 1f);

    /// <summary>Three cubes side by side; the left one's holes follow the bass hardest, the right one's the highs.</summary>
    private static Float2 Cubes(Float3 p, Float4 openings, float spin, float tilt)
    {
        float cell = CubeCell(p);
        Float3 local = Turn(p - new Float3(cell * CubeSpacing, 0f, 0f), spin + cell * 0.7f, tilt);
        Float4 bias = cell < -0.5f ? new Float4(1f, 0.6f, 0.3f, 0.1f) : (cell < 0.5f ? new Float4(0.4f, 1f, 1f, 0.4f) : new Float4(0.1f, 0.3f, 0.6f, 1f));
        return Menger(local, openings * bias);
    }

    // ---------- Mandelbox flight ----------

    /// <summary>Distance (X) and the smallest radius the orbit reached (Y, for colour).</summary>
    private static Float2 FoldedBox(Float3 p, float scale)
    {
        Float3 z = p;
        float dr = 1f;
        float trap = 10f;
        for (int i = 0; i < 10; i++)
        {
            z = Hlsl.Clamp(z, -1f, 1f) * 2f - z;
            float r2 = Hlsl.Dot(z, z);
            if (r2 < 0.25f)
            {
                z *= 4f;
                dr *= 4f;
            }
            else if (r2 < 1f)
            {
                z /= r2;
                dr /= r2;
            }
            z = z * scale + p;
            dr = dr * Hlsl.Abs(scale) + 1f;
            trap = Hlsl.Min(trap, r2);
        }
        return new Float2(Hlsl.Length(z) / Hlsl.Abs(dr), trap);
    }

    private Float3 Mandelbox(Float2 fragment)
    {
        Float2 uv = (fragment - size * 0.5f) / size.Y;
        uv.Y = -uv.Y;
        Float4 levels = new Float4(Band(0.05f), Band(0.35f), Band(0.65f), Band(0.95f));
        float scale = -1.75f - 0.12f * bass + 0.05f * Hlsl.Sin(time * 0.05f);
        // A slow orbit round the outside of the fractal, swinging closer with the bass.
        float path = travel * 0.03f;
        float distance = 8.5f - 1.2f * bass;
        Float3 origin = new Float3(Hlsl.Sin(path) * distance, 1.8f * Hlsl.Sin(path * 0.7f), Hlsl.Cos(path) * distance);
        Float3 forward = Hlsl.Normalize(-origin);
        Float3 right = Hlsl.Normalize(Hlsl.Cross(new Float3(0f, 1f, 0f), forward));
        Float3 upward = Hlsl.Cross(forward, right);
        Float3 dir = Look(Hlsl.Normalize(forward * 1.6f + uv.X * right + uv.Y * upward));
        float t = 0f;
        float hit = 0f;
        float steps = 0f;
        float trap = 0f;
        for (int i = 0; i < 90; i++)
        {
            if (hit < 0.5f && t < 16f)
            {
                Float2 d = FoldedBox(origin + dir * t, scale);
                trap = d.Y;
                if (d.X < 0.0008f * (1f + t * 2f)) hit = 1f;
                else
                {
                    t += d.X * 0.85f;
                    steps += 1f;
                }
            }
        }
        float depth = Hlsl.Saturate(steps / 90f);
        Float3 fogColour = Gradient(0.3f) * 0.08f * (0.5f + energy);
        if (hit < 0.5f) return fogColour + Gradient(0.15f) * depth * depth * (0.3f + flash);
        Float3 position = origin + dir * t;
        const float e = 0.0008f;
        Float3 normal = Hlsl.Normalize(new Float3(
            FoldedBox(position + new Float3(e, 0f, 0f), scale).X - FoldedBox(position - new Float3(e, 0f, 0f), scale).X,
            FoldedBox(position + new Float3(0f, e, 0f), scale).X - FoldedBox(position - new Float3(0f, e, 0f), scale).X,
            FoldedBox(position + new Float3(0f, 0f, e), scale).X - FoldedBox(position - new Float3(0f, 0f, e), scale).X));
        float diffuse = Hlsl.Max(Hlsl.Dot(normal, -dir), 0f);
        // The orbit trap picks a band: each region of the fractal lights with its own band.
        float bandPosition = Hlsl.Saturate(Hlsl.Sqrt(trap) * 1.1f);
        float slot = bandPosition * 3.99f;
        float level = slot < 1f ? levels.X : (slot < 2f ? levels.Y : (slot < 3f ? levels.Z : levels.W));
        Float3 colour = Gradient(bandPosition);
        float occlusion = Hlsl.Saturate(1f - depth * 1.3f);
        Float3 lit = colour * (0.2f + 0.8f * diffuse) * occlusion * (0.5f + 0.9f * level) + colour * level * level * 0.5f;
        float fog = Hlsl.Exp(-Hlsl.Max(t - 4f, 0f) * 0.25f);
        return Hlsl.Lerp(fogColour, lit, fog) + new Float3(flash, flash, flash) * 0.06f;
    }

    /// <summary>Spins a point about the vertical axis, then tilts it, so the cube turns in place.</summary>
    private static Float3 Turn(Float3 p, float spin, float tilt)
    {
        Float3 a = new Float3(p.X * Hlsl.Cos(spin) - p.Z * Hlsl.Sin(spin), p.Y, p.X * Hlsl.Sin(spin) + p.Z * Hlsl.Cos(spin));
        return new Float3(a.X, a.Y * Hlsl.Cos(tilt) - a.Z * Hlsl.Sin(tilt), a.Y * Hlsl.Sin(tilt) + a.Z * Hlsl.Cos(tilt));
    }

    /// <summary>A spectrum level at 0 (bass) to 1 (highs). Read outside the march loops.</summary>
    private float Band(float position) => data.Sample(new Float2((Hlsl.Saturate(position) * 95f + 0.5f) / 1024f, 0.0625f)).Y;

    private static Float3 Brighten(Float3 c) => c / Hlsl.Max(Hlsl.Max(c.X, c.Y), Hlsl.Max(c.Z, 0.001f));

    private Float3 Gradient(float t)
    {
        Float3 c = t < 0.5f ? Hlsl.Lerp(low, mid, t * 2f) : Hlsl.Lerp(mid, high, t * 2f - 1f);
        return c / Hlsl.Max(Hlsl.Max(c.X, c.Y), Hlsl.Max(c.Z, 0.001f));
    }
}
