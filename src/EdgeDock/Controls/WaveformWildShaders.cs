using ComputeSharp;
using ComputeSharp.D2D1;

namespace EdgeDock.Controls;

/// <summary>
/// The wild ones, written for EdgeDock. Style 0: cloudscape, a flight under raymarched clouds lit
/// by a low sun (lightning on the beat). 1: lasers, rave beams sweeping with the music. 2: Voronoi
/// cells, each lit by its own spectrum band. 3: liquid blob, a raymarched shape that swells with
/// the bass and ripples with the mids. 4: truchet, glowing arcs on a slowly turning grid.
/// </summary>
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader40)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct WaveformWildShader(
    Float2 size, Float3 low, Float3 mid, Float3 high,
    float time, float travel, float bass, float mids, float highs, float energy, float flash, int style) : ID2D1PixelShader
{
    /// <summary>The live data texture (spectrum levels in row 0, channel Y).</summary>
    [D2DResourceTextureIndex(0)]
    private readonly D2D1ResourceTexture2D<Float4> data;

    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        Float3 rgb = new Float3(0f, 0f, 0f);
        if (style == 0) rgb = Cloudscape(p);
        else if (style == 1) rgb = Lasers(p);
        else if (style == 2) rgb = Voronoi(p);
        else if (style == 3) rgb = Blob(p);
        else rgb = Truchet(p);
        rgb = Hlsl.Saturate(rgb);
        return new Float4(rgb, 1f);
    }

    // ---------- Cloudscape ----------

    private Float3 Cloudscape(Float2 fragment)
    {
        Float2 uv = (fragment - size * 0.5f) / size.Y;
        uv.Y = -uv.Y;
        Float3 origin = new Float3(0f, 1.2f, travel * 1.6f);
        Float3 dir = Hlsl.Normalize(new Float3(uv.X * 1.1f, uv.Y * 0.9f + 0.12f, 1f));
        Float3 sun = Hlsl.Normalize(new Float3(0.3f, 0.12f + 0.05f * Hlsl.Sin(time * 0.03f), 1f));
        float sunAmount = Hlsl.Max(Hlsl.Dot(dir, sun), 0f);

        // Sky: deep above, warm towards the horizon, tinted by the look; a sun with a soft halo.
        float up = Hlsl.Saturate(dir.Y * 2.2f);
        Float3 horizon = Hlsl.Lerp(new Float3(1f, 0.62f, 0.38f), Gradient(0.85f), 0.3f);
        Float3 zenith = Hlsl.Lerp(new Float3(0.12f, 0.24f, 0.52f), Gradient(0.15f) * 0.5f, 0.3f);
        Float3 sky = Hlsl.Lerp(horizon, zenith, Hlsl.Sqrt(up));
        sky += new Float3(1f, 0.8f, 0.55f) * (Hlsl.Pow(sunAmount, 900f) * 3f + Hlsl.Pow(sunAmount, 12f) * 0.35f);
        if (dir.Y < 0f)
        {
            // Below the horizon: a dark sea reflecting the sky.
            sky = Hlsl.Lerp(new Float3(0.04f, 0.05f, 0.08f), horizon * 0.45f, Hlsl.Exp(dir.Y * 25f));
        }

        // March through the cloud layer between heights 2 and 4.
        Float3 colour = new Float3(0f, 0f, 0f);
        float transmittance = 1f;
        if (dir.Y > 0.01f)
        {
            float start = (2f - origin.Y) / dir.Y;
            float end = Hlsl.Min((4f - origin.Y) / dir.Y, 40f);
            float step = (end - start) / 14f;
            float jitter = Hash(fragment.X * 0.13f + fragment.Y * 7.7f);
            for (int i = 0; i < 14; i++)
            {
                if (transmittance > 0.03f && start < 40f)
                {
                    Float3 position = origin + dir * (start + (i + jitter) * step);
                    float density = Density(position);
                    if (density > 0.001f)
                    {
                        // Two steps towards the sun: how much cloud the light passes first.
                        float shadow = Density(position + sun * 0.35f) + Density(position + sun * 0.8f);
                        float light = Hlsl.Exp(-shadow * 1.6f);
                        float powder = 1f - Hlsl.Exp(-density * 4f);
                        Float3 lit = new Float3(1f, 0.86f, 0.7f) * light * powder * (0.9f + 0.6f * Hlsl.Pow(sunAmount, 6f))
                                   + Hlsl.Lerp(zenith, horizon, 0.4f) * 0.35f + new Float3(flash, flash, flash * 1.1f) * 0.9f;
                        float absorbed = Hlsl.Exp(-density * step * 1.4f);
                        colour += transmittance * (1f - absorbed) * lit;
                        transmittance *= absorbed;
                    }
                }
            }
            // Distant clouds melt into the haze.
            float haze = Hlsl.Exp(-start * 0.04f);
            colour = Hlsl.Lerp(sky * (1f - transmittance), colour, haze);
        }
        return colour + sky * transmittance;
    }

    private float Density(Float3 position)
    {
        Float3 q = position * 0.45f + new Float3(time * 0.05f, 0f, time * 0.02f);
        float n = Noise3(q) * 0.55f + Noise3(q * 2.1f) * 0.27f + Noise3(q * 4.3f) * 0.13f + Noise3(q * 8.7f) * 0.05f;
        float height = Hlsl.Saturate((position.Y - 2f) * 2f) * Hlsl.Saturate((4f - position.Y) * 1.2f);
        float cover = 0.52f - 0.12f * energy - 0.05f * bass;
        return Hlsl.Saturate((n - cover) * 3f) * height;
    }

    private static float Hash(float n) => Hlsl.Frac(Hlsl.Sin(n * 12.9898f) * 43758.5453f);

    private static float Noise3(Float3 x)
    {
        Float3 i = Hlsl.Floor(x);
        Float3 f = Hlsl.Frac(x);
        f = f * f * (3f - 2f * f);
        float n = i.X + i.Y * 57f + i.Z * 113f;
        return Hlsl.Lerp(
            Hlsl.Lerp(Hlsl.Lerp(Hash(n), Hash(n + 1f), f.X), Hlsl.Lerp(Hash(n + 57f), Hash(n + 58f), f.X), f.Y),
            Hlsl.Lerp(Hlsl.Lerp(Hash(n + 113f), Hash(n + 114f), f.X), Hlsl.Lerp(Hash(n + 170f), Hash(n + 171f), f.X), f.Y),
            f.Z);
    }

    // ---------- Lasers ----------

    private Float3 Lasers(Float2 p)
    {
        Float3 rgb = new Float3(0f, 0f, 0f);
        for (int e = 0; e < 5; e++)
        {
            float fe = e;
            Float2 origin = new Float2(size.X * (0.1f + 0.2f * fe), size.Y * 1.02f);
            for (int j = 0; j < 6; j++)
            {
                float fj = j;
                float angle = -1.5708f + 0.95f * Hlsl.Sin(time * (0.45f + fe * 0.11f) + fj * 1.3f + fe) + (fj - 2.5f) * 0.12f * (1f + flash);
                Float2 dir = new Float2(Hlsl.Cos(angle), Hlsl.Sin(angle));
                Float2 d = p - origin;
                float along = Hlsl.Dot(d, dir);
                float across = Hlsl.Abs(d.X * dir.Y - d.Y * dir.X);
                float level = Band((fe * 6f + fj) / 30f);
                float beam = Hlsl.Step(0f, along) * (0.9f * Hlsl.Exp(-across / 1.6f) + 0.25f * Hlsl.Exp(-across / 16f)) * (0.15f + 1.3f * level);
                rgb += Gradient(Hlsl.Frac(fj / 6f + fe * 0.17f)) * beam;
            }
        }
        // A little haze so the beams have something to light.
        float haze = 0.05f * Noise3(new Float3(p / size.Y * 3f, time * 0.2f)) * (0.4f + energy);
        rgb += Gradient(0.5f) * haze + new Float3(flash, flash, flash) * 0.06f;
        return rgb / (1f + rgb);
    }

    // ---------- Voronoi ----------

    private Float3 Voronoi(Float2 p)
    {
        float scale = size.Y / (5f - 1.2f * bass);
        Float2 q = p / scale + new Float2(travel * 0.15f, 0f);
        Float2 cell = Hlsl.Floor(q);
        float nearest = 10f;
        float second = 10f;
        float id = 0f;
        for (int y = -1; y <= 1; y++)
        {
            for (int x = -1; x <= 1; x++)
            {
                Float2 c = cell + new Float2(x, y);
                float h = Hash(c.X * 31.7f + c.Y * 7.3f);
                Float2 point = c + 0.5f + 0.4f * new Float2(Hlsl.Sin(time * 0.7f + h * 6.28f), Hlsl.Cos(time * 0.6f + h * 9.1f));
                float d = Hlsl.Length(q - point);
                if (d < nearest)
                {
                    second = nearest;
                    nearest = d;
                    id = h;
                }
                else if (d < second) second = d;
            }
        }
        float level = Band(id);
        float edge = second - nearest;
        float line = Hlsl.Exp(-edge * 40f);
        Float3 fill = Gradient(id) * (0.08f + 0.9f * level) * (1f - nearest * 0.6f);
        return fill + Hlsl.Lerp(Gradient(id), new Float3(1f, 1f, 1f), 0.6f) * line * (0.4f + energy + flash);
    }

    // ---------- Liquid blob ----------

    private float Shape(Float3 q)
    {
        float wobble = Hlsl.Sin(q.X * 4f + time * 1.3f) * Hlsl.Sin(q.Y * 4.5f + time * 1.1f) * Hlsl.Sin(q.Z * 5f + time * 0.9f);
        float ripple = Hlsl.Sin(Hlsl.Length(q) * 14f - travel * 4f) * 0.03f * highs;
        return Hlsl.Length(q) - (1f + 0.28f * bass + 0.12f * flash) - wobble * (0.1f + 0.25f * mids) - ripple;
    }

    private Float3 Blob(Float2 fragment)
    {
        Float2 uv = (fragment - size * 0.5f) / size.Y;
        uv.Y = -uv.Y;
        float turn = time * 0.25f;
        Float3 origin = new Float3(Hlsl.Sin(turn) * 3.4f, 0.4f, Hlsl.Cos(turn) * 3.4f);
        Float3 forward = Hlsl.Normalize(-origin);
        Float3 right = Hlsl.Normalize(new Float3(forward.Z, 0f, -forward.X));
        Float3 upward = new Float3(0f, 1f, 0f);
        Float3 dir = Hlsl.Normalize(forward + uv.X * right + uv.Y * upward);
        float t = 0f;
        float hit = 0f;
        for (int i = 0; i < 56; i++)
        {
            if (hit < 0.5f && t < 8f)
            {
                float d = Shape(origin + dir * t);
                if (d < 0.002f) hit = 1f;
                else t += d * 0.7f;
            }
        }
        float glow = Hlsl.Exp(-Hlsl.Length(uv) * 3f) * (0.15f + 0.4f * bass);
        Float3 background = Gradient(0.1f) * glow + new Float3(0.01f, 0.01f, 0.02f);
        if (hit < 0.5f) return background;
        Float3 position = origin + dir * t;
        const float e = 0.002f;
        Float3 normal = Hlsl.Normalize(new Float3(
            Shape(position + new Float3(e, 0f, 0f)) - Shape(position - new Float3(e, 0f, 0f)),
            Shape(position + new Float3(0f, e, 0f)) - Shape(position - new Float3(0f, e, 0f)),
            Shape(position + new Float3(0f, 0f, e)) - Shape(position - new Float3(0f, 0f, e))));
        Float3 light = Hlsl.Normalize(new Float3(0.6f, 0.8f, -0.4f));
        float diffuse = Hlsl.Max(Hlsl.Dot(normal, light), 0f);
        float specular = Hlsl.Pow(Hlsl.Max(Hlsl.Dot(Hlsl.Reflect(-light, normal), -dir), 0f), 40f);
        float rim = Hlsl.Pow(1f - Hlsl.Max(Hlsl.Dot(normal, -dir), 0f), 3f);
        Float3 colour = Gradient(Hlsl.Saturate(normal.Y * 0.5f + 0.5f));
        return colour * (0.15f + 0.75f * diffuse) + new Float3(1f, 1f, 1f) * specular * 0.8f + Gradient(1f) * rim * (0.6f + energy);
    }

    // ---------- Truchet ----------

    private Float3 Truchet(Float2 p)
    {
        Float2 centre = size * 0.5f;
        float angle = time * 0.05f;
        Float2 d = p - centre;
        Float2 turned = new Float2(d.X * Hlsl.Cos(angle) - d.Y * Hlsl.Sin(angle), d.X * Hlsl.Sin(angle) + d.Y * Hlsl.Cos(angle));
        float tile = size.Y / (7f + 2f * Hlsl.Sin(time * 0.07f));
        Float2 q = turned / tile + new Float2(travel * 0.2f, travel * 0.05f);
        Float2 cell = Hlsl.Floor(q);
        Float2 f = Hlsl.Frac(q) - 0.5f;
        float flip = Hlsl.Step(0.5f, Hash(cell.X * 17.1f + cell.Y * 3.7f));
        if (flip > 0.5f) f.X = -f.X;
        // Two quarter circles per tile, joining into endless curves.
        float a = Hlsl.Abs(Hlsl.Length(f - 0.5f) - 0.5f);
        float b = Hlsl.Abs(Hlsl.Length(f + 0.5f) - 0.5f);
        float dist = Hlsl.Min(a, b) * tile;
        float width = tile * (0.06f + 0.06f * bass);
        float core = Hlsl.Saturate(width - dist);
        float glow = Hlsl.Exp(-dist / (tile * 0.12f)) * 0.45f;
        float hue = Hlsl.Frac((cell.X + cell.Y) * 0.07f + travel * 0.05f);
        Float3 colour = Gradient(1f - Hlsl.Abs(hue * 2f - 1f));
        return Hlsl.Lerp(colour, new Float3(1f, 1f, 1f), 0.5f) * Hlsl.Saturate(core) + colour * glow * (0.5f + energy + flash * 0.5f);
    }

    /// <summary>A spectrum level at 0 (bass) to 1 (highs).</summary>
    private float Band(float position) => data.Sample(new Float2((Hlsl.Saturate(position) * 95f + 0.5f) / 1024f, 0.0625f)).Y;

    private Float3 Gradient(float t)
    {
        Float3 c = t < 0.5f ? Hlsl.Lerp(low, mid, t * 2f) : Hlsl.Lerp(mid, high, t * 2f - 1f);
        return c / Hlsl.Max(Hlsl.Max(c.X, c.Y), Hlsl.Max(c.Z, 0.001f));
    }
}
