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
        public int Seed;
        public Color Col = Colors.Cyan;
    }

    private readonly List<ParticleNode> _particles = new();
    private string _seededEffect = "";
    private float _seedW = 0, _seedH = 0;
    private float _pointerX = -9999f, _pointerY = -9999f;
    private DateTime _pointerUtc = DateTime.MinValue;

    public void SetPointerPosition(float x, float y, bool active)
    {
        if (active)
        {
            _pointerX = x;
            _pointerY = y;
            _pointerUtc = DateTime.UtcNow;
        }
        else
        {
            _pointerUtc = DateTime.MinValue;
        }
    }

    private static readonly string[] MatrixGlyphs =
    {
        "0", "1", "7", "9", "A", "Z", "X", "Ω", "λ", "Σ", "Δ", "π",
        "ｱ", "ｲ", "ｳ", "ｴ", "ｵ", "ｶ", "ｷ", "ｸ", "ｹ", "ｺ", "ｻ", "ｼ", "ｽ", "ｾ", "ｿ"
    };

    private static string NormalizeParticleKey(string? raw)
    {
        string eff = (raw ?? "network").Trim().ToLowerInvariant();
        return eff switch
        {
            "" => "network",
            "off" or "none" or "disable" => "disable",
            _ => eff
        };
    }

    public void Step(double dt)
    {
        if (dt <= 0 || dt > 0.25) dt = 1.0 / 60.0;
        _t += dt;

        if (_lastW <= 10 || _lastH <= 10) return;

        string eff = NormalizeParticleKey(ParticleEffect);
        if (_seededEffect != eff || _particles.Count == 0 ||
            Math.Abs(_lastW - _seedW) > 16f || Math.Abs(_lastH - _seedH) > 16f)
        {
            SeedParticles(_lastW, _lastH);
        }

        if (eff == "disable") return;

        bool pointerActive = eff == "network"
            && (DateTime.UtcNow - _pointerUtc).TotalSeconds < 6.0
            && _pointerX >= 0 && _pointerY >= 0
            && _pointerX <= _lastW && _pointerY <= _lastH;

        float dtScale = (float)(dt * 60.0);
        for (int i = 0; i < _particles.Count; i++)
        {
            var p = _particles[i];
            p.Phase += p.Speed * dtScale;
            p.X += p.Vx * dtScale;
            p.Y += p.Vy * dtScale;

            if (pointerActive)
            {
                float mdx = p.X - _pointerX;
                float mdy = p.Y - _pointerY;
                float mdSq = mdx * mdx + mdy * mdy;
                if (mdSq > 1f && mdSq < 21025f) // < 145px
                {
                    float md = (float)Math.Sqrt(mdSq);
                    if (md < 52f)
                    {
                        float push = (1f - md / 52f) * 0.42f * dtScale;
                        p.X += (mdx / md) * push;
                        p.Y += (mdy / md) * push;
                    }
                    else
                    {
                        float pull = (1f - (md - 52f) / 93f) * 0.14f * dtScale;
                        p.X -= (mdx / md) * pull;
                        p.Y -= (mdy / md) * pull;
                    }
                }
            }

            if (eff is "snow" or "sakura" or "petals_snowfall")
            {
                p.X += (float)Math.Sin(p.Phase) * 0.45f * dtScale;
                if (p.Y > _lastH + 20) { p.Y = -12; p.X = (float)(_rng.NextDouble() * _lastW); }
                if (p.X < -20) p.X = _lastW + 10;
                if (p.X > _lastW + 20) p.X = -10;
            }
            else if (eff == "fireflies")
            {
                p.X += (float)Math.Sin(p.Phase * 0.7 + p.Seed) * 0.35f * dtScale;
                p.Y += (float)Math.Cos(p.Phase * 0.55 + p.Seed * 0.3) * 0.25f * dtScale;
                if (p.X < -10) p.X = _lastW + 10;
                if (p.X > _lastW + 10) p.X = -10;
                if (p.Y < -10) p.Y = _lastH + 10;
                if (p.Y > _lastH + 10) p.Y = -10;
            }
            else if (eff == "circuit_trace")
            {
                // Advance packet progress p.Phase [0..1] along stored PCB route
                p.Phase = (p.Phase + p.Speed * dtScale) % 1.0f;
            }
            else if (eff == "plasma_lightning")
            {
                if (p.Phase <= 0f)
                {
                    // Repick an edge/grid anchor & arc angle periodically
                    int edge = _rng.Next(4);
                    p.X = edge switch
                    {
                        0 => (float)(_rng.NextDouble() * _lastW),
                        1 => (float)(_rng.NextDouble() * _lastW),
                        2 => 4f + (float)(_rng.NextDouble() * (_lastW * 0.22f)),
                        _ => _lastW - 4f - (float)(_rng.NextDouble() * (_lastW * 0.22f))
                    };
                    p.Y = edge switch
                    {
                        0 => 4f + (float)(_rng.NextDouble() * (_lastH * 0.22f)),
                        1 => _lastH - 4f - (float)(_rng.NextDouble() * (_lastH * 0.22f)),
                        _ => (float)(_rng.NextDouble() * _lastH)
                    };
                    p.Vx = (float)(_rng.NextDouble() * Math.PI * 2.0); // angle
                    p.Seed = _rng.Next(1, 10000);
                    p.Phase = 1.0f + (float)_rng.NextDouble() * 0.8f;
                }
                else
                {
                    p.Phase -= p.Speed * dtScale;
                }
            }
            else if (eff == "matrix")
            {
                float trailPx = Math.Max(80f, p.R * 9f);
                if (p.Y - trailPx > _lastH)
                {
                    p.Y = -(float)(_rng.NextDouble() * 90.0);
                    p.X = (float)(_rng.NextDouble() * Math.Max(40f, _lastW - 12f) + 6f);
                }
            }
            else if (eff == "meteor")
            {
                if (p.X < -80 || p.Y > _lastH + 80)
                {
                    p.X = (float)(_lastW * (0.25 + _rng.NextDouble() * 0.95));
                    p.Y = -(float)(_rng.NextDouble() * (_lastH * 0.35) + 20f);
                }
            }
            else if (eff == "water_ripple")
            {
                p.R += p.Speed * 14f * dtScale;
                if (p.R > p.Vx)
                {
                    p.R = 2f;
                    p.X = (float)(_rng.NextDouble() * _lastW);
                    p.Y = (float)(_rng.NextDouble() * _lastH);
                }
            }
            else if (eff == "fading_stars")
            {
                float fade = 0.5f + 0.5f * (float)Math.Sin(p.Phase);
                if (fade < 0.03f && _rng.NextDouble() < 0.15)
                {
                    p.X = (float)(_rng.NextDouble() * _lastW);
                    p.Y = (float)(_rng.NextDouble() * _lastH);
                }
                if (p.X < 0) p.X += _lastW;
                if (p.X > _lastW) p.X -= _lastW;
                if (p.Y < 0) p.Y += _lastH;
                if (p.Y > _lastH) p.Y -= _lastH;
            }
            else if (eff is "embers" or "bubbles")
            {
                p.X += (float)Math.Sin(p.Phase) * 0.4f * dtScale;
                if (p.Y < -24) { p.Y = _lastH + 14; p.X = (float)(_rng.NextDouble() * _lastW); }
                if (p.X < -20) p.X = _lastW + 10;
                if (p.X > _lastW + 20) p.X = -10;
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
        string eff = NormalizeParticleKey(ParticleEffect);
        _seededEffect = eff;
        _seedW = w;
        _seedH = h;
        _particles.Clear();
        if (eff == "disable") return;

        int count = eff switch
        {
            "network" => 54,
            "fading_stars" => 70,
            "snow" => 58,
            "matrix" => Math.Clamp((int)(w / 18f), 18, 36),
            "fireflies" => 44,
            "sakura" => 36,
            "petals_snowfall" => 48,
            "circuit_trace" => 18,
            "plasma_lightning" => 9,
            "embers" => 46,
            "bubbles" => 32,
            "cosmic" => 65,
            "orbs" => 16,
            "lava_lamp" => 12,
            "meteor" => 16,
            "water_ripple" => 16,
            _ => 42
        };

        for (int i = 0; i < count; i++)
        {
            var n = new ParticleNode
            {
                X = (float)(_rng.NextDouble() * w),
                Y = (float)(_rng.NextDouble() * h),
                Phase = (float)(_rng.NextDouble() * Math.PI * 2),
                Speed = 0.025f + (float)(_rng.NextDouble() * 0.045f),
                Seed = _rng.Next(0, 1000)
            };

            switch (eff)
            {
                case "network":
                    n.Vx = ((float)_rng.NextDouble() - 0.5f) * 1.15f;
                    n.Vy = ((float)_rng.NextDouble() - 0.5f) * 1.15f;
                    if (Math.Abs(n.Vx) < 0.16f) n.Vx = (i % 2 == 0 ? 0.28f : -0.28f);
                    if (Math.Abs(n.Vy) < 0.16f) n.Vy = (i % 3 == 0 ? 0.28f : -0.28f);
                    n.R = 1.8f + (float)_rng.NextDouble() * 1.7f;
                    n.Col = (i % 3 == 0) ? Color.FromRgba(165, 180, 252, 225) : Color.FromRgba(226, 232, 240, 235);
                    break;
                case "fading_stars":
                    n.Vx = (float)(_rng.NextDouble() - 0.5) * 0.10f;
                    n.Vy = (float)(_rng.NextDouble() - 0.5) * 0.10f;
                    n.R = 1.3f + (float)_rng.NextDouble() * 2.2f;
                    n.Speed = 0.02f + (float)_rng.NextDouble() * 0.045f;
                    n.Col = (i % 3 == 0) ? Color.FromRgba(253, 224, 71, 235) : Color.FromRgba(224, 242, 254, 235);
                    break;
                case "snow":
                    n.Vx = (float)(_rng.NextDouble() - 0.5) * 0.4f;
                    n.Vy = 0.85f + (float)_rng.NextDouble() * 1.45f;
                    n.R = 1.8f + (float)_rng.NextDouble() * 2.5f;
                    n.Col = Color.FromRgba(241, 245, 249, 220);
                    break;
                case "matrix":
                    float colW = Math.Max(14f, w / count);
                    n.X = i * colW + colW * 0.5f;
                    n.Y = (float)(_rng.NextDouble() * (h + 180f)) - 120f;
                    n.Vx = 0;
                    n.Vy = 2.6f + (float)_rng.NextDouble() * 3.4f;
                    n.R = 12f + (i % 3) * 1.5f; // glyph size; trail count = 9..13
                    n.Speed = 0.08f + (float)_rng.NextDouble() * 0.06f;
                    n.Col = Color.FromRgba(16, 185, 129, 235);
                    break;
                case "fireflies":
                    n.Vx = (float)(_rng.NextDouble() - 0.5) * 0.55f;
                    n.Vy = (float)(_rng.NextDouble() - 0.5) * 0.50f;
                    n.R = 2.0f + (float)_rng.NextDouble() * 2.2f;
                    n.Speed = 0.02f + (float)_rng.NextDouble() * 0.028f;
                    n.Col = (i % 3 == 0) ? Color.FromRgba(253, 224, 71, 240) : Color.FromRgba(251, 191, 36, 235);
                    break;
                case "sakura":
                case "petals_snowfall":
                    bool isPetal = (eff == "sakura") || (i % 2 == 0);
                    n.Vx = 0.25f + (float)_rng.NextDouble() * 0.45f;
                    n.Vy = 0.65f + (float)_rng.NextDouble() * 1.05f;
                    n.R = isPetal ? (3.2f + (float)_rng.NextDouble() * 2.4f) : (2.0f + (float)_rng.NextDouble() * 2.2f);
                    n.Col = isPetal
                        ? Color.FromRgba(249, 168, 212, 220)
                        : Color.FromRgba(241, 245, 249, 220);
                    break;
                case "circuit_trace":
                    float step = 36f;
                    n.X = MathF.Round((float)(_rng.NextDouble() * w) / step) * step;
                    n.Y = MathF.Round((float)(_rng.NextDouble() * h) / step) * step;
                    n.Vx = (i % 2 == 0 ? 1f : -1f) * (step * (2 + _rng.Next(3))); // dx
                    n.Vy = (i % 3 == 0 ? 1f : -1f) * (step * (2 + _rng.Next(3))); // dy
                    n.R = step;
                    n.Phase = (float)_rng.NextDouble();
                    n.Speed = 0.0045f + (float)_rng.NextDouble() * 0.006f;
                    n.Col = (i % 3 == 0) ? Color.FromRgba(16, 185, 129, 220)
                          : (i % 3 == 1) ? Color.FromRgba(0, 240, 255, 220)
                          : Color.FromRgba(236, 72, 153, 210);
                    break;
                case "plasma_lightning":
                    n.Vx = (float)(_rng.NextDouble() * Math.PI * 2.0);
                    n.Vy = 0f;
                    n.R = 45f + (float)_rng.NextDouble() * 75f; // arc length
                    n.Speed = 0.045f + (float)_rng.NextDouble() * 0.055f;
                    n.Phase = (float)_rng.NextDouble();
                    n.Col = (i % 3 == 0) ? Color.FromRgba(192, 132, 252, 235) : Color.FromRgba(34, 211, 238, 240);
                    break;
                case "embers":
                    n.Vx = (float)(_rng.NextDouble() - 0.5) * 0.45f;
                    n.Vy = -(0.95f + (float)_rng.NextDouble() * 1.65f);
                    n.R = 1.8f + (float)_rng.NextDouble() * 2.3f;
                    n.Col = (i % 2 == 0) ? Color.FromRgba(249, 115, 22, 230) : Color.FromRgba(251, 191, 36, 230);
                    break;
                case "bubbles":
                    n.Vx = (float)(_rng.NextDouble() - 0.5) * 0.3f;
                    n.Vy = -(0.55f + (float)_rng.NextDouble() * 1.05f);
                    n.R = 4.2f + (float)_rng.NextDouble() * 6.5f;
                    n.Col = Color.FromRgba(56, 189, 248, 125);
                    break;
                case "cosmic":
                    n.Vx = (float)(_rng.NextDouble() - 0.5) * 0.35f;
                    n.Vy = (float)(_rng.NextDouble() - 0.5) * 0.35f;
                    n.R = 1.4f + (float)_rng.NextDouble() * 2.5f;
                    n.Col = (i % 4) switch
                    {
                        0 => Color.FromRgba(168, 85, 247, 230),
                        1 => Color.FromRgba(56, 189, 248, 230),
                        2 => Color.FromRgba(244, 114, 182, 220),
                        _ => Color.FromRgba(253, 224, 71, 225)
                    };
                    break;
                case "orbs":
                    n.Vx = (float)(_rng.NextDouble() - 0.5) * 0.55f;
                    n.Vy = (float)(_rng.NextDouble() - 0.5) * 0.55f;
                    n.R = 26f + (float)_rng.NextDouble() * 34f;
                    n.Speed = 0.015f + (float)_rng.NextDouble() * 0.02f;
                    n.Col = (i % 3) switch
                    {
                        0 => Color.FromRgba(56, 189, 248, 110),
                        1 => Color.FromRgba(168, 85, 247, 105),
                        _ => Color.FromRgba(45, 212, 191, 105)
                    };
                    break;
                case "lava_lamp":
                    n.Vx = (float)(_rng.NextDouble() - 0.5) * 0.25f;
                    n.Vy = (i % 2 == 0 ? -1 : 1) * (0.28f + (float)_rng.NextDouble() * 0.42f);
                    n.R = 32f + (float)_rng.NextDouble() * 38f;
                    n.Speed = 0.018f + (float)_rng.NextDouble() * 0.02f;
                    n.Col = (i % 3) switch
                    {
                        0 => Color.FromRgba(244, 63, 94, 130),
                        1 => Color.FromRgba(249, 115, 22, 125),
                        _ => Color.FromRgba(192, 132, 252, 120)
                    };
                    break;
                case "meteor":
                    n.X = (float)(_rng.NextDouble() * w * 1.2);
                    n.Y = (float)(_rng.NextDouble() * h * 0.6);
                    n.Vx = -(4.8f + (float)_rng.NextDouble() * 4.5f);
                    n.Vy = 2.8f + (float)_rng.NextDouble() * 2.8f;
                    n.R = 1.6f + (float)_rng.NextDouble() * 1.4f;
                    n.Col = (i % 2 == 0) ? Color.FromRgba(125, 211, 252, 235) : Color.FromRgba(254, 240, 138, 235);
                    break;
                case "water_ripple":
                    n.Vx = 28f + (float)_rng.NextDouble() * 48f; // max radius stored in Vx
                    n.Vy = 0;
                    n.R = (float)(_rng.NextDouble() * n.Vx);
                    n.Speed = 0.028f + (float)_rng.NextDouble() * 0.03f;
                    n.Col = Color.FromRgba(56, 189, 248, 200);
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

        string eff = NormalizeParticleKey(ParticleEffect);
        if (eff != "disable" && (_seededEffect != eff || _particles.Count == 0 ||
            Math.Abs(_lastW - _seedW) > 16f || Math.Abs(_lastH - _seedH) > 16f))
        {
            SeedParticles(w, h);
        }

        if (!IsDarkMode)
        {
            canvas.FillColor = Color.FromRgb(244, 246, 251);
            canvas.FillRectangle(0, 0, w, h);
        }
        else
        {
            // 1. Surface Backdrop Theme
            string theme = (BgTheme ?? "aurora").ToLowerInvariant();
            if (theme is "oled_obsidian" or "obsidian")
            {
                canvas.FillColor = Color.FromRgb(0, 0, 0);
                canvas.FillRectangle(0, 0, w, h);
            }
            else if (theme is "cyberpunk_matrix" or "cyberpunk")
            {
                FillVerticalGradient(canvas, new RectF(0, 0, w, h),
                    Color.FromRgb(11, 13, 20), Color.FromRgb(17, 21, 33), Color.FromRgb(9, 11, 16));
                DrawGlowOrb(canvas, w * 0.14f, h * 0.16f, Math.Min(w, h) * 0.48f, Color.FromRgba(0, 240, 255, 58));
                DrawGlowOrb(canvas, w * 0.86f, h * 0.82f, Math.Min(w, h) * 0.50f, Color.FromRgba(255, 42, 133, 52));
                DrawGlowOrb(canvas, w * 0.55f, h * 0.28f, Math.Min(w, h) * 0.34f, Color.FromRgba(56, 189, 248, 28));
            }
            else if (theme is "boreal_twilight" or "boreal")
            {
                FillVerticalGradient(canvas, new RectF(0, 0, w, h),
                    Color.FromRgb(6, 42, 32), Color.FromRgb(9, 32, 46), Color.FromRgb(6, 12, 34));
                DrawGlowOrb(canvas, w * 0.22f, h * 0.16f, Math.Min(w, h) * 0.46f, Color.FromRgba(16, 185, 129, 44));
                DrawGlowOrb(canvas, w * 0.78f, h * 0.84f, Math.Min(w, h) * 0.48f, Color.FromRgba(37, 99, 235, 42));
            }
            else if (theme is "nebula" or "particles")
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
            else if (theme == "daydusk")
            {
                int hr = DateTime.Now.Hour;
                if (hr >= 5 && hr < 10)
                    FillVerticalGradient(canvas, new RectF(0, 0, w, h), Color.FromRgb(42, 16, 60), Color.FromRgb(64, 24, 110), Color.FromRgb(15, 23, 42));
                else if (hr >= 10 && hr < 17)
                    FillVerticalGradient(canvas, new RectF(0, 0, w, h), Color.FromRgb(12, 45, 72), Color.FromRgb(30, 27, 75), Color.FromRgb(9, 13, 22));
                else if (hr >= 17 && hr < 20)
                    FillVerticalGradient(canvas, new RectF(0, 0, w, h), Color.FromRgb(67, 20, 7), Color.FromRgb(59, 7, 100), Color.FromRgb(9, 11, 26));
                else
                    FillVerticalGradient(canvas, new RectF(0, 0, w, h), Color.FromRgb(9, 11, 26), Color.FromRgb(30, 27, 75), Color.FromRgb(2, 6, 23));
            }
            else if (theme == "minimal")
            {
                canvas.FillColor = Color.FromRgb(11, 15, 25);
                canvas.FillRectangle(0, 0, w, h);
            }
            else
            {
                FillVerticalGradient(canvas, new RectF(0, 0, w, h),
                    Color.FromRgb(9, 11, 26), Color.FromRgb(13, 16, 37), Color.FromRgb(8, 10, 22));
            }
        }

        // 2. Ambient Particle Engine
        if (eff == "disable" || _particles.Count == 0) return;

        float lightMul = IsDarkMode ? 1.0f : 0.65f;

        if (eff == "network")
        {
            float maxDist = Math.Clamp(Math.Min(w, h) * 0.38f, 130f, 195f);
            float maxDistSq = maxDist * maxDist;
            canvas.StrokeSize = 1.15f;
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
                        float alpha = (1f - (float)Math.Sqrt(d2) / maxDist) * 0.66f * lightMul;
                        canvas.StrokeColor = IsDarkMode
                            ? Color.FromRgba(165, 180, 252, (int)(alpha * 255))
                            : Color.FromRgba(79, 70, 229, (int)(alpha * 255));
                        canvas.DrawLine(a.X, a.Y, b.X, b.Y);
                    }
                }
            }

            if ((DateTime.UtcNow - _pointerUtc).TotalSeconds < 6.0
                && _pointerX >= 0 && _pointerY >= 0 && _pointerX <= w && _pointerY <= h)
            {
                float ptrMax = Math.Clamp(Math.Min(w, h) * 0.44f, 145f, 220f);
                float ptrMaxSq = ptrMax * ptrMax;
                canvas.StrokeSize = 1.3f;
                for (int i = 0; i < _particles.Count; i++)
                {
                    var a = _particles[i];
                    float dx = a.X - _pointerX;
                    float dy = a.Y - _pointerY;
                    float d2 = dx * dx + dy * dy;
                    if (d2 < ptrMaxSq)
                    {
                        float alpha = (1f - (float)Math.Sqrt(d2) / ptrMax) * 0.78f * lightMul;
                        canvas.StrokeColor = IsDarkMode
                            ? Color.FromRgba(125, 211, 252, (int)(alpha * 255))
                            : Color.FromRgba(79, 70, 229, (int)(alpha * 255));
                        canvas.DrawLine(_pointerX, _pointerY, a.X, a.Y);
                    }
                }
            }
        }

        for (int i = 0; i < _particles.Count; i++)
        {
            var p = _particles[i];
            float pulse = 0.55f + 0.45f * (float)Math.Sin(p.Phase);

            if (eff == "network")
            {
                var col = IsDarkMode ? p.Col : Color.FromRgba(79, 70, 229, 210);
                canvas.FillColor = col.WithAlpha(0.18f * pulse * lightMul);
                canvas.FillCircle(p.X, p.Y, p.R * 2.2f);
                canvas.FillColor = col.WithAlpha(0.95f * lightMul);
                canvas.FillCircle(p.X, p.Y, p.R);
            }
            else if (eff == "bubbles")
            {
                canvas.StrokeColor = Color.FromRgba(125, 211, 252, (int)(160 * lightMul));
                canvas.StrokeSize = 1.35f;
                canvas.DrawCircle(p.X, p.Y, p.R);
            }
            else if (eff == "matrix")
            {
                int trail = 10 + (p.Seed % 4);
                float cellH = p.R + 2f;
                canvas.FontSize = p.R;
                for (int g = 0; g < trail; g++)
                {
                    float gy = p.Y - g * cellH;
                    if (gy < -cellH || gy > h + cellH) continue;
                    int glyphIdx = Math.Abs(p.Seed + g * 7 + (int)(_t * 4.5)) % MatrixGlyphs.Length;
                    if (g == 0)
                    {
                        canvas.FontColor = IsDarkMode
                            ? Color.FromRgba(209, 250, 229, 245)
                            : Color.FromRgba(6, 95, 70, 235);
                    }
                    else
                    {
                        float tAlpha = (1f - (float)g / trail) * 0.85f * lightMul;
                        canvas.FontColor = IsDarkMode
                            ? Color.FromRgba(16, 185, 129, (int)(tAlpha * 240))
                            : Color.FromRgba(5, 150, 105, (int)(tAlpha * 210));
                    }
                    canvas.DrawString(MatrixGlyphs[glyphIdx], p.X, gy, HorizontalAlignment.Center);
                }
            }
            else if (eff is "orbs" or "lava_lamp")
            {
                float rPulse = p.R * (0.90f + 0.14f * (float)Math.Sin(p.Phase));
                DrawGlowOrb(canvas, p.X, p.Y, rPulse, p.Col.WithAlpha(p.Col.Alpha * lightMul));
            }
            else if (eff == "meteor")
            {
                float tailMul = 5.5f;
                canvas.StrokeSize = p.R * 1.4f;
                canvas.StrokeColor = p.Col.WithAlpha(0.75f * lightMul);
                canvas.DrawLine(p.X, p.Y, p.X - p.Vx * tailMul, p.Y - p.Vy * tailMul);
                canvas.FillColor = Colors.White.WithAlpha(0.95f * lightMul);
                canvas.FillCircle(p.X, p.Y, p.R * 1.15f);
            }
            else if (eff == "water_ripple")
            {
                float a = Math.Clamp(1f - p.R / p.Vx, 0f, 1f) * 0.65f * lightMul;
                if (a > 0.03f)
                {
                    canvas.StrokeSize = 1.4f;
                    canvas.StrokeColor = Color.FromRgba(56, 189, 248, (int)(a * 255));
                    canvas.DrawCircle(p.X, p.Y, p.R);
                }
            }
            else if (eff == "fading_stars")
            {
                float fade = 0.5f + 0.5f * (float)Math.Sin(p.Phase);
                float a = fade * fade * 0.92f * lightMul;
                canvas.FillColor = (IsDarkMode ? p.Col : Color.FromRgb(99, 102, 241)).WithAlpha(a);
                canvas.FillCircle(p.X, p.Y, p.R);
            }
            else if (eff == "fireflies")
            {
                float glowA = (0.35f + 0.65f * pulse) * lightMul;
                DrawGlowOrb(canvas, p.X, p.Y, p.R * 4.5f, Color.FromRgba(245, 158, 11, (int)(glowA * 95)));
                canvas.FillColor = Color.FromRgba(254, 240, 138, (int)(glowA * 240));
                canvas.FillCircle(p.X, p.Y, p.R * (0.75f + 0.35f * pulse));
            }
            else if (eff is "sakura" or "petals_snowfall")
            {
                float fadeOut = p.Y > h * 0.80f ? Math.Clamp(1f - (p.Y - h * 0.80f) / (h * 0.22f), 0f, 1f) : 1f;
                float a = p.Col.Alpha * fadeOut * lightMul;
                if (eff == "sakura" || i % 2 == 0)
                {
                    canvas.SaveState();
                    canvas.Translate(p.X, p.Y);
                    canvas.Rotate((float)Math.Sin(p.Phase) * 35f + (p.Seed % 60));
                    canvas.FillColor = p.Col.WithAlpha(a);
                    canvas.FillEllipse(-p.R * 0.9f, -p.R * 0.48f, p.R * 1.8f, p.R * 0.96f);
                    canvas.RestoreState();
                }
                else
                {
                    canvas.FillColor = p.Col.WithAlpha(a * 0.85f);
                    canvas.FillCircle(p.X, p.Y, p.R);
                }
            }
            else if (eff == "circuit_trace")
            {
                // Orthogonal + 45deg PCB trace with pulsing via pads & traveling data packet
                float x0 = p.X, y0 = p.Y;
                float x1 = x0 + p.Vx * 0.6f, y1 = y0;
                float x2 = x0 + p.Vx, y2 = y0 + p.Vy * 0.45f;
                float x3 = x2, y3 = y0 + p.Vy;

                float pulseTrace = (0.20f + 0.16f * (float)Math.Sin(_t * 1.8 + p.Seed)) * lightMul;
                canvas.StrokeSize = 1.3f;
                canvas.StrokeColor = p.Col.WithAlpha(pulseTrace);
                var tr = new PathF();
                tr.MoveTo(x0, y0); tr.LineTo(x1, y1); tr.LineTo(x2, y2); tr.LineTo(x3, y3);
                canvas.DrawPath(tr);

                canvas.FillColor = p.Col.WithAlpha(pulseTrace * 1.35f);
                canvas.FillCircle(x0, y0, 2.5f);
                canvas.FillCircle(x3, y3, 2.5f);

                // Traveling glowing packet position along 3-segment polyline
                float tSeg = p.Phase * 3f;
                float px, py;
                if (tSeg < 1f) { px = x0 + (x1 - x0) * tSeg; py = y0 + (y1 - y0) * tSeg; }
                else if (tSeg < 2f) { float u = tSeg - 1f; px = x1 + (x2 - x1) * u; py = y1 + (y2 - y1) * u; }
                else { float u = tSeg - 2f; px = x2 + (x3 - x2) * u; py = y2 + (y3 - y2) * u; }

                DrawGlowOrb(canvas, px, py, 9f, p.Col.WithAlpha(0.35f * lightMul));
                canvas.FillColor = Colors.White.WithAlpha(0.88f * lightMul);
                canvas.FillCircle(px, py, 2.3f);
            }
            else if (eff == "plasma_lightning")
            {
                if (p.Phase > 0.25f)
                {
                    float flash = Math.Clamp((p.Phase - 0.25f) * 1.15f, 0f, 1f) * lightMul;
                    if ((int)(_t * 28 + p.Seed) % 5 < 3) // erratic crackle flicker
                    {
                        var bolt = new PathF();
                        bolt.MoveTo(p.X, p.Y);
                        float bx = p.X, by = p.Y;
                        int segs = 5;
                        for (int s = 1; s <= segs; s++)
                        {
                            float stepLen = p.R / segs;
                            float jitter = MathF.Sin(p.Seed * 1.7f + s * 3.9f + (float)_t * 40f) * 12f;
                            bx += MathF.Cos(p.Vx) * stepLen - MathF.Sin(p.Vx) * jitter;
                            by += MathF.Sin(p.Vx) * stepLen + MathF.Cos(p.Vx) * jitter;
                            bolt.LineTo(bx, by);
                        }
                        canvas.StrokeSize = 2.6f;
                        canvas.StrokeColor = p.Col.WithAlpha(0.28f * flash);
                        canvas.DrawPath(bolt);
                        canvas.StrokeSize = 1.1f;
                        canvas.StrokeColor = Colors.White.WithAlpha(0.75f * flash);
                        canvas.DrawPath(bolt);
                    }
                }
            }
            else
            {
                canvas.FillColor = p.Col.WithAlpha(pulse * 0.88f * lightMul);
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
            case "bioluminescent_grotto":
                DrawBioluminescentGrotto(canvas, w, h);
                break;
            case "observatory_morning":
                DrawObservatory(canvas, w, h, "morning");
                break;
            case "observatory_night":
                DrawObservatory(canvas, w, h, "night");
                break;
            case "observatory_sunset":
                DrawObservatory(canvas, w, h, "sunset");
                break;
            case "bamboo_garden":
                DrawBambooGarden(canvas, w, h);
                break;
            case "volcanic_caldera":
                DrawVolcanicCaldera(canvas, w, h);
                break;
            case "ancient_temple":
                DrawAncientTemple(canvas, w, h);
                break;
            case "fireplace":
                DrawFireplace(canvas, w, h);
                break;
            case "ocean_shore":
                DrawOceanShore(canvas, w, h);
                break;
            case "crystal_cave":
                DrawCrystalCave(canvas, w, h);
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

    private void DrawBioluminescentGrotto(ICanvas canvas, float w, float h)
    {
        FillVerticalGradient(canvas, new RectF(0, 0, w, h),
            Color.FromRgb(4, 9, 24), Color.FromRgb(6, 22, 42), Color.FromRgb(4, 15, 32));
        DrawGlowOrb(canvas, w * 0.5f, h * 0.42f, Math.Min(w, h) * 0.42f, Color.FromRgba(20, 184, 166, 48));

        // Underground lagoon
        FillVerticalGradient(canvas, new RectF(0, h * 0.68f, w, h * 0.32f),
            Color.FromRgb(7, 36, 58), Color.FromRgb(2, 8, 20));

        // Framing basalt walls & stalactites
        canvas.FillColor = Color.FromRgb(7, 13, 24);
        var leftWall = new PathF();
        leftWall.MoveTo(0, 0); leftWall.LineTo(w * 0.34f, 0); leftWall.LineTo(w * 0.16f, h * 0.40f);
        leftWall.LineTo(w * 0.21f, h * 0.69f); leftWall.LineTo(0, h); leftWall.Close();
        canvas.FillPath(leftWall);

        var rightWall = new PathF();
        rightWall.MoveTo(w, 0); rightWall.LineTo(w * 0.66f, 0); rightWall.LineTo(w * 0.84f, h * 0.40f);
        rightWall.LineTo(w * 0.79f, h * 0.69f); rightWall.LineTo(w, h); rightWall.Close();
        canvas.FillPath(rightWall);

        // Glowing stalactite tips
        for (int i = 0; i < 5; i++)
        {
            float sx = w * (0.22f + i * 0.14f);
            float sh = h * (0.14f + (i % 3) * 0.05f);
            var st = new PathF();
            st.MoveTo(sx - 12, 0); st.LineTo(sx + 12, 0); st.LineTo(sx, sh); st.Close();
            canvas.FillColor = Color.FromRgb(10, 20, 36);
            canvas.FillPath(st);
            DrawGlowOrb(canvas, sx, sh, 12f, i % 2 == 0 ? Color.FromRgba(34, 211, 238, 120) : Color.FromRgba(167, 139, 250, 120));
        }

        // Ripples & floating bio spores
        for (int r = 0; r < 4; r++)
        {
            float rx = w * (0.22f + r * 0.18f);
            float ry = h * (0.76f + (r % 2) * 0.08f);
            float rad = (float)(((r * 14 + _t * 16) % 36));
            canvas.StrokeColor = Color.FromRgba(45, 212, 191, (int)((1f - rad / 36f) * 140));
            canvas.StrokeSize = 1.2f;
            canvas.DrawEllipse(rx - rad, ry - rad * 0.3f, rad * 2f, rad * 0.6f);
        }

        for (int i = 0; i < 34; i++)
        {
            float bx = (float)(((i * 97 + _t * 8f * ((i % 3) - 1)) % w + w) % w);
            float by = (float)(((i * 53 - _t * (10 + (i % 4) * 4)) % (h * 0.70f) + (h * 0.70f)) % (h * 0.70f) + h * 0.10f);
            float pulse = 0.35f + 0.65f * (float)Math.Sin(_t * 1.8 + i);
            Color col = (i % 3 == 0) ? Color.FromRgba(34, 211, 238, (int)(pulse * 220))
                      : (i % 3 == 1) ? Color.FromRgba(45, 212, 191, (int)(pulse * 220))
                      : Color.FromRgba(192, 132, 252, (int)(pulse * 210));
            canvas.FillColor = col.WithAlpha(pulse * 0.28f);
            canvas.FillCircle(bx, by, 7f);
            canvas.FillColor = col;
            canvas.FillCircle(bx, by, 2.2f);
        }
    }

    private void DrawObservatory(ICanvas canvas, float w, float h, string mode)
    {
        bool isMorning = mode == "morning";
        bool isSunset = mode == "sunset";

        if (isMorning)
            FillVerticalGradient(canvas, new RectF(0, 0, w, h),
                Color.FromRgb(24, 58, 122), Color.FromRgb(86, 156, 244), Color.FromRgb(253, 186, 116));
        else if (isSunset)
            FillVerticalGradient(canvas, new RectF(0, 0, w, h),
                Color.FromRgb(20, 10, 48), Color.FromRgb(98, 28, 92), Color.FromRgb(249, 138, 60));
        else
            FillVerticalGradient(canvas, new RectF(0, 0, w, h),
                Color.FromRgb(2, 6, 20), Color.FromRgb(7, 17, 46), Color.FromRgb(15, 32, 68));

        if (!isMorning && !isSunset)
        {
            DrawStarfield(canvas, w, h * 0.68f, 95, 0.9f);
        }
        else if (isMorning)
        {
            DrawGlowOrb(canvas, w * 0.22f, h * 0.30f, 58f, Color.FromRgba(254, 240, 138, 205));
        }
        else
        {
            DrawStarfield(canvas, w, h * 0.32f, 45, 0.45f);
            DrawGlowOrb(canvas, w * 0.25f, h * 0.38f, 68f, Color.FromRgba(253, 186, 116, 185));
        }

        // Far + Mid Alpine Peaks + Lit Snow Slopes
        canvas.FillColor = isMorning ? Color.FromRgb(118, 146, 192) : isSunset ? Color.FromRgb(82, 32, 70) : Color.FromRgb(14, 24, 48);
        var farP = new PathF();
        farP.MoveTo(0, h); farP.LineTo(0, h * 0.60f); farP.LineTo(w * 0.18f, h * 0.45f);
        farP.LineTo(w * 0.36f, h * 0.56f); farP.LineTo(w * 0.56f, h * 0.42f); farP.LineTo(w * 0.78f, h * 0.53f);
        farP.LineTo(w, h * 0.46f); farP.LineTo(w, h); farP.Close();
        canvas.FillPath(farP);

        canvas.FillColor = isMorning ? Color.FromRgba(239, 246, 255, 135) : isSunset ? Color.FromRgba(253, 186, 116, 105) : Color.FromRgba(148, 163, 184, 70);
        var cap = new PathF();
        cap.MoveTo(w * 0.56f, h * 0.42f); cap.LineTo(w * 0.48f, h * 0.51f); cap.LineTo(w * 0.60f, h * 0.50f); cap.Close();
        canvas.FillPath(cap);

        canvas.FillColor = isMorning ? Color.FromRgb(64, 88, 132) : isSunset ? Color.FromRgb(50, 18, 48) : Color.FromRgb(9, 16, 34);
        var midP = new PathF();
        midP.MoveTo(0, h); midP.LineTo(0, h * 0.72f); midP.LineTo(w * 0.24f, h * 0.58f);
        midP.LineTo(w * 0.48f, h * 0.66f); midP.LineTo(w * 0.76f, h * 0.56f); midP.LineTo(w, h * 0.62f); midP.LineTo(w, h); midP.Close();
        canvas.FillPath(midP);

        if (isMorning || isSunset)
        {
            for (int c = 0; c < 4; c++)
            {
                float cx = (float)(((c * 140 + _t * (14 + c * 4)) % (w + 140)) - 70);
                float cy = h * (0.26f + c * 0.08f);
                canvas.FillColor = isMorning ? Color.FromRgba(255, 250, 240, 72) : Color.FromRgba(251, 146, 60, 62);
                canvas.FillEllipse(cx - 55, cy - 12, 110, 24);
            }
        }

        // Foreground Rugged Summit Peak + Summit Terrace Plateau
        canvas.FillColor = isMorning ? Color.FromRgb(22, 33, 54) : isSunset ? Color.FromRgb(22, 10, 28) : Color.FromRgb(5, 9, 20);
        var fgP = new PathF();
        fgP.MoveTo(0, h); fgP.LineTo(0, h * 0.84f); fgP.LineTo(w * 0.28f, h * 0.77f);
        fgP.LineTo(w * 0.56f, h * 0.68f); fgP.LineTo(w * 0.92f, h * 0.68f); fgP.LineTo(w, h * 0.76f); fgP.LineTo(w, h); fgP.Close();
        canvas.FillPath(fgP);

        float ox = w * 0.75f;
        float oy = h * 0.68f;
        float dr = Math.Min(w, h) * 0.11f;

        // Concrete Summit Terrace & Research Control Wing
        canvas.FillColor = isMorning ? Color.FromRgb(148, 163, 184) : isSunset ? Color.FromRgb(64, 34, 58) : Color.FromRgb(26, 36, 54);
        canvas.FillRectangle(ox - dr * 1.95f, oy - dr * 0.14f, dr * 3.15f, dr * 0.22f);

        Color bodyCol = isMorning ? Color.FromRgb(226, 232, 240) : isSunset ? Color.FromRgb(82, 42, 70) : Color.FromRgb(42, 56, 78);
        Color darkBody = isMorning ? Color.FromRgb(148, 163, 184) : isSunset ? Color.FromRgb(46, 20, 40) : Color.FromRgb(22, 32, 48);
        canvas.FillColor = bodyCol;
        float wingW = dr * 0.95f, wingH = dr * 0.40f, wingX = ox - dr * 1.75f, wingY = oy - dr * 0.14f - wingH;
        canvas.FillRectangle(wingX, wingY, wingW, wingH);

        canvas.FillColor = isMorning ? Color.FromRgb(254, 243, 199) : Color.FromRgb(251, 191, 36);
        canvas.FillRectangle(wingX + wingW * 0.18f, wingY + wingH * 0.30f, wingW * 0.22f, wingH * 0.28f);
        canvas.FillRectangle(wingX + wingW * 0.54f, wingY + wingH * 0.30f, wingW * 0.22f, wingH * 0.28f);

        // Main Observatory Drum Base + Hemisphere Dome + Open Shutter Aperture + Stepped Telescope
        float drumTop = oy - dr * 0.66f;
        canvas.FillColor = bodyCol;
        canvas.FillRectangle(ox - dr * 0.90f, drumTop, dr * 1.80f, oy - dr * 0.14f - drumTop);
        canvas.FillColor = darkBody;
        canvas.FillRectangle(ox - dr * 0.95f, drumTop - 2.5f, dr * 1.90f, 5f);

        // Dome back shell + slit aperture
        canvas.FillColor = bodyCol;
        canvas.FillEllipse(ox - dr * 0.92f, drumTop - dr * 0.88f, dr * 1.84f, dr * 1.76f);
        canvas.FillColor = Color.FromRgb(10, 15, 28);
        var slit = new PathF();
        slit.MoveTo(ox - dr * 0.36f, drumTop - dr * 0.08f);
        slit.LineTo(ox - dr * 0.50f, drumTop - dr * 0.68f);
        slit.LineTo(ox - dr * 0.26f, drumTop - dr * 0.84f);
        slit.LineTo(ox - dr * 0.10f, drumTop - dr * 0.10f);
        slit.Close();
        canvas.FillPath(slit);

        // Animated Stepped Telescope emerging from Shutter Slit
        float sweep = -2.14f + (float)Math.Sin(_t * 0.25) * 0.16f;
        float px = ox - dr * 0.24f, py = drumTop - dr * 0.34f;
        float mx = px + (float)Math.Cos(sweep) * dr * 0.72f;
        float my = py + (float)Math.Sin(sweep) * dr * 0.72f;
        float tx = px + (float)Math.Cos(sweep) * dr * 1.12f;
        float ty = py + (float)Math.Sin(sweep) * dr * 1.12f;
        canvas.StrokeSize = 6.5f;
        canvas.StrokeColor = isMorning ? Color.FromRgb(226, 232, 240) : isSunset ? Color.FromRgb(58, 30, 52) : Color.FromRgb(100, 116, 139);
        canvas.DrawLine(px, py, mx, my);
        canvas.StrokeSize = 4.6f;
        canvas.StrokeColor = isMorning ? Color.FromRgb(148, 163, 184) : Color.FromRgb(71, 85, 105);
        canvas.DrawLine(mx, my, tx, ty);
        canvas.FillColor = Color.FromRgba(125, 211, 252, 200);
        canvas.FillCircle(tx, ty, 2.5f);

        // Front Dome Lip covering bottom of drum/dome junction
        canvas.FillColor = bodyCol;
        canvas.FillRectangle(ox - dr * 0.90f, drumTop, dr * 1.80f, dr * 0.52f);
        canvas.FillColor = isMorning ? Color.FromRgb(254, 243, 199) : Color.FromRgb(251, 191, 36);
        canvas.FillRectangle(ox - dr * 0.15f, drumTop + dr * 0.14f, dr * 0.30f, dr * 0.18f);
    }

    private void DrawBambooGarden(ICanvas canvas, float w, float h)
    {
        FillVerticalGradient(canvas, new RectF(0, 0, w, h),
            Color.FromRgb(6, 40, 32), Color.FromRgb(16, 78, 56), Color.FromRgb(6, 30, 24));

        // Quiet garden pond
        FillVerticalGradient(canvas, new RectF(0, h * 0.80f, w, h * 0.20f),
            Color.FromRgb(9, 46, 42), Color.FromRgb(5, 25, 25));

        // Stone Lantern + Warm Amber Pulse
        float lx = w * 0.20f, ly = h * 0.78f;
        canvas.FillColor = Color.FromRgb(42, 52, 54);
        canvas.FillRectangle(lx - 7, ly - 38, 14, 38);
        canvas.FillRectangle(lx - 11, ly - 58, 22, 20);
        float glow = 0.85f + 0.15f * (float)Math.Sin(_t * 3.0);
        DrawGlowOrb(canvas, lx, ly - 48, 36f * glow, Color.FromRgba(253, 224, 71, 150));

        // Swaying bamboo stalks
        for (int i = 0; i < 12; i++)
        {
            float bx = w * (0.06f + (i / 11f) * 0.88f);
            float sway = (float)Math.Sin(_t * 0.85 + i * 0.7) * 14f;
            canvas.StrokeSize = (i % 3 == 0) ? 5f : (i % 3 == 1) ? 7.5f : 9.5f;
            canvas.StrokeColor = (i % 3 == 0) ? Color.FromRgb(22, 101, 52)
                               : (i % 3 == 1) ? Color.FromRgb(34, 139, 74)
                               : Color.FromRgb(74, 222, 128);
            var p = new PathF();
            p.MoveTo(bx, h * 0.82f);
            p.QuadTo(bx + sway * 0.35f, h * 0.42f, bx + sway, -8f);
            canvas.DrawPath(p);
        }

        // Drifting bamboo leaves
        canvas.FillColor = Color.FromRgba(134, 239, 172, 185);
        for (int l = 0; l < 18; l++)
        {
            float lyPos = (float)(((l * 59 + _t * 28f) % (h + 20)) - 10f);
            float lxPos = (float)(((l * 91 + _t * 14f + Math.Sin(_t + l) * 16f) % w + w) % w);
            canvas.FillEllipse(lxPos - 5, lyPos - 2, 10, 4);
        }
    }

    private void DrawVolcanicCaldera(ICanvas canvas, float w, float h)
    {
        FillVerticalGradient(canvas, new RectF(0, 0, w, h * 0.72f),
            Color.FromRgb(14, 10, 12), Color.FromRgb(48, 16, 14), Color.FromRgb(124, 34, 14));
        FillVerticalGradient(canvas, new RectF(0, h * 0.70f, w, h * 0.30f),
            Color.FromRgb(251, 146, 60), Color.FromRgb(220, 38, 38), Color.FromRgb(69, 10, 10));

        float pulse = 0.8f + 0.2f * (float)Math.Sin(_t * 2.0);
        DrawGlowOrb(canvas, w * 0.5f, h * 0.70f, w * 0.36f * pulse, Color.FromRgba(251, 146, 60, 125));

        canvas.FillColor = Color.FromRgb(18, 10, 12);
        var leftCone = new PathF();
        leftCone.MoveTo(0, h); leftCone.LineTo(0, h * 0.38f); leftCone.LineTo(w * 0.26f, h * 0.64f);
        leftCone.LineTo(w * 0.36f, h * 0.80f); leftCone.LineTo(0, h * 0.90f); leftCone.Close();
        canvas.FillPath(leftCone);

        var rightCone = new PathF();
        rightCone.MoveTo(w, h); rightCone.LineTo(w, h * 0.36f); rightCone.LineTo(w * 0.74f, h * 0.62f);
        rightCone.LineTo(w * 0.64f, h * 0.80f); rightCone.LineTo(w, h * 0.90f); rightCone.Close();
        canvas.FillPath(rightCone);

        for (int e = 0; e < 36; e++)
        {
            float ex = (float)(((e * 83 + Math.Sin(_t * 1.4 + e) * 18) % (w * 0.76) + w * 0.12));
            float ey = (float)(((h * 0.82f - ((e * 41 + _t * (38 + (e % 5) * 9)) % (h * 0.85f)))));
            canvas.FillColor = (e % 2 == 0) ? Color.FromRgba(251, 146, 60, 220) : Color.FromRgba(254, 240, 138, 225);
            canvas.FillCircle(ex, ey, 2.0f + (e % 2));
        }
    }

    private void DrawAncientTemple(ICanvas canvas, float w, float h)
    {
        FillVerticalGradient(canvas, new RectF(0, 0, w, h * 0.74f),
            Color.FromRgb(15, 23, 42), Color.FromRgb(55, 48, 86), Color.FromRgb(146, 94, 72));
        FillVerticalGradient(canvas, new RectF(0, h * 0.74f, w, h * 0.26f),
            Color.FromRgb(42, 34, 52), Color.FromRgb(15, 17, 29));
        DrawStarfield(canvas, w, h * 0.42f, 45, 0.5f);

        float cx = w * 0.5f;
        canvas.FillColor = Color.FromRgb(44, 42, 54);
        canvas.FillRectangle(cx - w * 0.22f, h * 0.62f, w * 0.44f, h * 0.12f);
        canvas.FillRectangle(cx - w * 0.16f, h * 0.51f, w * 0.32f, h * 0.11f);
        canvas.FillRectangle(cx - w * 0.11f, h * 0.41f, w * 0.22f, h * 0.10f);
        var roof = new PathF();
        roof.MoveTo(cx - w * 0.14f, h * 0.41f); roof.LineTo(cx + w * 0.14f, h * 0.41f); roof.LineTo(cx, h * 0.29f); roof.Close();
        canvas.FillPath(roof);

        float fl = 0.85f + 0.15f * (float)Math.Sin(_t * 6.0);
        DrawGlowOrb(canvas, w * 0.32f, h * 0.60f, 26f * fl, Color.FromRgba(254, 240, 138, 185));
        DrawGlowOrb(canvas, w * 0.68f, h * 0.60f, 26f * fl, Color.FromRgba(254, 240, 138, 185));

        for (int i = 0; i < 24; i++)
        {
            float mx = w * 0.24f + ((i * 59) % (int)Math.Max(40, w * 0.52f));
            float my = (float)(h * 0.76f - ((i * 31 + _t * 22) % (h * 0.50f)));
            canvas.FillColor = Color.FromRgba(253, 230, 138, 180);
            canvas.FillCircle(mx, my, 1.8f);
        }
    }

    private void DrawFireplace(ICanvas canvas, float w, float h)
    {
        FillVerticalGradient(canvas, new RectF(0, 0, w, h),
            Color.FromRgb(18, 10, 8), Color.FromRgb(28, 16, 12));

        float hx = w * 0.18f, hw = w * 0.64f, hy = h * 0.24f, hh = h * 0.66f;
        canvas.FillColor = Color.FromRgb(58, 28, 22);
        canvas.FillRectangle(hx, hy, hw, hh);
        canvas.FillColor = Color.FromRgb(46, 26, 16);
        canvas.FillRectangle(hx - 14, hy - 14, hw + 28, 18);
        canvas.FillColor = Color.FromRgb(15, 9, 8);
        canvas.FillRectangle(hx + hw * 0.12f, hy + 34, hw * 0.76f, hh - 34);

        float cx = w * 0.5f, baseY = h * 0.80f;
        float glow = 0.92f + 0.08f * (float)Math.Sin(_t * 5.2);
        DrawGlowOrb(canvas, cx, baseY - 60, hw * 0.55f * glow, Color.FromRgba(251, 146, 60, 120));

        // Logs + animated flames
        canvas.FillColor = Color.FromRgb(42, 24, 14);
        canvas.FillRoundedRectangle(cx - 58, baseY, 116, 18, 8);
        for (int t = -2; t <= 2; t++)
        {
            float tx = cx + t * 18 + (float)Math.Sin(_t * 7.0 + t) * 4f;
            float fh = (88f - Math.Abs(t) * 14f) * (0.85f + 0.16f * (float)Math.Sin(_t * 8.5 + t));
            var fl = new PathF();
            fl.MoveTo(tx - 16, baseY); fl.LineTo(tx + (float)Math.Sin(_t * 9 + t) * 7f, baseY - fh); fl.LineTo(tx + 16, baseY); fl.Close();
            canvas.FillColor = t == 0 ? Color.FromRgba(254, 240, 138, 225) : Color.FromRgba(249, 115, 22, 210);
            canvas.FillPath(fl);
        }

        for (int s = 0; s < 22; s++)
        {
            float sx = cx + (float)Math.Sin(_t * 2.1 + s) * 34f;
            float sy = (float)(baseY - ((s * 23 + _t * 52) % (h * 0.52f)));
            canvas.FillColor = Color.FromRgba(254, 215, 170, 210);
            canvas.FillCircle(sx, sy, 1.6f);
        }
    }

    private void DrawOceanShore(ICanvas canvas, float w, float h)
    {
        FillVerticalGradient(canvas, new RectF(0, 0, w, h * 0.50f),
            Color.FromRgb(14, 116, 144), Color.FromRgb(56, 189, 248), Color.FromRgb(254, 243, 199));
        FillVerticalGradient(canvas, new RectF(0, h * 0.50f, w, h * 0.22f),
            Color.FromRgb(12, 74, 110), Color.FromRgb(14, 116, 144));
        FillVerticalGradient(canvas, new RectF(0, h * 0.70f, w, h * 0.30f),
            Color.FromRgb(217, 179, 130), Color.FromRgb(161, 118, 72));

        float wash = ((float)Math.Sin(_t * 0.85) + 1f) * 0.5f;
        float surfY = h * (0.69f + wash * 0.08f);
        canvas.FillColor = Color.FromRgba(14, 116, 144, 75);
        canvas.FillRectangle(0, h * 0.69f, w, h * 0.10f);
        canvas.FillColor = Color.FromRgba(6, 182, 212, 165);
        canvas.FillRectangle(0, h * 0.67f, w, surfY - h * 0.67f);
        canvas.StrokeSize = 2.6f;
        canvas.StrokeColor = Color.FromRgba(240, 253, 250, 215);
        canvas.DrawLine(0, surfY, w, surfY + (float)Math.Sin(_t * 1.4) * 5f);

        for (int s = 0; s < 24; s++)
        {
            float sx = ((s * 73) % Math.Max(40, (int)w));
            float sy = h * 0.52f + ((s * 37) % Math.Max(20, (int)(h * 0.16f)));
            canvas.FillColor = Color.FromRgba(255, 255, 255, (int)((0.35f + 0.65f * Math.Sin(_t * 3.0 + s)) * 200));
            canvas.FillCircle(sx, sy, 1.5f);
        }
    }

    private void DrawCrystalCave(ICanvas canvas, float w, float h)
    {
        FillVerticalGradient(canvas, new RectF(0, 0, w, h),
            Color.FromRgb(12, 6, 30), Color.FromRgb(28, 12, 58), Color.FromRgb(10, 8, 28));
        FillVerticalGradient(canvas, new RectF(0, h * 0.74f, w, h * 0.26f),
            Color.FromRgb(24, 15, 48), Color.FromRgb(7, 5, 18));

        float shim = 0.78f + 0.22f * (float)Math.Sin(_t * 1.6);
        DrawGlowOrb(canvas, w * 0.18f, h * 0.58f, 62f * shim, Color.FromRgba(192, 132, 252, 115));
        DrawGlowOrb(canvas, w * 0.80f, h * 0.60f, 58f * shim, Color.FromRgba(103, 232, 249, 115));

        void Spire(float bx, float by, float tx, float ty, float hw, bool cyan)
        {
            var left = new PathF();
            left.MoveTo(bx - hw, by); left.LineTo(bx, by); left.LineTo(tx, ty); left.Close();
            canvas.FillColor = cyan ? Color.FromRgba(103, 232, 249, 200) : Color.FromRgba(168, 85, 247, 200);
            canvas.FillPath(left);
            var right = new PathF();
            right.MoveTo(bx, by); right.LineTo(bx + hw, by); right.LineTo(tx, ty); right.Close();
            canvas.FillColor = cyan ? Color.FromRgba(14, 116, 144, 185) : Color.FromRgba(107, 33, 168, 185);
            canvas.FillPath(right);
        }

        Spire(w * 0.16f, h * 0.78f, w * 0.21f, h * 0.45f, 24f, false);
        Spire(w * 0.27f, h * 0.80f, w * 0.31f, h * 0.56f, 18f, true);
        Spire(w * 0.84f, h * 0.78f, w * 0.78f, h * 0.43f, 26f, false);
        Spire(w * 0.73f, h * 0.80f, w * 0.69f, h * 0.57f, 18f, true);

        for (int i = 0; i < 28; i++)
        {
            float sx = ((i * 79) % Math.Max(40, (int)w));
            float sy = h * 0.12f + ((i * 53) % Math.Max(40, (int)(h * 0.62f)));
            float g = (float)Math.Pow(Math.Max(0, Math.Sin(_t * 2.5 + i)), 3);
            if (g > 0.1f)
            {
                canvas.FillColor = Color.FromRgba(255, 255, 255, (int)(g * 230));
                canvas.FillCircle(sx, sy, 2.0f);
            }
        }
    }
}
