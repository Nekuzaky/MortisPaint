using System;
using System.Collections.Generic;
using SkiaSharp;

namespace MortisPaint.Engine;

public static class FloodFill
{
    /// <summary>
    /// Calcule le masque de remplissage (255 = rempli) à partir d'un point, par balayage de lignes.
    /// </summary>
    public static byte[]? ComputeMask(SKBitmap source, int sx, int sy, int tolerance, int expand, out SKRectI bounds)
    {
        int w = source.Width, h = source.Height;
        bounds = SKRectI.Empty;
        if (sx < 0 || sy < 0 || sx >= w || sy >= h) return null;
        var mask = new byte[w * h];
        int minX = sx, maxX = sx, minY = sy, maxY = sy;
        unsafe
        {
            byte* basePtr = (byte*)source.GetPixels();
            int row = source.RowBytes;
            byte* seedPx = basePtr + sy * row + sx * 4;
            var ctx = new FillCtx
            {
                Base = basePtr, Row = row, W = w, Mask = mask, Tol = Math.Clamp(tolerance, 0, 255),
                S0 = seedPx[0], S1 = seedPx[1], S2 = seedPx[2], S3 = seedPx[3],
            };

            var stack = new Stack<(int X, int Y)>();
            stack.Push((sx, sy));
            while (stack.Count > 0)
            {
                var (x, y) = stack.Pop();
                if (!ctx.Match(x, y)) continue;
                int lx = x, rx = x;
                while (lx > 0 && ctx.Match(lx - 1, y)) lx--;
                while (rx < w - 1 && ctx.Match(rx + 1, y)) rx++;
                for (int i = lx; i <= rx; i++) mask[y * w + i] = 255;
                if (lx < minX) minX = lx;
                if (rx > maxX) maxX = rx;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                for (int dir = -1; dir <= 1; dir += 2)
                {
                    int ny = y + dir;
                    if (ny < 0 || ny >= h) continue;
                    bool inRun = false;
                    for (int i = lx; i <= rx; i++)
                    {
                        bool m = ctx.Match(i, ny);
                        if (m && !inRun) { stack.Push((i, ny)); inRun = true; }
                        else if (!m) inRun = false;
                    }
                }
            }
        }

        bounds = new SKRectI(minX, minY, maxX + 1, maxY + 1);
        if (expand > 0)
        {
            Dilate(mask, w, h, expand, ref bounds);
        }
        return mask;
    }

    /// <summary>Élargit le masque de n pixels pour couvrir l'anticrénelage des lignes.</summary>
    static void Dilate(byte[] mask, int w, int h, int n, ref SKRectI bounds)
    {
        var r = BitmapUtil.Clamp(new SKRectI(bounds.Left - n, bounds.Top - n, bounds.Right + n, bounds.Bottom + n), w, h);
        var tmp = new byte[w * h];
        // horizontal
        for (int y = r.Top; y < r.Bottom; y++)
        {
            int prev = -100000;
            for (int x = r.Left; x < r.Right; x++)
            {
                if (mask[y * w + x] != 0) prev = x;
                tmp[y * w + x] = (byte)(x - prev <= n ? 255 : 0);
            }
            int next = 100000;
            for (int x = r.Right - 1; x >= r.Left; x--)
            {
                if (mask[y * w + x] != 0) next = x;
                if (next - x <= n) tmp[y * w + x] = 255;
            }
        }
        // vertical
        for (int x = r.Left; x < r.Right; x++)
        {
            int prev = -100000;
            for (int y = r.Top; y < r.Bottom; y++)
            {
                if (tmp[y * w + x] != 0) prev = y;
                mask[y * w + x] = (byte)(y - prev <= n ? 255 : 0);
            }
            int next = 100000;
            for (int y = r.Bottom - 1; y >= r.Top; y--)
            {
                if (tmp[y * w + x] != 0) next = y;
                if (next - y <= n) mask[y * w + x] = 255;
            }
        }
        bounds = r;
    }

    /// <summary>Convertit le masque en bitmap Alpha8 limité à bounds.</summary>
    public static SKBitmap ToAlphaBitmap(byte[] mask, int w, SKRectI bounds)
    {
        var bmp = new SKBitmap(new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Alpha8, SKAlphaType.Premul));
        unsafe
        {
            byte* d = (byte*)bmp.GetPixels();
            int row = bmp.RowBytes;
            for (int y = 0; y < bounds.Height; y++)
            {
                int src = (bounds.Top + y) * w + bounds.Left;
                for (int x = 0; x < bounds.Width; x++)
                    d[y * row + x] = mask[src + x];
            }
        }
        return bmp;
    }

    unsafe struct FillCtx
    {
        public byte* Base;
        public int Row, W, Tol, S0, S1, S2, S3;
        public byte[] Mask;

        public bool Match(int x, int y)
        {
            if (Mask[y * W + x] != 0) return false;
            byte* p = Base + y * Row + x * 4;
            return Math.Abs(p[0] - S0) <= Tol && Math.Abs(p[1] - S1) <= Tol &&
                   Math.Abs(p[2] - S2) <= Tol && Math.Abs(p[3] - S3) <= Tol;
        }
    }
}
