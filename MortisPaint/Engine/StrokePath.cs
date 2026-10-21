using System;
using SkiaSharp;

namespace MortisPaint.Engine;

public readonly record struct InputPoint(float X, float Y, float Pressure);

/// <summary>
/// Transforme les points bruts du pointeur en une suite d'empreintes régulièrement espacées :
/// stabilisation (lissage par ressort), courbes de Bézier entre les milieux, espacement fonction de la taille.
/// </summary>
public sealed class StrokePath
{
    public delegate void DabHandler(float x, float y, float pressure, float angleRad, float distance);

    /// <summary>Pas minimal entre deux empreintes, en pixels de la toile (indépendant du zoom).</summary>
    public const float MinStep = 0.4f;

    readonly BrushSettings _brush;
    readonly DabHandler _onDab;

    bool _started;
    bool _firstPending;                  // 1ʳᵉ empreinte en attente de la direction du trait
    float _sx, _sy, _sp;                // position stabilisée
    InputPoint _prev, _lastMid;          // pour le lissage par courbes
    float _toNext;                       // distance restante avant la prochaine empreinte
    float _angle;
    float _travelled;
    InputPoint _lastRaw;

    /// <param name="scaleForSpacing">Ignoré : l'espacement est en pixels de la toile, quel que soit le zoom.</param>
    public StrokePath(BrushSettings brush, DabHandler onDab, float scaleForSpacing = 1f)
    {
        _brush = brush;
        _onDab = onDab;
        _angle = brush.Angle * MathF.PI / 180f;
    }

    public float Travelled => _travelled;

    /// <summary>
    /// Contrainte de règle, appliquée aux points déjà stabilisés : le trait lissé reste sur la règle
    /// (un cercle concentrique n'est pas coupé en cordes par le ressort).
    /// </summary>
    public Func<SKPoint, SKPoint>? Constraint { get; set; }

    public void Add(InputPoint p)
    {
        _lastRaw = p;
        if (!_started)
        {
            _started = true;
            _sx = p.X; _sy = p.Y; _sp = p.Pressure;
            var q = Constrained(p.X, p.Y, p.Pressure);
            _prev = q;
            _lastMid = q;
            _toNext = Step(q.Pressure, 0f);
            // Pointe qui suit la direction : la 1ʳᵉ empreinte attend de connaître cette direction.
            if (_brush.FollowDirection) _firstPending = true;
            else Emit(q.X, q.Y, q.Pressure, 0f);
            return;
        }

        float stab = Math.Clamp(_brush.Stabilizer, 0f, 20f);
        float follow = 1f / (1f + stab * 0.9f);
        _sx += (p.X - _sx) * follow;
        _sy += (p.Y - _sy) * follow;
        _sp += (p.Pressure - _sp) * MathF.Min(1f, follow * 1.5f);
        AddSmoothed(Constrained(_sx, _sy, _sp));
    }

    public void End()
    {
        if (!_started) return;
        // Rattrape le pointeur réel pour ne pas « raccourcir » le trait stabilisé.
        if (_brush.Stabilizer > 0.01f)
        {
            for (int i = 0; i < 8; i++)
            {
                _sx += (_lastRaw.X - _sx) * 0.5f;
                _sy += (_lastRaw.Y - _sy) * 0.5f;
                AddSmoothed(Constrained(_sx, _sy, _sp));
            }
        }
        WalkLine(_lastMid, _prev);
        // Simple tapotement : aucune direction connue, la pointe garde son angle fixe.
        if (_firstPending)
        {
            _firstPending = false;
            Emit(_lastMid.X, _lastMid.Y, _lastMid.Pressure, 0f);
        }
    }

    InputPoint Constrained(float x, float y, float pressure)
    {
        if (Constraint == null) return new InputPoint(x, y, pressure);
        var c = Constraint(new SKPoint(x, y));
        return new InputPoint(c.X, c.Y, pressure);
    }

    void AddSmoothed(InputPoint p)
    {
        float dx = p.X - _prev.X, dy = p.Y - _prev.Y;
        if (dx * dx + dy * dy < 0.04f) return;
        var mid = new InputPoint((_prev.X + p.X) * 0.5f, (_prev.Y + p.Y) * 0.5f, (_prev.Pressure + p.Pressure) * 0.5f);
        WalkQuad(_lastMid, _prev, mid);
        _lastMid = mid;
        _prev = p;
    }

    void WalkQuad(InputPoint a, InputPoint c, InputPoint b)
    {
        float approx = Dist(a, c) + Dist(c, b);
        int n = Math.Clamp((int)(approx / 2f), 1, 64);
        var last = a;
        for (int i = 1; i <= n; i++)
        {
            float t = i / (float)n, u = 1 - t;
            var pt = new InputPoint(
                u * u * a.X + 2 * u * t * c.X + t * t * b.X,
                u * u * a.Y + 2 * u * t * c.Y + t * t * b.Y,
                u * a.Pressure + t * b.Pressure);
            WalkLine(last, pt);
            last = pt;
        }
    }

    void WalkLine(InputPoint a, InputPoint b)
    {
        float dist = Dist(a, b);
        if (dist < 1e-4f) return;
        float dirX = (b.X - a.X) / dist, dirY = (b.Y - a.Y) / dist;
        if (_brush.FollowDirection) _angle = MathF.Atan2(dirY, dirX);
        if (_firstPending)
        {
            _firstPending = false;
            Emit(a.X, a.Y, a.Pressure, 0f);
        }
        float t = _toNext;
        while (t <= dist)
        {
            float f = t / dist;
            float pr = a.Pressure + (b.Pressure - a.Pressure) * f;
            Emit(a.X + dirX * t, a.Y + dirY * t, pr, _travelled + t);
            t += Step(pr, _travelled + t);
        }
        _toNext = t - dist;
        _travelled += dist;
    }

    void Emit(float x, float y, float pressure, float along)
    {
        _onDab(x, y, pressure, _angle, along);
    }

    /// <summary>Distance jusqu'à l'empreinte suivante : taille réelle (pression et effilement compris) × espacement.</summary>
    float Step(float pressure, float along)
    {
        if (_brush.Shape == BrushShape.Pixel) return 0.35f;
        float size = _brush.SizeAt(pressure, along);
        return MathF.Max(MinStep, size * MathF.Max(0.01f, _brush.Spacing));
    }

    static float Dist(InputPoint a, InputPoint b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
