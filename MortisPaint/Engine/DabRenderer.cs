using System;
using System.Collections.Generic;
using SkiaSharp;

namespace MortisPaint.Engine;

/// <summary>Dessine les empreintes d'un pinceau (rond, ellipse, carré, pixel) avec symétrie et grain.</summary>
public sealed class DabRenderer : IDisposable
{
    readonly BrushSettings _b;
    readonly SKColor _color;
    readonly List<SKMatrix> _sym;
    readonly Random _rng = new();
    readonly SKPaint _paint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    readonly SKColor[] _gradColors;
    readonly float[] _gradPos;
    readonly SKShader? _grain;
    SKRectI _dirty = SKRectI.Empty;

    public DabRenderer(BrushSettings brush, SKColor color, List<SKMatrix> symmetry)
    {
        _b = brush;
        _color = color.WithAlpha(255);
        _sym = symmetry;
        float h = Math.Clamp(brush.Hardness, 0f, 0.98f);
        _gradPos = new[] { 0f, h, h + (1 - h) * 0.33f, h + (1 - h) * 0.66f, 1f };
        _gradColors = new[]
        {
            _color, _color, _color.WithAlpha(158), _color.WithAlpha(56), _color.WithAlpha(0),
        };
        if (brush.Grain > 0.01f) _grain = GrainTexture.Get(brush.Grain);
    }

    public SKRectI TakeDirty()
    {
        var d = _dirty;
        _dirty = SKRectI.Empty;
        return d;
    }

    public float SizeAt(float pressure, float along)
    {
        float f = _b.PressureSize ? _b.MinSize + (1 - _b.MinSize) * pressure : 1f;
        if (_b.TaperStart > 0.5f && along < _b.TaperStart) f *= 0.2f + 0.8f * (along / _b.TaperStart);
        return _b.Size * f;
    }

    public void Dab(SKCanvas c, float x, float y, float pressure, float angle, float along)
    {
        float baseSize = SizeAt(pressure, along);
        float alpha = _b.Flow * (_b.PressureOpacity ? Math.Clamp(pressure, 0.05f, 1f) : 1f);
        int count = Math.Max(1, _b.Count);
        bool spray = count > 1;

        for (int k = 0; k < count; k++)
        {
            float d = spray ? MathF.Max(1f, baseSize * 0.05f) : baseSize;
            if (_b.SizeJitter > 0) d *= 1f + ((float)_rng.NextDouble() * 2f - 1f) * _b.SizeJitter;
            float px = x, py = y;
            if (_b.Scatter > 0)
            {
                double a = _rng.NextDouble() * Math.PI * 2;
                double rr = Math.Sqrt(_rng.NextDouble()) * _b.Scatter * baseSize;
                px += (float)(Math.Cos(a) * rr);
                py += (float)(Math.Sin(a) * rr);
            }

            for (int s = 0; s < _sym.Count; s++)
            {
                var m = _sym[s];
                float sx = px, sy = py, sa = angle;
                if (s > 0)
                {
                    var mp = m.MapPoint(px, py);
                    sx = mp.X; sy = mp.Y;
                    var v = m.MapVector(MathF.Cos(angle), MathF.Sin(angle));
                    sa = MathF.Atan2(v.Y, v.X);
                }
                DrawShape(c, sx, sy, MathF.Max(0.3f, d), sa, alpha);
            }
        }
    }

    void DrawShape(SKCanvas c, float x, float y, float d, float angle, float alpha)
    {
        byte a8 = (byte)Math.Clamp(alpha * 255f + 0.5f, 1f, 255f);
        float r = d * 0.5f;

        if (_b.Shape == BrushShape.Pixel)
        {
            int s = Math.Max(1, (int)MathF.Round(d));
            int ix = (int)MathF.Floor(x - s * 0.5f + 0.5f);
            int iy = (int)MathF.Floor(y - s * 0.5f + 0.5f);
            _paint.IsAntialias = false;
            _paint.Shader = null;
            _paint.Color = _color.WithAlpha(a8);
            c.DrawRect(ix, iy, s, s, _paint);
            Mark(new SKRectI(ix, iy, ix + s, iy + s));
            return;
        }

        _paint.IsAntialias = true;
        _paint.Color = _color.WithAlpha(a8);
        bool soft = _b.Hardness < 0.97f && _b.Shape != BrushShape.Square;

        if (_b.Shape == BrushShape.Round)
        {
            SKShader? shader = soft
                ? SKShader.CreateRadialGradient(new SKPoint(x, y), r, _gradColors, _gradPos, SKShaderTileMode.Clamp)
                : null;
            ApplyGrain(shader, SKMatrix.Identity, out var finalShader);
            _paint.Shader = finalShader;
            c.DrawCircle(x, y, r, _paint);
            _paint.Shader = null;
            finalShader?.Dispose();
            if (!ReferenceEquals(shader, finalShader)) shader?.Dispose();
        }
        else
        {
            float deg = (_b.FollowDirection ? angle : _b.Angle * MathF.PI / 180f) * 180f / MathF.PI;
            float ry = _b.Shape == BrushShape.Ellipse ? Math.Clamp(_b.Roundness, 0.05f, 1f) : 1f;
            var local = SKMatrix.CreateTranslation(x, y)
                .PreConcat(SKMatrix.CreateRotationDegrees(deg))
                .PreConcat(SKMatrix.CreateScale(1, ry));
            c.Save();
            c.Concat(in local);
            SKShader? shader = soft
                ? SKShader.CreateRadialGradient(new SKPoint(0, 0), r, _gradColors, _gradPos, SKShaderTileMode.Clamp)
                : null;
            local.TryInvert(out var inv);
            ApplyGrain(shader, inv, out var finalShader);
            _paint.Shader = finalShader;
            if (_b.Shape == BrushShape.Square) c.DrawRect(-r, -r, d, d, _paint);
            else c.DrawCircle(0, 0, r, _paint);
            _paint.Shader = null;
            finalShader?.Dispose();
            if (!ReferenceEquals(shader, finalShader)) shader?.Dispose();
            c.Restore();
        }
        Mark(BitmapUtil.RoundOut(new SKRect(x - r, y - r, x + r, y + r), 2));
    }

    void ApplyGrain(SKShader? shape, SKMatrix grainLocal, out SKShader? result)
    {
        if (_grain == null) { result = shape; return; }
        using var grain = grainLocal.IsIdentity ? null : _grain.WithLocalMatrix(grainLocal);
        using var solid = shape == null ? SKShader.CreateColor(_color) : null;
        result = SKShader.CreateCompose(shape ?? solid!, grain ?? _grain, SKBlendMode.DstIn);
    }

    void Mark(SKRectI r) => _dirty = BitmapUtil.Union(_dirty, r);

    public void Dispose() => _paint.Dispose();
}

/// <summary>Texture de grain « papier » partagée, fixée à la toile.</summary>
public static class GrainTexture
{
    const int N = 160;
    static float[]? _noise;
    static readonly Dictionary<int, SKShader> Cache = new();

    public static SKShader Get(float amount)
    {
        int key = Math.Clamp((int)MathF.Round(amount * 20f), 1, 20);
        if (Cache.TryGetValue(key, out var shader)) return shader;
        var noise = _noise ??= BuildNoise();
        float g = key / 20f;
        var bmp = new SKBitmap(BitmapUtil.Info(N, N)); // conservé : référencé par le shader
        unsafe
        {
            byte* p = (byte*)bmp.GetPixels();
            for (int i = 0; i < N * N; i++)
            {
                float a = 1f - g * noise[i];
                byte v = (byte)Math.Clamp(a * 255f, 0f, 255f);
                p[i * 4] = v; p[i * 4 + 1] = v; p[i * 4 + 2] = v; p[i * 4 + 3] = v;
            }
        }
        shader = SKShader.CreateBitmap(bmp, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
        Cache[key] = shader;
        return shader;
    }

    static float[] BuildNoise()
    {
        var rng = new Random(1337);
        var raw = new float[N * N];
        for (int i = 0; i < raw.Length; i++) raw[i] = (float)rng.NextDouble();
        // Léger flou pour imiter le grain du papier, puis contraste.
        var outp = new float[N * N];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float sum = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        sum += raw[((y + dy + N) % N) * N + (x + dx + N) % N];
                float v = sum / 9f;
                v = Math.Clamp((v - 0.5f) * 3.2f + 0.5f, 0f, 1f);
                outp[y * N + x] = v;
            }
        return outp;
    }
}
