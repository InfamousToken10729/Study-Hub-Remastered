using System;
using System.Collections.Generic;
using Microsoft.Maui.Graphics;

namespace StudyHub.Controls;

/// <summary>
/// Native .NET MAUI ICanvas renderer for Study Hub surface themes, 8 ambient particle engines,
/// and all 17 procedural animated Zen Mode worlds (plus None).
/// </summary>
public sealed class MauiBackdropDrawable : IDrawable
{
    public bool IsDarkMode { get; set; } = true;
    public bool IsZenMode { get; set; } = false;
    public string BgTheme { get; set; } = "aurora";
    public string ParticleEffect { get; set; } = "network";
    public string ZenScene { get; set; } = "north_pole";

    private double _t = 0;
    private float _lastW = 0, _lastH = 0;
    private readonly Random _rng = new(1337);

    private sealed class ParticleNode
    {
        public float X, Y, Vx, Vy, R, Phase, Speed;
        public Color Col = Colors.Cyan;
    }

    private readonly List<ParticleNode> _particles = new();
    private string _seededEffect = "";

    public void Step(double dt)
    {
        if (dt <= 0 || dt > 0.25) dt = 1.0 / 60.0;
        _t += dt;

        if (_lastW <= 10 || _lastH <= 10) return;

        if (_seededEffect != ParticleEffect || _particles.Count == 0)
        {
            SeedParticles(_lastW, _lastH);
        }

        string eff = (ParticleEffect ?? "network").ToLowerInvariant();
        if (eff == "none") return;

        float dtScale = (float)(dt * 60.0);
        for (int i = 0; i < _particles.Count; i++)
        {
            var p = _particles[i];
            p.Phase += p.Speed * dtScale;
            p.X += p.Vx * dtScale;
            p.Y += p.Vy * dtScale;

            if (eff is "snow" or "matrix" or "sakura")
            {
                if (eff is "snow" or "sakura")
                    p.X += (float)Math.Sin(p.Phase) * 0.45f * dtScale;
                if (p.Y > _lastH + 20) { p.Y = -12; p.X = (float)(_rng.NextDouble() * _lastW); }
                if (p.X < -20) p.X = _lastW + 10;
                if (p.X > _lastW + 20) p.X = -10;
            }
            else if (eff is "embers" or "bubbles")
            {
                p.X += (float)Math.Sin(p.Phase) * 0.4f * dtScale;
                if (p.Y < -20) { p.Y = _lastH + 12; p.X = (float)(_rng.NextDouble() * _lastW); }
            }
            else
            {
                if (p.X < 0) { p.X = 0; p.Vx = Math.Abs(p.Vx); }
                else if (p.X > _lastW) { p.X = _lastW; p.Vx = -Math.Abs(p.Vx); }
                if (p.Y < 0) { p.Y = 0; p.Vy = Math.Abs(p.Vy); }
                else if (p.Y > _lastH) { p.Y = _lastH; p.Vy = -Math.Abs(p.Vy); }
            }
        }
    }

    private void SeedParticles(float w, float h)
    {
        _seededEffect = ParticleEffect;
        _particles.Clear();
        string eff = (ParticleEffect ?? "network").ToLowerInvariant();
        if (eff == "none") return;

        int count = eff switch
        {
            "network" => 36,
            "fading_stars" => 65,
            "snow" => 55,
            "matrix" => 42,
            "fireflies" => 38,
            "sakura" => 36,
            "embers" => 44,
            "bubbles" => 30,
            _ => 36
        };

        for (int i = 0; i < count; i++)
        {
            var n = new ParticleNode
            {
                X = (float)(_rng.NextDouble() * w),
                Y = (float)(_rng.NextDouble() * h),
                Phase = (float)(_rng.NextDouble() * Math.PI * 2),
                Speed = 0.02f + (float)(_rng.NextDouble() * 0.04f)
            };

            switch (eff)
            {
                case "network":
                    n.Vx = (float)(_rng.NextDouble() - 0.5) * 0.65f;
                    n.Vy = (float)(_rng.NextDouble() - 0.5) * 0.65f;
                    n.R = 2.2f + (float)_rng.NextDouble() * 1.4f;
                    n.Col = Color.FromRgba(34, 211, 238, 200);
                    break;
                case "fading_stars":
                    n.Vx = (float)(_rng.NextDouble() - 0.5) * 0.12f;
                    n.Vy = (float)(_rng.NextDouble() - 0.5) * 0.12f;
                    n.R = 1.2f + (float)_rng.NextDouble() * 2.0f;
                    n.Col = (i % 3 == 0) ? Color.FromRgba(253, 224, 71, 220) : Color.FromRgba(224, 242, 254, 220);
                    break;
                case "snow":
                    n.Vx = (float)(_rng.NextDouble() - 0.5) * 0.35f;
                    n.Vy = 0.8f + (float)_rng.NextDouble() * 1.4f;
                    n.R = 1.8f + (float)_rng.NextDouble() * 2.4f;
                    n.Col = Color.FromRgba(241, 245, 249, 210);
                    break;
                case "matrix":
                    n.Vx = 0;
                    n.Vy = 1.8f + (float)_rng.NextDouble() * 2.5f;
                    n.R = 11f;
                    n.Col = Color.FromRgba(16, 185, 129, 210);
                    break;
                case "fireflies":
                    n.Vx = (float)(_rng.NextDouble() - 0.5) * 0.5f;
                    n.Vy = (float)(_rng.NextDouble() - 0.5) * 0.5f;
                    n.R = 2.5f + (float)_rng.NextDouble() * 2.5f;
                    n.Col = Color.FromRgba(250, 204, 21, 220);
                    break;
                case "sakura":
                    n.Vx = 0.3f + (float)_rng.NextDouble() * 0.5f;
                    n.Vy = 0.7f + (float)_rng.NextDouble() * 1.1f;
                    n.R = 3.2f + (float)_rng.NextDouble() * 2.2f;
                    n.Col = Color.FromRgba(244, 114, 182, 200);
                    break;
                case "embers":
                    n.Vx = (float)(_rng.NextDouble() - 0.5) * 0.4f;
                    n.Vy = -(0.9f + (float)_rng.NextDouble() * 1.6f);
                    n.R = 1.8f + (float)_rng.NextDouble() * 2.2f;
                    n.Col = (i % 2 == 0) ? Color.FromRgba(249, 115, 22, 220) : Color.FromRgba(251, 191, 36, 220);
                    break;
                case "bubbles":
                    n.Vx = (float)(_rng.NextDouble() - 0.5) * 0.25f;
                    n.Vy = -(0.5f + (float)_rng.NextDouble() * 1.0f);
                    n.R = 4.0f + (float)_rng.NextDouble() * 6.0f;
                    n.Col = Color.FromRgba(56, 189, 248, 95);
                    break;
            }
            _particles.Add(n);
        }
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        float w = dirtyRect.Width;
        float h = dirtyRect.Height;
        if (w <= 1 || h <= 1) return;
        _lastW = w;
        _lastH = h;

        if (IsZenMode)
        {
            DrawZenWorld(canvas, w, h);
            return;
        }

        if (!IsDarkMode)
        {
            canvas.FillColor = Color.FromRgb(244, 246, 251);
            canvas.FillRectangle(0, 0, w, h);
            return;
        }

        // 1. Surface Backdrop Theme
        string theme = (BgTheme ?? "aurora").ToLowerInvariant();
        if (theme == "obsidian")
        {
            canvas.FillColor = Color.FromRgb(3, 4, 8);
            canvas.FillRectangle(0, 0, w, h);
        }
        else if (theme == "nebula")
        {
            canvas.FillColor = Color.FromRgb(4, 3, 10);
            canvas.FillRectangle(0, 0, w, h);
            DrawGlowOrb(canvas, w * 0.18f, h * 0.22f, Math.Min(w, h) * 0.45f, Color.FromRgba(124, 58, 237, 55));
            DrawGlowOrb(canvas, w * 0.82f, h * 0.30f, Math.Min(w, h) * 0.40f, Color.FromRgba(219, 39, 119, 45));
            DrawGlowOrb(canvas, w * 0.52f, h * 0.78f, Math.Min(w, h) * 0.48f, Color.FromRgba(14, 165, 233, 42));
        }
        else if (theme == "acrylic")
        {
            FillVerticalGradient(canvas, new RectF(0, 0, w, h),
                Color.FromRgb(10, 18, 36), Color.FromRgb(13, 27, 46), Color.FromRgb(8, 24, 38));
            DrawGlowOrb(canvas, w * 0.75f, h * 0.72f, Math.Min(w, h) * 0.42f, Color.FromRgba(20, 184, 166, 38));
        }
        else
        {
            FillVerticalGradient(canvas, new RectF(0, 0, w, h),
                Color.FromRgb(9, 11, 26), Color.FromRgb(13, 16, 37), Color.FromRgb(8, 10, 22));
        }

        // 2. Ambient Particle Engine
        string eff = (ParticleEffect ?? "network").ToLowerInvariant();
        if (eff == "none" || _particles.Count == 0) return;

        if (eff == "network")
        {
            float maxDist = 135f;
            float maxDistSq = maxDist * maxDist;
            canvas.StrokeSize = 1f;
            for (int i = 0; i < _particles.Count; i++)
            {
                var a = _particles[i];
                for (int j = i + 1; j < _particles.Count; j++)
                {
                    var b = _particles[j];
                    float dx = a.X - b.X;
                    float dy = a.Y - b.Y;
                    float d2 = dx * dx + dy * dy;
                    if (d2 < maxDistSq)
                    {
                        float alpha = (1f - (float)Math.Sqrt(d2) / maxDist) * 0.25f;
                        canvas.StrokeColor = Color.FromRgba(34, 211, 238, (int)(alpha * 255));
                        canvas.DrawLine(a.X, a.Y, b.X, b.Y);
                    }
                }
            }
        }

        for (int i = 0; i < _particles.Count; i++)
        {
            var p = _particles[i];
            float pulse = 0.55f + 0.45f * (float)Math.Sin(p.Phase);
            if (eff == "bubbles")
            {
                canvas.StrokeColor = Color.FromRgba(125, 211, 252, 130);
                canvas.StrokeSize = 1.2f;
                canvas.DrawCircle(p.X, p.Y, p.R);
            }
            else if (eff == "matrix")
            {
                canvas.FillColor = Color.FromRgba(16, 185, 129, (int)(190 * pulse));
                canvas.FillRectangle(p.X, p.Y, 3f, 11f);
            }
            else
            {
                canvas.FillColor = p.Col.WithAlpha(pulse * 0.85f);
                canvas.FillCircle(p.X, p.Y, p.R);
            }
        }
    }

    private static void FillVerticalGradient(ICanvas canvas, RectF rect, params Color[] stops)
    {
        if (stops.Length == 0) return;
        if (stops.Length == 1)
        {
            canvas.FillColor = stops[0];
            canvas.FillRectangle(rect);
            return;
        }

        canvas.SaveState();
        var gradStops = new PaintGradientStop[stops.Length];
        for (int i = 0; i < stops.Length; i++)
        {
            gradStops[i] = new PaintGradientStop(i / (float)(stops.Length - 1), stops[i]);
        }
        var paint = new LinearGradientPaint
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops = gradStops
        };
        canvas.SetFillPaint(paint, rect);
        canvas.FillRectangle(rect);
        canvas.RestoreState();
    }

    private static void DrawGlowOrb(ICanvas canvas, float cx, float cy, float radius, Color centerCol)
    {
        for (int ring = 5; ring >= 1; ring--)
        {
            float r = radius * (ring / 5f);
            float factor = (6 - ring) / 5f;
            canvas.FillColor = centerCol.WithAlpha(centerCol.Alpha * factor * 0.42f);
            canvas.FillCircle(cx, cy, r);
        }
    }

    private static string NormalizeSceneKey(string? raw)
    {
        string s = (raw ?? "north_pole").Trim().ToLowerInvariant();
        return s switch
        {
            "desert_dunes" => "desert",
            "shipwreck_cove" => "shipwreck",
            "" or "custom" => "north_pole",
            _ => s
        };
    }

    private void DrawZenWorld(ICanvas canvas, float w, float h)
    {
        string scene = NormalizeSceneKey(ZenScene);
        if (scene == "none")
        {
            canvas.FillColor = Color.FromRgb(6, 9, 19);
            canvas.FillRectangle(0, 0, w, h);
            return;
        }

        switch (scene)
        {
            case "solar_system":
                DrawSolarSystem(canvas, w, h);
                break;
            case "far_space":
                DrawFarSpace(canvas, w, h);
                break;
            case "north_pole":
                DrawNorthPole(canvas, w, h);
                break;
            case "moon_night":
                DrawMoonNight(canvas, w, h);
                break;
            case "cyber_city":
                DrawCyberCity(canvas, w, h);
                break;
            case "nuclear_wasteland":
                DrawNuclearWasteland(canvas, w, h);
                break;
            case "wasteland":
                DrawWasteland(canvas, w, h);
                break;
            case "city_wasteland":
                DrawCityWasteland(canvas, w, h);
                break;
            case "desert":
                DrawDesert(canvas, w, h);
                break;
            case "lake_forest":
                DrawLakeForest(canvas, w, h);
                break;
            case "shipwreck":
                DrawShipwreck(canvas, w, h);
                break;
            case "ocean_life":
                DrawOceanLife(canvas, w, h);
                break;
            case "snowy_peaks":
                DrawSnowyPeaks(canvas, w, h);
                break;
            case "city_skyline":
                DrawCityMetropolis(canvas, w, h, isShore: false);
                break;
            case "city_shoreline":
                DrawCityMetropolis(canvas, w, h, isShore: true);
                break;
            case "sunset":
                DrawSunset(canvas, w, h);
                break;
            case "rainy_forest":
                DrawRainyForest(canvas, w, h);
                break;
            default:
                DrawNorthPole(canvas, w, h);
                break;
        }
    }

    private void DrawStarfield(ICanvas canvas, float w, float maxH, int count, float alphaScale = 0.85f)
    {
        for (int i = 0; i < count; i++)
        {
            float sx = ((i * 197 + 43) % 1000) / 1000f * w;
            float sy = ((i * 353 + 19) % 1000) / 1000f * maxH;
            float tw = 0.35f + 0.65f * (float)Math.Sin(_t * (1.2 + (i % 5) * 0.3) + i);
            Color baseCol = (i % 6 == 0)
                ? Color.FromRgb(254, 243, 199)
                : (i % 4 == 0 ? Color.FromRgb(186, 230, 253) : Colors.White);
            canvas.FillColor = baseCol.WithAlpha(tw * alphaScale);
            canvas.FillCircle(sx, sy, (i % 5 == 0) ? 1.8f : 1.1f);
        }
    }

    // 1. SOLAR SYSTEM
    private void DrawSolarSystem(ICanvas canvas, float w, float h)
    {
        FillVerticalGradient(canvas, new RectF(0, 0, w, h),
            Color.FromRgb(2, 4, 12), Color.FromRgb(10, 14, 34), Color.FromRgb(3, 5, 14));
        DrawStarfield(canvas, w, h, 85, 0.85f);

        float cx = w * 0.5f, cy = h * 0.55f;
        float maxSpan = Math.Min(w * 0.45f, h * 0.42f);
        const float tilt = 0.48f;

        // Sun corona & glowing disc
        DrawGlowOrb(canvas, cx, cy, Math.Max(36f, maxSpan * 0.25f), Color.FromRgba(249, 115, 22, 180));
        canvas.FillColor = Color.FromRgb(254, 240, 138);
        canvas.FillCircle(cx, cy, Math.Max(14f, maxSpan * 0.095f));
        canvas.FillColor = Colors.White.WithAlpha(0.9f);
        canvas.FillCircle(cx - 3f, cy - 3f, Math.Max(7f, maxSpan * 0.045f));

        // Asteroid Belt between Mars (0.42) & Jupiter (0.58)
        for (int a = 0; a < 55; a++)
        {
            double ang = _t * 0.08 + a * (Math.PI * 2 / 55.0);
            float rx = maxSpan * (0.48f + (a % 5) * 0.014f);
            float ry = rx * tilt;
            canvas.FillColor = Color.FromRgba(148, 163, 184, 130);
            canvas.FillCircle(cx + (float)Math.Cos(ang) * rx, cy + (float)Math.Sin(ang) * ry, 1.3f);
        }

        // Kuiper Belt outer icy specks
        for (int k = 0; k < 45; k++)
        {
            double ang = -_t * 0.03 + k * (Math.PI * 2 / 45.0);
            float rx = maxSpan * (0.94f + (k % 4) * 0.025f);
            float ry = rx * tilt;
            canvas.FillColor = Color.FromRgba(186, 230, 253, 95);
            canvas.FillCircle(cx + (float)Math.Cos(ang) * rx, cy + (float)Math.Sin(ang) * ry, 1.1f);
        }

        (float orbit, float speed, float r, Color col, bool hasRing, int moons)[] planets =
        {
            (0.16f, 1.45f, 3.6f, Color.FromRgb(168, 162, 158), false, 0),  // Mercury
            (0.24f, 1.05f, 5.2f, Color.FromRgb(251, 191, 36), false, 0),   // Venus
            (0.33f, 0.80f, 5.8f, Color.FromRgb(56, 189, 248), false, 1),   // Earth
            (0.42f, 0.62f, 4.6f, Color.FromRgb(239, 68, 68), false, 1),    // Mars
            (0.59f, 0.36f, 10.5f, Color.FromRgb(251, 146, 60), false, 2),  // Jupiter
            (0.73f, 0.25f, 8.6f, Color.FromRgb(253, 224, 71), true, 1),    // Saturn
            (0.84f, 0.17f, 6.6f, Color.FromRgb(103, 232, 249), true, 0),   // Uranus
            (0.93f, 0.12f, 6.2f, Color.FromRgb(96, 165, 250), false, 1),   // Neptune
            (1.01f, 0.08f, 3.0f, Color.FromRgb(203, 213, 225), false, 0)   // Pluto
        };

        canvas.StrokeSize = 1f;
        for (int idx = 0; idx < planets.Length; idx++)
        {
            var pl = planets[idx];
            float rx = maxSpan * pl.orbit;
            float ry = rx * tilt;
            canvas.StrokeColor = Color.FromRgba(148, 163, 184, 42);
            canvas.DrawEllipse(cx - rx, cy - ry, rx * 2, ry * 2);

            double ang = _t * pl.speed + idx * 1.3;
            float px = cx + (float)Math.Cos(ang) * rx;
            float py = cy + (float)Math.Sin(ang) * ry;

            if (pl.hasRing)
            {
                canvas.StrokeColor = idx == 5 ? Color.FromRgba(253, 224, 71, 180) : Color.FromRgba(103, 232, 249, 130);
                canvas.StrokeSize = idx == 5 ? 2.4f : 1.3f;
                canvas.DrawEllipse(px - pl.r * 2.0f, py - pl.r * 0.62f, pl.r * 4.0f, pl.r * 1.24f);
            }

            canvas.FillColor = pl.col;
            canvas.FillCircle(px, py, pl.r);

            // Moons
            for (int m = 0; m < pl.moons; m++)
            {
                double mAng = _t * (2.6 + m) + m * 2.1;
                float mx = px + (float)Math.Cos(mAng) * (pl.r + 5f + m * 3f);
                float my = py + (float)Math.Sin(mAng) * (pl.r + 5f + m * 3f) * 0.6f;
                canvas.FillColor = Color.FromRgb(226, 232, 240);
                canvas.FillCircle(mx, my, 1.7f);
            }
        }

        // Comet sweeping past
        double cometPhase = (_t * 0.14) % 1.0;
        float cometX = w * (float)(1.1 - cometPhase * 1.2);
        float cometY = h * (0.28f + (float)Math.Sin(cometPhase * Math.PI) * 0.18f);
        canvas.StrokeColor = Color.FromRgba(165, 243, 252, 160);
        canvas.StrokeSize = 2.6f;
        canvas.DrawLine(cometX, cometY, cometX + 42f, cometY - 14f);
        canvas.FillColor = Colors.White;
        canvas.FillCircle(cometX, cometY, 3.2f);
    }

    // 2. FAR SPACE
    private void DrawFarSpace(ICanvas canvas, float w, float h)
    {
        FillVerticalGradient(canvas, new RectF(0, 0, w, h),
            Color.FromRgb(4, 2, 14), Color.FromRgb(13, 6, 36), Color.FromRgb(2, 1, 8));
        DrawStarfield(canvas, w, h, 95, 0.9f);

        // Drifting nebulae
        DrawGlowOrb(canvas, w * 0.22f + (float)Math.Sin(_t * 0.2) * 18f, h * 0.30f, Math.Min(w, h) * 0.32f, Color.FromRgba(168, 85, 247, 110));
        DrawGlowOrb(canvas, w * 0.74f + (float)Math.Cos(_t * 0.18) * 16f, h * 0.28f, Math.Min(w, h) * 0.34f, Color.FromRgba(236, 72, 153, 95));
        DrawGlowOrb(canvas, w * 0.54f, h * 0.72f, Math.Min(w, h) * 0.30f, Color.FromRgba(34, 211, 238, 90));

        // Central Quasar / Pulsar with bipolar jets
        float qx = w * 0.48f, qy = h * 0.48f;
        canvas.StrokeColor = Color.FromRgba(103, 232, 249, 130);
        canvas.StrokeSize = 2.4f;
        float jetAng = 0.42f + (float)Math.Sin(_t * 0.4) * 0.08f;
        float jdx = (float)Math.Cos(jetAng) * 75f, jdy = (float)Math.Sin(jetAng) * 75f;
        canvas.DrawLine(qx - jdx, qy - jdy, qx + jdx, qy + jdy);
        DrawGlowOrb(canvas, qx, qy, 26f, Color.FromRgba(56, 189, 248, 210));
        canvas.FillColor = Colors.White;
        canvas.FillCircle(qx, qy, 5f);

        // Spiral galaxies
        (float gx, float gy, float gr, Color col)[] galaxies =
        {
            (w * 0.76f, h * 0.30f, 58f, Color.FromRgb(244, 114, 182)),
            (w * 0.22f, h * 0.67f, 48f, Color.FromRgb(129, 140, 248)),
            (w * 0.80f, h * 0.76f, 36f, Color.FromRgb(52, 211, 153))
        };
        foreach (var g in galaxies)
        {
            canvas.FillColor = Colors.White.WithAlpha(0.9f);
            canvas.FillCircle(g.gx, g.gy, 5.5f);
            for (int arm = 0; arm < 2; arm++)
            {
                for (int s = 1; s < 26; s++)
                {
                    float dist = 5f + (s / 26f) * g.gr;
                    double a = s * 0.32 + arm * Math.PI + _t * 0.35;
                    float sx = g.gx + (float)Math.Cos(a) * dist;
                    float sy = g.gy + (float)Math.Sin(a) * dist * 0.54f;
                    canvas.FillColor = g.col.WithAlpha(0.8f * (1f - s / 30f));
                    canvas.FillCircle(sx, sy, 1.8f);
                }
            }
        }

        // Periodic Shooting Star
        double cycle = (_t * 0.28) % 2.0;
        if (cycle < 0.7)
        {
            float prog = (float)(cycle / 0.7);
            float sx = w * (0.85f - prog * 0.55f);
            float sy = h * (0.14f + prog * 0.28f);
            canvas.StrokeColor = Color.FromRgba(254, 249, 195, 220);
            canvas.StrokeSize = 2.0f;
            canvas.DrawLine(sx, sy, sx + 48f, sy - 24f);
            canvas.FillColor = Colors.White;
            canvas.FillCircle(sx, sy, 2.5f);
        }
    }

    // 3. NORTHERN LIGHTS (NORTH POLE)
    private void DrawNorthPole(ICanvas canvas, float w, float h)
    {
        float horizonY = h * 0.72f;
        FillVerticalGradient(canvas, new RectF(0, 0, w, horizonY),
            Color.FromRgb(2, 5, 16), Color.FromRgb(7, 22, 48), Color.FromRgb(13, 43, 77));
        DrawStarfield(canvas, w, horizonY * 0.85f, 65, 0.8f);

        // 3 Flowing Aurora Borealis Ribbons
        Color[] ribbonCols =
        {
            Color.FromRgba(16, 185, 129, 78),
            Color.FromRgba(34, 211, 238, 66),
            Color.FromRgba(168, 85, 247, 56)
        };
        for (int b = 0; b < 3; b++)
        {
            var path = new PathF();
            float baseY = h * (0.18f + b * 0.08f);
            path.MoveTo(0, baseY);
            for (int x = 0; x <= 14; x++)
            {
                float px = (x / 14f) * w;
                float py = baseY + (float)Math.Sin(_t * 0.9 + x * 0.45 + b * 1.2) * 26f;
                path.LineTo(px, py);
            }
            path.LineTo(w, baseY + 84f);
            path.LineTo(0, baseY + 84f);
            path.Close();
            canvas.FillColor = ribbonCols[b];
            canvas.FillPath(path);
        }

        // Sculpted Faceted Ice Mountains
        float[] peaks = { 0.12f, 0.34f, 0.49f, 0.65f, 0.86f };
        float[] heights = { 0.33f, 0.42f, 0.29f, 0.45f, 0.36f };
        for (int m = 0; m < peaks.Length; m++)
        {
            float px = peaks[m] * w;
            float ph = heights[m] * h;
            float halfBase = w * 0.20f;

            var left = new PathF();
            left.MoveTo(px, horizonY - ph);
            left.LineTo(px - halfBase, horizonY);
            left.LineTo(px + halfBase * 0.08f, horizonY);
            left.Close();
            canvas.FillColor = Color.FromRgb(186, 230, 253);
            canvas.FillPath(left);

            var right = new PathF();
            right.MoveTo(px, horizonY - ph);
            right.LineTo(px + halfBase * 0.08f, horizonY);
            right.LineTo(px + halfBase, horizonY);
            right.Close();
            canvas.FillColor = Color.FromRgb(23, 55, 102);
            canvas.FillPath(right);
        }

        // Polar Sea
        FillVerticalGradient(canvas, new RectF(0, horizonY, w, h - horizonY),
            Color.FromRgb(7, 28, 54), Color.FromRgb(10, 39, 71), Color.FromRgb(3, 9, 20));

        // Drifting Ice Floes
        canvas.FillColor = Color.FromRgba(224, 242, 254, 190);
        for (int f = 0; f < 8; f++)
        {
            float speed = (f % 2 == 0 ? 14f : -11f);
            float fx = (float)(((f * 150 + _t * speed) % (w + 140) + (w + 140)) % (w + 140)) - 70f;
            float fy = horizonY + 12f + (f / 8f) * (h - horizonY - 24f);
            float fw = 42f + (f % 3) * 24f;
            canvas.FillRoundedRectangle(fx, fy, fw, 7f, 3f);
        }

        // Gentle Snowfall
        canvas.FillColor = Color.FromRgba(240, 249, 255, 195);
        for (int s = 0; s < 48; s++)
        {
            float sx = (float)(((s * 79 + Math.Sin(_t * 1.1 + s) * 18) % w + w) % w);
            float sy = (float)(((s * 53 + _t * (38 + (s % 4) * 14)) % h));
            canvas.FillCircle(sx, sy, 1.3f + (s % 2) * 0.9f);
        }
    }

    // 4. MOON NIGHT
    private void DrawMoonNight(ICanvas canvas, float w, float h)
    {
        float horizonY = h * 0.70f;
        FillVerticalGradient(canvas, new RectF(0, 0, w, horizonY),
            Color.FromRgb(2, 6, 23), Color.FromRgb(15, 23, 42), Color.FromRgb(30, 41, 59));
        DrawStarfield(canvas, w, horizonY * 0.85f, 70, 0.85f);

        float mx = w * 0.74f, my = h * 0.24f, mr = Math.Min(w, h) * 0.085f;
        DrawGlowOrb(canvas, mx, my, mr * 2.6f, Color.FromRgba(224, 242, 254, 120));
        canvas.FillColor = Color.FromRgb(241, 245, 249);
        canvas.FillCircle(mx, my, mr);
        canvas.FillColor = Color.FromRgba(100, 116, 139, 85);
        canvas.FillCircle(mx - mr * 0.32f, my - mr * 0.18f, mr * 0.20f);
        canvas.FillCircle(mx + mr * 0.25f, my + mr * 0.28f, mr * 0.15f);

        // Drifting silver clouds
        canvas.FillColor = Color.FromRgba(148, 163, 184, 42);
        for (int c = 0; c < 5; c++)
        {
            float cx = (float)((c * 190 + _t * (8 + c * 2)) % (w + 220)) - 110f;
            float cy = h * (0.18f + c * 0.07f);
            canvas.FillRoundedRectangle(cx, cy, 130f + c * 15f, 18f, 9f);
        }

        // Rolling Hills + Pine Silhouette
        var hill = new PathF();
        hill.MoveTo(0, horizonY);
        for (float x = 0; x <= w; x += 20f)
            hill.LineTo(x, horizonY - 22f - (float)Math.Sin(x * 0.008) * 24f - (float)Math.Cos(x * 0.015) * 12f);
        hill.LineTo(w, horizonY);
        hill.Close();
        canvas.FillColor = Color.FromRgb(9, 16, 36);
        canvas.FillPath(hill);

        // Lake & Moon Reflection Column
        FillVerticalGradient(canvas, new RectF(0, horizonY, w, h - horizonY),
            Color.FromRgb(11, 21, 40), Color.FromRgb(2, 6, 23));
        canvas.FillColor = Color.FromRgba(226, 232, 240, 72);
        for (int r = 0; r < 10; r++)
        {
            float ry = horizonY + 10f + r * ((h - horizonY - 18f) / 10f);
            float rw = (mr * 1.6f) * (1f - r * 0.06f) + (float)Math.Sin(_t * 2.5 + r) * 10f;
            canvas.FillRoundedRectangle(mx - rw * 0.5f, ry, Math.Max(10f, rw), 3.5f, 2f);
        }

        // Glowing Fireflies near shore
        for (int f = 0; f < 16; f++)
        {
            float fx = ((f * 131 + 29) % 1000) / 1000f * w + (float)Math.Sin(_t + f) * 14f;
            float fy = horizonY - 15f + ((f * 89) % 100) / 100f * (h - horizonY + 10f);
            float a = 0.35f + 0.65f * (float)Math.Abs(Math.Sin(_t * 1.9 + f * 1.4));
            canvas.FillColor = Color.FromRgba(253, 224, 71, (int)(a * 210));
            canvas.FillCircle(fx, fy, 2.2f);
        }
    }

    // 5. CYBER CITY
    private void DrawCyberCity(ICanvas canvas, float w, float h)
    {
        float baseY = h * 0.78f;
        FillVerticalGradient(canvas, new RectF(0, 0, w, baseY),
            Color.FromRgb(7, 2, 20), Color.FromRgb(26, 9, 54), Color.FromRgb(76, 10, 118));
        DrawStarfield(canvas, w, baseY * 0.65f, 45, 0.7f);

        // Synthwave Striped Sun behind Skyline
        float sunX = w * 0.5f, sunY = baseY - h * 0.14f, sunR = Math.Min(w, h) * 0.16f;
        DrawGlowOrb(canvas, sunX, sunY, sunR * 1.9f, Color.FromRgba(236, 72, 153, 120));
        canvas.FillColor = Color.FromRgb(249, 115, 22);
        canvas.FillCircle(sunX, sunY, sunR);
        canvas.FillColor = Color.FromRgb(59, 7, 100);
        for (int s = 0; s < 5; s++)
        {
            float sy = sunY + sunR * (0.12f + s * 0.17f);
            canvas.FillRectangle(sunX - sunR - 2f, sy, sunR * 2 + 4f, 3f + s * 1.4f);
        }

        // Neon Cyberpunk Skyline Towers
        int cols = 16;
        float bw = w / cols;
        for (int i = 0; i < cols; i++)
        {
            float bh = h * 0.16f + ((i * 73 + 29) % 130);
            float bx = i * bw;
            float by = baseY - bh;
            canvas.FillColor = (i % 2 == 0) ? Color.FromRgb(8, 6, 22) : Color.FromRgb(14, 10, 36);
            canvas.FillRectangle(bx + 1f, by, bw - 2f, bh);

            Color neon = (i % 2 == 0) ? Color.FromRgb(236, 72, 153) : Color.FromRgb(34, 211, 238);
            canvas.StrokeColor = neon.WithAlpha(0.75f);
            canvas.StrokeSize = 1.4f;
            canvas.DrawRectangle(bx + 1f, by, bw - 2f, bh);

            // Neon windows
            canvas.FillColor = neon.WithAlpha(0.75f);
            for (float wy = by + 10f; wy < baseY - 12f; wy += 13f)
            {
                if (((int)(wy + i * 19) % 3) != 0)
                    canvas.FillRectangle(bx + 5f, wy, Math.Max(3f, bw - 10f), 3.5f);
            }
        }

        // Perspective Neon Grid Floor
        canvas.FillColor = Color.FromRgb(5, 2, 14);
        canvas.FillRectangle(0, baseY, w, h - baseY);
        canvas.StrokeColor = Color.FromRgba(34, 211, 238, 120);
        canvas.StrokeSize = 1.1f;
        for (int gx = -6; gx <= 6; gx++)
        {
            float topX = w * 0.5f + gx * (w * 0.045f);
            float botX = w * 0.5f + gx * (w * 0.16f);
            canvas.DrawLine(topX, baseY, botX, h);
        }
        for (int gy = 0; gy < 8; gy++)
        {
            float frac = (float)Math.Pow(gy / 7.0, 1.5);
            float y = baseY + frac * (h - baseY);
            canvas.DrawLine(0, y, w, y);
        }

        // Horizontal Speed Light Streaks
        for (int st = 0; st < 7; st++)
        {
            float sx = (float)(((st * 190 + _t * (140 + st * 22)) % (w + 160)) - 80f);
            float sy = baseY + 10f + (st * 14f) % Math.Max(20f, h - baseY - 14f);
            canvas.StrokeColor = (st % 2 == 0) ? Color.FromRgba(236, 72, 153, 210) : Color.FromRgba(34, 211, 238, 210);
            canvas.StrokeSize = 2.2f;
            canvas.DrawLine(sx, sy, sx + 55f, sy);
        }
    }

    // 6. NUCLEAR WASTELAND
    private void DrawNuclearWasteland(ICanvas canvas, float w, float h)
    {
        float horizonY = h * 0.68f;
        FillVerticalGradient(canvas, new RectF(0, 0, w, horizonY),
            Color.FromRgb(12, 20, 13), Color.FromRgb(42, 52, 18), Color.FromRgb(163, 166, 38));

        // Irradiated Sun & Toxic Green Glow
        float sx = w * 0.28f, sy = horizonY - h * 0.19f, sr = Math.Min(w, h) * 0.085f;
        DrawGlowOrb(canvas, sx, sy, sr * 2.8f, Color.FromRgba(163, 230, 53, 150));
        canvas.FillColor = Color.FromRgb(254, 240, 138);
        canvas.FillCircle(sx, sy, sr * 0.85f);

        // Broken Skyline Ruins
        canvas.FillColor = Color.FromRgb(26, 33, 19);
        (float rx, float rw, float rh)[] ruins =
        {
            (w * 0.05f, 26f, h * 0.14f),
            (w * 0.14f, 34f, h * 0.19f),
            (w * 0.56f, 32f, h * 0.16f),
            (w * 0.66f, 40f, h * 0.22f),
            (w * 0.86f, 36f, h * 0.18f)
        };
        foreach (var r in ruins)
        {
            canvas.FillRectangle(r.rx, horizonY - r.rh, r.rw, r.rh);
        }

        // Iconic Nuclear Hyperboloid Cooling Tower
        float ctwX = w * 0.36f, ctwH = h * 0.28f, ctwBaseW = Math.Min(110f, w * 0.24f);
        float ctwMidW = ctwBaseW * 0.58f, ctwTopW = ctwBaseW * 0.68f;
        var ct = new PathF();
        ct.MoveTo(ctwX - ctwBaseW * 0.5f, horizonY);
        ct.QuadTo(ctwX - ctwMidW * 0.5f, horizonY - ctwH * 0.55f, ctwX - ctwTopW * 0.5f, horizonY - ctwH);
        ct.LineTo(ctwX + ctwTopW * 0.5f, horizonY - ctwH);
        ct.QuadTo(ctwX + ctwMidW * 0.5f, horizonY - ctwH * 0.55f, ctwX + ctwBaseW * 0.5f, horizonY);
        ct.Close();
        canvas.FillColor = Color.FromRgb(20, 26, 15);
        canvas.FillPath(ct);

        // Hazard amber band on Cooling Tower
        canvas.StrokeColor = Color.FromRgba(234, 179, 8, 120);
        canvas.StrokeSize = 4f;
        canvas.DrawLine(ctwX - ctwMidW * 0.46f, horizonY - ctwH * 0.52f, ctwX + ctwMidW * 0.46f, horizonY - ctwH * 0.52f);

        // Rising radioactive steam plumes
        for (int p = 0; p < 5; p++)
        {
            float rise = (float)(((_t * 18 + p * 26) % 110));
            float py = horizonY - ctwH - rise;
            float px = ctwX + rise * 0.25f + (float)Math.Sin(_t + p) * 6f;
            canvas.FillColor = Color.FromRgba(190, 242, 100, (int)((1f - rise / 110f) * 60));
            canvas.FillCircle(px, py, 14f + rise * 0.16f);
        }

        // Scorched Earth Ground + Glowing Lime Radioactive Fissures
        FillVerticalGradient(canvas, new RectF(0, horizonY, w, h - horizonY),
            Color.FromRgb(18, 24, 13), Color.FromRgb(5, 7, 4));
        float pulse = 0.55f + 0.45f * (float)Math.Sin(_t * 2.2);
        canvas.StrokeColor = Color.FromRgba(163, 230, 53, (int)(170 * pulse));
        canvas.StrokeSize = 2.2f;
        canvas.DrawLine(w * 0.15f, horizonY + 24f, w * 0.42f, horizonY + 56f);
        canvas.DrawLine(w * 0.62f, horizonY + 20f, w * 0.85f, horizonY + 48f);

        // Drifting Glowing Fallout Embers
        for (int f = 0; f < 32; f++)
        {
            float fx = (float)(((f * 97 + _t * 24) % (w + 20)) - 10f);
            float fy = (float)(((f * 73 - _t * (14 + (f % 3) * 8)) % h + h) % h);
            canvas.FillColor = (f % 2 == 0) ? Color.FromRgba(163, 230, 53, 185) : Color.FromRgba(250, 204, 21, 160);
            canvas.FillCircle(fx, fy, 1.8f);
        }
    }

    // 7. WASTELAND
    private void DrawWasteland(ICanvas canvas, float w, float h)
    {
        float horizonY = h * 0.70f;
        FillVerticalGradient(canvas, new RectF(0, 0, w, horizonY),
            Color.FromRgb(28, 18, 9), Color.FromRgb(120, 65, 29), Color.FromRgb(226, 163, 106));

        float sx = w * 0.74f, sy = horizonY - h * 0.22f, sr = Math.Min(w, h) * 0.08f;
        DrawGlowOrb(canvas, sx, sy, sr * 2.8f, Color.FromRgba(245, 158, 11, 140));
        canvas.FillColor = Color.FromRgb(254, 243, 199);
        canvas.FillCircle(sx, sy, sr * 0.55f);

        // Canyon Mesas
        canvas.FillColor = Color.FromRgb(56, 26, 12);
        var m1 = new PathF();
        m1.MoveTo(w * 0.04f, horizonY);
        m1.LineTo(w * 0.10f, horizonY - 68f);
        m1.LineTo(w * 0.28f, horizonY - 68f);
        m1.LineTo(w * 0.36f, horizonY);
        m1.Close();
        canvas.FillPath(m1);

        var m2 = new PathF();
        m2.MoveTo(w * 0.54f, horizonY);
        m2.LineTo(w * 0.62f, horizonY - 82f);
        m2.LineTo(w * 0.84f, horizonY - 82f);
        m2.LineTo(w * 0.94f, horizonY);
        m2.Close();
        canvas.FillPath(m2);

        // Cracked Desert Ground
        FillVerticalGradient(canvas, new RectF(0, horizonY, w, h - horizonY),
            Color.FromRgb(38, 19, 8), Color.FromRgb(14, 7, 3));

        // Spinning Windmill Silhouette
        float wmx = w * 0.26f, wmy = horizonY + 8f, wmH = Math.Min(130f, h * 0.22f);
        canvas.StrokeColor = Color.FromRgb(21, 10, 4);
        canvas.StrokeSize = 3f;
        canvas.DrawLine(wmx - 18f, wmy, wmx - 4f, wmy - wmH);
        canvas.DrawLine(wmx + 18f, wmy, wmx + 4f, wmy - wmH);
        float hubY = wmy - wmH;
        canvas.FillColor = Color.FromRgb(21, 10, 4);
        canvas.FillCircle(wmx, hubY, 5f);
        for (int b = 0; b < 3; b++)
        {
            double ang = _t * 1.4 + b * (Math.PI * 2 / 3.0);
            canvas.DrawLine(wmx, hubY, wmx + (float)Math.Cos(ang) * 34f, hubY + (float)Math.Sin(ang) * 34f);
        }

        // Rolling Tumbleweed & Dust Streaks
        float twx = w - (float)((_t * 55) % (w + 60)) + 30f;
        float twy = horizonY + 26f + Math.Abs((float)Math.Sin(_t * 3.5)) * -10f;
        canvas.StrokeColor = Color.FromRgb(69, 38, 20);
        canvas.StrokeSize = 1.6f;
        canvas.DrawCircle(twx, twy, 13f);
        canvas.DrawCircle(twx - 2f, twy + 2f, 8f);

        canvas.StrokeColor = Color.FromRgba(245, 158, 11, 125);
        canvas.StrokeSize = 1.3f;
        for (int d = 0; d < 14; d++)
        {
            float dx = (float)(((d * 130 - _t * 95) % (w + 80) + (w + 80)) % (w + 80)) - 40f;
            float dy = horizonY - 20f + (d * 23f) % Math.Max(40f, h - horizonY + 20f);
            canvas.DrawLine(dx, dy, dx + 38f, dy + 4f);
        }
    }

    // 8. CITY WASTELAND (OVERGROWN RUINS)
    private void DrawCityWasteland(ICanvas canvas, float w, float h)
    {
        float horizonY = h * 0.72f;
        FillVerticalGradient(canvas, new RectF(0, 0, w, horizonY),
            Color.FromRgb(12, 22, 21), Color.FromRgb(30, 52, 48), Color.FromRgb(57, 87, 79));

        // Sunlight Beams through Overcast Sky
        canvas.FillColor = Color.FromRgba(167, 243, 208, 28);
        for (int r = 0; r < 3; r++)
        {
            float rx = w * (0.20f + r * 0.28f);
            var beam = new PathF();
            beam.MoveTo(rx - 22f, 0);
            beam.LineTo(rx + 28f, 0);
            beam.LineTo(rx + 120f, horizonY + 20f);
            beam.LineTo(rx - 40f, horizonY + 20f);
            beam.Close();
            canvas.FillPath(beam);
        }

        // Ruined Overgrown Skyscrapers + Ivy Vines
        for (int t = 0; t < 7; t++)
        {
            float tx = w * (0.04f + t * 0.14f);
            float tw = w * 0.11f;
            float th = h * (0.22f + (t % 3) * 0.08f);
            float ty = horizonY - th;
            canvas.FillColor = Color.FromRgb(20, 36, 33);
            canvas.FillRectangle(tx, ty, tw, th);

            // Creeping Emerald Vines
            canvas.FillColor = Color.FromRgb(22, 101, 52);
            for (int v = 0; v < 3; v++)
            {
                float vx = tx + 6f + v * (tw * 0.30f);
                float vlen = 25f + ((t + v) % 4) * 18f;
                canvas.FillRoundedRectangle(vx, ty + 6f, 4f, vlen, 2f);
            }
            if (t % 2 == 0)
            {
                canvas.FillColor = Color.FromRgb(20, 83, 45);
                canvas.FillCircle(tx + tw * 0.5f, ty - 6f, 13f);
            }
        }

        // Ground & Expanding Puddle Ripples
        FillVerticalGradient(canvas, new RectF(0, horizonY, w, h - horizonY),
            Color.FromRgb(16, 28, 25), Color.FromRgb(4, 7, 6));
        float pudX = w * 0.42f, pudY = horizonY + (h - horizonY) * 0.45f;
        canvas.FillColor = Color.FromRgb(26, 48, 44);
        canvas.FillEllipse(pudX - 55f, pudY - 14f, 110f, 28f);
        float rip = (float)((_t * 18) % 36);
        canvas.StrokeColor = Color.FromRgba(167, 243, 208, (int)((1f - rip / 36f) * 160));
        canvas.StrokeSize = 1.2f;
        canvas.DrawEllipse(pudX - rip, pudY - rip * 0.35f, rip * 2f, rip * 0.7f);

        // Drifting Leaves & Green Spores
        for (int l = 0; l < 20; l++)
        {
            float lx = (float)(((l * 113 + _t * 28 + Math.Sin(_t * 1.5 + l) * 12) % (w + 20) + (w + 20)) % (w + 20)) - 10f;
            float ly = (float)(((l * 83 + _t * (16 + l % 4 * 5)) % h));
            canvas.FillColor = (l % 2 == 0) ? Color.FromRgba(34, 197, 94, 175) : Color.FromRgba(167, 243, 208, 165);
            canvas.FillCircle(lx, ly, 2.3f);
        }
    }

    // 9. DESERT DUNES
    private void DrawDesert(ICanvas canvas, float w, float h)
    {
        float horizonY = h * 0.58f;
        FillVerticalGradient(canvas, new RectF(0, 0, w, horizonY),
            Color.FromRgb(15, 23, 42), Color.FromRgb(59, 7, 100), Color.FromRgb(234, 88, 12), Color.FromRgb(253, 224, 71));

        float sunX = w * 0.70f, sunY = horizonY - h * 0.11f, sunR = Math.Min(w, h) * 0.088f;
        DrawGlowOrb(canvas, sunX, sunY, sunR * 2.5f, Color.FromRgba(251, 191, 36, 150));
        canvas.FillColor = Color.FromRgb(255, 251, 235);
        canvas.FillCircle(sunX, sunY, sunR);

        // Layered Rolling Sand Dunes
        var d1 = new PathF();
        d1.MoveTo(0, h);
        d1.LineTo(0, horizonY + 6f);
        d1.CurveTo(w * 0.25f, horizonY + 42f, w * 0.60f, horizonY - 28f, w, horizonY + 20f);
        d1.LineTo(w, h);
        d1.Close();
        canvas.FillColor = Color.FromRgb(180, 83, 9);
        canvas.FillPath(d1);

        float d2Y = horizonY + h * 0.14f;
        var d2 = new PathF();
        d2.MoveTo(0, h);
        d2.LineTo(0, d2Y + 30f);
        d2.CurveTo(w * 0.26f, d2Y - 22f, w * 0.64f, d2Y + 35f, w, d2Y - 12f);
        d2.LineTo(w, h);
        d2.Close();
        canvas.FillColor = Color.FromRgb(245, 158, 11);
        canvas.FillPath(d2);

        float d3Y = horizonY + h * 0.26f;
        var d3 = new PathF();
        d3.MoveTo(0, h);
        d3.LineTo(0, d3Y + 50f);
        d3.CurveTo(w * 0.35f, d3Y - 25f, w * 0.72f, d3Y + 18f, w, d3Y + 48f);
        d3.LineTo(w, h);
        d3.Close();
        canvas.FillColor = Color.FromRgb(217, 119, 6);
        canvas.FillPath(d3);

        // Blowing golden sand streaks
        canvas.StrokeColor = Color.FromRgba(254, 240, 138, 110);
        canvas.StrokeSize = 1.4f;
        for (int s = 0; s < 14; s++)
        {
            float sx = (float)(((s * 140 + _t * 95) % (w + 90)) - 45f);
            float sy = horizonY + 25f + (s * 24f) % Math.Max(30f, h - horizonY - 25f);
            canvas.DrawLine(sx, sy, sx + 48f, sy - 4f);
        }
    }

    // 10. LAKE FOREST
    private void DrawLakeForest(ICanvas canvas, float w, float h)
    {
        float horizonY = h * 0.58f;
        FillVerticalGradient(canvas, new RectF(0, 0, w, horizonY),
            Color.FromRgb(4, 16, 30), Color.FromRgb(15, 41, 66), Color.FromRgb(19, 78, 74));
        DrawStarfield(canvas, w, horizonY * 0.8f, 55, 0.75f);

        float mx = w * 0.68f, my = horizonY * 0.36f, mr = Math.Min(w, h) * 0.07f;
        DrawGlowOrb(canvas, mx, my, mr * 2.2f, Color.FromRgba(204, 251, 241, 95));
        canvas.FillColor = Color.FromRgb(240, 253, 250);
        canvas.FillCircle(mx, my, mr);

        // Mountain & Jagged Pine Treeline
        canvas.FillColor = Color.FromRgb(12, 39, 56);
        var mt = new PathF();
        mt.MoveTo(0, horizonY);
        for (float x = 0; x <= w; x += 24f)
            mt.LineTo(x, horizonY - Math.Abs((float)Math.Sin(x * 0.008) * (h * 0.14f) + (float)Math.Cos(x * 0.018) * (h * 0.05f)));
        mt.LineTo(w, horizonY);
        mt.Close();
        canvas.FillPath(mt);

        canvas.FillColor = Color.FromRgb(6, 28, 36);
        for (float px = 0; px < w + 18f; px += 16f)
        {
            float th = 22f + Math.Abs((float)Math.Sin(px * 0.21)) * 34f;
            var pine = new PathF();
            pine.MoveTo(px - 8f, horizonY);
            pine.LineTo(px, horizonY - th);
            pine.LineTo(px + 8f, horizonY);
            pine.Close();
            canvas.FillPath(pine);
        }

        // Tranquil Alpine Lake + Ripples & Moon Column
        FillVerticalGradient(canvas, new RectF(0, horizonY, w, h - horizonY),
            Color.FromRgb(15, 61, 62), Color.FromRgb(8, 32, 50), Color.FromRgb(3, 11, 20));
        canvas.FillColor = Color.FromRgba(167, 243, 208, 52);
        for (int r = 0; r < 10; r++)
        {
            float ry = horizonY + 12f + r * ((h - horizonY - 20f) / 10f);
            float rw = 36f + (float)Math.Sin(_t * 1.8 + r) * 16f;
            canvas.FillRoundedRectangle(mx - rw * 0.5f, ry, rw, 3f, 1.5f);
        }

        // Fireflies
        for (int f = 0; f < 18; f++)
        {
            float fx = ((f * 137 + 41) % 1000) / 1000f * w + (float)Math.Sin(_t * 1.2 + f) * 12f;
            float fy = h * 0.45f + ((f * 71) % 100) / 100f * (h * 0.48f);
            float a = 0.3f + 0.7f * (float)Math.Abs(Math.Sin(_t * 2.1 + f));
            canvas.FillColor = Color.FromRgba(167, 243, 208, (int)(a * 210));
            canvas.FillCircle(fx, fy, 2.1f);
        }
    }

    // 11. SHIPWRECK COVE
    private void DrawShipwreck(ICanvas canvas, float w, float h)
    {
        FillVerticalGradient(canvas, new RectF(0, 0, w, h),
            Color.FromRgb(10, 61, 77), Color.FromRgb(6, 36, 48), Color.FromRgb(3, 18, 25));

        // Animated Underwater Sunbeams
        for (int b = 0; b < 4; b++)
        {
            float bx = w * (0.14f + b * 0.24f) + (float)Math.Sin(_t * 0.4 + b) * 14f;
            var beam = new PathF();
            beam.MoveTo(bx - 16f, 0);
            beam.LineTo(bx + 26f, 0);
            beam.LineTo(bx + 110f, h * 0.85f);
            beam.LineTo(bx - 55f, h * 0.85f);
            beam.Close();
            canvas.FillColor = Color.FromRgba(103, 232, 249, 26);
            canvas.FillPath(beam);
        }

        // Sandy Seabed
        float floorY = h * 0.76f;
        var sand = new PathF();
        sand.MoveTo(0, h);
        for (float x = 0; x <= w; x += 24f)
            sand.LineTo(x, floorY + (float)Math.Sin(x * 0.012) * 12f);
        sand.LineTo(w, h);
        sand.Close();
        canvas.FillColor = Color.FromRgb(9, 43, 56);
        canvas.FillPath(sand);

        // Sunken Galleon Ship Silhouette (Tilted hull + exposed ribs + masts + tattered sail)
        float shX = w * 0.50f, shY = floorY - 6f;
        float scale = Math.Clamp(w / 460f, 0.65f, 1.35f);
        var hull = new PathF();
        hull.MoveTo(shX - 125f * scale, shY - 8f);
        hull.LineTo(shX - 140f * scale, shY - 56f * scale);
        hull.LineTo(shX + 125f * scale, shY - 46f * scale);
        hull.LineTo(shX + 95f * scale, shY - 4f);
        hull.Close();
        canvas.FillColor = Color.FromRgb(13, 37, 50);
        canvas.FillPath(hull);

        canvas.StrokeColor = Color.FromRgba(103, 232, 249, 130);
        canvas.StrokeSize = 3.2f;
        // Masts & Bowsprit
        canvas.DrawLine(shX - 45f * scale, shY - 48f * scale, shX - 52f * scale, shY - 160f * scale);
        canvas.DrawLine(shX + 45f * scale, shY - 46f * scale, shX + 52f * scale, shY - 140f * scale);
        canvas.DrawLine(shX + 125f * scale, shY - 46f * scale, shX + 175f * scale, shY - 76f * scale);

        // Swaying Kelp Forest
        canvas.StrokeColor = Color.FromRgb(7, 56, 68);
        canvas.StrokeSize = 4f;
        for (int k = 0; k < 9; k++)
        {
            float kx = w * (0.06f + k * 0.11f);
            float kh = 55f + (k % 3) * 28f;
            float sway = (float)Math.Sin(_t * 1.4 + k) * 10f;
            canvas.DrawLine(kx, floorY + 12f, kx + sway, floorY + 12f - kh);
        }

        // Rising Air Bubbles
        canvas.StrokeColor = Color.FromRgba(186, 230, 253, 165);
        canvas.StrokeSize = 1.2f;
        for (int bub = 0; bub < 22; bub++)
        {
            float bx = ((bub * 119 + 33) % 1000) / 1000f * w + (float)Math.Sin(_t * 2 + bub) * 8f;
            float by = h - (float)(((bub * 67 + _t * (38 + bub % 4 * 9)) % (h + 20)));
            canvas.DrawCircle(bx, by, 2.2f + (bub % 3) * 1.4f);
        }
    }

    // 12. OCEAN LIFE
    private void DrawOceanLife(ICanvas canvas, float w, float h)
    {
        FillVerticalGradient(canvas, new RectF(0, 0, w, h),
            Color.FromRgb(2, 132, 199), Color.FromRgb(3, 105, 161), Color.FromRgb(8, 47, 73));

        // Sunbeams from surface
        for (int b = 0; b < 4; b++)
        {
            float bx = w * (0.16f + b * 0.22f) + (float)Math.Sin(_t * 0.5 + b) * 16f;
            var beam = new PathF();
            beam.MoveTo(bx - 12f, 0);
            beam.LineTo(bx + 22f, 0);
            beam.LineTo(bx + 75f, h * 0.78f);
            beam.LineTo(bx - 35f, h * 0.78f);
            beam.Close();
            canvas.FillColor = Color.FromRgba(186, 230, 253, 32);
            canvas.FillPath(beam);
        }

        // Coral Reef Floor Silhouette
        var reef = new PathF();
        reef.MoveTo(0, h);
        for (float x = 0; x <= w; x += 22f)
        {
            float ch = 22f + Math.Abs((float)Math.Sin(x * 0.04)) * 34f + Math.Abs((float)Math.Cos(x * 0.11)) * 16f;
            reef.LineTo(x, h - ch);
        }
        reef.LineTo(w, h);
        reef.Close();
        canvas.FillColor = Color.FromRgb(5, 30, 48);
        canvas.FillPath(reef);

        // Swimming Schools of Fish
        for (int f = 0; f < 14; f++)
        {
            int dir = (f % 2 == 0) ? 1 : -1;
            float speed = (28f + (f % 4) * 12f) * dir;
            float fx = (float)(((f * 130 + _t * speed) % (w + 60) + (w + 60)) % (w + 60)) - 30f;
            float fy = h * (0.24f + (f % 6) * 0.09f) + (float)Math.Sin(_t * 2 + f) * 8f;
            Color fc = (f % 3 == 0) ? Color.FromRgb(251, 146, 60) : (f % 3 == 1 ? Color.FromRgb(250, 204, 21) : Color.FromRgb(103, 232, 249));
            canvas.FillColor = fc;
            canvas.FillEllipse(fx - 9f, fy - 4.5f, 18f, 9f);
            float tailX = fx - dir * 10f;
            var tail = new PathF();
            tail.MoveTo(fx - dir * 6f, fy);
            tail.LineTo(tailX - dir * 5f, fy - 4.5f);
            tail.LineTo(tailX - dir * 5f, fy + 4.5f);
            tail.Close();
            canvas.FillPath(tail);
        }

        // Bubbles & Bioluminescent Plankton
        canvas.StrokeColor = Color.FromRgba(224, 242, 254, 140);
        canvas.StrokeSize = 1.2f;
        for (int b = 0; b < 20; b++)
        {
            float bx = ((b * 97 + 19) % 1000) / 1000f * w;
            float by = h - (float)(((b * 71 + _t * 42) % (h + 20)));
            canvas.DrawCircle(bx, by, 2.0f + (b % 3));
        }
    }

    // 13. SNOWY MOUNTAIN TOPS
    private void DrawSnowyPeaks(ICanvas canvas, float w, float h)
    {
        float baseY = h * 0.82f;
        FillVerticalGradient(canvas, new RectF(0, 0, w, h),
            Color.FromRgb(9, 21, 43), Color.FromRgb(30, 58, 138), Color.FromRgb(147, 197, 253));
        DrawStarfield(canvas, w, h * 0.45f, 40, 0.7f);

        float[] cx = { w * 0.16f, w * 0.46f, w * 0.78f, w * 0.62f };
        float[] ph = { h * 0.44f, h * 0.54f, h * 0.48f, h * 0.38f };
        float[] bw = { w * 0.36f, w * 0.44f, w * 0.40f, w * 0.32f };

        for (int i = 0; i < cx.Length; i++)
        {
            float topY = baseY - ph[i];
            float ridgeX = cx[i] + bw[i] * 0.05f;
            var lit = new PathF();
            lit.MoveTo(cx[i], topY);
            lit.LineTo(cx[i] - bw[i] * 0.5f, baseY);
            lit.LineTo(ridgeX, baseY);
            lit.Close();
            canvas.FillColor = Color.FromRgb(248, 250, 252);
            canvas.FillPath(lit);

            var sh = new PathF();
            sh.MoveTo(cx[i], topY);
            sh.LineTo(ridgeX, baseY);
            sh.LineTo(cx[i] + bw[i] * 0.5f, baseY);
            sh.Close();
            canvas.FillColor = Color.FromRgb(29, 78, 216);
            canvas.FillPath(sh);
        }

        FillVerticalGradient(canvas, new RectF(0, baseY, w, h - baseY),
            Color.FromRgb(226, 232, 240), Color.FromRgb(148, 163, 184));

        // Snow Flurries
        canvas.FillColor = Colors.White.WithAlpha(0.85f);
        for (int s = 0; s < 55; s++)
        {
            float sx = (float)(((s * 83 + _t * 26 + Math.Sin(_t + s) * 14) % (w + 10) + (w + 10)) % (w + 10)) - 5f;
            float sy = (float)(((s * 61 + _t * (42 + s % 4 * 12)) % (h + 10)));
            canvas.FillCircle(sx, sy, 1.4f + (s % 3) * 0.8f);
        }
    }

    // 14 & 15. CITY SKYLINE & CITY SHORELINE
    private void DrawCityMetropolis(ICanvas canvas, float w, float h, bool isShore)
    {
        float baseY = isShore ? h * 0.62f : h * 0.84f;
        FillVerticalGradient(canvas, new RectF(0, 0, w, baseY),
            Color.FromRgb(4, 6, 19), Color.FromRgb(14, 23, 56), isShore ? Color.FromRgb(38, 24, 74) : Color.FromRgb(30, 27, 75));
        DrawStarfield(canvas, w, baseY * 0.65f, 55, 0.75f);

        int cols = 18;
        float bw = w / cols;
        for (int i = 0; i < cols; i++)
        {
            float bh = h * 0.14f + ((i * 67 + 31) % (int)(h * 0.28f));
            float bx = i * bw;
            float by = baseY - bh;
            canvas.FillColor = (i % 2 == 0) ? Color.FromRgb(9, 13, 30) : Color.FromRgb(14, 21, 46);
            canvas.FillRectangle(bx + 1f, by, bw - 2f, bh);

            // Rooftop spire + blinking aviation beacon
            if (i % 4 == 1)
            {
                canvas.StrokeColor = Color.FromRgb(22, 32, 68);
                canvas.StrokeSize = 1.6f;
                canvas.DrawLine(bx + bw * 0.5f, by, bx + bw * 0.5f, by - 20f);
                float blink = 0.25f + 0.75f * (float)Math.Abs(Math.Sin(_t * 2.4 + i));
                canvas.FillColor = Color.FromRgba(244, 63, 94, (int)(blink * 240));
                canvas.FillCircle(bx + bw * 0.5f, by - 21f, 2.4f);
            }

            // Lit gold & cyan windows
            for (float wy = by + 8f; wy < baseY - 8f; wy += 11f)
            {
                if (((int)(wy * 3 + i * 11) % 3) != 0)
                {
                    canvas.FillColor = ((int)(wy + i) % 2 == 0)
                        ? Color.FromRgba(254, 240, 138, 175)
                        : Color.FromRgba(56, 189, 248, 150);
                    canvas.FillRectangle(bx + 4f, wy, Math.Max(3f, bw - 8f), 3.5f);
                }
            }
        }

        if (isShore)
        {
            FillVerticalGradient(canvas, new RectF(0, baseY, w, h - baseY),
                Color.FromRgb(9, 19, 44), Color.FromRgb(5, 11, 26), Color.FromRgb(2, 4, 10));
            // Shimmering Vertical Neon Reflections on Harbor Water
            for (int r = 0; r < 16; r++)
            {
                float rx = (r + 0.4f) * (w / 16f) + (float)Math.Sin(_t * 2.2 + r) * 3f;
                float rlen = 24f + (r % 4) * 18f;
                canvas.FillColor = (r % 2 == 0) ? Color.FromRgba(253, 224, 71, 80) : Color.FromRgba(56, 189, 248, 85);
                canvas.FillRoundedRectangle(rx, baseY + 6f + (r % 3) * 12f, 5f, rlen, 2f);
            }
        }
        else
        {
            canvas.FillColor = Color.FromRgb(2, 4, 10);
            canvas.FillRectangle(0, baseY, w, h - baseY);
        }
    }

    // 16. SUNSET
    private void DrawSunset(ICanvas canvas, float w, float h)
    {
        float horY = h * 0.67f;
        FillVerticalGradient(canvas, new RectF(0, 0, w, horY),
            Color.FromRgb(26, 11, 46), Color.FromRgb(190, 24, 93), Color.FromRgb(249, 115, 22), Color.FromRgb(253, 224, 71));

        float sx = w * 0.5f, sy = horY - h * 0.06f, sr = Math.Min(w, h) * 0.13f;
        DrawGlowOrb(canvas, sx, sy, sr * 2.5f, Color.FromRgba(251, 146, 60, 140));
        canvas.FillColor = Color.FromRgb(254, 240, 138);
        canvas.FillCircle(sx, sy, sr);

        // Layered Silhouette Ridges
        var ridge = new PathF();
        ridge.MoveTo(0, horY);
        for (float x = 0; x <= w; x += 18f)
            ridge.LineTo(x, horY - Math.Abs((float)Math.Sin(x * 0.004) * (h * 0.12f) + (float)Math.Cos(x * 0.008) * (h * 0.05f)));
        ridge.LineTo(w, horY);
        ridge.Close();
        canvas.FillColor = Color.FromRgb(46, 8, 44);
        canvas.FillPath(ridge);

        // Twilight Ocean + Golden Shimmer Column
        FillVerticalGradient(canvas, new RectF(0, horY, w, h - horY),
            Color.FromRgb(76, 17, 59), Color.FromRgb(38, 8, 41), Color.FromRgb(13, 3, 20));
        for (int r = 0; r < 11; r++)
        {
            float ry = horY + 10f + r * ((h - horY - 18f) / 11f);
            float rw = (sr * 1.9f) * (1f - r * 0.05f) + (float)Math.Sin(_t * 2.4 + r) * 14f;
            canvas.FillColor = (r % 2 == 0) ? Color.FromRgba(254, 240, 138, 135) : Color.FromRgba(251, 146, 60, 100);
            canvas.FillRoundedRectangle(sx - rw * 0.5f, ry, Math.Max(14f, rw), 4f, 2f);
        }

        // Flock of Gliding Birds
        canvas.StrokeColor = Color.FromRgb(31, 5, 33);
        canvas.StrokeSize = 1.9f;
        for (int b = 0; b < 7; b++)
        {
            float bx = (float)(((b * 68 + _t * 36) % (w + 80)) - 40f);
            float by = h * (0.24f + (b % 4) * 0.045f) + (float)Math.Sin(_t + b) * 6f;
            float flap = (float)Math.Sin(_t * 7.0 + b) * 4.5f;
            canvas.DrawLine(bx - 8f, by - flap, bx, by);
            canvas.DrawLine(bx, by, bx + 8f, by - flap);
        }
    }

    // 17. RAINY FOREST
    private void DrawRainyForest(ICanvas canvas, float w, float h)
    {
        FillVerticalGradient(canvas, new RectF(0, 0, w, h),
            Color.FromRgb(5, 18, 22), Color.FromRgb(13, 37, 44), Color.FromRgb(5, 19, 23));

        // 3 Depth Layers of Evergreen Pine Woods
        (float baseY, float treeH, float step, Color col)[] layers =
        {
            (h * 0.64f, h * 0.22f, 34f, Color.FromRgb(22, 53, 61)),
            (h * 0.78f, h * 0.27f, 44f, Color.FromRgb(13, 40, 48)),
            (h * 0.94f, h * 0.32f, 56f, Color.FromRgb(5, 22, 26))
        };

        foreach (var L in layers)
        {
            canvas.FillColor = L.col;
            canvas.FillRectangle(0, L.baseY, w, h - L.baseY);
            for (float x = -10f; x <= w + 30f; x += L.step)
            {
                float th = L.treeH * (0.85f + 0.2f * (float)Math.Sin(x * 0.13));
                var tree = new PathF();
                tree.MoveTo(x - L.step * 0.65f, L.baseY);
                tree.LineTo(x, L.baseY - th);
                tree.LineTo(x + L.step * 0.65f, L.baseY);
                tree.Close();
                canvas.FillPath(tree);
            }
        }

        // Drifting Mist Fog Band
        float mistX = (float)(Math.Sin(_t * 0.25) * 40);
        canvas.FillColor = Color.FromRgba(204, 251, 241, 28);
        canvas.FillEllipse(mistX - 40f, h * 0.66f, w + 80f, 65f);

        // Slanted Falling Rain Streaks + Ground Splash Ripples
        canvas.StrokeColor = Color.FromRgba(186, 230, 253, 125);
        canvas.StrokeSize = 1.25f;
        for (int r = 0; r < 65; r++)
        {
            float spd = 340f + (r % 4) * 75f;
            float ry = (float)(((r * 59 + _t * spd) % (h + 40))) - 20f;
            float rx = (float)(((r * 97 - _t * spd * 0.15f) % w + w) % w);
            canvas.DrawLine(rx, ry, rx - 3.5f, ry + 18f);
        }
    }
}
