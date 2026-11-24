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
        Float3 rgb = style == 0 ? StarNest(p) : (style == 1 ? HexPlasma(p) : (style == 2 ? Clouds(p) : Circles(p)));
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

    private Float3 Gradient(float t)
    {
        Float3 c = t < 0.5f ? Hlsl.Lerp(low, mid, t * 2f) : Hlsl.Lerp(mid, high, t * 2f - 1f);
        return c / Hlsl.Max(Hlsl.Max(c.X, c.Y), Hlsl.Max(c.Z, 0.001f));
    }
}
