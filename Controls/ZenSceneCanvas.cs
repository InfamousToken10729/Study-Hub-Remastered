using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StudyHub.Controls
{
    /// <summary>
    /// High-performance native WPF canvas renderer for all 17 built-in animated Zen Mode environments (+ "none").
    /// Renders directly to the MIL visual composition layer via DrawingContext without allocating child UIElements per frame.
    /// </summary>
    public sealed class ZenSceneCanvas : FrameworkElement
    {
        private string _sceneId = "north_pole";
        private double _t = 0.0;
        private double _seedW = 0;
        private double _seedH = 0;
        private readonly Random _rng = new(1337);

        // DPI & static bitmap cache state (eliminates rebuilding static vectors/gradients every frame)
        private DpiScale _dpiScale = new(1.0, 1.0);
        private bool _dpiChecked = false;
        private string? _cachedSceneId;
        private double _cacheW, _cacheH, _cacheDpiX, _cacheDpiY;
        private ImageSource? _staticBgBitmap;
        private ImageSource? _staticFgBitmap;
        private readonly ImageSource?[] _layerBitmaps = new ImageSource?[4];

        // Viewport-sized reusable geometries & brushes (rebuilt only on resize/seed)
        private Brush?[]? _northRibbonBrushes;
        private StreamGeometry? _shipSunbeamGeo;
        private Brush? _shipSunbeamBrush;
        private StreamGeometry? _shipSandGeo;
        private Brush? _shipSandBrush;
        private StreamGeometry? _oceanSunbeamGeo;
        private Brush? _oceanSunbeamBrush;

        // Pre-frozen reusable palettes, 32-step alpha LUTs & soft sprites (0 per-frame GC churn)
        private const int LUT_STEPS = 32;
        private static readonly Brush NoneBgBrush = Freeze(new SolidColorBrush(Color.FromRgb(6, 9, 19)));
        private static readonly Brush PureWhiteBrush = Brushes.White;
        private static readonly Brush PolarSnowBrush = Freeze(new SolidColorBrush(Color.FromArgb(209, 240, 249, 255)));
        private static readonly Brush PolarIceFloeBrush = Freeze(new SolidColorBrush(Color.FromArgb(199, 224, 242, 254)));
        private static readonly Brush AuroraWaterReflectA = Freeze(new SolidColorBrush(Color.FromArgb(26, 52, 211, 153)));
        private static readonly Brush AuroraWaterReflectB = Freeze(new SolidColorBrush(Color.FromArgb(23, 34, 211, 238)));

        private static readonly Brush[][] StarHueLuts = BuildStarHueLuts();
        private static readonly Brush[] IceStarLut = BuildAlphaBrushLut(224, 242, 254);
        private static readonly Brush[] SlateStarLut = BuildAlphaBrushLut(226, 232, 240);
        private static readonly Brush[] CreamStarLut = BuildAlphaBrushLut(254, 243, 199);
        private static readonly Brush[] MintStarLut = BuildAlphaBrushLut(204, 251, 241);
        private static readonly Brush[] WhiteStarLut = BuildAlphaBrushLut(255, 255, 255);
        private static readonly Brush[] GoldFireflyLut = BuildAlphaBrushLut(253, 224, 71);
        private static readonly Brush[] MintFireflyLut = BuildAlphaBrushLut(167, 243, 208);
        private static readonly Brush[] CyanPlanktonLut = BuildAlphaBrushLut(103, 232, 249);
        private static readonly Brush[] RedBeaconLut = BuildAlphaBrushLut(244, 63, 94);
        private static readonly Brush[] LimeEmberLut = BuildAlphaBrushLut(163, 230, 53);
        private static readonly Brush[] YellowEmberLut = BuildAlphaBrushLut(250, 204, 21);

        private static readonly Pen[] LimeFissurePenLut = BuildAlphaPenLut(163, 230, 53, 2.5);
        private static readonly Pen[] AmberDustPenLut = BuildAlphaPenLut(245, 158, 11, 1.4);
        private static readonly Pen[] DesertDustPenLut = BuildAlphaPenLut(254, 240, 138, 1.6);
        private static readonly Pen[] PuddleRipplePenLut = BuildAlphaPenLut(167, 243, 208, 1.2);
        private static readonly Pen[] RainDropPenLut = BuildAlphaPenLut(186, 230, 253, 1.25, roundCaps: true);

        private static readonly Brush[][] CyberAccentLuts = BuildCyberAccentLuts();
        private static readonly Brush[][] CityNeonRefLuts = BuildCityNeonRefLuts();

        // Far Space sprites & frozen quasar/shooting star resources
        private static readonly Color[] FarSpaceNebCols =
        {
            Color.FromRgb(168, 85, 247), Color.FromRgb(236, 72, 153),
            Color.FromRgb(34, 211, 238), Color.FromRgb(99, 102, 241),
            Color.FromRgb(245, 158, 11), Color.FromRgb(16, 185, 129)
        };
        private static readonly ImageSource[] NebulaSprites = BuildNebulaSprites();
        private static readonly Pen QuasarJetPen = Freeze(new Pen(MakeLinearAbs(new Point(-55, 0), new Point(55, 0),
            (Color.FromArgb(0, 56, 189, 248), 0.0),
            (Color.FromArgb(107, 165, 243, 252), 0.5),
            (Color.FromArgb(0, 56, 189, 248), 1.0)), 2.2));
        private static readonly Brush QuasarCoreBrush = MakeRadialGrad(
            (Colors.White, 0.0), (Color.FromArgb(166, 56, 189, 248), 0.4), (Color.FromArgb(0, 56, 189, 248), 1.0));
        private static readonly Pen ShootingStarPen = Freeze(new Pen(MakeLinearAbs(new Point(0, 0), new Point(71.5, -35.2),
            (Color.FromArgb(235, 254, 249, 195), 0.0), (Color.FromArgb(115, 56, 189, 248), 0.4), (Color.FromArgb(0, 0, 0, 0), 1.0)), 1.6));

        // Solar System shared frozen resources
        private static readonly Brush SolarSunGlowBrush = MakeRadialGrad(
            (Colors.White, 0.0), (Color.FromRgb(254, 240, 138), 0.22),
            (Color.FromArgb(140, 249, 115, 22), 0.5), (Color.FromArgb(0, 234, 88, 12), 1.0));
        private static readonly Brush SolarAstBrush = Freeze(new SolidColorBrush(Color.FromArgb(148, 148, 163, 184)));
        private static readonly Brush SolarKuiperIcyBrush = Freeze(new SolidColorBrush(Color.FromArgb(133, 186, 230, 253)));
        private static readonly Brush SolarKuiperRockBrush = Freeze(new SolidColorBrush(Color.FromArgb(107, 148, 163, 184)));
        private static readonly Pen SolarOrbitPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(36, 148, 163, 184))), 1.0));
        private static readonly Pen SolarPlutoPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(26, 186, 230, 253))), 1.0));
        private static readonly Pen SolarSatRingPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(174, 253, 224, 71))), 2.8));
        private static readonly Pen SolarUrRingPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(107, 103, 232, 249))), 1.4));
        private static readonly Pen SolarCometTailPen = Freeze(new Pen(MakeLinearAbs(new Point(0, 0), new Point(58, 0),
            (Color.FromArgb(224, 186, 230, 253), 0.0), (Color.FromArgb(89, 56, 189, 248), 0.4), (Color.FromArgb(0, 56, 189, 248), 1.0)), 3.2)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });

        // Moon Night shared frozen resources
        private static readonly Brush MoonHaloBrush = MakeRadialGrad(
            (Color.FromArgb(115, 224, 242, 254), 0.0), (Color.FromArgb(36, 148, 163, 184), 0.45), (Color.FromArgb(0, 0, 0, 0), 1.0));
        private static readonly Brush MoonDiscBrush = MakeRadialGrad(new Point(0.5, 0.5), new Point(0.37, 0.37),
            (Colors.White, 0.0), (Color.FromRgb(226, 232, 240), 0.65), (Color.FromRgb(148, 163, 184), 1.0));
        private static readonly Brush MoonCraterBrush = Freeze(new SolidColorBrush(Color.FromArgb(56, 100, 116, 139)));
        private static readonly Brush MoonCloudBrush = Freeze(new SolidColorBrush(Color.FromArgb(36, 148, 163, 184)));
        private static readonly Brush MoonLakeReflBrush = Freeze(new SolidColorBrush(Color.FromArgb(56, 226, 232, 240)));

        // Wasteland shared frozen resources & unit geometries
        private static readonly Brush NuclearAshBrush = Freeze(new SolidColorBrush(Color.FromArgb(82, 168, 162, 158)));
        private static readonly Pen WastelandWirePen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(166, 38, 19, 8))), 1.2));
        private static readonly Pen WastelandWheelPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(45, 22, 9))), 2.2));
        private static readonly Brush WastelandHubBrush = Freeze(new SolidColorBrush(Color.FromRgb(21, 10, 4)));
        private static readonly Brush WastelandDustDevilBrush = MakeVerticalGradRelative(
            (Color.FromArgb(0, 217, 119, 6), 0.0), (Color.FromArgb(41, 245, 158, 11), 0.5), (Color.FromArgb(71, 217, 119, 6), 1.0));
        private static readonly Pen WastelandTumblePen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(69, 38, 20))), 1.4));
        private static readonly GeometryGroup WindmillWheelGeo = BuildWindmillWheelGeometry();
        private static readonly GeometryGroup TumbleweedUnitGeo = BuildUnitTumbleweedGeometry();

        // Lake, Shipwreck, Ocean, Snowy Peaks, Sunset, Rainy Forest shared frozen resources & soft sprites
        private static readonly Brush LakeRippleBrush = Freeze(new SolidColorBrush(Color.FromArgb(31, 94, 234, 212)));
        private static readonly Pen ShipCaustPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(41, 103, 232, 249))), 1.5));
        private static readonly Brush ShipSailBrush1 = Freeze(new SolidColorBrush(Color.FromArgb(41, 103, 232, 249)));
        private static readonly Brush ShipSailBrush2 = Freeze(new SolidColorBrush(Color.FromArgb(38, 103, 232, 249)));
        private static readonly Pen ShipKelpPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(7, 51, 64))), 4.5));
        private static readonly Pen ShipBubPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(140, 186, 230, 253))), 1.3));
        private static readonly Pen OceanBubPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(128, 224, 242, 254))), 1.2));
        private static readonly ImageSource SnowCloudSprite = BuildSoftOrbSprite(128, 128,
            (Color.FromArgb(184, 241, 245, 249), 0.0), (Color.FromArgb(89, 226, 232, 240), 0.6), (Color.FromArgb(0, 226, 232, 240), 1.0));
        private static readonly Brush SunsetCloudBrush = Freeze(new SolidColorBrush(Color.FromArgb(56, 251, 113, 133)));
        private static readonly Brush SunsetRefGold = Freeze(new SolidColorBrush(Color.FromArgb(115, 254, 240, 138)));
        private static readonly Brush SunsetRefOrange = Freeze(new SolidColorBrush(Color.FromArgb(71, 251, 146, 60)));
        private static readonly Pen SunsetBirdPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(31, 5, 33))), 1.8));
        private static readonly ImageSource RainMistSprite = BuildSoftOrbSprite(128, 64,
            (Color.FromArgb(33, 204, 251, 241), 0.0), (Color.FromArgb(0, 204, 251, 241), 1.0));

        private sealed class StarSeed
        {
            public double X, Y, R, Phase, Speed;
            public int Hue;
            public Color BaseRgb;
            public Brush[] HueLut = null!;
        }

        private sealed class SnowParticle
        {
            public double X, Y, Vx, Vy, R, Phase;
        }

        private sealed class IceFloe
        {
            public double X, Y, W, H, Vx;
        }

        private sealed class NebulaCloud
        {
            public double X, Y, R, Vx, Vy, Phase;
            public ImageSource Sprite = null!;
        }

        private sealed class GalaxyStarSeed
        {
            public double Dist, BaseAngle, Size;
            public Brush ColBrush = null!;
        }

        private sealed class GalaxySeed
        {
            public double Gx, Gy, Rad, Tilt, RotDeg, Speed, HalfExtent;
            public Brush CoreBrush = null!;
            public List<GalaxyStarSeed> Stars = new();
            public ImageSource? Sprite;
        }

        private sealed class ShootingStar
        {
            public double X, Y, Vx, Vy;
            public int Timer;
        }

        private sealed class OrbitSpeck
        {
            public double Angle, RadiusOffset, Speed, R;
            public bool Icy;
        }

        private sealed class CloudBand
        {
            public double X, Y, W, H, Vx;
        }

        private sealed class Firefly
        {
            public double X, Y, Vx, Vy, Phase, R;
        }

        private sealed class CyberBuilding
        {
            public double X, Y, W, H, SignH;
            public int AccentIdx;
            public Color Accent;
            public bool HasSign;
            public Pen OutlinePen = null!;
            public Brush SignFill = null!;
        }

        private sealed class LightStreak
        {
            public double X, Y, Vx, Len;
            public Color Col;
            public Pen StreakPen = null!;
        }

        private sealed class FalloutParticle
        {
            public double X, Y, Vx, Vy, R, Phase;
            public bool Ember;
        }

        private sealed class Tumbleweed
        {
            public double X, Y, Vx, R, RotDeg, RotSpeedDeg, BouncePhase;
            public double CachedR;
            public Pen? ScaledPen;
        }

        private sealed class DustStreak
        {
            public double X, Y, Vx, Vy, Len, Alpha;
        }

        private sealed class DustDevil
        {
            public double X, Y, H, W, Vx, Phase;
        }

        private sealed class LeafSpore
        {
            public bool IsLeaf;
            public double X, Y, Vx, Vy, R, RotDeg, RotSpeedDeg, Phase;
            public Brush ColBrush = null!;
        }

        private sealed class PuddleRipple
        {
            public double X, Y, Rx, Ry, RippleR, MaxR;
        }

        private sealed class SwimFish
        {
            public double X, Y, Vx, Size, Phase;
            public int Dir;
            public Brush BodyBrush = null!;
        }

        private sealed class Bubble
        {
            public double X, Y, Vx, Vy, R, Phase;
        }

        private sealed class Plankton
        {
            public double X, Y, Vx, Vy, R, Phase;
        }

        private sealed class SkylineWin
        {
            public double Rx, Ry, Phase;
            public Color Col;
            public Brush BrightBrush = null!;
            public Brush DimBrush = null!;
            public Rect AbsRect;
            public bool IsDynamic;
            public bool StaticBright;
        }

        private sealed class SkylineBuilding
        {
            public int Layer, AccentIdx;
            public double X, Y, W, H, WinAlpha;
            public Color BodyCol, AccentCol;
            public Brush BodyBrush = null!;
            public bool HasSpire;
            public List<SkylineWin> Windows = new();
            public List<SkylineWin> DynamicWindows = new();
        }

        private sealed class RuinBuilding
        {
            public double X, W, H;
            public List<Rect> HoleRects = new();
        }

        private sealed class OvergrownTower
        {
            public double X, W, H;
            public bool RoofTree;
            public Brush BodyBrush = null!;
            public List<Rect> HoleRects = new();
        }

        private sealed class BirdGlider
        {
            public double X, Y, Vx, Vy, WingPhase, Size;
        }

        private sealed class RainDrop
        {
            public double X, Y, Speed, Len, Alpha;
        }

        // World state collections
        private string? _seededMode;
        private readonly List<StarSeed> _stars = new();
        private readonly List<SnowParticle> _snow = new();
        private readonly List<IceFloe> _floes = new();
        private readonly List<NebulaCloud> _nebulae = new();
        private readonly List<GalaxySeed> _galaxies = new();
        private ShootingStar? _shootingStar;
        private readonly List<OrbitSpeck> _asteroids = new();
        private readonly List<OrbitSpeck> _kuiper = new();
        private readonly List<CloudBand> _clouds = new();
        private readonly List<Firefly> _fireflies = new();
        private readonly List<CyberBuilding> _cyberBuildings = new();
        private readonly List<LightStreak> _streaks = new();
        private readonly List<FalloutParticle> _fallout = new();
        private readonly List<RuinBuilding> _ruinTowers = new();
        private readonly List<Tumbleweed> _tumbleweeds = new();
        private readonly List<DustStreak> _dustStreaks = new();
        private readonly List<DustDevil> _dustDevils = new();
        private readonly List<LeafSpore> _leafSpores = new();
        private readonly List<PuddleRipple> _puddles = new();
        private readonly List<OvergrownTower> _overgrownTowers = new();
        private readonly List<SwimFish> _fishes = new();
        private readonly List<Bubble> _bubbles = new();
        private readonly List<Plankton> _planktons = new();
        private readonly List<SkylineBuilding> _skylineBuildings = new();
        private readonly List<BirdGlider> _birds = new();
        private readonly List<RainDrop> _rainDrops = new();

        private readonly Dictionary<string, Brush> _planetBrushes = new();
        private readonly Dictionary<(string, int), Brush> _planetMoonBrushes = new();

        public ZenSceneCanvas()
        {
            IsHitTestVisible = false;
            BuildPlanetBrushes();
        }

        public string ActiveSceneId => _sceneId;

        public void SetScene(string sceneId)
        {
            string normalized = NormalizeSceneId(sceneId);
            if (_sceneId != normalized || _seededMode != normalized)
            {
                _sceneId = normalized;
                _t = 0.0;
                InvalidateStaticCaches();
                EnsureSeeded(force: true);
                InvalidateVisual();
            }
        }

        public void EnsureSeeded(bool force = false)
        {
            double w = GetViewportWidth();
            double h = GetViewportHeight();
            if (force || _seededMode != _sceneId || Math.Abs(w - _seedW) > 24 || Math.Abs(h - _seedH) > 24)
            {
                SeedWorld(_sceneId, w, h);
                InvalidateStaticCaches();
            }
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            _dpiScale = newDpi;
            _dpiChecked = true;
            InvalidateStaticCaches();
            InvalidateVisual();
        }

        private void InvalidateStaticCaches()
        {
            _cachedSceneId = null;
            _staticBgBitmap = null;
            _staticFgBitmap = null;
            for (int i = 0; i < _layerBitmaps.Length; i++) _layerBitmaps[i] = null;
            foreach (var g in _galaxies) g.Sprite = null;
            _northRibbonBrushes = null;
            _shipSunbeamGeo = null;
            _shipSunbeamBrush = null;
            _shipSandGeo = null;
            _shipSandBrush = null;
            _oceanSunbeamGeo = null;
            _oceanSunbeamBrush = null;
        }

        public void StepFrame(double dtSeconds)
        {
            if (_sceneId == "none") return;

            EnsureSeeded(force: false);
            double w = _seedW;
            double h = _seedH;
            double scale = Math.Clamp(dtSeconds / 0.016, 0.25, 3.0);
            _t += 0.016 * scale;

            foreach (var st in _stars)
            {
                st.Phase += st.Speed * scale;
            }

            switch (_sceneId)
            {
                case "north_pole":
                    foreach (var fl in _floes)
                    {
                        fl.X += fl.Vx * scale;
                        if (fl.X > w + fl.W) fl.X = -fl.W;
                        if (fl.X < -fl.W) fl.X = w + fl.W;
                    }
                    foreach (var sn in _snow)
                    {
                        sn.X += (sn.Vx + Math.Sin(_t + sn.Phase) * 0.25) * scale;
                        sn.Y += sn.Vy * scale;
                        if (sn.Y > h + 10) { sn.Y = -10; sn.X = _rng.NextDouble() * w; }
                        if (sn.X < 0) sn.X = w;
                        if (sn.X > w) sn.X = 0;
                    }
                    break;

                case "far_space":
                    foreach (var nb in _nebulae)
                    {
                        nb.X += nb.Vx * scale;
                        nb.Y += nb.Vy * scale;
                        nb.Phase += 0.01 * scale;
                        if (nb.X < -nb.R) nb.X = w + nb.R;
                        if (nb.X > w + nb.R) nb.X = -nb.R;
                        if (nb.Y < -nb.R) nb.Y = h + nb.R;
                        if (nb.Y > h + nb.R) nb.Y = -nb.R;
                    }
                    if (_shootingStar != null)
                    {
                        if (_shootingStar.Timer > 0)
                        {
                            _shootingStar.Timer -= (int)Math.Max(1, Math.Round(scale));
                        }
                        else
                        {
                            _shootingStar.X += _shootingStar.Vx * scale;
                            _shootingStar.Y += _shootingStar.Vy * scale;
                            if (_shootingStar.X < -120 || _shootingStar.Y > h + 120)
                            {
                                _shootingStar.X = w * (0.35 + _rng.NextDouble() * 0.6);
                                _shootingStar.Y = _rng.NextDouble() * (h * 0.35);
                                _shootingStar.Timer = 180 + _rng.Next(0, 220);
                            }
                        }
                    }
                    break;

                case "moon_night":
                    foreach (var cl in _clouds)
                    {
                        cl.X += cl.Vx * scale;
                        if (cl.X - cl.W > w) cl.X = -cl.W;
                    }
                    double moonHor = h * 0.70;
                    foreach (var ff in _fireflies)
                    {
                        ff.X += ff.Vx * scale;
                        ff.Y += ff.Vy * scale;
                        ff.Phase += 0.06 * scale;
                        if (ff.X < 0) ff.X = w;
                        if (ff.X > w) ff.X = 0;
                        if (ff.Y < moonHor - 60) ff.Vy = Math.Abs(ff.Vy);
                        if (ff.Y > h - 10) ff.Vy = -Math.Abs(ff.Vy);
                    }
                    break;

                case "cyber_city":
                    foreach (var v in _streaks)
                    {
                        v.X += v.Vx * scale;
                        if (v.X > w + 80) v.X = -80;
                        if (v.X < -80) v.X = w + 80;
                    }
                    break;

                case "nuclear_wasteland":
                    foreach (var p in _fallout)
                    {
                        p.X += p.Vx * scale;
                        p.Y += (p.Vy + Math.Sin(_t + p.Phase) * 0.25) * scale;
                        if (p.X > w + 20) p.X = -20;
                        if (p.Y < -20) p.Y = h + 20;
                        if (p.Y > h + 20) p.Y = -20;
                    }
                    break;

                case "wasteland":
                    foreach (var dd in _dustDevils)
                    {
                        dd.X += dd.Vx * scale;
                        if (dd.X < -60) dd.X = w + 60;
                    }
                    foreach (var tw in _tumbleweeds)
                    {
                        tw.X += tw.Vx * scale;
                        tw.RotDeg += tw.RotSpeedDeg * scale;
                        if (tw.X < -tw.R * 2)
                        {
                            tw.X = w + tw.R * 2;
                            tw.R = 12 + _rng.NextDouble() * 11;
                        }
                    }
                    double wasteHor = h * 0.70;
                    foreach (var ds in _dustStreaks)
                    {
                        ds.X += ds.Vx * scale;
                        ds.Y += ds.Vy * scale;
                        if (ds.X < -30) ds.X = w + 30;
                        if (ds.Y > h || ds.Y < wasteHor) ds.Y = wasteHor + _rng.NextDouble() * (h - wasteHor);
                    }
                    break;

                case "city_wasteland":
                    foreach (var pd in _puddles)
                    {
                        pd.RippleR = (pd.RippleR + 0.22 * scale) % pd.MaxR;
                    }
                    foreach (var lp in _leafSpores)
                    {
                        lp.X += (lp.Vx + Math.Sin(_t * 1.5 + lp.Phase) * 0.35) * scale;
                        lp.Y += lp.Vy * scale;
                        lp.RotDeg += lp.RotSpeedDeg * scale;
                        if (lp.X > w + 20) lp.X = -20;
                        if (lp.Y > h + 20) { lp.Y = -20; lp.X = _rng.NextDouble() * w; }
                    }
                    break;

                case "desert":
                    foreach (var ds in _dustStreaks)
                    {
                        ds.X += ds.Vx * scale;
                        if (ds.X > w + 60) ds.X = -40;
                    }
                    break;

                case "lake_forest":
                    foreach (var ff in _fireflies)
                    {
                        ff.X += ff.Vx * scale;
                        ff.Y += ff.Vy * scale;
                        ff.Phase += 0.05 * scale;
                        if (ff.X < 0) ff.X = w;
                        if (ff.X > w) ff.X = 0;
                        if (ff.Y < h * 0.3) ff.Vy = Math.Abs(ff.Vy);
                        if (ff.Y > h - 10) ff.Vy = -Math.Abs(ff.Vy);
                    }
                    break;

                case "shipwreck":
                    foreach (var f in _fishes)
                    {
                        f.X += f.Vx * scale;
                        f.Y += Math.Sin(_t * 2 + f.Phase) * 0.4 * scale;
                        if (f.Vx > 0 && f.X > w + 30) f.X = -30;
                        if (f.Vx < 0 && f.X < -30) f.X = w + 30;
                    }
                    foreach (var b in _bubbles)
                    {
                        b.Y += b.Vy * scale;
                        b.X += (b.Vx + Math.Sin(_t * 2 + b.Phase) * 0.3) * scale;
                        if (b.Y < -15) { b.Y = h + 15; b.X = _rng.NextDouble() * w; }
                    }
                    foreach (var pl in _planktons)
                    {
                        pl.Y += pl.Vy * scale;
                        pl.X += (pl.Vx + Math.Sin(_t + pl.Phase) * 0.2) * scale;
                        if (pl.Y < -10) { pl.Y = h + 10; pl.X = _rng.NextDouble() * w; }
                    }
                    break;

                case "ocean_life":
                    foreach (var f in _fishes)
                    {
                        f.X += f.Vx * scale;
                        f.Phase += 0.12 * scale;
                        if (f.Dir > 0 && f.X > w + 30) f.X = -30;
                        if (f.Dir < 0 && f.X < -30) f.X = w + 30;
                    }
                    foreach (var b in _bubbles)
                    {
                        b.Y -= b.Vy * scale;
                        if (b.Y < -15) { b.Y = h + 15; b.X = _rng.NextDouble() * w; }
                    }
                    break;

                case "snowy_peaks":
                    foreach (var cl in _clouds)
                    {
                        cl.X += cl.Vx * scale;
                        if (cl.X - cl.W > w) cl.X = -cl.W;
                    }
                    foreach (var sn in _snow)
                    {
                        sn.X += sn.Vx * scale;
                        sn.Y += (sn.Vy + Math.Sin(_t + sn.Phase) * 0.25) * scale;
                        if (sn.X > w + 10) sn.X = -10;
                        if (sn.Y > h + 10) sn.Y = -10;
                    }
                    break;

                case "city_skyline":
                case "city_shoreline":
                    foreach (var tr in _streaks)
                    {
                        tr.X += tr.Vx * scale;
                        if (tr.X > w + 80) tr.X = -80;
                        if (tr.X < -80) tr.X = w + 80;
                    }
                    break;

                case "sunset":
                    foreach (var cl in _clouds)
                    {
                        cl.X += cl.Vx * scale;
                        if (cl.X - cl.W > w) cl.X = -cl.W;
                    }
                    foreach (var bd in _birds)
                    {
                        bd.X += bd.Vx * scale;
                        bd.Y += bd.Vy * scale;
                        bd.WingPhase += 0.12 * scale;
                        if (bd.X > w + 30)
                        {
                            bd.X = -30;
                            bd.Y = h * 0.18 + _rng.NextDouble() * (h * 0.32);
                        }
                    }
                    break;

                case "rainy_forest":
                    foreach (var cl in _clouds)
                    {
                        cl.X += cl.Vx * scale;
                        if (cl.X > w + cl.W * 0.5) cl.X = -cl.W * 0.5;
                        if (cl.X < -cl.W * 0.5) cl.X = w + cl.W * 0.5;
                    }
                    foreach (var r in _rainDrops)
                    {
                        r.Y += r.Speed * scale;
                        r.X -= r.Speed * 0.15 * scale;
                        if (r.Y > h + 30)
                        {
                            r.Y = -30;
                            r.X = _rng.NextDouble() * (w + 180);
                        }
                    }
                    break;
            }

            InvalidateVisual();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            if (sizeInfo.NewSize.Width > 50 && sizeInfo.NewSize.Height > 50)
            {
                EnsureSeeded(force: true);
                InvalidateVisual();
            }
        }

        private double GetViewportWidth() => ActualWidth > 50 ? ActualWidth : (RenderSize.Width > 50 ? RenderSize.Width : 1440);
        private double GetViewportHeight() => ActualHeight > 50 ? ActualHeight : (RenderSize.Height > 50 ? RenderSize.Height : 900);

        private static string NormalizeSceneId(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "north_pole";
            if (string.Equals(id, "custom", StringComparison.OrdinalIgnoreCase)) return "north_pole";
            return id.Trim().ToLowerInvariant();
        }

        private void SeedWorld(string sceneId, double w, double h)
        {
            _seededMode = sceneId;
            _seedW = w;
            _seedH = h;

            _stars.Clear();
            _snow.Clear();
            _floes.Clear();
            _nebulae.Clear();
            _galaxies.Clear();
            _shootingStar = null;
            _asteroids.Clear();
            _kuiper.Clear();
            _clouds.Clear();
            _fireflies.Clear();
            _cyberBuildings.Clear();
            _streaks.Clear();
            _fallout.Clear();
            _ruinTowers.Clear();
            _tumbleweeds.Clear();
            _dustStreaks.Clear();
            _dustDevils.Clear();
            _leafSpores.Clear();
            _puddles.Clear();
            _overgrownTowers.Clear();
            _fishes.Clear();
            _bubbles.Clear();
            _planktons.Clear();
            _skylineBuildings.Clear();
            _birds.Clear();
            _rainDrops.Clear();

            int[] starHues = { 210, 45, 280, 190, 330 };
            for (int i = 0; i < 135; i++)
            {
                int hueIdx = i % starHues.Length;
                int hue = starHues[hueIdx];
                _stars.Add(new StarSeed
                {
                    X = _rng.NextDouble() * w,
                    Y = _rng.NextDouble() * h,
                    R = _rng.NextDouble() * 1.8 + 0.4,
                    Phase = _rng.NextDouble() * Math.PI * 2,
                    Speed = 0.018 + _rng.NextDouble() * 0.038,
                    Hue = hue,
                    BaseRgb = ColorFromHsl(hue, 0.85, 0.85),
                    HueLut = StarHueLuts[hueIdx]
                });
            }

            switch (sceneId)
            {
                case "north_pole":
                    for (int i = 0; i < 65; i++)
                    {
                        _snow.Add(new SnowParticle
                        {
                            X = _rng.NextDouble() * w,
                            Y = _rng.NextDouble() * h,
                            Vx = (_rng.NextDouble() - 0.5) * 0.6,
                            Vy = 0.4 + _rng.NextDouble() * 0.9,
                            R = 1.0 + _rng.NextDouble() * 2.2,
                            Phase = _rng.NextDouble() * Math.PI * 2
                        });
                    }
                    for (int f = 0; f < 9; f++)
                    {
                        double depth = f / 9.0;
                        _floes.Add(new IceFloe
                        {
                            X = _rng.NextDouble() * w,
                            Y = h * 0.75 + depth * (h * 0.21),
                            W = 45 + depth * 90,
                            H = 6 + depth * 12,
                            Vx = (0.12 + depth * 0.18) * (f % 2 == 0 ? 1 : -1)
                        });
                    }
                    break;

                case "far_space":
                    for (int i = 0; i < 12; i++)
                    {
                        _nebulae.Add(new NebulaCloud
                        {
                            X = _rng.NextDouble() * w,
                            Y = _rng.NextDouble() * h,
                            R = 140 + _rng.NextDouble() * 230,
                            Vx = (_rng.NextDouble() - 0.5) * 0.12,
                            Vy = (_rng.NextDouble() - 0.5) * 0.12,
                            Phase = _rng.NextDouble() * Math.PI * 2,
                            Sprite = NebulaSprites[i % NebulaSprites.Length]
                        });
                    }

                    double minDim = Math.Min(w, h);
                    var defs = new[]
                    {
                        (gx: w * 0.75, gy: h * 0.31, rad: minDim * 0.17, tilt: 0.50, rot: -0.32, spd: 0.16, arms: 2, core: Color.FromArgb(235, 254, 249, 195), mid: Color.FromRgb(244, 114, 182), outer: Color.FromRgb(56, 189, 248), count: 130),
                        (gx: w * 0.21, gy: h * 0.67, rad: minDim * 0.125, tilt: 0.42, rot: 0.48, spd: -0.13, arms: 3, core: Color.FromArgb(230, 224, 242, 254), mid: Color.FromRgb(167, 139, 250), outer: Color.FromRgb(34, 211, 238), count: 95),
                        (gx: w * 0.26, gy: h * 0.23, rad: minDim * 0.078, tilt: 0.34, rot: -0.65, spd: 0.21, arms: 2, core: Color.FromArgb(224, 253, 230, 138), mid: Color.FromRgb(251, 146, 60), outer: Color.FromRgb(192, 132, 252), count: 65),
                        (gx: w * 0.80, gy: h * 0.76, rad: minDim * 0.068, tilt: 0.62, rot: 0.22, spd: 0.14, arms: 2, core: Color.FromArgb(217, 167, 243, 208), mid: Color.FromRgb(52, 211, 153), outer: Color.FromRgb(129, 140, 248), count: 55)
                    };

                    var creamBrush = Freeze(new SolidColorBrush(Color.FromRgb(254, 249, 195)));
                    foreach (var gd in defs)
                    {
                        var midBrush = Freeze(new SolidColorBrush(gd.mid));
                        var outBrush = Freeze(new SolidColorBrush(gd.outer));
                        var coreGrad = new RadialGradientBrush(
                            new GradientStopCollection
                            {
                                new GradientStop(gd.core, 0.0),
                                new GradientStop(Color.FromArgb(85, gd.mid.R, gd.mid.G, gd.mid.B), 0.42),
                                new GradientStop(Color.FromArgb(0, 0, 0, 0), 1.0)
                            })
                        { Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.5, 0.5), RadiusX = 0.5, RadiusY = 0.5 };
                        coreGrad.Freeze();

                        var gStars = new List<GalaxyStarSeed>(gd.count);
                        for (int g = 0; g < gd.count; g++)
                        {
                            int arm = g % gd.arms;
                            double dist = (double)g / gd.count;
                            gStars.Add(new GalaxyStarSeed
                            {
                                Dist = dist,
                                BaseAngle = dist * Math.PI * 3.6 + (arm * (Math.PI * 2 / gd.arms)) + (_rng.NextDouble() - 0.5) * 0.42,
                                Size = (1.0 - dist * 0.55) * 2.0,
                                ColBrush = dist < 0.24 ? creamBrush : (dist < 0.62 ? midBrush : outBrush)
                            });
                        }

                        _galaxies.Add(new GalaxySeed
                        {
                            Gx = gd.gx, Gy = gd.gy, Rad = gd.rad, Tilt = gd.tilt,
                            RotDeg = gd.rot * 180.0 / Math.PI, Speed = gd.spd,
                            HalfExtent = Math.Ceiling(gd.rad + 4.0),
                            CoreBrush = coreGrad, Stars = gStars
                        });
                    }

                    _shootingStar = new ShootingStar
                    {
                        X = _rng.NextDouble() * w,
                        Y = _rng.NextDouble() * h * 0.5,
                        Vx = -6.5, Vy = 3.2, Timer = 140
                    };
                    break;

                case "solar_system":
                    for (int a = 0; a < 110; a++)
                    {
                        _asteroids.Add(new OrbitSpeck
                        {
                            Angle = _rng.NextDouble() * Math.PI * 2,
                            RadiusOffset = (_rng.NextDouble() - 0.5) * 0.028,
                            Speed = 0.18 + _rng.NextDouble() * 0.07,
                            R = 0.8 + _rng.NextDouble() * 1.4
                        });
                    }
                    for (int k = 0; k < 185; k++)
                    {
                        _kuiper.Add(new OrbitSpeck
                        {
                            Angle = _rng.NextDouble() * Math.PI * 2,
                            RadiusOffset = (_rng.NextDouble() - 0.5) * 0.065,
                            Speed = 0.06 + _rng.NextDouble() * 0.04,
                            R = 0.8 + _rng.NextDouble() * 1.7,
                            Icy = k % 3 == 0
                        });
                    }
                    break;

                case "moon_night":
                    for (int c = 0; c < 7; c++)
                    {
                        _clouds.Add(new CloudBand
                        {
                            X = _rng.NextDouble() * w,
                            Y = h * 0.14 + _rng.NextDouble() * (h * 0.34),
                            W = 160 + _rng.NextDouble() * 200,
                            H = 24 + _rng.NextDouble() * 26,
                            Vx = 0.12 + _rng.NextDouble() * 0.22
                        });
                    }
                    for (int f = 0; f < 36; f++)
                    {
                        _fireflies.Add(new Firefly
                        {
                            X = _rng.NextDouble() * w,
                            Y = h * 0.52 + _rng.NextDouble() * (h * 0.45),
                            Vx = (_rng.NextDouble() - 0.5) * 0.45,
                            Vy = (_rng.NextDouble() - 0.5) * 0.35,
                            Phase = _rng.NextDouble() * Math.PI * 2,
                            R = 1.5 + _rng.NextDouble() * 2.0
                        });
                    }
                    break;

                case "cyber_city":
                    double cyberBaseY = h * 0.82;
                    Color[] neonCols =
                    {
                        Color.FromRgb(34, 211, 238), Color.FromRgb(244, 114, 182),
                        Color.FromRgb(168, 85, 247), Color.FromRgb(253, 224, 71), Color.FromRgb(56, 189, 248)
                    };
                    for (int i = 0; i < 28; i++)
                    {
                        double bw = 46 + ((i * 39) % 64);
                        double bh = (0.24 + ((i * 61) % 48) / 100.0) * h;
                        double bx = (i / 28.0) * (w + 80) - 40;
                        int accIdx = i % neonCols.Length;
                        Color acc = neonCols[accIdx];
                        var pen = new Pen(Freeze(new SolidColorBrush(Color.FromArgb(140, acc.R, acc.G, acc.B))), 1.5);
                        pen.Freeze();
                        var signBrush = Freeze(new SolidColorBrush(Color.FromArgb(92, acc.R, acc.G, acc.B)));
                        _cyberBuildings.Add(new CyberBuilding
                        {
                            X = bx, Y = cyberBaseY - bh, W = bw, H = bh,
                            AccentIdx = accIdx, Accent = acc, HasSign = i % 3 == 0, SignH = 18 + (i % 4) * 10,
                            OutlinePen = pen, SignFill = signBrush
                        });
                    }
                    for (int v = 0; v < 22; v++)
                    {
                        int dir = v % 2 == 0 ? 1 : -1;
                        double vx = dir * (1.8 + _rng.NextDouble() * 2.8);
                        double len = 22 + _rng.NextDouble() * 34;
                        Color col = dir > 0 ? Color.FromRgb(34, 211, 238) : Color.FromRgb(244, 63, 94);
                        var stGrad = MakeLinearAbs(new Point(0, 0), new Point(-dir * len, 0), (col, 0.0), (Color.FromArgb(0, 0, 0, 0), 1.0));
                        var stPen = Freeze(new Pen(stGrad, 2.4));
                        _streaks.Add(new LightStreak
                        {
                            X = _rng.NextDouble() * w,
                            Y = h * 0.25 + (v / 22.0) * (h * 0.48),
                            Vx = vx,
                            Len = len,
                            Col = col,
                            StreakPen = stPen
                        });
                    }
                    break;

                case "nuclear_wasteland":
                    for (int i = 0; i < 75; i++)
                    {
                        _fallout.Add(new FalloutParticle
                        {
                            X = _rng.NextDouble() * w,
                            Y = _rng.NextDouble() * h,
                            Vx = 0.45 + _rng.NextDouble() * 0.95,
                            Vy = -0.15 + (_rng.NextDouble() - 0.5) * 0.4,
                            R = 1.0 + _rng.NextDouble() * 2.4,
                            Ember = i < 22,
                            Phase = _rng.NextDouble() * Math.PI * 2
                        });
                    }
                    double nHor = h * 0.68;
                    var rawRuins = new[]
                    {
                        (x: w * 0.06, rw: 38.0, rh: 90.0),
                        (x: w * 0.12, rw: 50.0, rh: 140.0),
                        (x: w * 0.48, rw: 44.0, rh: 110.0),
                        (x: w * 0.54, rw: 58.0, rh: 165.0),
                        (x: w * 0.62, rw: 36.0, rh: 80.0),
                        (x: w * 0.85, rw: 65.0, rh: 130.0),
                        (x: w * 0.92, rw: 42.0, rh: 75.0)
                    };
                    for (int ri = 0; ri < rawRuins.Length; ri++)
                    {
                        var r = rawRuins[ri];
                        var holes = new List<Rect>();
                        var rr = new Random(910 + ri);
                        for (double wy = nHor - r.rh + 12; wy < nHor - 8; wy += 14)
                        {
                            for (double wx = r.x + 6; wx < r.x + r.rw - 8; wx += 10)
                            {
                                if (rr.NextDouble() > 0.35) holes.Add(new Rect(wx, wy, 5, 8));
                            }
                        }
                        _ruinTowers.Add(new RuinBuilding { X = r.x, W = r.rw, H = r.rh, HoleRects = holes });
                    }
                    break;

                case "wasteland":
                    for (int tw = 0; tw < 3; tw++)
                    {
                        _tumbleweeds.Add(new Tumbleweed
                        {
                            X = w * (0.25 + tw * 0.35),
                            Y = h * 0.77,
                            Vx = -(1.3 + _rng.NextDouble() * 0.8),
                            R = 13 + _rng.NextDouble() * 11,
                            RotDeg = _rng.NextDouble() * 360.0,
                            RotSpeedDeg = (-0.04 - _rng.NextDouble() * 0.03) * 180.0 / Math.PI,
                            BouncePhase = _rng.NextDouble() * Math.PI * 2
                        });
                    }
                    for (int i = 0; i < 55; i++)
                    {
                        _dustStreaks.Add(new DustStreak
                        {
                            X = _rng.NextDouble() * w,
                            Y = h * 0.42 + _rng.NextDouble() * (h * 0.58),
                            Vx = -(1.5 + _rng.NextDouble() * 1.8),
                            Vy = (_rng.NextDouble() - 0.5) * 0.25,
                            Len = 12 + _rng.NextDouble() * 24,
                            Alpha = 0.12 + _rng.NextDouble() * 0.18
                        });
                    }
                    for (int dd = 0; dd < 2; dd++)
                    {
                        _dustDevils.Add(new DustDevil
                        {
                            X = w * (0.22 + dd * 0.52),
                            Y = h * 0.72,
                            H = 95 + dd * 35,
                            W = 20 + dd * 12,
                            Vx = -(0.25 + dd * 0.15),
                            Phase = _rng.NextDouble() * Math.PI * 2
                        });
                    }
                    break;

                case "city_wasteland":
                    Color[] leafCols =
                    {
                        Color.FromRgb(74, 222, 128), Color.FromRgb(34, 197, 94),
                        Color.FromRgb(134, 239, 172), Color.FromRgb(163, 230, 53),
                        Color.FromRgb(202, 138, 4), Color.FromRgb(21, 128, 61)
                    };
                    for (int i = 0; i < 50; i++)
                    {
                        bool isLeaf = i < 20;
                        _leafSpores.Add(new LeafSpore
                        {
                            IsLeaf = isLeaf,
                            X = _rng.NextDouble() * w,
                            Y = _rng.NextDouble() * h,
                            Vx = 0.35 + _rng.NextDouble() * 0.7,
                            Vy = 0.35 + _rng.NextDouble() * 0.65,
                            R = isLeaf ? (4.5 + _rng.NextDouble() * 5.5) : (1.2 + _rng.NextDouble() * 2.0),
                            RotDeg = _rng.NextDouble() * 360.0,
                            RotSpeedDeg = (_rng.NextDouble() - 0.5) * 0.04 * 180.0 / Math.PI,
                            ColBrush = Freeze(new SolidColorBrush(leafCols[i % leafCols.Length])),
                            Phase = _rng.NextDouble() * Math.PI * 2
                        });
                    }
                    for (int p = 0; p < 4; p++)
                    {
                        _puddles.Add(new PuddleRipple
                        {
                            X = w * (0.16 + p * 0.23),
                            Y = h * 0.88 + (p % 2) * 16,
                            Rx = 34 + p * 14,
                            Ry = 8 + p * 3,
                            RippleR = (p * 7) % 25,
                            MaxR = 26 + p * 8
                        });
                    }
                    double cwHor = h * 0.72;
                    var rawCw = new[]
                    {
                        (x: w * 0.03, tw: 75.0, th: h * 0.58, rt: true),
                        (x: w * 0.15, tw: 90.0, th: h * 0.72, rt: false),
                        (x: w * 0.32, tw: 80.0, th: h * 0.52, rt: true),
                        (x: w * 0.47, tw: 105.0, th: h * 0.68, rt: true),
                        (x: w * 0.65, tw: 70.0, th: h * 0.48, rt: false),
                        (x: w * 0.78, tw: 95.0, th: h * 0.65, rt: true),
                        (x: w * 0.90, tw: 85.0, th: h * 0.55, rt: true)
                    };
                    var cwBrushA = Freeze(new SolidColorBrush(Color.FromRgb(18, 32, 30)));
                    var cwBrushB = Freeze(new SolidColorBrush(Color.FromRgb(14, 26, 24)));
                    for (int ti = 0; ti < rawCw.Length; ti++)
                    {
                        var t = rawCw[ti];
                        double ty = cwHor - t.th;
                        int cols = (int)Math.Floor((t.tw - 12) / 12);
                        int rows = (int)Math.Floor((t.th - 20) / 16);
                        var holes = new List<Rect>();
                        var trng = new Random(1700 + ti);
                        for (int r = 0; r < rows; r++)
                        {
                            for (int c = 0; c < cols; c++)
                            {
                                if (trng.NextDouble() > 0.32)
                                    holes.Add(new Rect(t.x + 6 + c * 12, ty + 10 + r * 16, 7, 10));
                            }
                        }
                        _overgrownTowers.Add(new OvergrownTower
                        {
                            X = t.x, W = t.tw, H = t.th, RoofTree = t.rt,
                            BodyBrush = ti % 2 == 0 ? cwBrushA : cwBrushB,
                            HoleRects = holes
                        });
                    }
                    break;

                case "desert":
                    for (int i = 0; i < 65; i++)
                    {
                        _dustStreaks.Add(new DustStreak
                        {
                            X = _rng.NextDouble() * w,
                            Y = h * 0.45 + _rng.NextDouble() * (h * 0.55),
                            Vx = 0.9 + _rng.NextDouble() * 1.4,
                            Vy = (_rng.NextDouble() - 0.5) * 0.2,
                            Len = 18 + _rng.NextDouble() * 28,
                            Alpha = 0.08 + _rng.NextDouble() * 0.16
                        });
                    }
                    break;

                case "lake_forest":
                    for (int i = 0; i < 34; i++)
                    {
                        _fireflies.Add(new Firefly
                        {
                            X = _rng.NextDouble() * w,
                            Y = h * 0.35 + _rng.NextDouble() * (h * 0.58),
                            Vx = (_rng.NextDouble() - 0.5) * 0.35,
                            Vy = (_rng.NextDouble() - 0.5) * 0.25,
                            Phase = _rng.NextDouble() * Math.PI * 2,
                            R = 1.4 + _rng.NextDouble() * 1.8
                        });
                    }
                    break;

                case "shipwreck":
                    Color[] reefFish =
                    {
                        Color.FromRgb(56, 189, 248), Color.FromRgb(45, 212, 191),
                        Color.FromRgb(251, 191, 36), Color.FromRgb(244, 114, 182), Color.FromRgb(52, 211, 153)
                    };
                    for (int f = 0; f < 18; f++)
                    {
                        int dir = f % 2 == 0 ? 1 : -1;
                        _fishes.Add(new SwimFish
                        {
                            X = _rng.NextDouble() * w,
                            Y = h * 0.28 + _rng.NextDouble() * (h * 0.48),
                            Vx = dir * (0.4 + _rng.NextDouble() * 0.7),
                            Size = 4 + _rng.NextDouble() * 6,
                            Dir = dir,
                            BodyBrush = Freeze(new SolidColorBrush(reefFish[f % reefFish.Length])),
                            Phase = _rng.NextDouble() * Math.PI * 2
                        });
                    }
                    for (int b = 0; b < 35; b++)
                    {
                        _bubbles.Add(new Bubble
                        {
                            X = _rng.NextDouble() * w,
                            Y = _rng.NextDouble() * h,
                            Vy = -0.35 - _rng.NextDouble() * 0.65,
                            Vx = (_rng.NextDouble() - 0.5) * 0.2,
                            R = 1.2 + _rng.NextDouble() * 2.8,
                            Phase = _rng.NextDouble() * Math.PI * 2
                        });
                    }
                    for (int p = 0; p < 30; p++)
                    {
                        _planktons.Add(new Plankton
                        {
                            X = _rng.NextDouble() * w,
                            Y = _rng.NextDouble() * h,
                            Vx = (_rng.NextDouble() - 0.5) * 0.25,
                            Vy = -0.15 - _rng.NextDouble() * 0.35,
                            R = 1.0 + _rng.NextDouble() * 1.8,
                            Phase = _rng.NextDouble() * Math.PI * 2
                        });
                    }
                    break;

                case "ocean_life":
                    Color[] oceanFish =
                    {
                        Color.FromRgb(56, 189, 248), Color.FromRgb(251, 146, 60),
                        Color.FromRgb(244, 114, 182), Color.FromRgb(253, 224, 71), Color.FromRgb(52, 211, 153)
                    };
                    for (int f = 0; f < 32; f++)
                    {
                        int dir = f % 3 == 0 ? -1 : 1;
                        var fc = oceanFish[f % oceanFish.Length];
                        _fishes.Add(new SwimFish
                        {
                            X = _rng.NextDouble() * w,
                            Y = h * 0.16 + _rng.NextDouble() * (h * 0.66),
                            Vx = dir * (0.45 + _rng.NextDouble() * 0.85),
                            Size = 5 + _rng.NextDouble() * 8,
                            BodyBrush = Freeze(new SolidColorBrush(Color.FromArgb(209, fc.R, fc.G, fc.B))),
                            Phase = _rng.NextDouble() * Math.PI * 2,
                            Dir = dir
                        });
                    }
                    for (int b = 0; b < 45; b++)
                    {
                        _bubbles.Add(new Bubble
                        {
                            X = _rng.NextDouble() * w,
                            Y = _rng.NextDouble() * h,
                            Vy = 0.4 + _rng.NextDouble() * 0.8,
                            R = 1.2 + _rng.NextDouble() * 3.2,
                            Phase = _rng.NextDouble() * Math.PI * 2
                        });
                    }
                    break;

                case "snowy_peaks":
                    for (int s = 0; s < 80; s++)
                    {
                        _snow.Add(new SnowParticle
                        {
                            X = _rng.NextDouble() * w,
                            Y = _rng.NextDouble() * h,
                            Vx = 0.55 + _rng.NextDouble() * 1.1,
                            Vy = 0.35 + _rng.NextDouble() * 0.75,
                            R = 1.0 + _rng.NextDouble() * 2.2,
                            Phase = _rng.NextDouble() * Math.PI * 2
                        });
                    }
                    for (int c = 0; c < 14; c++)
                    {
                        _clouds.Add(new CloudBand
                        {
                            X = (c / 14.0) * w,
                            Y = h * 0.72 + (c % 3) * 24,
                            W = 220 + _rng.NextDouble() * 180,
                            H = 45 + _rng.NextDouble() * 30,
                            Vx = 0.12 + _rng.NextDouble() * 0.18
                        });
                    }
                    break;

                case "city_skyline":
                case "city_shoreline":
                    bool isShore = sceneId == "city_shoreline";
                    double baseY = isShore ? h * 0.62 : h * 0.88;
                    var layers = new[]
                    {
                        (count: 22, minW: 42, maxW: 78, minH: 0.22, maxH: 0.44, col: Color.FromRgb(11, 17, 36), winA: 0.35),
                        (count: 18, minW: 48, maxW: 88, minH: 0.18, maxH: 0.38, col: Color.FromRgb(7, 11, 24), winA: 0.65),
                        (count: 14, minW: 54, maxW: 96, minH: 0.12, maxH: 0.30, col: Color.FromRgb(3, 6, 17), winA: 0.90)
                    };
                    Color[] cityNeon =
                    {
                        Color.FromRgb(251, 191, 36), Color.FromRgb(56, 189, 248),
                        Color.FromRgb(244, 114, 182), Color.FromRgb(167, 139, 250), Color.FromRgb(254, 240, 138)
                    };
                    for (int lIdx = 0; lIdx < layers.Length; lIdx++)
                    {
                        var L = layers[lIdx];
                        var bBrush = Freeze(new SolidColorBrush(L.col));
                        byte brightA = AlphaByte(L.winA);
                        byte dimA = AlphaByte(L.winA * 0.15);
                        for (int i = 0; i < L.count; i++)
                        {
                            double bw = L.minW + ((i * 37 + lIdx * 19) % (L.maxW - L.minW));
                            double bh = (L.minH + (((i * 53 + lIdx * 29) % 100) / 100.0) * (L.maxH - L.minH)) * h;
                            double bx = ((double)i / L.count) * (w + 60) - 30;
                            double by = baseY - bh;
                            bool hasSpire = lIdx >= 1 && i % 3 == 0;
                            int accIdx = (i + lIdx) % cityNeon.Length;
                            Color accCol = cityNeon[accIdx];
                            var wins = new List<SkylineWin>();
                            var dynWins = new List<SkylineWin>();
                            int cols = Math.Max(2, (int)Math.Floor((bw - 12) / 10));
                            int rows = Math.Max(3, (int)Math.Floor((bh - 16) / 14));
                            for (int r = 0; r < rows; r++)
                            {
                                for (int c = 0; c < cols; c++)
                                {
                                    if (_rng.NextDouble() < 0.58)
                                    {
                                        double pick = _rng.NextDouble();
                                        Color wc = pick < 0.72
                                            ? Color.FromRgb(253, 230, 138)
                                            : (pick < 0.86 ? Color.FromRgb(186, 230, 253) : Color.FromRgb(251, 207, 232));
                                        double rx = 6 + c * 10;
                                        double ry = 10 + r * 14;
                                        bool isDyn = _rng.NextDouble() < 0.14;
                                        bool stBright = _rng.NextDouble() < 0.77;
                                        var sw = new SkylineWin
                                        {
                                            Rx = rx,
                                            Ry = ry,
                                            Col = wc,
                                            Phase = _rng.NextDouble() * Math.PI * 2,
                                            BrightBrush = Freeze(new SolidColorBrush(Color.FromArgb(brightA, wc.R, wc.G, wc.B))),
                                            DimBrush = Freeze(new SolidColorBrush(Color.FromArgb(dimA, wc.R, wc.G, wc.B))),
                                            AbsRect = new Rect(bx + rx, by + ry, 5, 8),
                                            IsDynamic = isDyn,
                                            StaticBright = stBright
                                        };
                                        wins.Add(sw);
                                        if (isDyn) dynWins.Add(sw);
                                    }
                                }
                            }
                            _skylineBuildings.Add(new SkylineBuilding
                            {
                                Layer = lIdx, AccentIdx = accIdx, X = bx, Y = by, W = bw, H = bh,
                                BodyCol = L.col, BodyBrush = bBrush, WinAlpha = L.winA,
                                HasSpire = hasSpire, AccentCol = accCol, Windows = wins, DynamicWindows = dynWins
                            });
                        }
                    }
                    for (int m = 0; m < 8; m++)
                    {
                        Color mCol = m % 2 == 0 ? Color.FromRgb(56, 189, 248) : Color.FromRgb(251, 191, 36);
                        _streaks.Add(new LightStreak
                        {
                            X = _rng.NextDouble() * w,
                            Y = isShore ? (baseY + 25 + _rng.NextDouble() * (h - baseY - 40)) : (baseY - 20 - _rng.NextDouble() * 60),
                            Vx = (0.8 + _rng.NextDouble() * 1.4) * (m % 2 == 0 ? 1 : -1),
                            Col = mCol,
                            Len = isShore ? 18 : 42,
                            StreakPen = Freeze(new Pen(Freeze(new SolidColorBrush(mCol)), 2.2))
                        });
                    }
                    break;

                case "sunset":
                    for (int c = 0; c < 8; c++)
                    {
                        _clouds.Add(new CloudBand
                        {
                            X = _rng.NextDouble() * w,
                            Y = h * 0.12 + _rng.NextDouble() * (h * 0.35),
                            W = 140 + _rng.NextDouble() * 180,
                            H = 22 + _rng.NextDouble() * 26,
                            Vx = 0.15 + _rng.NextDouble() * 0.25
                        });
                    }
                    for (int b = 0; b < 12; b++)
                    {
                        _birds.Add(new BirdGlider
                        {
                            X = _rng.NextDouble() * w,
                            Y = h * 0.18 + _rng.NextDouble() * (h * 0.32),
                            Vx = 0.65 + _rng.NextDouble() * 0.55,
                            Vy = (_rng.NextDouble() - 0.5) * 0.15,
                            WingPhase = _rng.NextDouble() * Math.PI * 2,
                            Size = 5 + _rng.NextDouble() * 5
                        });
                    }
                    break;

                case "rainy_forest":
                    for (int m = 0; m < 7; m++)
                    {
                        _clouds.Add(new CloudBand
                        {
                            X = _rng.NextDouble() * w,
                            Y = h * 0.48 + (m / 7.0) * (h * 0.42),
                            W = w * 0.55 + _rng.NextDouble() * (w * 0.35),
                            H = 38 + _rng.NextDouble() * 45,
                            Vx = (0.25 + _rng.NextDouble() * 0.3) * (m % 2 == 0 ? 1 : -1)
                        });
                    }
                    for (int r = 0; r < 130; r++)
                    {
                        _rainDrops.Add(new RainDrop
                        {
                            X = _rng.NextDouble() * (w + 200) - 100,
                            Y = _rng.NextDouble() * h,
                            Speed = 10 + _rng.NextDouble() * 7,
                            Len = 18 + _rng.NextDouble() * 22,
                            Alpha = 0.18 + _rng.NextDouble() * 0.28
                        });
                    }
                    break;
            }
        }

        private void BuildPlanetBrushes()
        {
            void AddPlanet(string name, Color col)
            {
                var g = new RadialGradientBrush(
                    new GradientStopCollection
                    {
                        new GradientStop(Colors.White, 0.0),
                        new GradientStop(col, 0.35),
                        new GradientStop(Color.FromRgb(9, 13, 22), 1.0)
                    })
                { Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.35, 0.35), RadiusX = 0.5, RadiusY = 0.5 };
                g.Freeze();
                _planetBrushes[name] = g;
            }

            AddPlanet("Mercury", Color.FromRgb(203, 213, 225));
            AddPlanet("Venus", Color.FromRgb(253, 230, 138));
            AddPlanet("Earth", Color.FromRgb(56, 189, 248));
            AddPlanet("Mars", Color.FromRgb(248, 113, 113));
            AddPlanet("Jupiter", Color.FromRgb(253, 186, 116));
            AddPlanet("Saturn", Color.FromRgb(253, 224, 71));
            AddPlanet("Uranus", Color.FromRgb(103, 232, 249));
            AddPlanet("Neptune", Color.FromRgb(96, 165, 250));
            AddPlanet("Pluto", Color.FromRgb(214, 211, 209));

            _planetMoonBrushes[("Earth", 0)] = Freeze(new SolidColorBrush(Color.FromRgb(226, 232, 240)));
            _planetMoonBrushes[("Jupiter", 0)] = Freeze(new SolidColorBrush(Color.FromRgb(254, 240, 138)));
            _planetMoonBrushes[("Jupiter", 1)] = Freeze(new SolidColorBrush(Color.FromRgb(186, 230, 253)));
            _planetMoonBrushes[("Jupiter", 2)] = Freeze(new SolidColorBrush(Color.FromRgb(203, 213, 225)));
            _planetMoonBrushes[("Saturn", 0)] = Freeze(new SolidColorBrush(Color.FromRgb(251, 191, 36)));
            _planetMoonBrushes[("Neptune", 0)] = Freeze(new SolidColorBrush(Color.FromRgb(191, 219, 254)));
            _planetMoonBrushes[("Pluto", 0)] = Freeze(new SolidColorBrush(Color.FromRgb(168, 162, 158)));
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            EnsureSeeded(force: false);
            double w = GetViewportWidth();
            double h = GetViewportHeight();
            if (w <= 1 || h <= 1) return;

            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));

            if (_sceneId == "none")
            {
                dc.DrawRectangle(NoneBgBrush, null, new Rect(0, 0, w, h));
                dc.Pop();
                return;
            }

            EnsureStaticCaches(_sceneId, w, h);

            switch (_sceneId)
            {
                case "north_pole": RenderNorthPole(dc, w, h); break;
                case "far_space": RenderFarSpace(dc, w, h); break;
                case "solar_system": RenderSolarSystem(dc, w, h); break;
                case "moon_night": RenderMoonNight(dc, w, h); break;
                case "cyber_city": RenderCyberCity(dc, w, h); break;
                case "nuclear_wasteland": RenderNuclearWasteland(dc, w, h); break;
                case "wasteland": RenderWasteland(dc, w, h); break;
                case "city_wasteland": RenderCityWasteland(dc, w, h); break;
                case "desert": RenderDesert(dc, w, h); break;
                case "lake_forest": RenderLakeForest(dc, w, h); break;
                case "shipwreck": RenderShipwreck(dc, w, h); break;
                case "ocean_life": RenderOceanLife(dc, w, h); break;
                case "snowy_peaks": RenderSnowyPeaks(dc, w, h); break;
                case "city_skyline": RenderCityMetropolis(dc, w, h, isShore: false); break;
                case "city_shoreline": RenderCityMetropolis(dc, w, h, isShore: true); break;
                case "sunset": RenderSunset(dc, w, h); break;
                case "rainy_forest": RenderRainyForest(dc, w, h); break;
                default: RenderNorthPole(dc, w, h); break;
            }

            dc.Pop();
        }

        private void EnsureStaticCaches(string sceneId, double w, double h)
        {
            if (!_dpiChecked)
            {
                _dpiScale = VisualTreeHelper.GetDpi(this);
                _dpiChecked = true;
            }
            double dpiX = _dpiScale.DpiScaleX > 0.1 ? _dpiScale.DpiScaleX : 1.0;
            double dpiY = _dpiScale.DpiScaleY > 0.1 ? _dpiScale.DpiScaleY : 1.0;

            if (_cachedSceneId == sceneId &&
                Math.Abs(_cacheW - w) < 0.5 &&
                Math.Abs(_cacheH - h) < 0.5 &&
                Math.Abs(_cacheDpiX - dpiX) < 0.01 &&
                Math.Abs(_cacheDpiY - dpiY) < 0.01)
            {
                return;
            }

            InvalidateStaticCaches();
            _cachedSceneId = sceneId;
            _cacheW = w;
            _cacheH = h;
            _cacheDpiX = dpiX;
            _cacheDpiY = dpiY;

            switch (sceneId)
            {
                case "north_pole":
                    _northRibbonBrushes = new Brush?[]
                    {
                        MakeVerticalGrad(new Rect(0, h * 0.22 - 95, w, 210),
                            (Color.FromArgb(0, 16, 185, 129), 0.0), (Color.FromArgb(107, 52, 211, 153), 0.5), (Color.FromArgb(0, 16, 185, 129), 1.0)),
                        MakeVerticalGrad(new Rect(0, h * 0.29 - 95, w, 210),
                            (Color.FromArgb(0, 56, 189, 248), 0.0), (Color.FromArgb(92, 34, 211, 238), 0.5), (Color.FromArgb(0, 56, 189, 248), 1.0)),
                        MakeVerticalGrad(new Rect(0, h * 0.17 - 95, w, 210),
                            (Color.FromArgb(0, 236, 72, 153), 0.0), (Color.FromArgb(77, 168, 85, 247), 0.5), (Color.FromArgb(0, 236, 72, 153), 1.0))
                    };
                    _staticBgBitmap = BakeToBitmap(w, h, dpiX, dpiY, bdc =>
                    {
                        var sky = MakeVerticalGrad(new Rect(0, 0, w, h * 0.74),
                            (Color.FromRgb(2, 5, 16), 0.0), (Color.FromRgb(7, 22, 48), 0.5), (Color.FromRgb(13, 43, 77), 1.0));
                        bdc.DrawRectangle(sky, null, new Rect(0, 0, w, h));
                    });
                    _staticFgBitmap = BakeToBitmap(w, h, dpiX, dpiY, bdc =>
                    {
                        double horizonY = h * 0.73;
                        var icePeaks = new[]
                        {
                            (cx: w * 0.12, bw: w * 0.28, ph: h * 0.34, lCol: Color.FromRgb(219, 234, 254), rCol: Color.FromRgb(30, 58, 95)),
                            (cx: w * 0.34, bw: w * 0.34, ph: h * 0.42, lCol: Color.FromRgb(239, 246, 255), rCol: Color.FromRgb(23, 37, 84)),
                            (cx: w * 0.62, bw: w * 0.38, ph: h * 0.46, lCol: Color.FromRgb(224, 242, 254), rCol: Color.FromRgb(15, 41, 74)),
                            (cx: w * 0.86, bw: w * 0.32, ph: h * 0.37, lCol: Color.FromRgb(219, 234, 254), rCol: Color.FromRgb(30, 58, 95)),
                            (cx: w * 0.48, bw: w * 0.26, ph: h * 0.31, lCol: Color.FromRgb(186, 230, 253), rCol: Color.FromRgb(12, 30, 59))
                        };
                        foreach (var pk in icePeaks)
                        {
                            double topY = horizonY - pk.ph;
                            double ridgeX = pk.cx + pk.bw * 0.04;
                            var leftFace = BuildTriangle(new Point(pk.cx, topY), new Point(pk.cx - pk.bw * 0.5, horizonY), new Point(ridgeX, horizonY));
                            var lg = MakeLinearAbs(new Point(pk.cx, topY), new Point(pk.cx - pk.bw * 0.5, horizonY),
                                (pk.lCol, 0.0), (Color.FromRgb(147, 197, 253), 0.55), (Color.FromRgb(30, 58, 95), 1.0));
                            bdc.DrawGeometry(lg, null, leftFace);

                            var rightFace = BuildTriangle(new Point(pk.cx, topY), new Point(ridgeX, horizonY), new Point(pk.cx + pk.bw * 0.5, horizonY));
                            var rg = MakeLinearAbs(new Point(pk.cx, topY), new Point(pk.cx + pk.bw * 0.5, horizonY),
                                (Color.FromRgb(96, 165, 250), 0.0), (pk.rCol, 0.4), (Color.FromRgb(9, 21, 43), 1.0));
                            bdc.DrawGeometry(rg, null, rightFace);
                        }
                        var seaGrad = MakeVerticalGrad(new Rect(0, horizonY, w, h - horizonY),
                            (Color.FromRgb(7, 28, 54), 0.0), (Color.FromRgb(10, 39, 71), 0.5), (Color.FromRgb(3, 9, 20), 1.0));
                        bdc.DrawRectangle(seaGrad, null, new Rect(0, horizonY, w, h - horizonY));
                    });
                    break;

                case "far_space":
                    _staticBgBitmap = BakeToBitmap(w, h, dpiX, dpiY, bdc =>
                    {
                        var bg = MakeRadialGrad(
                            new Point(0.5, 0.45), new Point(0.5, 0.45), 0.72, 0.72,
                            (Color.FromRgb(13, 6, 40), 0.0), (Color.FromRgb(6, 3, 24), 0.5), (Color.FromRgb(2, 1, 8), 1.0));
                        bdc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
                    });
                    foreach (var gal in _galaxies)
                    {
                        double e = gal.HalfExtent;
                        gal.Sprite = BakeToBitmap(e * 2, e * 2, dpiX * 2.0, dpiY * 2.0, bdc =>
                        {
                            bdc.DrawEllipse(gal.CoreBrush, null, new Point(e, e), gal.Rad * 0.58, gal.Rad * 0.58);
                            foreach (var sp in gal.Stars)
                            {
                                double r = sp.Dist * gal.Rad;
                                bdc.DrawEllipse(sp.ColBrush, null, new Point(e + Math.Cos(sp.BaseAngle) * r, e + Math.Sin(sp.BaseAngle) * r), sp.Size, sp.Size);
                            }
                        });
                    }
                    break;

                case "solar_system":
                    _staticBgBitmap = BakeToBitmap(w, h, dpiX, dpiY, bdc =>
                    {
                        var bg = MakeRadialGrad(
                            new Point(0.5, 0.56), new Point(0.5, 0.56), 0.72, 0.72,
                            (Color.FromRgb(19, 13, 38), 0.0), (Color.FromRgb(7, 9, 25), 0.45), (Color.FromRgb(2, 3, 10), 1.0));
                        bdc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
                    });
                    _staticFgBitmap = BakeToBitmap(w, h, dpiX, dpiY, bdc =>
                    {
                        double cx = w * 0.5, cy = h * 0.56;
                        double maxSpan = Math.Min(w * 0.46, h * 0.42);
                        const double tilt = 0.48;
                        foreach (var pl in SolarPlanets)
                        {
                            double rx = maxSpan * pl.orbit;
                            bdc.DrawEllipse(null, pl.name == "Pluto" ? SolarPlutoPen : SolarOrbitPen, new Point(cx, cy), rx, rx * tilt);
                        }
                    });
                    break;

                case "moon_night":
                    _staticBgBitmap = BakeToBitmap(w, h, dpiX, dpiY, bdc =>
                    {
                        double horizonY = h * 0.70;
                        var sky = MakeVerticalGrad(new Rect(0, 0, w, horizonY),
                            (Color.FromRgb(2, 6, 23), 0.0), (Color.FromRgb(15, 23, 42), 0.55), (Color.FromRgb(30, 41, 59), 1.0));
                        bdc.DrawRectangle(sky, null, new Rect(0, 0, w, h));
                    });
                    _staticFgBitmap = BakeToBitmap(w, h, dpiX, dpiY, bdc =>
                    {
                        double horizonY = h * 0.70;
                        var hillsSg = new StreamGeometry();
                        using (var ctx = hillsSg.Open())
                        {
                            ctx.BeginFigure(new Point(0, horizonY), true, true);
                            for (double x = 0; x <= w; x += 24)
                                ctx.LineTo(new Point(x, horizonY - 22 - Math.Sin(x * 0.004) * 28 - Math.Cos(x * 0.009) * 14), false, true);
                            ctx.LineTo(new Point(w, horizonY - 22 - Math.Sin(w * 0.004) * 28 - Math.Cos(w * 0.009) * 14), false, true);
                            ctx.LineTo(new Point(w, horizonY), false, false);
                        }
                        hillsSg.Freeze();
                        bdc.DrawGeometry(Freeze(new SolidColorBrush(Color.FromRgb(9, 16, 36))), null, hillsSg);

                        var lake = MakeVerticalGrad(new Rect(0, horizonY, w, h - horizonY),
                            (Color.FromRgb(11, 21, 40), 0.0), (Color.FromRgb(2, 6, 23), 1.0));
                        bdc.DrawRectangle(lake, null, new Rect(0, horizonY, w, h - horizonY));
                    });
                    break;

                case "cyber_city":
                    _staticBgBitmap = BakeToBitmap(w, h, dpiX, dpiY, bdc =>
                    {
                        double baseY = h * 0.82;
                        var sky = MakeVerticalGrad(new Rect(0, 0, w, baseY),
                            (Color.FromRgb(7, 2, 20), 0.0), (Color.FromRgb(26, 9, 54), 0.55), (Color.FromRgb(59, 7, 100), 1.0));
                        bdc.DrawRectangle(sky, null, new Rect(0, 0, w, h));

                        var glow = MakeRadialGrad(new Point(0.5, 0.82), new Point(0.5, 0.82), 0.45, 0.55,
                            (Color.FromArgb(97, 236, 72, 153), 0.0), (Color.FromArgb(36, 34, 211, 238), 0.5), (Color.FromArgb(0, 0, 0, 0), 1.0));
                        bdc.DrawRectangle(glow, null, new Rect(0, 0, w, h));

                        var bEven = Freeze(new SolidColorBrush(Color.FromRgb(7, 5, 20)));
                        var bOdd = Freeze(new SolidColorBrush(Color.FromRgb(12, 8, 34)));
                        for (int idx = 0; idx < _cyberBuildings.Count; idx++)
                        {
                            var b = _cyberBuildings[idx];
                            bdc.DrawRectangle(idx % 2 == 0 ? bEven : bOdd, b.OutlinePen, new Rect(b.X, b.Y, b.W, b.H));
                        }

                        bdc.DrawRectangle(Freeze(new SolidColorBrush(Color.FromRgb(5, 2, 13))), null, new Rect(0, baseY, w, h - baseY));
                        var gridPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(64, 34, 211, 238))), 1.0));
                        for (double gx = -w * 0.5; gx <= w * 1.5; gx += 70)
                            bdc.DrawLine(gridPen, new Point(w * 0.5 + (gx - w * 0.5) * 0.25, baseY), new Point(gx, h));
                        for (double gy = baseY; gy <= h; gy += 16)
                            bdc.DrawLine(gridPen, new Point(0, gy), new Point(w, gy));
                    });
                    break;

                case "nuclear_wasteland":
                    _staticBgBitmap = BakeToBitmap(w, h, dpiX, dpiY, bdc =>
                    {
                        double horizonY = h * 0.68;
                        var sky = MakeVerticalGrad(new Rect(0, 0, w, horizonY),
                            (Color.FromRgb(12, 20, 13), 0.0), (Color.FromRgb(30, 36, 16), 0.42),
                            (Color.FromRgb(56, 66, 20), 0.72), (Color.FromRgb(113, 120, 28), 0.92), (Color.FromRgb(163, 166, 38), 1.0));
                        bdc.DrawRectangle(sky, null, new Rect(0, 0, w, h));

                        double sx = w * 0.28, sy = horizonY - h * 0.18, sr = Math.Min(w, h) * 0.085;
                        var sg = MakeRadialGrad(
                            (Color.FromRgb(254, 240, 138), 0.0), (Color.FromArgb(115, 163, 230, 53), 0.35),
                            (Color.FromArgb(56, 113, 120, 28), 0.7), (Color.FromArgb(0, 0, 0, 0), 1.0));
                        bdc.DrawEllipse(sg, null, new Point(sx, sy), sr * 2.8, sr * 2.8);

                        var ruinBrush = Freeze(new SolidColorBrush(Color.FromRgb(26, 33, 19)));
                        var holeBrush = Freeze(new SolidColorBrush(Color.FromArgb(217, 12, 20, 13)));
                        foreach (var r in _ruinTowers)
                        {
                            bdc.DrawRectangle(ruinBrush, null, new Rect(r.X, horizonY - r.H, r.W, r.H));
                            foreach (var hr in r.HoleRects) bdc.DrawRectangle(holeBrush, null, hr);
                        }

                        double ctwX = w * 0.36, ctwY = horizonY;
                        double ctwH = h * 0.28, ctwBaseW = 110, ctwTopW = 74, ctwMidW = 62;
                        var ctGeo = new StreamGeometry();
                        using (var ctx = ctGeo.Open())
                        {
                            ctx.BeginFigure(new Point(ctwX - ctwBaseW * 0.5, ctwY), true, true);
                            ctx.QuadraticBezierTo(new Point(ctwX - ctwMidW * 0.5, ctwY - ctwH * 0.55), new Point(ctwX - ctwTopW * 0.5, ctwY - ctwH), false, true);
                            ctx.LineTo(new Point(ctwX - ctwTopW * 0.2, ctwY - ctwH + 5), false, false);
                            ctx.LineTo(new Point(ctwX, ctwY - ctwH), false, false);
                            ctx.LineTo(new Point(ctwX + ctwTopW * 0.25, ctwY - ctwH + 8), false, false);
                            ctx.LineTo(new Point(ctwX + ctwTopW * 0.5, ctwY - ctwH), false, false);
                            ctx.QuadraticBezierTo(new Point(ctwX + ctwMidW * 0.5, ctwY - ctwH * 0.55), new Point(ctwX + ctwBaseW * 0.5, ctwY), false, true);
                        }
                        ctGeo.Freeze();
                        bdc.DrawGeometry(Freeze(new SolidColorBrush(Color.FromRgb(20, 26, 15))), null, ctGeo);

                        var hazPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(72, 234, 179, 8))), 6.0));
                        bdc.DrawLine(hazPen, new Point(ctwX - ctwMidW * 0.48, ctwY - ctwH * 0.52), new Point(ctwX + ctwMidW * 0.48, ctwY - ctwH * 0.52));

                        var overPassBrush = Freeze(new SolidColorBrush(Color.FromRgb(15, 20, 12)));
                        var ovGeo = new StreamGeometry();
                        using (var ctx = ovGeo.Open())
                        {
                            ctx.BeginFigure(new Point(w * 0.68, horizonY - 25), true, true);
                            ctx.LineTo(new Point(w * 0.88, horizonY - 45), false, false);
                            ctx.LineTo(new Point(w * 0.88, horizonY - 34), false, false);
                            ctx.LineTo(new Point(w * 0.80, horizonY - 18), false, false);
                            ctx.LineTo(new Point(w * 0.77, horizonY + 2), false, false);
                            ctx.LineTo(new Point(w * 0.68, horizonY - 16), false, false);
                        }
                        ovGeo.Freeze();
                        bdc.DrawGeometry(overPassBrush, null, ovGeo);
                        bdc.DrawRectangle(overPassBrush, null, new Rect(w * 0.72, horizonY - 20, 14, 50));

                        var gGrad = MakeVerticalGrad(new Rect(0, horizonY - 6, w, h - horizonY + 6),
                            (Color.FromRgb(18, 24, 13), 0.0), (Color.FromRgb(10, 13, 7), 0.5), (Color.FromRgb(5, 7, 4), 1.0));
                        var groundGeo = new StreamGeometry();
                        using (var ctx = groundGeo.Open())
                        {
                            ctx.BeginFigure(new Point(0, h), true, true);
                            ctx.LineTo(new Point(0, horizonY), false, false);
                            ctx.LineTo(new Point(w * 0.22, horizonY + 6), false, false);
                            ctx.LineTo(new Point(w * 0.45, horizonY - 4), false, false);
                            ctx.LineTo(new Point(w * 0.75, horizonY + 10), false, false);
                            ctx.LineTo(new Point(w, horizonY + 2), false, false);
                            ctx.LineTo(new Point(w, h), false, false);
                        }
                        groundGeo.Freeze();
                        bdc.DrawGeometry(gGrad, null, groundGeo);

                        var bbPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(10, 14, 8))), 3.0));
                        double bbx = w * 0.14, bby = horizonY + 8;
                        bdc.DrawLine(bbPen, new Point(bbx, bby), new Point(bbx, bby - 55));
                        bdc.DrawLine(bbPen, new Point(bbx + 40, bby), new Point(bbx + 40, bby - 55));
                        bdc.DrawLine(bbPen, new Point(bbx - 8, bby - 55), new Point(bbx + 48, bby - 55));
                        bdc.DrawLine(bbPen, new Point(bbx - 8, bby - 25), new Point(bbx + 48, bby - 25));
                        bdc.DrawLine(bbPen, new Point(bbx, bby - 55), new Point(bbx + 40, bby - 25));
                    });
                    break;

                case "wasteland":
                    _staticBgBitmap = BakeToBitmap(w, h, dpiX, dpiY, bdc =>
                    {
                        double horizonY = h * 0.70;
                        var sky = MakeVerticalGrad(new Rect(0, 0, w, horizonY),
                            (Color.FromRgb(28, 18, 9), 0.0), (Color.FromRgb(68, 37, 18), 0.38),
                            (Color.FromRgb(120, 65, 29), 0.70), (Color.FromRgb(184, 112, 56), 0.90), (Color.FromRgb(226, 163, 106), 1.0));
                        bdc.DrawRectangle(sky, null, new Rect(0, 0, w, h));

                        double sx = w * 0.74, sy = horizonY - h * 0.24, sr = Math.Min(w, h) * 0.08;
                        var sg = MakeRadialGrad(
                            (Color.FromArgb(242, 254, 240, 138), 0.0), (Color.FromArgb(102, 245, 158, 11), 0.3),
                            (Color.FromArgb(38, 217, 119, 6), 0.7), (Color.FromArgb(0, 0, 0, 0), 1.0));
                        bdc.DrawEllipse(sg, null, new Point(sx, sy), sr * 3.2, sr * 3.2);
                        bdc.DrawEllipse(Freeze(new SolidColorBrush(Color.FromRgb(254, 243, 199))), null, new Point(sx, sy), sr * 0.55, sr * 0.55);

                        var ridgeGeo = new StreamGeometry();
                        using (var ctx = ridgeGeo.Open())
                        {
                            ctx.BeginFigure(new Point(0, horizonY), true, true);
                            for (double x = 0; x <= w; x += 30)
                                ctx.LineTo(new Point(x, horizonY - 45 - Math.Sin(x * 0.003) * 28 - Math.Sin(x * 0.012) * 14), false, true);
                            ctx.LineTo(new Point(w, horizonY - 45 - Math.Sin(w * 0.003) * 28 - Math.Sin(w * 0.012) * 14), false, true);
                            ctx.LineTo(new Point(w, horizonY), false, false);
                        }
                        ridgeGeo.Freeze();
                        bdc.DrawGeometry(Freeze(new SolidColorBrush(Color.FromRgb(74, 37, 18))), null, ridgeGeo);

                        var mesaBrush = Freeze(new SolidColorBrush(Color.FromRgb(49, 24, 11)));
                        var striaPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(56, 217, 119, 6))), 1.6));
                        var mesas = new[]
                        {
                            (x: w * 0.04, mw: 140.0, mh: 78.0, flatW: 90.0),
                            (x: w * 0.38, mw: 180.0, mh: 96.0, flatW: 120.0),
                            (x: w * 0.65, mw: 120.0, mh: 64.0, flatW: 75.0),
                            (x: w * 0.84, mw: 160.0, mh: 86.0, flatW: 100.0)
                        };
                        foreach (var m in mesas)
                        {
                            var mg = BuildQuad(
                                new Point(m.x, horizonY), new Point(m.x + (m.mw - m.flatW) * 0.45, horizonY - m.mh),
                                new Point(m.x + m.mw - (m.mw - m.flatW) * 0.45, horizonY - m.mh), new Point(m.x + m.mw, horizonY));
                            bdc.DrawGeometry(mesaBrush, null, mg);
                            for (double s = m.x + 15; s < m.x + m.mw - 15; s += 16)
                                bdc.DrawLine(striaPen, new Point(s, horizonY - m.mh + 6), new Point(s + Math.Sin(s) * 4, horizonY - 4));
                        }

                        var gGrad = MakeVerticalGrad(new Rect(0, horizonY - 12, w, h - horizonY + 12),
                            (Color.FromRgb(38, 19, 8), 0.0), (Color.FromRgb(25, 12, 5), 0.4), (Color.FromRgb(14, 7, 3), 1.0));
                        var gGeo = new StreamGeometry();
                        using (var ctx = gGeo.Open())
                        {
                            ctx.BeginFigure(new Point(0, h), true, true);
                            ctx.LineTo(new Point(0, horizonY), false, false);
                            for (double x = 0; x <= w; x += 40)
                                ctx.LineTo(new Point(x, horizonY + Math.Sin(x * 0.008) * 6 + Math.Cos(x * 0.015) * 4), false, true);
                            ctx.LineTo(new Point(w, horizonY + Math.Sin(w * 0.008) * 6 + Math.Cos(w * 0.015) * 4), false, true);
                            ctx.LineTo(new Point(w, h), false, false);
                        }
                        gGeo.Freeze();
                        bdc.DrawGeometry(gGrad, null, gGeo);

                        var crackPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(191, 68, 37, 18))), 2.0));
                        var cracks = new[]
                        {
                            (w * 0.12, horizonY + 35, w * 0.18, horizonY + 65, w * 0.14, horizonY + 110),
                            (w * 0.18, horizonY + 65, w * 0.25, horizonY + 80, w * 0.32, horizonY + 130),
                            (w * 0.50, horizonY + 25, w * 0.56, horizonY + 58, w * 0.52, horizonY + 95),
                            (w * 0.56, horizonY + 58, w * 0.64, horizonY + 75, w * 0.60, horizonY + 120),
                            (w * 0.78, horizonY + 30, w * 0.85, horizonY + 68, w * 0.88, horizonY + 115)
                        };
                        foreach (var (x0, y0, x1, y1, x2, y2) in cracks)
                        {
                            bdc.DrawLine(crackPen, new Point(x0, y0), new Point(x1, y1));
                            bdc.DrawLine(crackPen, new Point(x1, y1), new Point(x2, y2));
                        }

                        double wmx = w * 0.26, wmy = horizonY + 6, wmH = 135;
                        bdc.PushTransform(new TranslateTransform(wmx, wmy));
                        var legBrush = WastelandHubBrush;
                        var legPen = Freeze(new Pen(legBrush, 3.5));
                        var strutPen = Freeze(new Pen(legBrush, 1.8));
                        bdc.DrawLine(legPen, new Point(-22, 0), new Point(-7, -wmH));
                        bdc.DrawLine(legPen, new Point(22, 0), new Point(7, -wmH));
                        for (double st = -20; st > -wmH + 15; st -= 22)
                        {
                            double legW = 22 * (1 - (-st / wmH));
                            bdc.DrawLine(strutPen, new Point(-legW, st), new Point(legW, st));
                            bdc.DrawLine(strutPen, new Point(-legW, st), new Point(legW * 0.7, st - 22));
                            bdc.DrawLine(strutPen, new Point(legW, st), new Point(-legW * 0.7, st - 22));
                        }
                        bdc.DrawRectangle(legBrush, null, new Rect(-10, -wmH - 4, 20, 8));
                        bdc.DrawGeometry(Freeze(new SolidColorBrush(Color.FromRgb(31, 15, 6))), null,
                            BuildQuad(new Point(0, -wmH), new Point(38, -wmH - 6), new Point(40, -wmH - 24), new Point(15, -wmH - 12)));
                        bdc.Pop();

                        double treeX = w * 0.76, treeY = horizonY + 8;
                        var treeBrush = Freeze(new SolidColorBrush(Color.FromRgb(20, 9, 4)));
                        var treeGeo = new StreamGeometry();
                        using (var ctx = treeGeo.Open())
                        {
                            ctx.BeginFigure(new Point(treeX - 8, treeY), true, true);
                            ctx.QuadraticBezierTo(new Point(treeX - 12, treeY - 35), new Point(treeX - 4, treeY - 75), false, true);
                            ctx.LineTo(new Point(treeX + 4, treeY - 75), false, false);
                            ctx.QuadraticBezierTo(new Point(treeX + 10, treeY - 35), new Point(treeX + 8, treeY), false, true);
                        }
                        treeGeo.Freeze();
                        bdc.DrawGeometry(treeBrush, null, treeGeo);
                        var branchPen = Freeze(new Pen(treeBrush, 3.2));
                        bdc.DrawGeometry(null, branchPen, BuildQuadBezier(new Point(treeX - 4, treeY - 60), new Point(treeX - 28, treeY - 80), new Point(treeX - 38, treeY - 95)));
                        bdc.DrawLine(branchPen, new Point(treeX - 25, treeY - 82), new Point(treeX - 18, treeY - 105));
                        bdc.DrawGeometry(null, branchPen, BuildQuadBezier(new Point(treeX + 3, treeY - 65), new Point(treeX + 26, treeY - 85), new Point(treeX + 42, treeY - 100)));
                        bdc.DrawLine(branchPen, new Point(treeX + 22, treeY - 86), new Point(treeX + 32, treeY - 110));
                        bdc.DrawLine(branchPen, new Point(treeX - 2, treeY - 75), new Point(treeX + 2, treeY - 118));

                        double poleX = w * 0.48, poleY = horizonY + 12;
                        var polePen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(22, 11, 5))), 3.5));
                        bdc.DrawLine(polePen, new Point(poleX, poleY), new Point(poleX - 6, poleY - 85));
                        bdc.DrawLine(polePen, new Point(poleX - 24, poleY - 76), new Point(poleX + 14, poleY - 78));

                        double skX = w * 0.16, skY = horizonY + 45;
                        var skullBrush = Freeze(new SolidColorBrush(Color.FromRgb(226, 216, 204)));
                        bdc.DrawEllipse(skullBrush, null, new Point(skX, skY), 9, 7);
                        bdc.DrawGeometry(skullBrush, null, BuildTriangle(new Point(skX - 5, skY + 2), new Point(skX + 5, skY + 2), new Point(skX, skY + 14)));
                        var eyeBrush = Freeze(new SolidColorBrush(Color.FromRgb(28, 16, 7)));
                        bdc.DrawEllipse(eyeBrush, null, new Point(skX - 3.5, skY), 2.2, 2.2);
                        bdc.DrawEllipse(eyeBrush, null, new Point(skX + 3.5, skY), 2.2, 2.2);
                        var hornPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(197, 181, 158))), 2.5));
                        bdc.DrawGeometry(null, hornPen, BuildQuadBezier(new Point(skX - 7, skY - 3), new Point(skX - 22, skY - 14), new Point(skX - 24, skY - 4)));
                        bdc.DrawGeometry(null, hornPen, BuildQuadBezier(new Point(skX + 7, skY - 3), new Point(skX + 22, skY - 14), new Point(skX + 24, skY - 4)));
                    });
                    break;

                case "city_wasteland":
                    _staticBgBitmap = BakeToBitmap(w, h, dpiX, dpiY, bdc =>
                    {
                        double horizonY = h * 0.72;
                        var sky = MakeVerticalGrad(new Rect(0, 0, w, horizonY),
                            (Color.FromRgb(12, 22, 21), 0.0), (Color.FromRgb(22, 40, 38), 0.45),
                            (Color.FromRgb(36, 60, 55), 0.78), (Color.FromRgb(57, 87, 79), 1.0));
                        bdc.DrawRectangle(sky, null, new Rect(0, 0, w, h));

                        for (int r = 0; r < 4; r++)
                        {
                            double rx = w * (0.18 + r * 0.22);
                            var rg = MakeLinearAbs(new Point(rx, 0), new Point(rx + 120, horizonY + 40),
                                (Color.FromArgb(41, 167, 243, 208), 0.0), (Color.FromArgb(20, 110, 231, 183), 0.6), (Color.FromArgb(0, 167, 243, 208), 1.0));
                            bdc.DrawGeometry(rg, null, BuildQuad(new Point(rx - 30, 0), new Point(rx + 35, 0), new Point(rx + 150, horizonY + 40), new Point(rx - 70, horizonY + 40)));
                        }

                        var holeBrush = Freeze(new SolidColorBrush(Color.FromRgb(6, 12, 11)));
                        var vineDark = Freeze(new SolidColorBrush(Color.FromRgb(22, 101, 52)));
                        var vineBright = Freeze(new SolidColorBrush(Color.FromRgb(34, 197, 94)));
                        var canopyBrush = Freeze(new SolidColorBrush(Color.FromRgb(20, 83, 45)));

                        foreach (var tw in _overgrownTowers)
                        {
                            double ty = horizonY - tw.H;
                            bdc.DrawRectangle(tw.BodyBrush, null, new Rect(tw.X, ty, tw.W, tw.H));
                            foreach (var hr in tw.HoleRects) bdc.DrawRectangle(holeBrush, null, hr);
                            for (int iv = 0; iv < 4; iv++)
                            {
                                double ivX = tw.X + 8 + iv * (tw.W / 4);
                                double ivLen = 35 + (iv * 23) % 80;
                                bdc.DrawGeometry(vineDark, null, BuildTriangle(new Point(ivX, ty + 15), new Point(ivX + 6, ty + 15), new Point(ivX + 3, ty + 15 + ivLen)));
                                bdc.DrawEllipse(vineBright, null, new Point(ivX + 3, ty + 15 + ivLen), 4, 4);
                            }
                            if (tw.RoofTree)
                            {
                                bdc.DrawEllipse(canopyBrush, null, new Point(tw.X + tw.W * 0.5, ty - 8), 16, 16);
                                bdc.DrawEllipse(canopyBrush, null, new Point(tw.X + tw.W * 0.35, ty - 4), 12, 12);
                                bdc.DrawEllipse(canopyBrush, null, new Point(tw.X + tw.W * 0.65, ty - 5), 13, 13);
                            }
                        }

                        double hwY = horizonY - 15;
                        var hwBrush = Freeze(new SolidColorBrush(Color.FromRgb(15, 28, 26)));
                        bdc.DrawGeometry(hwBrush, null, BuildQuad(new Point(0, hwY - 20), new Point(w * 0.52, hwY + 10), new Point(w * 0.49, hwY + 32), new Point(0, hwY + 8)));
                        bdc.DrawGeometry(hwBrush, null, BuildQuad(new Point(w * 0.58, hwY + 22), new Point(w, hwY - 10), new Point(w, hwY + 16), new Point(w * 0.58, hwY + 44)));

                        var rebarPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(55, 65, 81))), 1.8));
                        bdc.DrawLine(rebarPen, new Point(w * 0.51, hwY + 12), new Point(w * 0.54, hwY + 36));
                        bdc.DrawLine(rebarPen, new Point(w * 0.50, hwY + 22), new Point(w * 0.52, hwY + 45));
                        bdc.DrawLine(rebarPen, new Point(w * 0.58, hwY + 24), new Point(w * 0.56, hwY + 42));

                        bdc.DrawRectangle(Freeze(new SolidColorBrush(Color.FromRgb(10, 20, 18))), null, new Rect(w * 0.28, hwY - 5, 26, horizonY - hwY + 25));
                        var pillarVinePen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(21, 128, 61))), 3.0));
                        bdc.DrawGeometry(null, pillarVinePen, BuildQuadBezier(new Point(w * 0.28, horizonY + 15), new Point(w * 0.30, horizonY - 10), new Point(w * 0.30 + 15, hwY + 5)));

                        var groundGrad = MakeVerticalGrad(new Rect(0, horizonY, w, h - horizonY),
                            (Color.FromRgb(16, 28, 25), 0.0), (Color.FromRgb(9, 16, 14), 0.5), (Color.FromRgb(4, 7, 6), 1.0));
                        bdc.DrawGeometry(groundGrad, null, BuildQuad(new Point(0, h), new Point(0, horizonY), new Point(w, horizonY + 5), new Point(w, h)));

                        double carX = w * 0.72, carY = horizonY + 38;
                        bdc.PushTransform(new TranslateTransform(carX, carY));
                        bdc.PushTransform(new RotateTransform(-0.08 * 180.0 / Math.PI));
                        bdc.DrawRoundedRectangle(Freeze(new SolidColorBrush(Color.FromRgb(44, 30, 19))), null, new Rect(-45, -16, 90, 22), 6, 6);
                        bdc.DrawGeometry(Freeze(new SolidColorBrush(Color.FromRgb(30, 20, 12))), null,
                            BuildQuad(new Point(-28, -16), new Point(-18, -32), new Point(18, -32), new Point(26, -16)));
                        var carWin = Freeze(new SolidColorBrush(Color.FromRgb(11, 7, 4)));
                        bdc.DrawRectangle(carWin, null, new Rect(-22, -28, 18, 10));
                        bdc.DrawRectangle(carWin, null, new Rect(2, -28, 18, 10));
                        var wheelB = Freeze(new SolidColorBrush(Color.FromRgb(7, 5, 3)));
                        bdc.DrawEllipse(wheelB, null, new Point(-28, 6), 9, 9);
                        bdc.DrawEllipse(wheelB, null, new Point(28, 6), 9, 9);
                        var mossDark = Freeze(new SolidColorBrush(Color.FromRgb(21, 128, 61)));
                        bdc.DrawEllipse(mossDark, null, new Point(-32, -18), 10, 10);
                        bdc.DrawEllipse(mossDark, null, new Point(-15, -32), 8, 8);
                        bdc.DrawEllipse(mossDark, null, new Point(10, -33), 9, 9);
                        bdc.DrawEllipse(mossDark, null, new Point(32, -16), 11, 11);
                        bdc.DrawEllipse(vineBright, null, new Point(-30, -19), 6, 6);
                        bdc.DrawEllipse(vineBright, null, new Point(8, -34), 5, 5);
                        bdc.Pop();
                        bdc.Pop();

                        double lampX = w * 0.18, lampY = horizonY + 45;
                        var lampPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(26, 41, 38))), 3.5));
                        bdc.DrawLine(lampPen, new Point(lampX, lampY), new Point(lampX - 12, lampY - 95));
                        bdc.DrawGeometry(null, lampPen, BuildQuadBezier(new Point(lampX - 12, lampY - 95), new Point(lampX - 14, lampY - 115), new Point(lampX + 6, lampY - 118)));
                        var lvPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(22, 163, 74))), 1.8));
                        bdc.DrawLine(lvPen, new Point(lampX + 5, lampY - 118), new Point(lampX + 3, lampY - 80));
                        bdc.DrawLine(lvPen, new Point(lampX - 8, lampY - 70), new Point(lampX - 10, lampY - 35));

                        var pudFill = Freeze(new SolidColorBrush(Color.FromRgb(26, 48, 44)));
                        foreach (var pd in _puddles) bdc.DrawEllipse(pudFill, null, new Point(pd.X, pd.Y), pd.Rx, pd.Ry);
                    });
                    break;

                case "desert":
                    _staticBgBitmap = BakeToBitmap(w, h, dpiX, dpiY, bdc =>
                    {
                        double horizonY = h * 0.58;
                        var sky = MakeVerticalGrad(new Rect(0, 0, w, horizonY),
                            (Color.FromRgb(15, 23, 42), 0.0), (Color.FromRgb(59, 7, 100), 0.38),
                            (Color.FromRgb(154, 52, 18), 0.72), (Color.FromRgb(234, 88, 12), 0.92), (Color.FromRgb(253, 224, 71), 1.0));
                        bdc.DrawRectangle(sky, null, new Rect(0, 0, w, h));
                    });
                    _staticFgBitmap = BakeToBitmap(w, h, dpiX, dpiY, bdc =>
                    {
                        double horizonY = h * 0.58;
                        double sunX = w * 0.70, sunY = horizonY - h * 0.12, sunR = Math.Min(w, h) * 0.088;
                        var sg = MakeRadialGrad(
                            (Color.FromRgb(255, 251, 235), 0.0), (Color.FromArgb(140, 251, 191, 36), 0.4),
                            (Color.FromArgb(64, 234, 88, 12), 0.75), (Color.FromArgb(0, 0, 0, 0), 1.0));
                        bdc.DrawEllipse(sg, null, new Point(sunX, sunY), sunR * 2.5, sunR * 2.5);

                        var mesaGeo = new StreamGeometry();
                        using (var ctx = mesaGeo.Open())
                        {
                            ctx.BeginFigure(new Point(0, horizonY), true, true);
                            ctx.LineTo(new Point(w * 0.15, horizonY - 24), false, false);
                            ctx.LineTo(new Point(w * 0.32, horizonY - 14), false, false);
                            ctx.LineTo(new Point(w * 0.48, horizonY - 28), false, false);
                            ctx.LineTo(new Point(w * 0.62, horizonY - 10), false, false);
                            ctx.LineTo(new Point(w * 0.85, horizonY - 32), false, false);
                            ctx.LineTo(new Point(w, horizonY - 18), false, false);
                            ctx.LineTo(new Point(w, horizonY), false, false);
                        }
                        mesaGeo.Freeze();
                        bdc.DrawGeometry(Freeze(new SolidColorBrush(Color.FromRgb(69, 26, 3))), null, mesaGeo);

                        double d1Y = horizonY + 8;
                        var g1 = MakeVerticalGrad(new Rect(0, d1Y - 30, w, 100), (Color.FromRgb(245, 158, 11), 0.0), (Color.FromRgb(154, 52, 18), 1.0));
                        var d1Geo = new StreamGeometry();
                        using (var ctx = d1Geo.Open())
                        {
                            ctx.BeginFigure(new Point(0, h), true, true);
                            ctx.LineTo(new Point(0, d1Y), false, false);
                            ctx.BezierTo(new Point(w * 0.25, d1Y + 45), new Point(w * 0.6, d1Y - 35), new Point(w, d1Y + 25), false, true);
                            ctx.LineTo(new Point(w, h), false, false);
                        }
                        d1Geo.Freeze();
                        bdc.DrawGeometry(g1, null, d1Geo);

                        double d2PeakX = w * 0.44, d2PeakY = horizonY + h * 0.14;
                        var g2Lit = MakeLinearAbs(new Point(0, d2PeakY - 40), new Point(w * 0.6, d2PeakY + 90),
                            (Color.FromRgb(253, 224, 71), 0.0), (Color.FromRgb(245, 158, 11), 0.45), (Color.FromRgb(217, 119, 6), 1.0));
                        var d2Geo = new StreamGeometry();
                        using (var ctx = d2Geo.Open())
                        {
                            ctx.BeginFigure(new Point(0, h), true, true);
                            ctx.LineTo(new Point(0, d2PeakY + 40), false, false);
                            ctx.BezierTo(new Point(w * 0.22, d2PeakY + 10), new Point(w * 0.35, d2PeakY - 25), new Point(d2PeakX, d2PeakY), false, true);
                            ctx.BezierTo(new Point(w * 0.65, d2PeakY + 35), new Point(w * 0.85, d2PeakY - 10), new Point(w, d2PeakY + 50), false, true);
                            ctx.LineTo(new Point(w, h), false, false);
                        }
                        d2Geo.Freeze();
                        bdc.DrawGeometry(g2Lit, null, d2Geo);

                        var g2Shad = MakeLinearAbs(new Point(d2PeakX, d2PeakY), new Point(d2PeakX + 90, d2PeakY + 110),
                            (Color.FromRgb(180, 83, 9), 0.0), (Color.FromRgb(120, 53, 15), 0.65), (Color.FromRgb(69, 26, 3), 1.0));
                        var d2ShadGeo = new StreamGeometry();
                        using (var ctx = d2ShadGeo.Open())
                        {
                            ctx.BeginFigure(new Point(d2PeakX, d2PeakY), true, true);
                            ctx.BezierTo(new Point(w * 0.52, d2PeakY + 45), new Point(w * 0.62, d2PeakY + 95), new Point(w * 0.68, h), false, true);
                            ctx.LineTo(new Point(d2PeakX - 80, h), false, false);
                        }
                        d2ShadGeo.Freeze();
                        bdc.DrawGeometry(g2Shad, null, d2ShadGeo);

                        double d3PeakX = w * 0.72, d3PeakY = horizonY + h * 0.27;
                        var g3Lit = MakeLinearAbs(new Point(0, d3PeakY - 30), new Point(w, d3PeakY + 120),
                            (Color.FromRgb(254, 240, 138), 0.0), (Color.FromRgb(245, 158, 11), 0.3),
                            (Color.FromRgb(180, 83, 9), 0.8), (Color.FromRgb(120, 53, 15), 1.0));
                        var d3Geo = new StreamGeometry();
                        using (var ctx = d3Geo.Open())
                        {
                            ctx.BeginFigure(new Point(0, h), true, true);
                            ctx.LineTo(new Point(0, d3PeakY + 70), false, false);
                            ctx.BezierTo(new Point(w * 0.28, d3PeakY + 20), new Point(w * 0.52, d3PeakY - 35), new Point(d3PeakX, d3PeakY), false, true);
                            ctx.BezierTo(new Point(w * 0.88, d3PeakY + 40), new Point(w * 0.95, d3PeakY + 20), new Point(w, d3PeakY + 60), false, true);
                            ctx.LineTo(new Point(w, h), false, false);
                        }
                        d3Geo.Freeze();
                        bdc.DrawGeometry(g3Lit, null, d3Geo);

                        var d3ShadGeo = new StreamGeometry();
                        using (var ctx = d3ShadGeo.Open())
                        {
                            ctx.BeginFigure(new Point(d3PeakX, d3PeakY), true, true);
                            ctx.BezierTo(new Point(w * 0.64, d3PeakY + 50), new Point(w * 0.58, d3PeakY + 110), new Point(w * 0.52, h), false, true);
                            ctx.LineTo(new Point(d3PeakX + 40, h), false, false);
                        }
                        d3ShadGeo.Freeze();
                        bdc.DrawGeometry(Freeze(new SolidColorBrush(Color.FromArgb(199, 69, 26, 3))), null, d3ShadGeo);

                        var ripPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(56, 254, 240, 138))), 1.2));
                        for (int r = 0; r < 9; r++)
                        {
                            double ry = d3PeakY + 25 + r * 11;
                            var rg = new StreamGeometry();
                            using (var ctx = rg.Open())
                            {
                                ctx.BeginFigure(new Point(w * 0.12, ry + Math.Sin(r + 1) * 4), false, false);
                                ctx.BezierTo(new Point(w * 0.28, ry - 3), new Point(w * 0.42, ry + 4), new Point(w * 0.55, ry - 2), true, true);
                            }
                            rg.Freeze();
                            bdc.DrawGeometry(null, ripPen, rg);
                        }
                    });
                    break;

                case "lake_forest":
                    _staticBgBitmap = BakeToBitmap(w, h, dpiX, dpiY, bdc =>
                    {
                        double horizonY = h * 0.58;
                        var sky = MakeVerticalGrad(new Rect(0, 0, w, horizonY),
                            (Color.FromRgb(4, 16, 30), 0.0), (Color.FromRgb(15, 41, 66), 0.55), (Color.FromRgb(19, 78, 74), 1.0));
                        bdc.DrawRectangle(sky, null, new Rect(0, 0, w, h));
                    });
                    _staticFgBitmap = BakeToBitmap(w, h, dpiX, dpiY, bdc =>
                    {
                        double horizonY = h * 0.58;
                        var mtGeo = new StreamGeometry();
                        using (var ctx = mtGeo.Open())
                        {
                            ctx.BeginFigure(new Point(0, horizonY), true, true);
                            for (double x = 0; x <= w; x += 24)
                                ctx.LineTo(new Point(x, horizonY - Math.Abs(Math.Sin(x * 0.0025) * (h * 0.18) + Math.Cos(x * 0.006) * (h * 0.07))), false, true);
                            ctx.LineTo(new Point(w, horizonY - Math.Abs(Math.Sin(w * 0.0025) * (h * 0.18) + Math.Cos(w * 0.006) * (h * 0.07))), false, true);
                            ctx.LineTo(new Point(w, horizonY), false, false);
                        }
                        mtGeo.Freeze();
                        bdc.DrawGeometry(Freeze(new SolidColorBrush(Color.FromRgb(12, 39, 56))), null, mtGeo);

                        var pineGeo = new StreamGeometry();
                        using (var ctx = pineGeo.Open())
                        {
                            ctx.BeginFigure(new Point(0, horizonY), true, true);
                            for (double x = 0; x <= w; x += 18)
                            {
                                double th = 28 + Math.Abs(Math.Sin(x * 0.19)) * 42;
                                ctx.LineTo(new Point(x - 8, horizonY), false, false);
                                ctx.LineTo(new Point(x, horizonY - th), false, false);
                                ctx.LineTo(new Point(x + 8, horizonY), false, false);
                            }
                            ctx.LineTo(new Point(w, horizonY), false, false);
                        }
                        pineGeo.Freeze();
                        bdc.DrawGeometry(Freeze(new SolidColorBrush(Color.FromRgb(6, 28, 36))), null, pineGeo);

                        var lake = MakeVerticalGrad(new Rect(0, horizonY, w, h - horizonY),
                            (Color.FromRgb(15, 61, 62), 0.0), (Color.FromRgb(8, 32, 50), 0.45), (Color.FromRgb(3, 11, 20), 1.0));
                        bdc.DrawRectangle(lake, null, new Rect(0, horizonY, w, h - horizonY));
                    });
                    break;

                case "shipwreck":
                    double floorY = h * 0.76;
                    _shipSunbeamGeo = BuildQuad(new Point(-30, 0), new Point(45, 0), new Point(190, h * 0.95), new Point(-85, h * 0.95));
                    _shipSunbeamBrush = MakeLinearAbs(new Point(0, 0), new Point(130, h * 0.95),
                        (Color.FromArgb(66, 103, 232, 249), 0.0), (Color.FromArgb(31, 56, 189, 248), 0.5), (Color.FromArgb(0, 103, 232, 249), 1.0));
                    _shipSandBrush = MakeVerticalGrad(new Rect(0, floorY - 20, w, h - floorY + 20),
                        (Color.FromRgb(12, 53, 69), 0.0), (Color.FromRgb(7, 36, 48), 0.45), (Color.FromRgb(3, 20, 27), 1.0));
                    var sandGeo = new StreamGeometry();
                    using (var ctx = sandGeo.Open())
                    {
                        ctx.BeginFigure(new Point(0, h), true, true);
                        for (double x = 0; x <= w; x += 25)
                            ctx.LineTo(new Point(x, floorY + Math.Sin(x * 0.007) * 14 + Math.Cos(x * 0.014) * 8), false, true);
                        ctx.LineTo(new Point(w, floorY + Math.Sin(w * 0.007) * 14 + Math.Cos(w * 0.014) * 8), false, true);
                        ctx.LineTo(new Point(w, h), false, false);
                    }
                    sandGeo.Freeze();
                    _shipSandGeo = sandGeo;

                    _staticBgBitmap = BakeToBitmap(w, h, dpiX, dpiY, bdc =>
                    {
                        var bg = MakeVerticalGrad(new Rect(0, 0, w, h),
                            (Color.FromRgb(10, 61, 77), 0.0), (Color.FromRgb(7, 43, 56), 0.42),
                            (Color.FromRgb(5, 30, 40), 0.75), (Color.FromRgb(3, 18, 25), 1.0));
                        bdc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
                    });
                    _staticFgBitmap = BakeToBitmap(w, h, dpiX, dpiY, bdc =>
                    {
                        var aquaRimPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(166, 103, 232, 249))), 2.2));
                        var gpStroke = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(128, 103, 232, 249))), 1.2));
                        var ropePen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(89, 103, 232, 249))), 1.2));

                        // Ship #1 static
                        bdc.PushTransform(new TranslateTransform(w * 0.20, floorY - 10));
                        bdc.PushTransform(new RotateTransform(0.12 * 180.0 / Math.PI));
                        var h1Geo = new StreamGeometry();
                        using (var ctx = h1Geo.Open())
                        {
                            ctx.BeginFigure(new Point(-110, 0), true, true);
                            ctx.LineTo(new Point(-120, -32), false, false);
                            ctx.LineTo(new Point(-85, -42), false, false);
                            ctx.LineTo(new Point(10, -30), false, false);
                            ctx.LineTo(new Point(80, -44), false, false);
                            ctx.LineTo(new Point(105, -34), false, false);
                            ctx.LineTo(new Point(110, 0), false, false);
                        }
                        h1Geo.Freeze();
                        bdc.DrawGeometry(Freeze(new SolidColorBrush(Color.FromRgb(17, 43, 56))), null, h1Geo);
                        var strakePen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(27, 69, 89))), 2.0));
                        bdc.DrawLine(strakePen, new Point(-115, -12), new Point(108, -12));
                        bdc.DrawLine(strakePen, new Point(-118, -22), new Point(105, -22));
                        bdc.DrawGeometry(null, aquaRimPen, BuildPolyline(false, new Point(-120, -32), new Point(-85, -42), new Point(10, -30), new Point(80, -44), new Point(105, -34)));
                        var gpFill = Freeze(new SolidColorBrush(Color.FromRgb(6, 19, 26)));
                        var cannonFill = Freeze(new SolidColorBrush(Color.FromRgb(9, 29, 38)));
                        for (int gp = 0; gp < 3; gp++)
                        {
                            double gpx = -45 + gp * 32;
                            bdc.DrawRectangle(gpFill, gpStroke, new Rect(gpx, -24, 10, 10));
                            bdc.DrawRectangle(cannonFill, null, new Rect(gpx - 4, -21, 6, 4));
                        }
                        var cabGlow = Freeze(new SolidColorBrush(Color.FromArgb(191, 251, 191, 36)));
                        bdc.DrawRectangle(cabGlow, null, new Rect(-112, -34, 6, 7));
                        bdc.DrawRectangle(cabGlow, null, new Rect(-103, -35, 6, 7));
                        var mastPen1 = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(23, 60, 78))), 4.2));
                        bdc.DrawLine(mastPen1, new Point(55, -36), new Point(60, -145));
                        bdc.DrawLine(mastPen1, new Point(-25, -32), new Point(-20, -170));
                        bdc.DrawLine(mastPen1, new Point(105, -34), new Point(145, -55));
                        var yardPen1 = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(23, 60, 78))), 2.4));
                        bdc.DrawLine(yardPen1, new Point(25, -115), new Point(85, -110));
                        bdc.DrawLine(yardPen1, new Point(-55, -135), new Point(15, -130));
                        bdc.DrawLine(ropePen, new Point(60, -145), new Point(30, -34));
                        bdc.DrawLine(ropePen, new Point(60, -145), new Point(90, -38));
                        bdc.DrawLine(ropePen, new Point(-20, -170), new Point(-65, -38));
                        bdc.DrawLine(ropePen, new Point(-20, -170), new Point(25, -30));
                        bdc.Pop();
                        bdc.Pop();

                        // Ship #2 static
                        bdc.PushTransform(new TranslateTransform(w * 0.52, floorY + 4));
                        bdc.PushTransform(new RotateTransform(-0.11 * 180.0 / Math.PI));
                        var h2Geo = new StreamGeometry();
                        using (var ctx = h2Geo.Open())
                        {
                            ctx.BeginFigure(new Point(-180, -12), true, true);
                            ctx.LineTo(new Point(-205, -78), false, false);
                            ctx.LineTo(new Point(-170, -96), false, false);
                            ctx.LineTo(new Point(-100, -72), false, false);
                            ctx.LineTo(new Point(20, -52), false, false);
                            ctx.LineTo(new Point(105, -74), false, false);
                            ctx.LineTo(new Point(185, -98), false, false);
                            ctx.LineTo(new Point(200, -76), false, false);
                            ctx.LineTo(new Point(155, -8), false, false);
                            ctx.LineTo(new Point(-145, -4), false, false);
                        }
                        h2Geo.Freeze();
                        bdc.DrawGeometry(Freeze(new SolidColorBrush(Color.FromRgb(13, 37, 50))), null, h2Geo);
                        var g2RimPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(166, 103, 232, 249))), 2.4));
                        bdc.DrawGeometry(null, g2RimPen, BuildPolyline(false, new Point(-205, -78), new Point(-170, -96), new Point(-100, -72), new Point(20, -52), new Point(105, -74), new Point(185, -98)));
                        var strake2Pen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(24, 62, 80))), 1.8));
                        bdc.DrawLine(strake2Pen, new Point(-190, -45), new Point(170, -45));
                        bdc.DrawLine(strake2Pen, new Point(-175, -25), new Point(150, -25));
                        bdc.DrawEllipse(Freeze(new SolidColorBrush(Color.FromRgb(4, 16, 23))), null, new Point(-25, -35), 70, 24);
                        var ribPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(29, 72, 92))), 4.0));
                        var ribRim = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(140, 103, 232, 249))), 1.6));
                        for (int rib = -72; rib <= 30; rib += 16)
                        {
                            bdc.DrawGeometry(null, ribPen, BuildQuadBezier(new Point(rib, -54), new Point(rib - 6, -30), new Point(rib + 4, -12)));
                            bdc.DrawLine(ribRim, new Point(rib, -54), new Point(rib - 2, -44));
                        }
                        var sternGlow = Freeze(new SolidColorBrush(Color.FromArgb(166, 56, 189, 248)));
                        var sternBorder = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(204, 103, 232, 249))), 1.0));
                        for (int wnd = -195; wnd <= -170; wnd += 9) bdc.DrawRectangle(sternGlow, sternBorder, new Rect(wnd, -82, 6, 10));
                        for (int wnd = -190; wnd <= -175; wnd += 8) bdc.DrawRectangle(sternGlow, null, new Rect(wnd, -64, 5, 8));
                        var gMastPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(22, 59, 77))), 5.5));
                        bdc.DrawLine(gMastPen, new Point(125, -72), new Point(135, -225));
                        bdc.DrawLine(gMastPen, new Point(-95, -70), new Point(-105, -245));
                        bdc.DrawLine(gMastPen, new Point(-165, -88), new Point(-172, -195));
                        bdc.DrawLine(gMastPen, new Point(185, -98), new Point(260, -140));
                        bdc.DrawRectangle(Freeze(new SolidColorBrush(Color.FromRgb(22, 59, 77))), gpStroke, new Rect(-115, -195, 20, 10));
                        var gYardPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(27, 69, 89))), 3.2));
                        bdc.DrawLine(gYardPen, new Point(85, -180), new Point(175, -170));
                        bdc.DrawLine(gYardPen, new Point(-150, -190), new Point(-55, -180));
                        bdc.DrawLine(gYardPen, new Point(-195, -155), new Point(-145, -150));
                        bdc.DrawLine(ropePen, new Point(-105, -245), new Point(-180, -85));
                        bdc.DrawLine(ropePen, new Point(-105, -245), new Point(-20, -55));
                        bdc.DrawLine(ropePen, new Point(135, -225), new Point(75, -60));
                        bdc.DrawLine(ropePen, new Point(135, -225), new Point(195, -90));
                        bdc.DrawEllipse(null, Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(153, 103, 232, 249))), 1.5)), new Point(-110, -82), 8, 8);
                        bdc.Pop();
                        bdc.Pop();

                        // Ship #3 static
                        bdc.PushTransform(new TranslateTransform(w * 0.85, floorY + 12));
                        bdc.PushTransform(new RotateTransform(-0.25 * 180.0 / Math.PI));
                        var bowGeo = BuildQuad(new Point(-60, 0), new Point(-85, -60), new Point(60, -85), new Point(85, 0));
                        bdc.DrawGeometry(Freeze(new SolidColorBrush(Color.FromRgb(16, 42, 54))), null, bowGeo);
                        bdc.DrawGeometry(null, aquaRimPen, BuildPolyline(false, new Point(-85, -60), new Point(60, -85), new Point(85, 0)));
                        var figPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(153, 103, 232, 249))), 1.2));
                        var figBrush = Freeze(new SolidColorBrush(Color.FromRgb(30, 72, 90)));
                        bdc.DrawEllipse(figBrush, figPen, new Point(65, -92), 7, 7);
                        var bowSp = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(24, 60, 78))), 4.2));
                        bdc.DrawLine(bowSp, new Point(55, -82), new Point(110, -115));
                        bdc.DrawLine(bowSp, new Point(-20, -50), new Point(-12, -145));
                        var anchorPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(34, 82, 104))), 5.5));
                        var anchorHi = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(191, 103, 232, 249))), 1.8));
                        bdc.DrawLine(anchorPen, new Point(-32, 8), new Point(-44, -54));
                        bdc.DrawLine(anchorHi, new Point(-31, 8), new Point(-43, -54));
                        bdc.DrawRectangle(Freeze(new SolidColorBrush(Color.FromRgb(45, 24, 11))),
                            Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(128, 103, 232, 249))), 1.0)), new Rect(-62, -48, 36, 6));
                        bdc.DrawEllipse(null, Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(34, 82, 104))), 3.5)), new Point(-45, -59), 8, 8);
                        var armPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(34, 82, 104))), 5.0));
                        bdc.DrawGeometry(null, armPen, BuildArc(-32, -4, 22, Math.PI * 0.55, Math.PI * 1.45));
                        bdc.DrawGeometry(Freeze(new SolidColorBrush(Color.FromRgb(34, 82, 104))), null,
                            BuildTriangle(new Point(-52, -22), new Point(-44, -32), new Point(-38, -18)));
                        var chainPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(204, 103, 232, 249))), 2.0));
                        var pts = new (double x, double y)[] { (-45, -59), (-40, -42), (-32, -22), (-22, -6), (-10, 10), (5, 22), (22, 28), (40, 32) };
                        for (int c = 0; c < pts.Length - 1; c++)
                        {
                            double midX = (pts[c].x + pts[c + 1].x) * 0.5;
                            double midY = (pts[c].y + pts[c + 1].y) * 0.5;
                            double deg = Math.Atan2(pts[c + 1].y - pts[c].y, pts[c + 1].x - pts[c].x) * 180.0 / Math.PI;
                            bdc.PushTransform(new TranslateTransform(midX, midY));
                            bdc.PushTransform(new RotateTransform(deg));
                            bdc.DrawEllipse(null, chainPen, new Point(0, 0), 7, 3.5);
                            bdc.Pop();
                            bdc.Pop();
                        }
                        bdc.Pop();
                        bdc.Pop();
                    });
                    break;

                case "ocean_life":
                    _oceanSunbeamGeo = BuildQuad(new Point(-25, 0), new Point(45, 0), new Point(125, h * 0.78), new Point(-55, h * 0.78));
                    _oceanSunbeamBrush = MakeLinearAbs(new Point(0, 0), new Point(70, h * 0.78),
                        (Color.FromArgb(56, 186, 230, 253), 0.0), (Color.FromArgb(0, 186, 230, 253), 1.0));
                    _staticBgBitmap = BakeToBitmap(w, h, dpiX, dpiY, bdc =>
                    {
                        var bg = MakeVerticalGrad(new Rect(0, 0, w, h),
                            (Color.FromRgb(2, 132, 199), 0.0), (Color.FromRgb(3, 105, 161), 0.42),
                            (Color.FromRgb(12, 74, 110), 0.8), (Color.FromRgb(8, 47, 73), 1.0));
                        bdc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
                    });
                    _staticFgBitmap = BakeToBitmap(w, h, dpiX, dpiY, bdc =>
                    {
                        var reefGeo = new StreamGeometry();
                        using (var ctx = reefGeo.Open())
                        {
                            ctx.BeginFigure(new Point(0, h), true, true);
                            for (double x = 0; x <= w; x += 22)
                            {
                                double ch = 22 + Math.Abs(Math.Sin(x * 0.04)) * 36 + Math.Abs(Math.Cos(x * 0.11)) * 18;
                                ctx.LineTo(new Point(x, h - ch), false, false);
                            }
                            double chEnd = 22 + Math.Abs(Math.Sin(w * 0.04)) * 36 + Math.Abs(Math.Cos(w * 0.11)) * 18;
                            ctx.LineTo(new Point(w, h - chEnd), false, false);
                            ctx.LineTo(new Point(w, h), false, false);
                        }
                        reefGeo.Freeze();
                        bdc.DrawGeometry(Freeze(new SolidColorBrush(Color.FromRgb(5, 30, 48))), null, reefGeo);
                    });
                    break;

                case "snowy_peaks":
                    _staticBgBitmap = BakeToBitmap(w, h, dpiX, dpiY, bdc =>
                    {
                        var sky = MakeVerticalGrad(new Rect(0, 0, w, h * 0.76),
                            (Color.FromRgb(9, 21, 43), 0.0), (Color.FromRgb(30, 58, 138), 0.5), (Color.FromRgb(147, 197, 253), 1.0));
                        bdc.DrawRectangle(sky, null, new Rect(0, 0, w, h));
                    });
                    _staticFgBitmap = BakeToBitmap(w, h, dpiX, dpiY, bdc =>
                    {
                        double baseY = h * 0.78;
                        var summits = new[]
                        {
                            (cx: w * 0.16, bw: w * 0.34, ph: h * 0.44, lit: Color.FromRgb(248, 250, 252), shade: Color.FromRgb(59, 130, 246)),
                            (cx: w * 0.46, bw: w * 0.42, ph: h * 0.54, lit: Color.FromRgb(255, 255, 255), shade: Color.FromRgb(37, 99, 235)),
                            (cx: w * 0.78, bw: w * 0.38, ph: h * 0.48, lit: Color.FromRgb(241, 245, 249), shade: Color.FromRgb(29, 78, 216)),
                            (cx: w * 0.62, bw: w * 0.30, ph: h * 0.38, lit: Color.FromRgb(226, 232, 240), shade: Color.FromRgb(30, 64, 175))
                        };
                        foreach (var pk in summits)
                        {
                            double topY = baseY - pk.ph;
                            double ridgeX = pk.cx + pk.bw * 0.05;
                            bdc.DrawGeometry(Freeze(new SolidColorBrush(pk.lit)), null,
                                BuildTriangle(new Point(pk.cx, topY), new Point(pk.cx - pk.bw * 0.5, baseY), new Point(ridgeX, baseY)));
                            var rg = MakeLinearAbs(new Point(pk.cx, topY), new Point(pk.cx + pk.bw * 0.5, baseY), (pk.shade, 0.0), (Color.FromRgb(15, 23, 42), 1.0));
                            bdc.DrawGeometry(rg, null, BuildTriangle(new Point(pk.cx, topY), new Point(ridgeX, baseY), new Point(pk.cx + pk.bw * 0.5, baseY)));
                        }
                    });
                    break;

                case "city_skyline":
                case "city_shoreline":
                    bool isShore = sceneId == "city_shoreline";
                    double cityBaseY = isShore ? h * 0.62 : h * 0.88;
                    _staticBgBitmap = BakeToBitmap(w, h, dpiX, dpiY, bdc =>
                    {
                        var sky = MakeVerticalGrad(new Rect(0, 0, w, cityBaseY),
                            (Color.FromRgb(4, 6, 19), 0.0), (Color.FromRgb(14, 23, 56), 0.55),
                            (isShore ? Color.FromRgb(38, 24, 74) : Color.FromRgb(30, 27, 75), 1.0));
                        bdc.DrawRectangle(sky, null, new Rect(0, 0, w, h));
                    });
                    for (int lIdx = 0; lIdx < 3; lIdx++)
                    {
                        int targetLayer = lIdx;
                        _layerBitmaps[lIdx] = BakeToBitmap(w, h, dpiX, dpiY, bdc =>
                        {
                            foreach (var b in _skylineBuildings)
                            {
                                if (b.Layer != targetLayer) continue;
                                bdc.DrawRectangle(b.BodyBrush, null, new Rect(b.X, b.Y, b.W, b.H));
                                if (b.HasSpire)
                                    bdc.DrawLine(new Pen(b.BodyBrush, 2.0), new Point(b.X + b.W * 0.5, b.Y), new Point(b.X + b.W * 0.5, b.Y - 26));
                                foreach (var win in b.Windows)
                                {
                                    if (!win.IsDynamic)
                                        bdc.DrawRectangle(win.StaticBright ? win.BrightBrush : win.DimBrush, null, win.AbsRect);
                                }
                            }
                        });
                    }
                    _staticFgBitmap = BakeToBitmap(w, h, dpiX, dpiY, bdc =>
                    {
                        if (isShore)
                        {
                            var water = MakeVerticalGrad(new Rect(0, cityBaseY, w, h - cityBaseY),
                                (Color.FromRgb(9, 19, 44), 0.0), (Color.FromRgb(5, 11, 26), 0.5), (Color.FromRgb(2, 4, 10), 1.0));
                            bdc.DrawRectangle(water, null, new Rect(0, cityBaseY, w, h - cityBaseY));
                            var promBrush = Freeze(new SolidColorBrush(Color.FromArgb(217, 254, 240, 138)));
                            for (double lx = 20; lx < w; lx += 55) bdc.DrawEllipse(promBrush, null, new Point(lx, cityBaseY), 2.0, 2.0);
                        }
                        else
                        {
                            bdc.DrawRectangle(Freeze(new SolidColorBrush(Color.FromRgb(2, 4, 10))), null, new Rect(0, cityBaseY, w, h - cityBaseY));
                        }
                    });
                    break;

                case "sunset":
                    double sunHorY = h * 0.67;
                    _staticBgBitmap = BakeToBitmap(w, h, dpiX, dpiY, bdc =>
                    {
                        var sky = MakeVerticalGrad(new Rect(0, 0, w, sunHorY),
                            (Color.FromRgb(26, 11, 46), 0.0), (Color.FromRgb(88, 28, 79), 0.32),
                            (Color.FromRgb(190, 24, 93), 0.62), (Color.FromRgb(249, 115, 22), 0.84), (Color.FromRgb(253, 224, 71), 1.0));
                        bdc.DrawRectangle(sky, null, new Rect(0, 0, w, h));

                        double sunX = w * 0.5, sunY = sunHorY - h * 0.06, sunR = Math.Min(w, h) * 0.13;
                        var sunHalo = MakeRadialGrad(
                            (Color.FromArgb(242, 254, 249, 195), 0.0), (Color.FromArgb(115, 251, 146, 60), 0.4), (Color.FromArgb(0, 251, 146, 60), 1.0));
                        bdc.DrawEllipse(sunHalo, null, new Point(sunX, sunY), sunR * 2.8, sunR * 2.8);

                        var sunDisc = MakeVerticalGrad(new Rect(sunX - sunR, sunY - sunR, sunR * 2, sunR * 2),
                            (Color.FromRgb(254, 249, 195), 0.0), (Color.FromRgb(251, 146, 60), 0.6), (Color.FromRgb(225, 29, 72), 1.0));
                        bdc.DrawEllipse(sunDisc, null, new Point(sunX, sunY), sunR, sunR);
                    });
                    _staticFgBitmap = BakeToBitmap(w, h, dpiX, dpiY, bdc =>
                    {
                        void DrawRidge(double by, double amp, double freq, Color col)
                        {
                            var sg = new StreamGeometry();
                            using (var ctx = sg.Open())
                            {
                                ctx.BeginFigure(new Point(0, by), true, true);
                                for (double x = 0; x <= w; x += 18)
                                    ctx.LineTo(new Point(x, by - Math.Abs(Math.Sin(x * freq) * amp + Math.Cos(x * freq * 1.7) * (amp * 0.45))), false, true);
                                ctx.LineTo(new Point(w, by - Math.Abs(Math.Sin(w * freq) * amp + Math.Cos(w * freq * 1.7) * (amp * 0.45))), false, true);
                                ctx.LineTo(new Point(w, by), false, false);
                            }
                            sg.Freeze();
                            bdc.DrawGeometry(Freeze(new SolidColorBrush(col)), null, sg);
                        }
                        DrawRidge(sunHorY, h * 0.14, 0.0028, Color.FromRgb(59, 10, 55));
                        DrawRidge(sunHorY + 4, h * 0.09, 0.0042, Color.FromRgb(34, 5, 36));
                        var water = MakeVerticalGrad(new Rect(0, sunHorY, w, h - sunHorY),
                            (Color.FromRgb(76, 17, 59), 0.0), (Color.FromRgb(38, 8, 41), 0.45), (Color.FromRgb(13, 3, 20), 1.0));
                        bdc.DrawRectangle(water, null, new Rect(0, sunHorY, w, h - sunHorY));
                    });
                    break;

                case "rainy_forest":
                    var rfLayers = new[]
                    {
                        (baseY: h * 0.62, treeH: h * 0.24, treeW: 34.0, step: 42.0, col: Color.FromRgb(22, 53, 61), trunk: Color.FromRgb(15, 36, 42), tiers: 3),
                        (baseY: h * 0.74, treeH: h * 0.28, treeW: 46.0, step: 52.0, col: Color.FromRgb(13, 40, 48), trunk: Color.FromRgb(9, 28, 34), tiers: 4),
                        (baseY: h * 0.86, treeH: h * 0.33, treeW: 60.0, step: 64.0, col: Color.FromRgb(7, 29, 34), trunk: Color.FromRgb(5, 19, 23), tiers: 4),
                        (baseY: h * 0.99, treeH: h * 0.38, treeW: 76.0, step: 78.0, col: Color.FromRgb(3, 17, 20), trunk: Color.FromRgb(2, 11, 13), tiers: 5)
                    };
                    void DrawForestLayer(DrawingContext bdc, int lIdx)
                    {
                        var L = rfLayers[lIdx];
                        var colB = Freeze(new SolidColorBrush(L.col));
                        var trunkB = Freeze(new SolidColorBrush(L.trunk));
                        var floorGeo = new StreamGeometry();
                        using (var ctx = floorGeo.Open())
                        {
                            ctx.BeginFigure(new Point(0, h), true, true);
                            for (double x = 0; x <= w; x += 30)
                                ctx.LineTo(new Point(x, L.baseY + Math.Sin(x * 0.008 + lIdx) * 12), false, true);
                            ctx.LineTo(new Point(w, L.baseY + Math.Sin(w * 0.008 + lIdx) * 12), false, true);
                            ctx.LineTo(new Point(w, h), false, false);
                        }
                        floorGeo.Freeze();
                        bdc.DrawGeometry(colB, null, floorGeo);

                        for (double x = -30; x <= w + 50; x += L.step)
                        {
                            double seed = Math.Sin(x * 0.17 + lIdx * 3.1) * 0.28 + 0.95;
                            double th = L.treeH * seed;
                            double tw = L.treeW * seed;
                            double ty = L.baseY + Math.Sin(x * 0.008 + lIdx) * 12;
                            double trunkW = Math.Max(3, tw * 0.16);
                            bdc.DrawRectangle(trunkB, null, new Rect(x - trunkW * 0.5, ty - th * 0.28, trunkW, th * 0.30));
                            for (int tier = 0; tier < L.tiers; tier++)
                            {
                                double tp = (double)tier / L.tiers;
                                double yBot = ty - th * (0.18 + tp * 0.65);
                                double yTop = ty - th * (0.38 + tp * 0.62);
                                double curW = tw * (1.0 - tp * 0.65);
                                var tg = new StreamGeometry();
                                using (var ctx = tg.Open())
                                {
                                    ctx.BeginFigure(new Point(x - curW * 0.5, yBot), true, true);
                                    ctx.LineTo(new Point(x - curW * 0.28, yBot + 3), false, false);
                                    ctx.LineTo(new Point(x, yTop), false, false);
                                    ctx.LineTo(new Point(x + curW * 0.28, yBot + 3), false, false);
                                    ctx.LineTo(new Point(x + curW * 0.5, yBot), false, false);
                                }
                                tg.Freeze();
                                bdc.DrawGeometry(colB, null, tg);
                            }
                        }
                    }
                    _staticBgBitmap = BakeToBitmap(w, h, dpiX, dpiY, bdc =>
                    {
                        var sky = MakeVerticalGrad(new Rect(0, 0, w, h),
                            (Color.FromRgb(5, 18, 22), 0.0), (Color.FromRgb(13, 37, 44), 0.48), (Color.FromRgb(5, 19, 23), 1.0));
                        bdc.DrawRectangle(sky, null, new Rect(0, 0, w, h));
                        DrawForestLayer(bdc, 0);
                    });
                    for (int lIdx = 1; lIdx < 4; lIdx++)
                    {
                        int capture = lIdx;
                        _layerBitmaps[lIdx - 1] = BakeToBitmap(w, h, dpiX, dpiY, bdc => DrawForestLayer(bdc, capture));
                    }
                    break;
            }
        }

        private static ImageSource BakeToBitmap(double w, double h, double dpiX, double dpiY, Action<DrawingContext> draw)
        {
            int pw = Math.Max(1, (int)Math.Ceiling(w * dpiX));
            int ph = Math.Max(1, (int)Math.Ceiling(h * dpiY));
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                draw(dc);
            }
            var rtb = new RenderTargetBitmap(pw, ph, 96.0 * dpiX, 96.0 * dpiY, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        // =========================== 1. NORTHERN LIGHTS ===========================
        private void RenderNorthPole(DrawingContext dc, double w, double h)
        {
            if (_staticBgBitmap != null)
                dc.DrawImage(_staticBgBitmap, new Rect(0, 0, _staticBgBitmap.Width, _staticBgBitmap.Height));

            foreach (var st in _stars)
            {
                if (st.Y > h * 0.68) continue;
                dc.DrawEllipse(PickLut(IceStarLut, 0.25 + 0.75 * Math.Abs(Math.Sin(st.Phase))), null, new Point(st.X, st.Y), st.R, st.R);
            }

            double[] yBases = { h * 0.22, h * 0.29, h * 0.17 };
            double[] amps = { 36.0, 42.0, 28.0 };
            double[] freqs = { 0.0032, 0.0026, 0.0040 };
            double[] spds = { 1.15, -0.9, 1.45 };
            for (int r = 0; r < 3; r++)
            {
                double yBase = yBases[r], amp = amps[r], freq = freqs[r], spd = spds[r];
                var sg = new StreamGeometry();
                using (var ctx = sg.Open())
                {
                    ctx.BeginFigure(new Point(0, yBase + 115), true, true);
                    for (double x = 0; x <= w; x += 20)
                    {
                        double y = yBase + Math.Sin(x * freq + _t * spd) * amp + Math.Cos(x * freq * 0.55 - _t * 0.65) * (amp * 0.45);
                        ctx.LineTo(new Point(x, y - 75), false, true);
                    }
                    double yEnd = yBase + Math.Sin(w * freq + _t * spd) * amp + Math.Cos(w * freq * 0.55 - _t * 0.65) * (amp * 0.45);
                    ctx.LineTo(new Point(w, yEnd - 75), false, true);
                    ctx.LineTo(new Point(w, yBase + 115), false, false);
                }
                sg.Freeze();
                dc.DrawGeometry(_northRibbonBrushes?[r], null, sg);
            }

            if (_staticFgBitmap != null)
                dc.DrawImage(_staticFgBitmap, new Rect(0, 0, _staticFgBitmap.Width, _staticFgBitmap.Height));

            double horizonY = h * 0.73;
            for (int i = 0; i < 24; i++)
            {
                double ry = horizonY + 8 + (i / 24.0) * (h - horizonY - 12);
                double rw = 80 + Math.Sin(_t * 1.8 + i) * 40 + i * 5;
                double rx = w * 0.5 + Math.Sin(_t * 0.7 + i * 1.3) * (w * 0.35);
                dc.DrawRectangle(i % 2 == 0 ? AuroraWaterReflectA : AuroraWaterReflectB, null, new Rect(rx - rw * 0.5, ry, rw, 2.2));
            }

            var floesGeo = new StreamGeometry();
            using (var ctx = floesGeo.Open())
            {
                foreach (var fl in _floes)
                {
                    ctx.BeginFigure(new Point(fl.X - fl.W * 0.5, fl.Y), true, true);
                    ctx.LineTo(new Point(fl.X - fl.W * 0.3, fl.Y - fl.H), false, false);
                    ctx.LineTo(new Point(fl.X + fl.W * 0.35, fl.Y - fl.H * 0.8), false, false);
                    ctx.LineTo(new Point(fl.X + fl.W * 0.5, fl.Y), false, false);
                }
            }
            floesGeo.Freeze();
            dc.DrawGeometry(PolarIceFloeBrush, null, floesGeo);

            foreach (var sn in _snow)
                dc.DrawEllipse(PolarSnowBrush, null, new Point(sn.X, sn.Y), sn.R, sn.R);
        }

        // =========================== 2. FAR SPACE ===========================
        private void RenderFarSpace(DrawingContext dc, double w, double h)
        {
            if (_staticBgBitmap != null)
                dc.DrawImage(_staticBgBitmap, new Rect(0, 0, _staticBgBitmap.Width, _staticBgBitmap.Height));

            foreach (var nb in _nebulae)
            {
                double rad = nb.R * (0.85 + 0.15 * Math.Sin(nb.Phase));
                dc.DrawImage(nb.Sprite, new Rect(nb.X - rad, nb.Y - rad, rad * 2, rad * 2));
            }

            foreach (var st in _stars)
                dc.DrawEllipse(PickLut(st.HueLut, 0.3 + 0.7 * Math.Abs(Math.Sin(st.Phase))), null, new Point(st.X, st.Y), st.R, st.R);

            double qx = w * 0.54, qy = h * 0.21;
            double qPulse = 0.75 + 0.25 * Math.Sin(_t * 2.1);
            Matrix qm = Matrix.Identity;
            qm.Scale(qPulse, qPulse);
            qm.Rotate(-0.55 * 180.0 / Math.PI);
            qm.Translate(qx, qy);
            dc.PushTransform(new MatrixTransform(qm));
            dc.DrawLine(QuasarJetPen, new Point(-55, 0), new Point(55, 0));
            dc.DrawEllipse(QuasarCoreBrush, null, new Point(0, 0), 18, 18);
            dc.Pop();

            foreach (var gal in _galaxies)
            {
                if (gal.Sprite == null) continue;
                Matrix gm = Matrix.Identity;
                gm.Rotate((_t * gal.Speed) * (180.0 / Math.PI));
                gm.Scale(1.0, gal.Tilt);
                gm.Rotate(gal.RotDeg);
                gm.Translate(gal.Gx, gal.Gy);
                dc.PushTransform(new MatrixTransform(gm));
                double e = gal.HalfExtent;
                dc.DrawImage(gal.Sprite, new Rect(-e, -e, e * 2, e * 2));
                dc.Pop();
            }

            if (_shootingStar != null && _shootingStar.Timer <= 0)
            {
                dc.PushTransform(new TranslateTransform(_shootingStar.X, _shootingStar.Y));
                dc.DrawLine(ShootingStarPen, new Point(0, 0), new Point(-_shootingStar.Vx * 11, -_shootingStar.Vy * 11));
                dc.Pop();
            }
        }

        // =========================== 3. SOLAR SYSTEM ===========================
        private static readonly (string name, double orbit, double spd, double r, double phase, bool hasRings, bool hasURing, (double dist, double spd, double r)[] moons)[] SolarPlanets =
        {
            ("Mercury", 0.12, 1.55, 3.2, 0.4, false, false, Array.Empty<(double, double, double)>()),
            ("Venus",   0.19, 1.15, 5.2, 2.1, false, false, Array.Empty<(double, double, double)>()),
            ("Earth",   0.27, 0.85, 6.2, 4.0, false, false, new[] { (2.2, 4.2, 1.9) }),
            ("Mars",    0.35, 0.65, 4.6, 1.2, false, false, Array.Empty<(double, double, double)>()),
            ("Jupiter", 0.55, 0.42, 12.0, 5.1, false, false, new[] { (1.6, 3.8, 1.6), (2.0, 2.7, 1.5), (2.5, 1.9, 1.8) }),
            ("Saturn",  0.69, 0.30, 9.6, 2.8, true,  false, new[] { (2.6, 2.2, 1.9) }),
            ("Uranus",  0.80, 0.21, 6.8, 0.9, false, true,  Array.Empty<(double, double, double)>()),
            ("Neptune", 0.89, 0.15, 6.6, 3.7, false, false, new[] { (2.1, -2.6, 1.6) }),
            ("Pluto",   0.98, 0.10, 3.1, 5.8, false, false, new[] { (2.2, 3.1, 1.3) })
        };

        private void RenderSolarSystem(DrawingContext dc, double w, double h)
        {
            if (_staticBgBitmap != null)
                dc.DrawImage(_staticBgBitmap, new Rect(0, 0, _staticBgBitmap.Width, _staticBgBitmap.Height));

            foreach (var st in _stars)
                dc.DrawEllipse(PickLut(SlateStarLut, 0.25 + 0.7 * Math.Abs(Math.Sin(st.Phase))), null, new Point(st.X, st.Y), st.R * 0.85, st.R * 0.85);

            double cx = w * 0.5, cy = h * 0.56;
            double maxSpan = Math.Min(w * 0.46, h * 0.42);
            const double tilt = 0.48;

            double sunR = Math.Max(19, maxSpan * 0.064);
            double haloR = sunR * 3.6 * (1 + 0.06 * Math.Sin(_t * 2.4));
            dc.DrawEllipse(SolarSunGlowBrush, null, new Point(cx, cy), haloR, haloR);

            foreach (var ast in _asteroids)
            {
                double aRad = maxSpan * (0.455 + ast.RadiusOffset);
                double ang = ast.Angle + _t * ast.Speed;
                dc.DrawEllipse(SolarAstBrush, null, new Point(cx + Math.Cos(ang) * aRad, cy + Math.Sin(ang) * aRad * tilt), ast.R, ast.R);
            }

            foreach (var kbo in _kuiper)
            {
                double kRad = maxSpan * (0.96 + kbo.RadiusOffset);
                double ang = kbo.Angle + _t * kbo.Speed;
                dc.DrawEllipse(kbo.Icy ? SolarKuiperIcyBrush : SolarKuiperRockBrush, null, new Point(cx + Math.Cos(ang) * kRad, cy + Math.Sin(ang) * kRad * tilt), kbo.R, kbo.R);
            }

            if (_staticFgBitmap != null)
                dc.DrawImage(_staticFgBitmap, new Rect(0, 0, _staticFgBitmap.Width, _staticFgBitmap.Height));

            foreach (var pl in SolarPlanets)
            {
                double rx = maxSpan * pl.orbit;
                double ry = rx * tilt;
                double ang = pl.phase + _t * pl.spd;
                double px = cx + Math.Cos(ang) * rx;
                double py = cy + Math.Sin(ang) * ry;

                if (pl.hasRings)
                {
                    dc.PushTransform(new RotateTransform(-0.25 * 180.0 / Math.PI, px, py));
                    dc.DrawEllipse(null, SolarSatRingPen, new Point(px, py), pl.r * 2.25, pl.r * 0.75);
                    dc.Pop();
                }
                if (pl.hasURing)
                {
                    dc.PushTransform(new RotateTransform(0.2 * 180.0 / Math.PI, px, py));
                    dc.DrawEllipse(null, SolarUrRingPen, new Point(px, py), pl.r * 0.65, pl.r * 1.85);
                    dc.Pop();
                }

                dc.DrawEllipse(_planetBrushes[pl.name], null, new Point(px, py), pl.r, pl.r);

                for (int mIdx = 0; mIdx < pl.moons.Length; mIdx++)
                {
                    var m = pl.moons[mIdx];
                    double mAng = _t * m.spd + mIdx * 2.1;
                    double mx = px + Math.Cos(mAng) * (pl.r * m.dist);
                    double my = py + Math.Sin(mAng) * (pl.r * m.dist * 0.62);
                    dc.DrawEllipse(_planetMoonBrushes[(pl.name, mIdx)], null, new Point(mx, my), m.r, m.r);
                }
            }

            double cAng = _t * 0.28 + 1.4;
            double cometX = cx + Math.Cos(cAng) * (maxSpan * 0.72) - maxSpan * 0.18;
            double cometY = cy + Math.Sin(cAng) * (maxSpan * 0.24);
            double awayDeg = Math.Atan2(cometY - cy, cometX - cx) * (180.0 / Math.PI);
            Matrix cm = Matrix.Identity;
            cm.Rotate(awayDeg);
            cm.Translate(cometX, cometY);
            dc.PushTransform(new MatrixTransform(cm));
            dc.DrawLine(SolarCometTailPen, new Point(0, 0), new Point(58, 0));
            dc.Pop();
            dc.DrawEllipse(PureWhiteBrush, null, new Point(cometX, cometY), 2.6, 2.6);
        }

        // =========================== 4. MOON NIGHT ===========================
        private void RenderMoonNight(DrawingContext dc, double w, double h)
        {
            if (_staticBgBitmap != null)
                dc.DrawImage(_staticBgBitmap, new Rect(0, 0, _staticBgBitmap.Width, _staticBgBitmap.Height));

            double horizonY = h * 0.70;
            foreach (var st in _stars)
            {
                if (st.Y > horizonY) continue;
                dc.DrawEllipse(PickLut(SlateStarLut, 0.25 + 0.7 * Math.Abs(Math.Sin(st.Phase))), null, new Point(st.X, st.Y), st.R * 0.9, st.R * 0.9);
            }

            double mx = w * 0.72, my = h * 0.26, mr = Math.Min(w, h) * 0.095;
            dc.DrawEllipse(MoonHaloBrush, null, new Point(mx, my), mr * 3.4, mr * 3.4);
            dc.DrawEllipse(MoonDiscBrush, null, new Point(mx, my), mr, mr);
            dc.DrawEllipse(MoonCraterBrush, null, new Point(mx - 0.28 * mr, my - 0.18 * mr), 0.22 * mr, 0.22 * mr);
            dc.DrawEllipse(MoonCraterBrush, null, new Point(mx + 0.22 * mr, my + 0.12 * mr), 0.26 * mr, 0.26 * mr);
            dc.DrawEllipse(MoonCraterBrush, null, new Point(mx - 0.12 * mr, my + 0.32 * mr), 0.16 * mr, 0.16 * mr);
            dc.DrawEllipse(MoonCraterBrush, null, new Point(mx + 0.18 * mr, my - 0.30 * mr), 0.14 * mr, 0.14 * mr);

            foreach (var cl in _clouds)
                dc.DrawEllipse(MoonCloudBrush, null, new Point(cl.X, cl.Y), cl.W * 0.5, cl.H * 0.5);

            if (_staticFgBitmap != null)
                dc.DrawImage(_staticFgBitmap, new Rect(0, 0, _staticFgBitmap.Width, _staticFgBitmap.Height));

            for (int i = 0; i < 22; i++)
            {
                double ry = horizonY + 6 + (i / 22.0) * (h - horizonY - 12);
                double rw = (mr * 1.3) * (1 - (i / 22.0) * 0.3) + Math.Sin(_t * 2.5 + i) * 14;
                dc.DrawRectangle(MoonLakeReflBrush, null, new Rect(mx - rw * 0.5 + Math.Sin(_t + i) * 8, ry, rw, 2.2));
            }

            foreach (var ff in _fireflies)
                dc.DrawEllipse(PickLut(GoldFireflyLut, 0.25 + 0.75 * Math.Abs(Math.Sin(ff.Phase))), null, new Point(ff.X, ff.Y), ff.R * 1.4, ff.R * 1.4);
        }

        // =========================== 5. CYBER CITY ===========================
        private void RenderCyberCity(DrawingContext dc, double w, double h)
        {
            if (_staticBgBitmap != null)
                dc.DrawImage(_staticBgBitmap, new Rect(0, 0, _staticBgBitmap.Width, _staticBgBitmap.Height));

            for (int idx = 0; idx < _cyberBuildings.Count; idx++)
            {
                var b = _cyberBuildings[idx];
                var stripB = PickLut(CyberAccentLuts[b.AccentIdx], 0.35 + 0.25 * Math.Sin(_t * 2 + idx));
                double stripH = Math.Max(4, b.H - 24);
                dc.DrawRectangle(stripB, null, new Rect(b.X + 8, b.Y + 12, 3, stripH));
                dc.DrawRectangle(stripB, null, new Rect(b.X + b.W - 11, b.Y + 12, 3, stripH));

                if (b.HasSign)
                {
                    var sb = PickLut(CyberAccentLuts[b.AccentIdx], 0.28 + 0.15 * Math.Sin(_t * 4 + idx));
                    dc.DrawRectangle(sb, null, new Rect(b.X + 6, b.Y + 24, b.W - 12, b.SignH));
                }
            }

            foreach (var v in _streaks)
            {
                dc.PushTransform(new TranslateTransform(v.X, v.Y));
                dc.DrawLine(v.StreakPen, new Point(0, 0), new Point(-Math.Sign(v.Vx) * v.Len, 0));
                dc.Pop();
            }
        }

        // =========================== 6. NUCLEAR WASTELAND ===========================
        private void RenderNuclearWasteland(DrawingContext dc, double w, double h)
        {
            if (_staticBgBitmap != null)
                dc.DrawImage(_staticBgBitmap, new Rect(0, 0, _staticBgBitmap.Width, _staticBgBitmap.Height));

            double horizonY = h * 0.68;
            var fissurePen = PickLut(LimeFissurePenLut, 0.35 + 0.35 * (0.5 + 0.5 * Math.Sin(_t * 1.6)));
            dc.DrawLine(fissurePen, new Point(w * 0.18, horizonY + 12), new Point(w * 0.26, horizonY + 36));
            dc.DrawLine(fissurePen, new Point(w * 0.26, horizonY + 36), new Point(w * 0.35, horizonY + 52));
            dc.DrawLine(fissurePen, new Point(w * 0.35, horizonY + 52), new Point(w * 0.44, horizonY + 70));
            dc.DrawLine(fissurePen, new Point(w * 0.65, horizonY + 18), new Point(w * 0.72, horizonY + 45));
            dc.DrawLine(fissurePen, new Point(w * 0.72, horizonY + 45), new Point(w * 0.82, horizonY + 62));

            foreach (var p in _fallout)
            {
                if (p.Ember)
                {
                    double a = 0.4 + 0.6 * Math.Abs(Math.Sin(_t * 2.5 + p.Phase));
                    var eb = PickLut(p.Phase > 3 ? LimeEmberLut : YellowEmberLut, a);
                    dc.DrawEllipse(eb, null, new Point(p.X, p.Y), p.R * 1.2, p.R * 1.2);
                }
                else
                {
                    dc.DrawEllipse(NuclearAshBrush, null, new Point(p.X, p.Y), p.R, p.R);
                }
            }
        }

        // =========================== 7. WASTELAND ===========================
        private void RenderWasteland(DrawingContext dc, double w, double h)
        {
            if (_staticBgBitmap != null)
                dc.DrawImage(_staticBgBitmap, new Rect(0, 0, _staticBgBitmap.Width, _staticBgBitmap.Height));

            double horizonY = h * 0.70;
            double wmx = w * 0.26, wmTopY = horizonY + 6 - 135;
            Matrix wm = Matrix.Identity;
            wm.Rotate((_t * 0.85) * (180.0 / Math.PI));
            wm.Translate(wmx, wmTopY);
            dc.PushTransform(new MatrixTransform(wm));
            dc.DrawGeometry(null, WastelandWheelPen, WindmillWheelGeo);
            dc.DrawEllipse(WastelandHubBrush, null, new Point(0, 0), 7, 7);
            dc.Pop();

            double poleX = w * 0.48, poleY = horizonY + 12;
            double wireSag = Math.Sin(_t * 1.5) * 4;
            dc.DrawGeometry(null, WastelandWirePen, BuildQuadBezier(
                new Point(poleX - 22, poleY - 76), new Point(poleX - 10, poleY - 45 + wireSag), new Point(poleX - 4, poleY - 20)));

            foreach (var dd in _dustDevils)
            {
                double sway = Math.Sin(_t * 1.4 + dd.Phase) * 12;
                dc.DrawGeometry(WastelandDustDevilBrush, null, BuildQuad(
                    new Point(dd.X - 4, dd.Y), new Point(dd.X + 4, dd.Y),
                    new Point(dd.X + dd.W * 0.5 + sway, dd.Y - dd.H), new Point(dd.X - dd.W * 0.5 + sway, dd.Y - dd.H)));
            }

            foreach (var tw in _tumbleweeds)
            {
                if (tw.ScaledPen == null || Math.Abs(tw.CachedR - tw.R) > 0.01)
                {
                    tw.CachedR = tw.R;
                    tw.ScaledPen = Freeze(new Pen(WastelandTumblePen.Brush, 1.4 / Math.Max(1.0, tw.R)));
                }
                double bounce = Math.Abs(Math.Sin(tw.X * 0.02 + tw.BouncePhase)) * 12;
                Matrix tm = Matrix.Identity;
                tm.Rotate(tw.RotDeg);
                tm.Scale(tw.R, tw.R);
                tm.Translate(tw.X, tw.Y - tw.R - bounce);
                dc.PushTransform(new MatrixTransform(tm));
                dc.DrawGeometry(null, tw.ScaledPen, TumbleweedUnitGeo);
                dc.Pop();
            }

            foreach (var ds in _dustStreaks)
                dc.DrawLine(PickLut(AmberDustPenLut, ds.Alpha), new Point(ds.X, ds.Y), new Point(ds.X + ds.Len, ds.Y - 1));
        }

        // =========================== 8. CITY WASTELAND ===========================
        private void RenderCityWasteland(DrawingContext dc, double w, double h)
        {
            if (_staticBgBitmap != null)
                dc.DrawImage(_staticBgBitmap, new Rect(0, 0, _staticBgBitmap.Width, _staticBgBitmap.Height));

            foreach (var pd in _puddles)
            {
                double ripAlpha = (1 - (pd.RippleR / pd.MaxR)) * 0.38;
                dc.DrawEllipse(null, PickLut(PuddleRipplePenLut, ripAlpha), new Point(pd.X, pd.Y),
                    pd.RippleR * (pd.Rx / pd.MaxR), pd.RippleR * (pd.Ry / pd.MaxR));
            }

            foreach (var lp in _leafSpores)
            {
                if (lp.IsLeaf)
                {
                    Matrix lm = Matrix.Identity;
                    lm.Rotate(lp.RotDeg);
                    lm.Translate(lp.X, lp.Y);
                    dc.PushTransform(new MatrixTransform(lm));
                    dc.DrawEllipse(lp.ColBrush, null, new Point(0, 0), lp.R, lp.R * 0.45);
                    dc.Pop();
                }
                else
                {
                    dc.DrawEllipse(PickLut(MintFireflyLut, 0.3 + 0.6 * Math.Abs(Math.Sin(_t * 2 + lp.Phase))),
                        null, new Point(lp.X, lp.Y), lp.R, lp.R);
                }
            }
        }

        // =========================== 9. DESERT DUNES ===========================
        private void RenderDesert(DrawingContext dc, double w, double h)
        {
            if (_staticBgBitmap != null)
                dc.DrawImage(_staticBgBitmap, new Rect(0, 0, _staticBgBitmap.Width, _staticBgBitmap.Height));

            double horizonY = h * 0.58;
            foreach (var st in _stars)
            {
                if (st.Y > horizonY * 0.65) continue;
                dc.DrawEllipse(PickLut(CreamStarLut, 0.25 + 0.65 * Math.Abs(Math.Sin(st.Phase))), null, new Point(st.X, st.Y), st.R * 0.85, st.R * 0.85);
            }

            if (_staticFgBitmap != null)
                dc.DrawImage(_staticFgBitmap, new Rect(0, 0, _staticFgBitmap.Width, _staticFgBitmap.Height));

            foreach (var ds in _dustStreaks)
            {
                double plumeY = ds.Y + Math.Sin(_t * 1.5 + ds.X * 0.015) * 5;
                dc.DrawLine(PickLut(DesertDustPenLut, Math.Min(1.0, ds.Alpha * 1.35)), new Point(ds.X, plumeY), new Point(ds.X - ds.Len, plumeY - 2));
            }
        }

        // =========================== 10. LAKE FOREST ===========================
        private void RenderLakeForest(DrawingContext dc, double w, double h)
        {
            if (_staticBgBitmap != null)
                dc.DrawImage(_staticBgBitmap, new Rect(0, 0, _staticBgBitmap.Width, _staticBgBitmap.Height));

            double horizonY = h * 0.58;
            foreach (var st in _stars)
            {
                if (st.Y > horizonY * 0.85) continue;
                dc.DrawEllipse(PickLut(MintStarLut, 0.25 + 0.65 * Math.Abs(Math.Sin(st.Phase))), null, new Point(st.X, st.Y), st.R * 0.85, st.R * 0.85);
            }

            if (_staticFgBitmap != null)
                dc.DrawImage(_staticFgBitmap, new Rect(0, 0, _staticFgBitmap.Width, _staticFgBitmap.Height));

            double span = Math.Max(100, w * 0.7);
            for (int i = 0; i < 28; i++)
            {
                double ry = horizonY + 8 + (i / 28.0) * (h - horizonY - 14);
                double rw = 55 + Math.Sin(_t * 1.6 + i) * 28 + i * 3;
                double rx = w * 0.15 + ((i * 97) % span) + Math.Sin(_t + i) * 10;
                dc.DrawRectangle(LakeRippleBrush, null, new Rect(rx - rw * 0.5, ry, rw, 2));
            }

            foreach (var ff in _fireflies)
                dc.DrawEllipse(PickLut(MintFireflyLut, 0.25 + 0.75 * Math.Abs(Math.Sin(ff.Phase))), null, new Point(ff.X, ff.Y), ff.R * 1.3, ff.R * 1.3);
        }

        // =========================== 11. SHIPWRECK COVE ===========================
        private void RenderShipwreck(DrawingContext dc, double w, double h)
        {
            if (_staticBgBitmap != null)
                dc.DrawImage(_staticBgBitmap, new Rect(0, 0, _staticBgBitmap.Width, _staticBgBitmap.Height));

            if (_shipSunbeamGeo != null && _shipSunbeamBrush != null)
            {
                for (int r = 0; r < 7; r++)
                {
                    double rx = w * (0.08 + r * 0.15) + Math.Sin(_t * 0.7 + r * 1.2) * 28;
                    dc.PushTransform(new TranslateTransform(rx, 0));
                    dc.DrawGeometry(_shipSunbeamBrush, null, _shipSunbeamGeo);
                    dc.Pop();
                }
            }

            if (_shipSandGeo != null && _shipSandBrush != null)
                dc.DrawGeometry(_shipSandBrush, null, _shipSandGeo);

            double floorY = h * 0.76;
            var caustGeo = new StreamGeometry();
            using (var ctx = caustGeo.Open())
            {
                for (double cx = 20; cx < w; cx += 55)
                {
                    double cp = Math.Sin(_t * 1.8 + cx * 0.02) * 8;
                    ctx.BeginFigure(new Point(cx, floorY + 10 + cp), false, false);
                    ctx.QuadraticBezierTo(new Point(cx + 25, floorY + 22 - cp), new Point(cx + 50, floorY + 12 + cp), true, true);
                }
            }
            caustGeo.Freeze();
            dc.DrawGeometry(null, ShipCaustPen, caustGeo);

            if (_staticFgBitmap != null)
                dc.DrawImage(_staticFgBitmap, new Rect(0, 0, _staticFgBitmap.Width, _staticFgBitmap.Height));

            // Dynamic swaying sails for Ship #1 & Ship #2
            Matrix s1m = Matrix.Identity;
            s1m.Rotate(0.12 * 180.0 / Math.PI);
            s1m.Translate(w * 0.20, floorY - 10);
            dc.PushTransform(new MatrixTransform(s1m));
            double sailSway1 = Math.Sin(_t * 1.3) * 6;
            var sailGeo1 = new StreamGeometry();
            using (var ctx = sailGeo1.Open())
            {
                ctx.BeginFigure(new Point(-50, -134), true, true);
                ctx.QuadraticBezierTo(new Point(-20 + sailSway1, -100), new Point(10, -129), false, true);
                ctx.LineTo(new Point(-5 + sailSway1 * 0.7, -90), false, false);
                ctx.LineTo(new Point(-40 + sailSway1 * 0.4, -92), false, false);
            }
            sailGeo1.Freeze();
            dc.DrawGeometry(ShipSailBrush1, null, sailGeo1);
            dc.Pop();

            Matrix s2m = Matrix.Identity;
            s2m.Rotate(-0.11 * 180.0 / Math.PI);
            s2m.Translate(w * 0.52, floorY + 4);
            dc.PushTransform(new MatrixTransform(s2m));
            double gSailSway = Math.Sin(_t * 0.95) * 8;
            var gSailGeo = new StreamGeometry();
            using (var ctx = gSailGeo.Open())
            {
                ctx.BeginFigure(new Point(-145, -188), true, true);
                ctx.QuadraticBezierTo(new Point(-105 + gSailSway, -148), new Point(-60, -178), false, true);
                ctx.LineTo(new Point(-75 + gSailSway * 0.8, -135), false, false);
                ctx.LineTo(new Point(-135 + gSailSway * 0.5, -140), false, false);
            }
            gSailGeo.Freeze();
            dc.DrawGeometry(ShipSailBrush2, null, gSailGeo);
            dc.Pop();

            // Batched swaying kelp fronds
            var kelpGeo = new StreamGeometry();
            using (var ctx = kelpGeo.Open())
            {
                for (int k = 0; k < 20; k++)
                {
                    double kx = (k / 20.0) * w + 15;
                    double kh = 85 + (k % 5) * 36;
                    ctx.BeginFigure(new Point(kx, h), false, false);
                    for (double y = 0; y <= kh; y += 14)
                        ctx.LineTo(new Point(kx + Math.Sin(_t * 1.5 + y * 0.04 + k) * 14, h - y), true, true);
                }
            }
            kelpGeo.Freeze();
            dc.DrawGeometry(null, ShipKelpPen, kelpGeo);

            foreach (var f in _fishes)
            {
                Matrix fm = Matrix.Identity;
                if (f.Vx < 0) fm.Scale(-1, 1);
                fm.Translate(f.X, f.Y);
                dc.PushTransform(new MatrixTransform(fm));
                dc.DrawEllipse(f.BodyBrush, null, new Point(0, 0), f.Size, f.Size * 0.5);
                double wag = Math.Sin(_t * 12 + f.Phase) * (f.Size * 0.4);
                dc.DrawGeometry(f.BodyBrush, null, BuildTriangle(
                    new Point(-f.Size * 0.8, 0),
                    new Point(-f.Size * 1.4, -f.Size * 0.4 + wag),
                    new Point(-f.Size * 1.4, f.Size * 0.4 + wag)));
                dc.Pop();
            }

            foreach (var b in _bubbles) dc.DrawEllipse(null, ShipBubPen, new Point(b.X, b.Y), b.R * 1.3, b.R * 1.3);

            foreach (var pl in _planktons)
                dc.DrawEllipse(PickLut(CyanPlanktonLut, 0.35 + 0.65 * Math.Abs(Math.Sin(_t * 2.2 + pl.Phase))), null, new Point(pl.X, pl.Y), pl.R, pl.R);
        }

        // =========================== 12. OCEAN LIFE ===========================
        private void RenderOceanLife(DrawingContext dc, double w, double h)
        {
            if (_staticBgBitmap != null)
                dc.DrawImage(_staticBgBitmap, new Rect(0, 0, _staticBgBitmap.Width, _staticBgBitmap.Height));

            if (_oceanSunbeamGeo != null && _oceanSunbeamBrush != null)
            {
                for (int i = 0; i < 7; i++)
                {
                    double rx = (i / 7.0) * w + Math.Sin(_t * 0.9 + i) * 35;
                    dc.PushTransform(new TranslateTransform(rx, 0));
                    dc.DrawGeometry(_oceanSunbeamBrush, null, _oceanSunbeamGeo);
                    dc.Pop();
                }
            }

            foreach (var f in _fishes)
            {
                double bobY = f.Y + Math.Sin(f.Phase) * 3.5;
                Matrix fm = Matrix.Identity;
                fm.Scale(f.Dir, 1);
                fm.Translate(f.X, bobY);
                dc.PushTransform(new MatrixTransform(fm));
                dc.DrawEllipse(f.BodyBrush, null, new Point(0, 0), f.Size, f.Size * 0.48);
                double wag = Math.Sin(f.Phase * 1.5) * 2.5;
                dc.DrawGeometry(f.BodyBrush, null, BuildTriangle(
                    new Point(-f.Size * 0.8, 0),
                    new Point(-f.Size * 1.55, -f.Size * 0.45 + wag),
                    new Point(-f.Size * 1.55, f.Size * 0.45 + wag)));
                dc.Pop();
            }

            foreach (var b in _bubbles)
            {
                double bx = b.X + Math.Sin(_t * 1.8 + b.Phase) * 5;
                dc.DrawEllipse(null, OceanBubPen, new Point(bx, b.Y), b.R, b.R);
            }

            if (_staticFgBitmap != null)
                dc.DrawImage(_staticFgBitmap, new Rect(0, 0, _staticFgBitmap.Width, _staticFgBitmap.Height));
        }

        // =========================== 13. SNOWY MOUNTAIN TOPS ===========================
        private void RenderSnowyPeaks(DrawingContext dc, double w, double h)
        {
            if (_staticBgBitmap != null)
                dc.DrawImage(_staticBgBitmap, new Rect(0, 0, _staticBgBitmap.Width, _staticBgBitmap.Height));

            foreach (var st in _stars)
            {
                if (st.Y > h * 0.42) continue;
                dc.DrawEllipse(PickLut(WhiteStarLut, 0.2 + 0.6 * Math.Abs(Math.Sin(st.Phase))), null, new Point(st.X, st.Y), st.R * 0.85, st.R * 0.85);
            }

            if (_staticFgBitmap != null)
                dc.DrawImage(_staticFgBitmap, new Rect(0, 0, _staticFgBitmap.Width, _staticFgBitmap.Height));

            foreach (var cl in _clouds)
                dc.DrawImage(SnowCloudSprite, new Rect(cl.X - cl.W * 0.55, cl.Y - cl.H, cl.W * 1.10, cl.H * 2.0));

            foreach (var sn in _snow)
                dc.DrawEllipse(PolarSnowBrush, null, new Point(sn.X, sn.Y), sn.R, sn.R);
        }

        // =========================== 14/15. CITY SKYLINE & SHORELINE ===========================
        private void RenderCityMetropolis(DrawingContext dc, double w, double h, bool isShore)
        {
            if (_staticBgBitmap != null)
                dc.DrawImage(_staticBgBitmap, new Rect(0, 0, _staticBgBitmap.Width, _staticBgBitmap.Height));

            double baseY = isShore ? h * 0.62 : h * 0.88;
            foreach (var st in _stars)
            {
                if (st.Y > baseY * 0.75) continue;
                dc.DrawEllipse(PickLut(SlateStarLut, 0.2 + 0.6 * Math.Abs(Math.Sin(st.Phase))), null, new Point(st.X, st.Y), st.R * 0.8, st.R * 0.8);
            }

            for (int lIdx = 0; lIdx < 3; lIdx++)
            {
                if (_layerBitmaps[lIdx] is { } layerBmp)
                    dc.DrawImage(layerBmp, new Rect(0, 0, layerBmp.Width, layerBmp.Height));
                foreach (var b in _skylineBuildings)
                {
                    if (b.Layer != lIdx) continue;
                    if (b.HasSpire)
                        dc.DrawEllipse(PickLut(RedBeaconLut, 0.35 + 0.65 * Math.Abs(Math.Sin(_t * 2.6 + b.X))), null, new Point(b.X + b.W * 0.5, b.Y - 26), 2.2, 2.2);
                    foreach (var win in b.DynamicWindows)
                    {
                        bool bright = Math.Sin(_t * 0.4 + win.Phase) > -0.75;
                        dc.DrawRectangle(bright ? win.BrightBrush : win.DimBrush, null, win.AbsRect);
                    }
                }
            }

            if (_staticFgBitmap != null)
                dc.DrawImage(_staticFgBitmap, new Rect(0, 0, _staticFgBitmap.Width, _staticFgBitmap.Height));

            if (isShore)
            {
                for (int idx = 0; idx < _skylineBuildings.Count; idx++)
                {
                    var b = _skylineBuildings[idx];
                    if (b.Layer < 1) continue;
                    double refX = b.X + b.W * 0.5;
                    const int strips = 14;
                    var stripBrushes = CityNeonRefLuts[b.AccentIdx];
                    for (int s = 0; s < strips; s++)
                    {
                        double sy = baseY + 6 + ((double)s / strips) * (h - baseY - 12);
                        double waveX = Math.Sin(_t * 2.4 + s * 0.7 + idx) * (6 + s * 0.8);
                        double sw = Math.Max(8, b.W * (0.65 - ((double)s / strips) * 0.35));
                        dc.DrawRectangle(stripBrushes[s], null, new Rect(refX - sw * 0.5 + waveX, sy, sw, 2.5));
                    }
                }
            }

            foreach (var tr in _streaks)
                dc.DrawLine(tr.StreakPen, new Point(tr.X, tr.Y), new Point(tr.X - tr.Len, tr.Y));
        }

        // =========================== 16. SUNSET ===========================
        private void RenderSunset(DrawingContext dc, double w, double h)
        {
            if (_staticBgBitmap != null)
                dc.DrawImage(_staticBgBitmap, new Rect(0, 0, _staticBgBitmap.Width, _staticBgBitmap.Height));

            foreach (var cl in _clouds)
                dc.DrawEllipse(SunsetCloudBrush, null, new Point(cl.X, cl.Y), cl.W * 0.5, cl.H * 0.5);

            if (_staticFgBitmap != null)
                dc.DrawImage(_staticFgBitmap, new Rect(0, 0, _staticFgBitmap.Width, _staticFgBitmap.Height));

            double horizonY = h * 0.67;
            double sunX = w * 0.5, sunR = Math.Min(w, h) * 0.13;
            for (int i = 0; i < 26; i++)
            {
                double ry = horizonY + 5 + (i / 26.0) * (h - horizonY - 10);
                double rw = (sunR * 1.6) * (1 - (i / 26.0) * 0.35) + Math.Sin(_t * 3 + i) * 18;
                double rx = sunX + Math.Sin(_t * 1.5 + i * 0.9) * 12;
                dc.DrawRectangle(i < 10 ? SunsetRefGold : SunsetRefOrange, null, new Rect(rx - rw * 0.5, ry, rw, 2.5));
            }

            if (_birds.Count > 0)
            {
                var birdGeo = new StreamGeometry();
                using (var ctx = birdGeo.Open())
                {
                    foreach (var bd in _birds)
                    {
                        double flap = Math.Sin(bd.WingPhase) * (bd.Size * 0.55);
                        ctx.BeginFigure(new Point(bd.X - bd.Size, bd.Y - flap), false, false);
                        ctx.QuadraticBezierTo(new Point(bd.X - bd.Size * 0.4, bd.Y - bd.Size * 0.4), new Point(bd.X, bd.Y), true, true);
                        ctx.QuadraticBezierTo(new Point(bd.X + bd.Size * 0.4, bd.Y - bd.Size * 0.4), new Point(bd.X + bd.Size, bd.Y - flap), true, true);
                    }
                }
                birdGeo.Freeze();
                dc.DrawGeometry(null, SunsetBirdPen, birdGeo);
            }
        }

        // =========================== 17. RAINY FOREST ===========================
        private void RenderRainyForest(DrawingContext dc, double w, double h)
        {
            double[] rfBaseYs = { h * 0.62, h * 0.74, h * 0.86, h * 0.99 };
            if (_staticBgBitmap != null)
                dc.DrawImage(_staticBgBitmap, new Rect(0, 0, _staticBgBitmap.Width, _staticBgBitmap.Height));
            if (0 < _clouds.Count)
            {
                var mist = _clouds[0];
                dc.DrawImage(RainMistSprite, new Rect(mist.X - mist.W * 0.5, rfBaseYs[0] - 90, mist.W, 140));
            }
            for (int lIdx = 1; lIdx < 4; lIdx++)
            {
                if (_layerBitmaps[lIdx - 1] is { } layerBmp)
                    dc.DrawImage(layerBmp, new Rect(0, 0, layerBmp.Width, layerBmp.Height));
                if (lIdx < _clouds.Count)
                {
                    var mist = _clouds[lIdx];
                    dc.DrawImage(RainMistSprite, new Rect(mist.X - mist.W * 0.5, rfBaseYs[lIdx] - 90, mist.W, 140));
                }
            }

            foreach (var r in _rainDrops)
                dc.DrawLine(PickLut(RainDropPenLut, r.Alpha), new Point(r.X, r.Y), new Point(r.X - r.Len * 0.15, r.Y + r.Len));
        }

        // =========================== LUT & SPRITE BUILDERS ===========================
        private static Brush PickLut(Brush[] lut, double alpha)
        {
            int idx = (int)Math.Clamp(Math.Round(alpha * (LUT_STEPS - 1)), 0, LUT_STEPS - 1);
            return lut[idx];
        }

        private static Pen PickLut(Pen[] lut, double alpha)
        {
            int idx = (int)Math.Clamp(Math.Round(alpha * (LUT_STEPS - 1)), 0, LUT_STEPS - 1);
            return lut[idx];
        }

        private static Brush[] BuildAlphaBrushLut(byte r, byte g, byte b)
        {
            var lut = new Brush[LUT_STEPS];
            for (int i = 0; i < LUT_STEPS; i++)
            {
                byte a = AlphaByte((double)i / (LUT_STEPS - 1));
                lut[i] = Freeze(new SolidColorBrush(Color.FromArgb(a, r, g, b)));
            }
            return lut;
        }

        private static Pen[] BuildAlphaPenLut(byte r, byte g, byte b, double thickness, bool roundCaps = false)
        {
            var lut = new Pen[LUT_STEPS];
            for (int i = 0; i < LUT_STEPS; i++)
            {
                byte a = AlphaByte((double)i / (LUT_STEPS - 1));
                var p = new Pen(Freeze(new SolidColorBrush(Color.FromArgb(a, r, g, b))), thickness);
                if (roundCaps)
                {
                    p.StartLineCap = PenLineCap.Round;
                    p.EndLineCap = PenLineCap.Round;
                }
                p.Freeze();
                lut[i] = p;
            }
            return lut;
        }

        private static Brush[][] BuildStarHueLuts()
        {
            int[] starHues = { 210, 45, 280, 190, 330 };
            var res = new Brush[starHues.Length][];
            for (int i = 0; i < starHues.Length; i++)
            {
                Color c = ColorFromHsl(starHues[i], 0.85, 0.85);
                res[i] = BuildAlphaBrushLut(c.R, c.G, c.B);
            }
            return res;
        }

        private static Brush[][] BuildCyberAccentLuts()
        {
            Color[] neonCols =
            {
                Color.FromRgb(34, 211, 238), Color.FromRgb(244, 114, 182),
                Color.FromRgb(168, 85, 247), Color.FromRgb(253, 224, 71), Color.FromRgb(56, 189, 248)
            };
            var res = new Brush[neonCols.Length][];
            for (int i = 0; i < neonCols.Length; i++)
                res[i] = BuildAlphaBrushLut(neonCols[i].R, neonCols[i].G, neonCols[i].B);
            return res;
        }

        private static Brush[][] BuildCityNeonRefLuts()
        {
            Color[] cityNeon =
            {
                Color.FromRgb(251, 191, 36), Color.FromRgb(56, 189, 248),
                Color.FromRgb(244, 114, 182), Color.FromRgb(167, 139, 250), Color.FromRgb(254, 240, 138)
            };
            const int strips = 14;
            var res = new Brush[cityNeon.Length][];
            for (int i = 0; i < cityNeon.Length; i++)
            {
                var row = new Brush[strips];
                for (int s = 0; s < strips; s++)
                {
                    byte a = AlphaByte((1.0 - (double)s / strips) * 0.22);
                    row[s] = Freeze(new SolidColorBrush(Color.FromArgb(a, cityNeon[i].R, cityNeon[i].G, cityNeon[i].B)));
                }
                res[i] = row;
            }
            return res;
        }

        private static ImageSource[] BuildNebulaSprites()
        {
            var res = new ImageSource[FarSpaceNebCols.Length];
            for (int i = 0; i < FarSpaceNebCols.Length; i++)
            {
                Color col = FarSpaceNebCols[i];
                res[i] = BuildSoftOrbSprite(256, 256,
                    (Color.FromArgb(46, col.R, col.G, col.B), 0.0),
                    (Color.FromArgb(15, col.R, col.G, col.B), 0.55),
                    (Color.FromArgb(0, col.R, col.G, col.B), 1.0));
            }
            return res;
        }

        private static ImageSource BuildSoftOrbSprite(int pw, int ph, params (Color c, double off)[] stops)
        {
            var grad = MakeRadialGrad(stops);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawEllipse(grad, null, new Point(pw * 0.5, ph * 0.5), pw * 0.5, ph * 0.5);
            }
            var rtb = new RenderTargetBitmap(pw, ph, 96.0, 96.0, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        private static GeometryGroup BuildWindmillWheelGeometry()
        {
            var gg = new GeometryGroup();
            gg.Children.Add(Freeze(new EllipseGeometry(new Point(0, 0), 36, 36)));
            for (int b = 0; b < 12; b++)
            {
                double ang = (b / 12.0) * Math.PI * 2;
                gg.Children.Add(Freeze(new LineGeometry(new Point(0, 0), new Point(Math.Cos(ang) * 36, Math.Sin(ang) * 36))));
            }
            gg.Freeze();
            return gg;
        }

        private static GeometryGroup BuildUnitTumbleweedGeometry()
        {
            var gg = new GeometryGroup();
            gg.Children.Add(Freeze(new EllipseGeometry(new Point(0, 0), 1.0, 1.0)));
            for (int i = 0; i < 6; i++)
            {
                double ang = (i / 6.0) * Math.PI * 2;
                gg.Children.Add(BuildQuadBezier(
                    new Point(0, 0),
                    new Point(Math.Cos(ang + 0.5) * 0.7, Math.Sin(ang + 0.5) * 0.7),
                    new Point(Math.Cos(ang), Math.Sin(ang))));
            }
            gg.Freeze();
            return gg;
        }

        private static LinearGradientBrush MakeVerticalGradRelative(params (Color c, double off)[] stops)
        {
            var gsc = new GradientStopCollection();
            foreach (var (c, off) in stops) gsc.Add(new GradientStop(c, off));
            var b = new LinearGradientBrush(gsc, new Point(0, 0), new Point(0, 1))
            {
                MappingMode = BrushMappingMode.RelativeToBoundingBox
            };
            b.Freeze();
            return b;
        }

        // =========================== GEOMETRY & BRUSH HELPERS ===========================
        private static T Freeze<T>(T f) where T : Freezable
        {
            if (f.CanFreeze) f.Freeze();
            return f;
        }

        private static byte AlphaByte(double alpha) => (byte)Math.Clamp((int)Math.Round(alpha * 255.0), 0, 255);

        private static Color AlphaCol(byte r, byte g, byte b, double alpha) => Color.FromArgb(AlphaByte(alpha), r, g, b);

        private static LinearGradientBrush MakeVerticalGrad(Rect bounds, params (Color c, double off)[] stops)
        {
            var gsc = new GradientStopCollection();
            foreach (var (c, off) in stops) gsc.Add(new GradientStop(c, off));
            var b = new LinearGradientBrush(gsc, new Point(0, bounds.Top), new Point(0, bounds.Bottom))
            {
                MappingMode = BrushMappingMode.Absolute
            };
            b.Freeze();
            return b;
        }

        private static LinearGradientBrush MakeLinearAbs(Point p0, Point p1, params (Color c, double off)[] stops)
        {
            var gsc = new GradientStopCollection();
            foreach (var (c, off) in stops) gsc.Add(new GradientStop(c, off));
            var b = new LinearGradientBrush(gsc, p0, p1) { MappingMode = BrushMappingMode.Absolute };
            b.Freeze();
            return b;
        }

        private static RadialGradientBrush MakeRadialGrad(params (Color c, double off)[] stops)
            => MakeRadialGrad(new Point(0.5, 0.5), new Point(0.5, 0.5), 0.5, 0.5, stops);

        private static RadialGradientBrush MakeRadialGrad(Point center, Point origin, params (Color c, double off)[] stops)
            => MakeRadialGrad(center, origin, 0.5, 0.5, stops);

        private static RadialGradientBrush MakeRadialGrad(Point center, Point origin, double rx, double ry, params (Color c, double off)[] stops)
        {
            var gsc = new GradientStopCollection();
            foreach (var (c, off) in stops) gsc.Add(new GradientStop(c, off));
            var b = new RadialGradientBrush(gsc)
            {
                Center = center,
                GradientOrigin = origin,
                RadiusX = rx,
                RadiusY = ry
            };
            b.Freeze();
            return b;
        }

        private static StreamGeometry BuildTriangle(Point p0, Point p1, Point p2)
        {
            var sg = new StreamGeometry();
            using (var ctx = sg.Open())
            {
                ctx.BeginFigure(p0, true, true);
                ctx.LineTo(p1, false, false);
                ctx.LineTo(p2, false, false);
            }
            sg.Freeze();
            return sg;
        }

        private static StreamGeometry BuildQuad(Point p0, Point p1, Point p2, Point p3)
        {
            var sg = new StreamGeometry();
            using (var ctx = sg.Open())
            {
                ctx.BeginFigure(p0, true, true);
                ctx.LineTo(p1, false, false);
                ctx.LineTo(p2, false, false);
                ctx.LineTo(p3, false, false);
            }
            sg.Freeze();
            return sg;
        }

        private static StreamGeometry BuildQuadBezier(Point start, Point control, Point end)
        {
            var sg = new StreamGeometry();
            using (var ctx = sg.Open())
            {
                ctx.BeginFigure(start, false, false);
                ctx.QuadraticBezierTo(control, end, true, true);
            }
            sg.Freeze();
            return sg;
        }

        private static StreamGeometry BuildPolyline(bool closed, params Point[] pts)
        {
            var sg = new StreamGeometry();
            using (var ctx = sg.Open())
            {
                ctx.BeginFigure(pts[0], false, closed);
                for (int i = 1; i < pts.Length; i++) ctx.LineTo(pts[i], true, true);
            }
            sg.Freeze();
            return sg;
        }

        private static StreamGeometry BuildArc(double cx, double cy, double r, double a1Rad, double a2Rad)
        {
            Point start = new(cx + Math.Cos(a1Rad) * r, cy + Math.Sin(a1Rad) * r);
            Point end = new(cx + Math.Cos(a2Rad) * r, cy + Math.Sin(a2Rad) * r);
            bool large = Math.Abs(a2Rad - a1Rad) > Math.PI;
            var sg = new StreamGeometry();
            using (var ctx = sg.Open())
            {
                ctx.BeginFigure(start, false, false);
                ctx.ArcTo(end, new Size(r, r), 0, large, SweepDirection.Clockwise, true, true);
            }
            sg.Freeze();
            return sg;
        }

        private static Color ColorFromHsl(double hDeg, double s, double l)
        {
            double c = (1.0 - Math.Abs(2.0 * l - 1.0)) * s;
            double hh = (hDeg % 360.0) / 60.0;
            double x = c * (1.0 - Math.Abs(hh % 2.0 - 1.0));
            double r1 = 0, g1 = 0, b1 = 0;
            if (hh < 1) { r1 = c; g1 = x; }
            else if (hh < 2) { r1 = x; g1 = c; }
            else if (hh < 3) { g1 = c; b1 = x; }
            else if (hh < 4) { g1 = x; b1 = c; }
            else if (hh < 5) { r1 = x; b1 = c; }
            else { r1 = c; b1 = x; }
            double m = l - c / 2.0;
            return Color.FromRgb(
                (byte)Math.Clamp(Math.Round((r1 + m) * 255), 0, 255),
                (byte)Math.Clamp(Math.Round((g1 + m) * 255), 0, 255),
                (byte)Math.Clamp(Math.Round((b1 + m) * 255), 0, 255));
        }
    }
}
