using ComputeSharp;
using ComputeSharp.D2D1;

namespace EdgeDock.Controls;

// Views adapted from shaders whose authors chose permissive licences. Each is credited below and
// in THIRD-PARTY-NOTICES.md. They were translated from GLSL to C# for ComputeSharp and changed to
// follow the music (speed, brightness, zoom) and the widget's colour looks.
//
// Style 0: "Star Nest" by Pablo Roman Andrioli (Kali), https://www.shadertoy.com/view/XlfGRj,
//          MIT License.
// Style 1: "Hexagon Plasma" by Nemerix, https://www.shadertoy.com/view/3fy3z3, MIT License.
// Style 2: "2D Clouds" by drift, https://www.shadertoy.com/view/4tdSWr. The author: "Anyone and
//          everyone who wishes to use this shader I give my permission to use it in any way that
//          you choose. Credit would be nice but I won't insist on it."
// Style 3: "B/W logarithmic circles II" by mrange, https://www.shadertoy.com/view/mljcWR, CC0.
// Style 4: "Very fast procedural ocean" by afl_ext, https://www.shadertoy.com/view/MdXyzX, MIT
//          License. EdgeDock adds the cloud layer, the look's colours and the music.

[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader40)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct WaveformBorrowedShader(
    Float2 size, Float3 low, Float3 mid, Float3 high,
    float time, float travel, float bass, float energy, float flash, int style) : ID2D1PixelShader
{
    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        Float3 rgb = style == 0 ? StarNest(p) : (style == 1 ? HexPlasma(p) : (style == 2 ? Clouds(p) : (style == 3 ? Circles(p) : Ocean(p))));
        return new Float4(Hlsl.Saturate(rgb), 1f);
    }

    // ---------- Star Nest (MIT) ----------

    private Float3 StarNest(Float2 fragment)
    {
        Float2 uv = fragment / size - 0.5f;
        uv.Y *= size.Y / size.X;
        Float3 dir = new Float3(uv * (0.8f - 0.25f * bass), 1f);
        float t = travel * 0.012f + 0.25f;
        float a1 = 0.5f + 0.3f * Hlsl.Sin(time * 0.05f);
        float a2 = 0.8f + 0.2f * Hlsl.Cos(time * 0.04f);
        Float2 xz = Rotate(new Float2(dir.X, dir.Z), a1);
        dir = new Float3(xz.X, dir.Y, xz.Y);
        Float2 xy = Rotate(new Float2(dir.X, dir.Y), a2);
        dir = new Float3(xy.X, xy.Y, dir.Z);
        Float3 from = new Float3(1f, 0.5f, 0.5f) + new Float3(t * 2f, t, -2f);
        xz = Rotate(new Float2(from.X, from.Z), a1);
        from = new Float3(xz.X, from.Y, xz.Y);
        xy = Rotate(new Float2(from.X, from.Y), a2);
        from = new Float3(xy.X, xy.Y, from.Z);

        float s = 0.1f;
        float fade = 1f;
        Float3 v = new Float3(0f, 0f, 0f);
        for (int r = 0; r < 20; r++)
        {
            Float3 q = from + s * dir * 0.5f;
            q = Hlsl.Abs(new Float3(0.85f, 0.85f, 0.85f) - Mod(q, 1.7f));
            float pa = 0f;
            float a = 0f;
            for (int i = 0; i < 17; i++)
            {
                q = Hlsl.Abs(q) / Hlsl.Dot(q, q) - 0.53f;
                a += Hlsl.Abs(Hlsl.Length(q) - pa);
                pa = Hlsl.Length(q);
            }
            float dm = Hlsl.Max(0f, 0.3f - a * a * 0.001f);
            a *= a * a;
            if (r > 6) fade *= 1f - dm;
            v += new Float3(fade, fade, fade);
            v += new Float3(s, s * s, s * s * s * s) * a * 0.0015f * fade;
            fade *= 0.73f;
            s += 0.1f;
        }
        float length = Hlsl.Length(v);
        v = Hlsl.Lerp(new Float3(length, length, length), v, 0.85f) * 0.01f;
        // Tint towards the look and brighten with the music.
        float luma = Hlsl.Dot(v, new Float3(0.3f, 0.5f, 0.2f));
        Float3 tinted = Hlsl.Lerp(v, Gradient(Hlsl.Saturate(luma * 0.8f)) * luma * 1.4f, 0.55f);
        return tinted * (0.5f + 1.0f * energy + 0.5f * flash);
    }

    private static Float2 Rotate(Float2 v, float a) =>
        new Float2(v.X * Hlsl.Cos(a) + v.Y * Hlsl.Sin(a), -v.X * Hlsl.Sin(a) + v.Y * Hlsl.Cos(a));

    private static Float3 Mod(Float3 x, float y) => x - y * Hlsl.Floor(x / y);

    // ---------- Hexagon Plasma (MIT) ----------

    private static float HexSdf(Float2 position) =>
        Hlsl.Max(Hlsl.Max(Hlsl.Abs(Hlsl.Dot(position, new Float2(0f, 2f))), Hlsl.Abs(Hlsl.Dot(position, new Float2(1.732f, 1f)))),
                 Hlsl.Abs(Hlsl.Dot(position, new Float2(1.732f, -1f))));

    private Float3 HexPlasma(Float2 fragment)
    {
        Float2 uv = (2f * fragment - size) / size.Y;
        float hex = HexSdf(uv);
        float d = Hlsl.Lerp(hex * hex, Hlsl.Dot(uv, uv), Hlsl.SmoothStep(0.8f, 1.5f, Hlsl.Dot(uv, uv))) - (1f + 0.6f * bass);
        d = Hlsl.Sqrt(Hlsl.Abs(d));
        Float2 sample = uv * d;
        float y = 0f;
        float scale = 1f;
        float t = travel * 0.6f;
        for (int i = 0; i < 4; i++)
        {
            float h = HexSdf(sample);
            Float2 turned = new Float2(0.6f * sample.X - 0.8f * sample.Y, 0.8f * sample.X + 0.6f * sample.Y) * h;
            sample = Hlsl.Sin(turned + new Float2(t, t));
            y += scale * sample.X * sample.Y;
            scale *= 0.75f;
        }
        float spread = Tanh(Hlsl.Dot(uv, uv));
        Float3 colour = Hlsl.Lerp(Gradient(0.25f + 0.5f * spread), new Float3(0.5f, 0.5f, 0.5f), 0.55f * spread);
        Float3 col = colour / Hlsl.Abs(y);
        return new Float3(Tanh(0.1f * col.X), Tanh(0.1f * col.Y), Tanh(0.1f * col.Z)) * (0.45f + 0.85f * energy + 0.4f * flash);
    }

    private static float Tanh(float x) => 1f - 2f / (Hlsl.Exp(2f * Hlsl.Min(x, 20f)) + 1f);

    // ---------- 2D Clouds (drift, free use) ----------

    private static Float2 Hash2(Float2 p)
    {
        p = new Float2(Hlsl.Dot(p, new Float2(127.1f, 311.7f)), Hlsl.Dot(p, new Float2(269.5f, 183.3f)));
        return -1f + 2f * Hlsl.Frac(Hlsl.Sin(p) * 43758.5453123f);
    }

    private static float Noise(Float2 p)
    {
        const float K1 = 0.366025404f;
        const float K2 = 0.211324865f;
        Float2 i = Hlsl.Floor(p + (p.X + p.Y) * K1);
        Float2 a = p - i + (i.X + i.Y) * K2;
        Float2 o = a.X > a.Y ? new Float2(1f, 0f) : new Float2(0f, 1f);
        Float2 b = a - o + K2;
        Float2 c = a - 1f + 2f * K2;
        Float3 h = Hlsl.Max(0.5f - new Float3(Hlsl.Dot(a, a), Hlsl.Dot(b, b), Hlsl.Dot(c, c)), new Float3(0f, 0f, 0f));
        Float3 n = h * h * h * h * new Float3(Hlsl.Dot(a, Hash2(i)), Hlsl.Dot(b, Hash2(i + o)), Hlsl.Dot(c, Hash2(i + 1f)));
        return Hlsl.Dot(n, new Float3(70f, 70f, 70f));
    }

    private static Float2 M(Float2 n) => new Float2(1.6f * n.X - 1.2f * n.Y, 1.2f * n.X + 1.6f * n.Y);

    private Float3 Clouds(Float2 fragment)
    {
        Float2 p = fragment / size;
        p.Y = 1f - p.Y;
        Float2 baseUv = p * new Float2(size.X / size.Y, 1f);
        const float cloudScale = 1.1f;
        float t = travel * 0.03f;
        // The q field: a coarse fbm that bends the finer layers.
        float q = 0f;
        Float2 n = baseUv * cloudScale * 0.5f;
        float amplitude = 0.1f;
        for (int i = 0; i < 7; i++)
        {
            q += Noise(n) * amplitude;
            n = M(n);
            amplitude *= 0.4f;
        }

        float r = 0f;
        Float2 uv = baseUv * cloudScale - q + t;
        float weight = 0.8f;
        for (int j = 0; j < 8; j++)
        {
            r += Hlsl.Abs(weight * Noise(uv));
            uv = M(uv) + t;
            weight *= 0.7f;
        }
        float f = 0f;
        uv = baseUv * cloudScale - q + t;
        weight = 0.7f;
        for (int k = 0; k < 8; k++)
        {
            f += weight * Noise(uv);
            uv = M(uv) + t;
            weight *= 0.6f;
        }
        f *= r + f;
        float c = 0f;
        float t2 = t * 2f;
        uv = baseUv * cloudScale * 2f - q + t2;
        weight = 0.4f;
        for (int l = 0; l < 7; l++)
        {
            c += weight * Noise(uv);
            uv = M(uv) + t2;
            weight *= 0.6f;
        }
        float c1 = 0f;
        float t3 = t * 3f;
        uv = baseUv * cloudScale * 3f - q + t3;
        weight = 0.4f;
        for (int m = 0; m < 7; m++)
        {
            c1 += Hlsl.Abs(weight * Noise(uv));
            uv = M(uv) + t3;
            weight *= 0.6f;
        }
        c += c1;

        // A real sky blue, nudged towards the look; louder music brings more cloud.
        Float3 sky = Hlsl.Lerp(new Float3(0.4f, 0.7f, 1f), new Float3(0.2f, 0.4f, 0.6f), p.Y);
        sky = Hlsl.Lerp(sky, Gradient(1f - p.Y) * 0.8f, 0.45f);
        Float3 cloud = new Float3(1.1f, 1.1f, 0.9f) * Hlsl.Saturate(0.5f + 0.3f * c) + new Float3(flash, flash, flash) * 0.35f;
        f = 0.12f + 0.45f * energy + 8f * f * r;
        return Hlsl.Lerp(sky, Hlsl.Saturate(0.5f * sky + cloud), Hlsl.Saturate(f + c));
    }

    // ---------- B/W logarithmic circles II (CC0) ----------

    private Float3 Circles(Float2 fragment)
    {
        Float2 p = -1f + 2f * fragment / size;
        p.Y = -p.Y;
        p.X *= size.X / size.Y;
        float expBy = Hlsl.Log2(4.1f);
        const float radius = 0.3175f;
        const float tau = 6.2831853f;
        float aa = 4f / size.Y;
        float shade = 0.1f * Hlsl.SmoothStep(-0.7071f, 0.7071f, Hlsl.Sin(0.5f * tau * p.Y / aa));
        Float3 col = new Float3(shade, shade, shade);
        float tm = 0.2f * travel * 0.5f;
        for (int k = 0; k < 2; k++)
        {
            float fi = k;
            float ltm = tm + 0.5f * fi;
            float mtm = Hlsl.Frac(ltm);
            float zz = Hlsl.Exp2(expBy * mtm);
            Float2 p0 = Rotate(p, -0.5f * tm);
            p0 = Rotate(p0, fi * 0.5f * tau / 8f);
            p0 /= zz;
            float l0 = Hlsl.Length(p0);
            float n0 = Hlsl.Ceil(Hlsl.Log2(l0) / expBy);
            float r0 = Hlsl.Exp2(expBy * n0);
            float r1 = Hlsl.Exp2(expBy * (n0 - 1f));
            float rr = (r0 + r1) / 2f;
            float w = r0 - r1;
            // Eight-way polar repeat.
            float angle = tau / 8f;
            float at = Hlsl.Atan2(p0.Y, p0.X) + angle / 2f;
            float cell = Hlsl.Floor(at / angle);
            at = at - angle * Hlsl.Floor(at / angle) - angle / 2f;
            if (Hlsl.Abs(cell) >= 4f) cell = Hlsl.Abs(cell);
            Float2 p1 = new Float2(Hlsl.Cos(at), Hlsl.Sin(at)) * l0;
            p1.X -= rr;
            float a = Hlsl.Frac(0.5f * ltm + cell / 8f);
            float pulse = radius * (1f + 0.8f * bass);
            float d1 = (Hlsl.Length(p1) - pulse * w) * zz;
            float d2 = (Hlsl.Length(p1) - pulse * w * Hlsl.SmoothStep(0f, 0.45f, a - 0.5f * Hlsl.Floor(a / 0.5f))) * zz;
            float inner = Hlsl.SmoothStep(0f, -aa, d2);
            if (a >= 0.5f) inner = 1f - inner;
            col = Hlsl.Lerp(col, new Float3(inner, inner, inner), Hlsl.SmoothStep(0f, -aa, d1));
        }
        col = Hlsl.Sqrt(col);
        // Black and white become the look's darkest and brightest colours.
        return Hlsl.Lerp(Gradient(0f) * 0.06f, Hlsl.Lerp(Gradient(0.5f + 0.5f * Hlsl.Sin(time * 0.2f)), new Float3(1f, 1f, 1f), 0.3f), col.X)
            * (0.8f + 0.4f * flash);
    }

    // ---------- Very fast procedural ocean (afl_ext, MIT) ----------

    private static Float2 WaveDx(Float2 position, Float2 direction, float frequency, float timeshift)
    {
        float x = Hlsl.Dot(direction, position) * frequency + timeshift;
        float wave = Hlsl.Exp(Hlsl.Sin(x) - 1f);
        return new Float2(wave, -wave * Hlsl.Cos(x));
    }

    private float Waves(Float2 position, int iterations)
    {
        float phase = Hlsl.Length(position) * 0.1f;
        float iter = 0f;
        float frequency = 1f;
        float timeMultiplier = 2f;
        float weight = 1f;
        float values = 0f;
        float weights = 0f;
        float clock = travel * 0.55f;
        for (int i = 0; i < 36; i++)
        {
            if (i < iterations)
            {
                Float2 p = new Float2(Hlsl.Sin(iter), Hlsl.Cos(iter));
                Float2 res = WaveDx(position, p, frequency, clock * timeMultiplier + phase);
                position += p * res.Y * weight * 0.38f;
                values += res.X * weight;
                weights += weight;
                weight = Hlsl.Lerp(weight, 0f, 0.2f);
                frequency *= 1.18f;
                timeMultiplier *= 1.07f;
                iter += 1232.399963f;
            }
        }
        return values / weights;
    }

    private Float3 SunDirection() => Hlsl.Normalize(new Float3(-0.0773502691896258f, 0.18f + Hlsl.Sin(time * 0.05f + 2.6f) * 0.12f, 0.5773502691896258f));

    private Float3 Atmosphere(Float3 raydir, Float3 sundir)
    {
        float trick = 1f / (raydir.Y + 0.1f);
        float trick2 = 1f / (sundir.Y * 11f + 1f);
        float raysundt = Hlsl.Pow(Hlsl.Abs(Hlsl.Dot(sundir, raydir)), 2f);
        Float3 blueBase = new Float3(5.5f, 13f, 22.4f) / 22.4f;
        Float3 suncolor = Hlsl.Lerp(new Float3(1f, 1f, 1f), Hlsl.Max(new Float3(0f, 0f, 0f), new Float3(1f, 1f, 1f) - blueBase), trick2);
        Float3 bluesky = blueBase * suncolor;
        Float3 bluesky2 = Hlsl.Max(new Float3(0f, 0f, 0f), bluesky - new Float3(5.5f, 13f, 22.4f) * 0.002f * (trick - 6f * sundir.Y * sundir.Y));
        bluesky2 *= trick * (0.24f + raysundt * 0.24f);
        Float3 sky = bluesky2 * (1f + Hlsl.Pow(1f - raydir.Y, 3f)) * 0.5f;
        // EdgeDock: lean the sky towards the look's colours.
        return Hlsl.Lerp(sky, Gradient(Hlsl.Saturate(raydir.Y * 2f)) * Hlsl.Dot(sky, new Float3(0.33f, 0.33f, 0.33f)) * 1.6f, 0.35f);
    }

    private float Sun(Float3 dir) => Hlsl.Pow(Hlsl.Max(0f, Hlsl.Dot(dir, SunDirection())), 720f) * 210f * (0.7f + 0.8f * energy);

    /// <summary>EdgeDock: soft clouds over the ocean, the same layer in the sky and in reflections.</summary>
    private Float3 SkyWithClouds(Float3 dir)
    {
        Float3 sun = SunDirection();
        Float3 sky = Atmosphere(dir, sun) + Sun(dir);
        if (dir.Y > 0.01f)
        {
            Float2 cloudUv = dir.XZ / (dir.Y + 0.08f) * 0.9f + new Float2(time * 0.02f, travel * 0.03f);
            float shape = 0f;
            float weight = 0.6f;
            Float2 n = cloudUv;
            for (int i = 0; i < 6; i++)
            {
                shape += weight * Noise(n);
                n = M(n) + time * 0.01f;
                weight *= 0.5f;
            }
            float cover = 0.12f + 0.25f * energy;
            float density = Hlsl.SmoothStep(0.05f, 0.45f, shape + cover - 0.2f) * Hlsl.Saturate(dir.Y * 6f);
            float lit = Hlsl.Pow(Hlsl.Max(Hlsl.Dot(dir, sun), 0f), 4f);
            Float3 cloud = Hlsl.Lerp(Atmosphere(new Float3(dir.X, 0.05f, dir.Z), sun) * 1.4f, new Float3(1f, 0.9f, 0.8f), 0.35f + 0.4f * lit)
                         + new Float3(flash, flash, flash) * 0.35f;
            sky = Hlsl.Lerp(sky, cloud, density * 0.85f);
        }
        return sky;
    }

    private static Float3 Aces(Float3 color)
    {
        Float3 v = new Float3(
            0.59719f * color.X + 0.35458f * color.Y + 0.04823f * color.Z,
            0.07600f * color.X + 0.90834f * color.Y + 0.01566f * color.Z,
            0.02840f * color.X + 0.13383f * color.Y + 0.83777f * color.Z);
        Float3 a = v * (v + 0.0245786f) - 0.000090537f;
        Float3 b = v * (0.983729f * v + 0.4329510f) + 0.238081f;
        Float3 r = a / b;
        Float3 m = new Float3(
            1.60475f * r.X - 0.53108f * r.Y - 0.07367f * r.Z,
            -0.10208f * r.X + 1.10813f * r.Y - 0.00605f * r.Z,
            -0.00327f * r.X - 0.07276f * r.Y + 1.07602f * r.Z);
        return Hlsl.Pow(Hlsl.Saturate(m), new Float3(1f / 2.2f, 1f / 2.2f, 1f / 2.2f));
    }

    private Float3 Ocean(Float2 fragment)
    {
        Float2 uv = (fragment / size * 2f - 1f) * new Float2(size.X / size.Y, 1f);
        uv.Y = -uv.Y;
        Float3 ray = Hlsl.Normalize(new Float3(uv.X, uv.Y, 1.5f));
        // Look slightly down at the water.
        float pitch = -0.17f;
        ray = new Float3(ray.X, ray.Y * Hlsl.Cos(pitch) - ray.Z * Hlsl.Sin(pitch), ray.Y * Hlsl.Sin(pitch) + ray.Z * Hlsl.Cos(pitch));
        Float3 result = ray.Y >= 0f ? Aces(SkyWithClouds(ray) * 2f) : Water(ray);
        return result;
    }

    private Float3 Water(Float3 ray)
    {
        // EdgeDock: the bass raises the swell.
        float depth = 1f + 0.9f * bass;
        Float3 origin = new Float3(travel * 0.4f, 1.5f, 1f);
        float highHit = Hlsl.Clamp(-origin.Y / ray.Y, -1f, 9991999f);
        float lowHit = Hlsl.Clamp((-depth - origin.Y) / ray.Y, -1f, 9991999f);
        Float3 start = origin + ray * highHit;
        Float3 end = origin + ray * lowHit;
        Float3 pos = start;
        Float3 dir = Hlsl.Normalize(end - start);
        float dist = Hlsl.Distance(start, origin);
        float found = 0f;
        for (int i = 0; i < 64; i++)
        {
            if (found < 0.5f)
            {
                float height = Waves(pos.XZ, 12) * depth - depth;
                if (height + 0.01f > pos.Y)
                {
                    dist = Hlsl.Distance(pos, origin);
                    found = 1f;
                }
                else pos += dir * (pos.Y - height);
            }
        }
        Float3 hit = origin + ray * dist;
        const float e = 0.01f;
        float h = Waves(hit.XZ, 36) * depth;
        Float3 a = new Float3(hit.X, h, hit.Z);
        Float3 normal = Hlsl.Normalize(Hlsl.Cross(
            a - new Float3(hit.X - e, Waves(hit.XZ - new Float2(e, 0f), 36) * depth, hit.Z),
            a - new Float3(hit.X, Waves(hit.XZ + new Float2(0f, e), 36) * depth, hit.Z + e)));
        normal = Hlsl.Lerp(normal, new Float3(0f, 1f, 0f), 0.8f * Hlsl.Min(1f, Hlsl.Sqrt(dist * 0.01f) * 1.1f));
        float fresnel = 0.04f + 0.96f * Hlsl.Pow(1f - Hlsl.Max(0f, Hlsl.Dot(-normal, ray)), 5f);
        Float3 reflected = Hlsl.Normalize(Hlsl.Reflect(ray, normal));
        reflected.Y = Hlsl.Abs(reflected.Y);
        Float3 reflection = SkyWithClouds(reflected);
        Float3 scattering = new Float3(0.0293f, 0.0698f, 0.1717f) * 0.1f * (0.2f + (hit.Y + depth) / depth)
                          + Gradient(0.3f) * 0.012f * bass;
        return Aces((fresnel * reflection + scattering) * 2f);
    }

    private Float3 Gradient(float t)
    {
        Float3 c = t < 0.5f ? Hlsl.Lerp(low, mid, t * 2f) : Hlsl.Lerp(mid, high, t * 2f - 1f);
        return c / Hlsl.Max(Hlsl.Max(c.X, c.Y), Hlsl.Max(c.Z, 0.001f));
    }
}
