using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Storage;
using StudyHub.Controls;
using StudyHub.Models;
using StudyHub.Services;
using Path = System.IO.Path;

namespace StudyHub;

public partial class MauiMainPage : ContentPage
{
    private readonly StorageService _storage;
    private readonly AudioStudioService _audio;
    private readonly GitHubUpdateService _updater;
    private AppStateData _state;

    private readonly MauiBackdropDrawable _backdrop = new();
    private readonly MauiWhiteboardDrawable _whiteboard = new();
    private Color _currentWbStrokeColor = Color.FromRgb(124, 92, 255);
    private WhiteboardStroke? _activeWbStroke;

    private SubjectItem? _currentSubject;
    private string _activeSubjectTab = "Files";
    private int _currentFlashcardIndex = 0;
    private bool _isFlashcardFlipped = false;

    // Responsive Screen Dimensions
    private double _pageWidth = 390;
    private double _pageHeight = 844;
    private string _currentSearchQuery = "";

    // Focus Timer State
    private bool _isPomoRunning = false;
    private bool _isPomoWorkPhase = true;
    private int _pomoTotalSeconds = 25 * 60;
    private int _pomoRemainingSeconds = 25 * 60;
    private int _sessionActiveMinutes = 0;
    private int _sessionSecondsAccumulator = 0;

    // In-App PDF Reader State
    private string _currentPdfFilePath = "";
    private bool _isPdfDarkMode = false;

    // Animation & Clock Timers
    private readonly Stopwatch _animWatch = Stopwatch.StartNew();
    private double _lastAnimSec = 0;

    private static readonly (string Key, string Label, string Subtitle)[] ZenScenes =
    {
        ("north_pole", "🌌 Northern Lights", "Northern Lights • North Pole & Ice Mountains"),
        ("far_space", "🪐 Far Space", "Far Space • Deep Cosmos, Multi-Galaxies & Quasar"),
        ("solar_system", "☀️ Solar System", "Solar System • Sun & 8 Orbiting Planets"),
        ("moon_night", "🌕 Moon Night", "Moon Night • Full Moon, Clouds & Pine Silhouette"),
        ("cyber_city", "🌆 Cyber City", "Cyber City • Neon Horizon Grid & Synth Sun"),
        ("nuclear_wasteland", "☢️ Nuclear Wasteland", "Nuclear Wasteland • Irradiated Skies & Cooling Tower"),
        ("wasteland", "🏜️ Wasteland", "Wasteland • Dust Storm & Windmill Silhouettes"),
        ("city_wasteland", "🏚️ City Wasteland", "City Wasteland • Overgrown Ruins & Drifting Ash"),
        ("desert", "🐪 Desert Dunes", "Desert Dunes • Warm Rolling Sands & Horizon Haze"),
        ("lake_forest", "🌲 Lake Forest", "Lake Forest • Mirror Alpine Lake & Fireflies"),
        ("shipwreck", "⚓ Shipwreck Cove", "Shipwreck Cove • Underwater Sunbeams & Sunken Hull"),
        ("ocean_life", "🐋 Ocean Life", "Ocean Life • Deep Coral Reef & Swimming Schools"),
        ("snowy_peaks", "🏔️ Snowy Mountain Tops", "Snowy Mountain Tops • High Altitude Alpenglow"),
        ("city_skyline", "🌃 City Skyline", "City Skyline • Midnight Metropolis & Lit Windows"),
        ("city_shoreline", "🌉 City Shoreline", "City Shoreline • Waterfront Harbor Reflections"),
        ("sunset", "🌅 Sunset", "Sunset • Golden Dusk Sky & Gliding Flock"),
        ("rainy_forest", "🌧️ Rainy Forest", "Rainy Forest • Misty Canopy & Falling Rain"),
        ("none", "🌑 None (Minimalist)", "None • Static Focus Backdrop")
    };

    public MauiMainPage()
    {
        InitializeComponent();

        _storage = new StorageService();
        _state = _storage.LoadState();
        _audio = new AudioStudioService(_storage.SoundsDir);
        _updater = new GitHubUpdateService(_storage.BaseDir);

        _audio.SyncCustomSounds(_state.CustomSounds, _state.Settings.MixerLevels, _state.Settings.MixerMaster);
        _audio.IsMasterMuted = _state.Settings.MixerMuted;
        _audio.PlaybackStateChanged += () => MainThread.BeginInvokeOnMainThread(UpdateSoundMixerBadge);

        BackdropCanvas.Drawable = _backdrop;
        WhiteboardCanvas.Drawable = _whiteboard;

        PopulateZenScenePicker();
        ApplySavedSettingsToUi();
        SeedDefaultCoursesIfEmpty();

        UpdateClockDisplay();
        UpdateDashboardStats();
        RenderHeatmap();
        RenderSubjectsGrid();
        RenderArchivedDrawer();
        BuildSoundMixerChannelsUi();
        UpdateTimerUi();

        StartBackgroundLoops();
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width <= 0 || height <= 0) return;

        bool widthChangedSignificantly = Math.Abs(width - _pageWidth) > 4;
        _pageWidth = width;
        _pageHeight = height;

        ApplyResponsiveLayout(width, height);
        if (widthChangedSignificantly)
        {
            RenderSubjectsGrid(_currentSearchQuery);
        }
    }

    private void ApplyResponsiveLayout(double width, double height)
    {
        bool isWide = width >= 660;

        // 1. Adaptive Top Bar: Single row on Landscape/Tablet, 2-Row on Phone Portrait
        WideNavCenterPills.IsVisible = isWide;
        CompactNavSecondRow.IsVisible = !isWide;

        // 2. Adaptive Hero Banner: Side-by-Side on Landscape/Tablet, Stacked Full-Width on Phone Portrait
        if (width >= 680)
        {
            Grid.SetRow(HeroTextStack, 0);
            Grid.SetColumn(HeroTextStack, 0);
            Grid.SetColumnSpan(HeroTextStack, 1);

            Grid.SetRow(BdHeatmapCard, 0);
            Grid.SetColumn(BdHeatmapCard, 1);
            Grid.SetColumnSpan(BdHeatmapCard, 1);
            BdHeatmapCard.Margin = new Thickness(16, 0, 0, 0);
            BdHeatmapCard.HorizontalOptions = LayoutOptions.End;

            LblWelcomeYour.FontSize = 24;
            LblWelcomeAccent.FontSize = 24;
        }
        else
        {
            Grid.SetRow(HeroTextStack, 0);
            Grid.SetColumn(HeroTextStack, 0);
            Grid.SetColumnSpan(HeroTextStack, 2);

            Grid.SetRow(BdHeatmapCard, 1);
            Grid.SetColumn(BdHeatmapCard, 0);
            Grid.SetColumnSpan(BdHeatmapCard, 2);
            BdHeatmapCard.Margin = new Thickness(0, 10, 0, 0);
            BdHeatmapCard.HorizontalOptions = LayoutOptions.Fill;

            LblWelcomeYour.FontSize = 21;
            LblWelcomeAccent.FontSize = 21;
        }

        // 3. Adaptive Drawer Widths for Phones vs Tablets
        LeftDrawerPanel.WidthRequest = Math.Clamp(width * 0.82, 260, 320);
        SoundMixerPanel.WidthRequest = Math.Clamp(width * 0.86, 280, 360);
    }

    private void StartBackgroundLoops()
    {
        // 60 FPS Smooth Procedural Animation Loop for Particles & 17 Zen Scenes
        var animTimer = Dispatcher.CreateTimer();
        animTimer.Interval = TimeSpan.FromMilliseconds(16);
        animTimer.Tick += (_, _) =>
        {
            double now = _animWatch.Elapsed.TotalSeconds;
            double dt = now - _lastAnimSec;
            _lastAnimSec = now;
            _backdrop.Step(dt);
            BackdropCanvas.Invalidate();
        };
        animTimer.Start();

        // 250ms Anti-Flicker Clock Loop
        var clockTimer = Dispatcher.CreateTimer();
        clockTimer.Interval = TimeSpan.FromMilliseconds(250);
        clockTimer.Tick += (_, _) => UpdateClockDisplay();
        clockTimer.Start();

        // 1-Second Focus Timer & Study Activity Tracker
        var pomoTimer = Dispatcher.CreateTimer();
        pomoTimer.Interval = TimeSpan.FromSeconds(1);
        pomoTimer.Tick += (_, _) => TickPomodoroSecond();
        pomoTimer.Start();
    }

    private void SeedDefaultCoursesIfEmpty()
    {
        if (_state.Subjects.Count == 0)
        {
            _state.Subjects.Add(new SubjectItem
            {
                Id = "sub_mobile_comm",
                Name = "Mobile Communication",
                Icon = "📡",
                Color = "#22D3EE",
                Folders = new List<string> { "Lectures", "Tutorials", "Assignments", "Quizzes", "Labs", "References" }
            });
            _state.Subjects.Add(new SubjectItem
            {
                Id = "sub_avionics",
                Name = "Avionics",
                Icon = "✈️",
                Color = "#A855F7",
                Folders = new List<string> { "Lectures", "Tutorials", "Assignments", "Quizzes", "Labs", "References" }
            });
            _ = _storage.SaveStateAsync(_state);
        }
    }

    private static string NormalizeZenSceneKey(string? key) => (key ?? "north_pole").ToLowerInvariant() switch
    {
        "desert_dunes" => "desert",
        "shipwreck_cove" => "shipwreck",
        "custom" or "" => "north_pole",
        var k => k
    };

    private void PopulateZenScenePicker()
    {
        _state.Settings.ZenScene = NormalizeZenSceneKey(_state.Settings.ZenScene);
        _backdrop.ZenScene = _state.Settings.ZenScene;
        UpdateZenSceneUi();
        BuildZenSceneModalList();
    }

    private void UpdateZenSceneUi()
    {
        string activeKey = NormalizeZenSceneKey(_state.Settings.ZenScene);
        var match = ZenScenes.FirstOrDefault(s => string.Equals(s.Key, activeKey, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(match.Key))
            match = ZenScenes[0];

        BtnZenSceneSelector.Text = $"{match.Label} ▾";
        BtnSettingsZenScene.Text = $"🧘 Zen World: {match.Label} ▾";
        LblZenSceneBadge.Text = $"● {match.Subtitle}";
    }

    private void BuildZenSceneModalList()
    {
        ZenScenesListContainer.Children.Clear();
        string activeKey = NormalizeZenSceneKey(_state.Settings.ZenScene);

        foreach (var scene in ZenScenes)
        {
            bool isSelected = string.Equals(scene.Key, activeKey, StringComparison.OrdinalIgnoreCase);

            var card = new Border
            {
                BackgroundColor = isSelected ? Color.FromArgb("#232B58") : Color.FromArgb("#151A36"),
                Stroke = isSelected ? Color.FromArgb("#7C5CFF") : Color.FromArgb("#28315A"),
                StrokeThickness = isSelected ? 1.8 : 1.0,
                StrokeShape = new RoundRectangle { CornerRadius = 14 },
                Padding = new Thickness(14, 10)
            };

            var row = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Star },
                    new ColumnDefinition { Width = GridLength.Auto }
                },
                ColumnSpacing = 10
            };

            var info = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
            info.Children.Add(new Label
            {
                Text = scene.Label,
                FontSize = 13.5,
                FontAttributes = FontAttributes.Bold,
                TextColor = isSelected ? Color.FromArgb("#FACC15") : Color.FromArgb("#F8FAFC")
            });
            info.Children.Add(new Label
            {
                Text = scene.Subtitle,
                FontSize = 10.5,
                TextColor = Color.FromArgb("#94A3C6"),
                LineBreakMode = LineBreakMode.TailTruncation
            });
            row.Add(info, 0, 0);

            if (isSelected)
            {
                row.Add(new Border
                {
                    BackgroundColor = Color.FromArgb("#7C5CFF"),
                    StrokeThickness = 0,
                    StrokeShape = new RoundRectangle { CornerRadius = 10 },
                    Padding = new Thickness(8, 4),
                    VerticalOptions = LayoutOptions.Center,
                    Content = new Label
                    {
                        Text = "Active ✓",
                        FontSize = 10.5,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = Colors.White
                    }
                }, 1, 0);
            }

            string capturedKey = scene.Key;
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => SelectZenScene(capturedKey);
            card.GestureRecognizers.Add(tap);

            card.Content = row;
            ZenScenesListContainer.Children.Add(card);
        }
    }

    private void ApplySavedSettingsToUi()
    {
        _backdrop.IsDarkMode = !string.Equals(_state.Theme, "light", StringComparison.OrdinalIgnoreCase);
        _backdrop.BgTheme = _state.Settings.BgTheme ?? "aurora";
        _backdrop.ParticleEffect = _state.Settings.ParticleEffect ?? "fading_stars";
        _state.Settings.ZenScene = NormalizeZenSceneKey(_state.Settings.ZenScene);
        _backdrop.ZenScene = _state.Settings.ZenScene;
        UpdateZenSceneUi();

        _isPdfDarkMode = _state.Settings.PdfDarkMode;
        UpdatePdfDarkModeButtonVisual();

        int workMin = Math.Clamp(_state.Settings.PomoWork, 1, 180);
        int breakMin = Math.Clamp(_state.Settings.PomoBreak, 1, 90);
        EntryPomoWorkMin.Text = workMin.ToString();
        EntryPomoBreakMin.Text = breakMin.ToString();
        BtnPomoWorkMode.Text = $"{workMin}m Work";
        BtnPomoBreakMode.Text = $"{breakMin}m Break";
        _pomoTotalSeconds = workMin * 60;
        _pomoRemainingSeconds = _pomoTotalSeconds;

        SliderMasterVolume.Value = Math.Clamp(_state.Settings.MixerMaster, 0, 100);
        LblMasterVolPct.Text = $"{(int)SliderMasterVolume.Value}%";
        EntryGithubRepoUrl.Text = _state.Settings.GithubRepoUrl ?? "";

        ApplyTimerDockPosition(_state.TimerDock ?? "bottom-right");
        ApplySurfaceThemeColors();
    }

    private void ApplySurfaceThemeColors()
    {
        bool isDark = _backdrop.IsDarkMode;
        BtnThemeToggle.Text = isDark ? "☀️" : "🌙";
        BtnThemeToggle.TextColor = isDark ? Color.FromArgb("#FACC15") : Color.FromArgb("#6366F1");

        if (!isDark)
        {
            // Clean Daylight Mode
            RootGrid.BackgroundColor = Color.FromArgb("#F1F5F9");
            TopNavBorder.BackgroundColor = Color.FromArgb("#F2FFFFFF");
            TopNavBorder.Stroke = Color.FromArgb("#CBD5E1");
            LblBrandTitle.TextColor = Color.FromArgb("#0F172A");

            NavClockBorder.BackgroundColor = Color.FromArgb("#F8FAFC");
            NavClockBorder.Stroke = Color.FromArgb("#CBD5E1");
            LblNavClockTime.TextColor = Color.FromArgb("#0F172A");
            LblNavClockDate.TextColor = Color.FromArgb("#475569");

            NavClockBorderWide.BackgroundColor = Color.FromArgb("#F8FAFC");
            NavClockBorderWide.Stroke = Color.FromArgb("#CBD5E1");
            LblNavClockTimeWide.TextColor = Color.FromArgb("#0F172A");
            LblNavClockDateWide.TextColor = Color.FromArgb("#475569");

            NavSearchPill.BackgroundColor = Color.FromArgb("#F8FAFC");
            NavSearchPill.Stroke = Color.FromArgb("#CBD5E1");
            EntryGlobalSearch.TextColor = Color.FromArgb("#0F172A");

            NavSearchPillWide.BackgroundColor = Color.FromArgb("#F8FAFC");
            NavSearchPillWide.Stroke = Color.FromArgb("#CBD5E1");
            EntryGlobalSearchWide.TextColor = Color.FromArgb("#0F172A");

            HeroBannerBorder.BackgroundColor = Color.FromArgb("#F2FFFFFF");
            HeroBannerBorder.Stroke = Color.FromArgb("#CBD5E1");
            LblWelcomeYour.TextColor = Color.FromArgb("#0F172A");
            LblHeroSubtitle.TextColor = Color.FromArgb("#475569");
            BdHeatmapCard.BackgroundColor = Color.FromArgb("#F8FAFC");
            BdHeatmapCard.Stroke = Color.FromArgb("#CBD5E1");
            LblMySubjectsHeader.TextColor = Color.FromArgb("#0F172A");
            BdSubjectHeader.BackgroundColor = Color.FromArgb("#F2FFFFFF");
            BdSubjectHeader.Stroke = Color.FromArgb("#CBD5E1");
            LblCurrentSubjectTitle.TextColor = Color.FromArgb("#0F172A");
            BdSubjectTabHost.BackgroundColor = Color.FromArgb("#F2FFFFFF");
            BdSubjectTabHost.Stroke = Color.FromArgb("#CBD5E1");
            BdFloatingTimerExpanded.BackgroundColor = Color.FromArgb("#F8FFFFFF");
            BdFloatingTimerExpanded.Stroke = Color.FromArgb("#CBD5E1");
            LblTimerHeaderTitle.TextColor = Color.FromArgb("#0F172A");
            BdFloatingTimerMinimized.BackgroundColor = Color.FromArgb("#F8FFFFFF");
            BdFloatingTimerMinimized.Stroke = Color.FromArgb("#CBD5E1");
            LblMinimizedTimerTitle.TextColor = Color.FromArgb("#0F172A");
        }
        else
        {
            string bgTheme = (_backdrop.BgTheme ?? "aurora").ToLowerInvariant();
            (string navBg, string cardBg, string strokeCol) = bgTheme switch
            {
                "nebula" => ("#E60A0614", "#D8110A22", "#341F5C"),
                "acrylic" => ("#E60C1D28", "#D8102636", "#23536B"),
                "obsidian" => ("#E605070C", "#D80A0D16", "#1E263B"),
                _ => ("#E611152C", "#D8131830", "#263056")
            };

            RootGrid.BackgroundColor = Color.FromArgb("#090B1A");
            TopNavBorder.BackgroundColor = Color.FromArgb(navBg);
            TopNavBorder.Stroke = Color.FromArgb(strokeCol);
            LblBrandTitle.TextColor = Color.FromArgb("#F8FAFC");

            NavClockBorder.BackgroundColor = Color.FromArgb("#CC12172E");
            NavClockBorder.Stroke = Color.FromArgb("#485C94");
            LblNavClockTime.TextColor = Color.FromArgb("#F8FAFC");
            LblNavClockDate.TextColor = Color.FromArgb("#8A95B7");

            NavClockBorderWide.BackgroundColor = Color.FromArgb("#CC12172E");
            NavClockBorderWide.Stroke = Color.FromArgb("#485C94");
            LblNavClockTimeWide.TextColor = Color.FromArgb("#F8FAFC");
            LblNavClockDateWide.TextColor = Color.FromArgb("#8A95B7");

            NavSearchPill.BackgroundColor = Color.FromArgb("#CC151930");
            NavSearchPill.Stroke = Color.FromArgb("#252D50");
            EntryGlobalSearch.TextColor = Color.FromArgb("#F8FAFC");

            NavSearchPillWide.BackgroundColor = Color.FromArgb("#CC151930");
            NavSearchPillWide.Stroke = Color.FromArgb("#252D50");
            EntryGlobalSearchWide.TextColor = Color.FromArgb("#F8FAFC");

            HeroBannerBorder.BackgroundColor = Color.FromArgb(cardBg);
            HeroBannerBorder.Stroke = Color.FromArgb(strokeCol);
            LblWelcomeYour.TextColor = Color.FromArgb("#F8FAFC");
            LblHeroSubtitle.TextColor = Color.FromArgb("#94A3C6");
            BdHeatmapCard.BackgroundColor = Color.FromArgb("#0D1124");
            BdHeatmapCard.Stroke = Color.FromArgb(strokeCol);
            LblMySubjectsHeader.TextColor = Color.FromArgb("#F8FAFC");
            BdSubjectHeader.BackgroundColor = Color.FromArgb(cardBg);
            BdSubjectHeader.Stroke = Color.FromArgb(strokeCol);
            LblCurrentSubjectTitle.TextColor = Color.FromArgb("#F8FAFC");
            BdSubjectTabHost.BackgroundColor = Color.FromArgb(navBg);
            BdSubjectTabHost.Stroke = Color.FromArgb(strokeCol);
            BdFloatingTimerExpanded.BackgroundColor = Color.FromArgb("#F4131832");
            BdFloatingTimerExpanded.Stroke = Color.FromArgb("#2A3562");
            LblTimerHeaderTitle.TextColor = Color.FromArgb("#F8FAFC");
            BdFloatingTimerMinimized.BackgroundColor = Color.FromArgb("#F512172E");
            BdFloatingTimerMinimized.Stroke = Color.FromArgb("#2E3B6E");
            LblMinimizedTimerTitle.TextColor = Color.FromArgb("#F8FAFC");
        }

        RenderSubjectsGrid(_currentSearchQuery);
    }

    // ================= CLOCK & GREETING =================

    private void UpdateClockDisplay()
    {
        DateTime now = DateTime.Now;
        bool is24h = string.Equals(_state.Settings.TimeFormat, "24h", StringComparison.OrdinalIgnoreCase);

        string navTime = is24h ? now.ToString("HH:mm:ss") : now.ToString("hh:mm:ss tt");
        string navDateWide = now.ToString("ddd, MMM d");
        string navDateCompact = now.ToString("MMM d");

        if (LblNavClockTime.Text != navTime) LblNavClockTime.Text = navTime;
        if (LblNavClockDate.Text != navDateCompact) LblNavClockDate.Text = navDateCompact;
        if (LblNavClockTimeWide.Text != navTime) LblNavClockTimeWide.Text = navTime;
        if (LblNavClockDateWide.Text != navDateWide) LblNavClockDateWide.Text = navDateWide;

        string zenMain = is24h ? now.ToString("HH:mm") : now.ToString("hh:mm");
        string zenAmPm = is24h ? "24H" : now.ToString("tt").ToUpperInvariant();
        if (LblZenClockMain.Text != zenMain) LblZenClockMain.Text = zenMain;
        if (LblZenClockAmPm.Text != zenAmPm) LblZenClockAmPm.Text = zenAmPm;

        int h = now.Hour;
        string greeting = h switch
        {
            >= 5 and < 12 => "☀️ EARLY BIRD FOCUS 🚀",
            >= 12 and < 17 => "⚡ AFTERNOON DEEP WORK 🔥",
            >= 17 and < 22 => "🌆 EVENING STUDY FLOW ✨",
            _ => "🌙 LATE NIGHT GRIND ⚡"
        };
        if (LblGreetingTag.Text != greeting) LblGreetingTag.Text = greeting;
    }

    private void OnClockFormatToggleTapped(object? sender, EventArgs e)
    {
        _state.Settings.TimeFormat = string.Equals(_state.Settings.TimeFormat, "24h", StringComparison.OrdinalIgnoreCase) ? "12h" : "24h";
        UpdateClockDisplay();
        _ = _storage.SaveStateAsync(_state);
        ShowToast($"Clock switched to {_state.Settings.TimeFormat.ToUpperInvariant()} format");
    }

    // ================= DASHBOARD STATS, HEATMAP & RESPONSIVE COURSES GRID =================

    private void UpdateDashboardStats()
    {
        LblSessionUptime.Text = $"{_sessionActiveMinutes}m active";

        SubjectItem? topSub = _state.Subjects.FirstOrDefault(s => !s.Archived);
        if (topSub != null)
        {
            LblTopSubject.Text = $"Top: {topSub.Name}";
        }
        else
        {
            LblTopSubject.Text = "Top: None";
        }

        int totalTasks = _state.Tasks.Count;
        int doneTasks = _state.Tasks.Count(t => t.Completed);
        int ratio = totalTasks > 0 ? (int)Math.Round(doneTasks * 100.0 / totalTasks) : 100;
        LblFocusRatio.Text = $"{ratio}%";
    }

    private void RenderHeatmap()
    {
        HeatmapFlex.Children.Clear();
        DateTime today = DateTime.Today;
        for (int i = 29; i >= 0; i--)
        {
            string key = today.AddDays(-i).ToString("yyyy-MM-dd");
            int val = _state.Activity.TryGetValue(key, out int v) ? v : (i % 7 == 0 ? 2 : (i % 5 == 0 ? 1 : 0));
            Color c = val switch
            {
                <= 0 => Color.FromArgb("#1E2544"),
                1 => Color.FromArgb("#0284C7"),
                2 => Color.FromArgb("#22D3EE"),
                _ => Color.FromArgb("#A855F7")
            };

            HeatmapFlex.Children.Add(new BoxView
            {
                WidthRequest = 10,
                HeightRequest = 10,
                CornerRadius = 2.5,
                Color = c,
                Margin = new Thickness(1.5)
            });
        }
    }

    private void RenderSubjectsGrid(string searchFilter = "")
    {
        SubjectsFlexGrid.Children.Clear();
        bool isDark = _backdrop.IsDarkMode;

        // Calculate Responsive Card Width for Phone Portrait, Phone Landscape, and Tablet
        double availW = Math.Max(280, _pageWidth - 28);
        bool isPhonePortrait = _pageWidth < 540;
        double cardW;
        Thickness cardMargin;

        if (isPhonePortrait)
        {
            // Full-width cards in Phone Portrait (no wasted space on the right!)
            cardW = availW;
            cardMargin = new Thickness(0, 0, 0, 12);
        }
        else if (_pageWidth < 880)
        {
            // 2-Column Grid in Phone Landscape & Compact Tablets
            cardW = Math.Floor((availW - 14) / 2.0);
            cardMargin = new Thickness(0, 0, 12, 12);
        }
        else
        {
            // 3-Column Grid on Large Tablets
            cardW = Math.Floor((availW - 28) / 3.0);
            cardMargin = new Thickness(0, 0, 12, 12);
        }

        // 1. "+ Add Subject" Dashed Card (Compact Horizontal Banner on Phone Portrait, Full Card on Landscape/Tablet)
        var addCard = new Border
        {
            WidthRequest = cardW,
            HeightRequest = isPhonePortrait ? 76 : 150,
            BackgroundColor = isDark ? Color.FromArgb("#9911162B") : Color.FromArgb("#E6F8FAFC"),
            Stroke = isDark ? Color.FromArgb("#334170") : Color.FromArgb("#94A3B8"),
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            StrokeShape = new RoundRectangle { CornerRadius = 18 },
            Margin = cardMargin,
            Padding = new Thickness(14)
        };
        var addTap = new TapGestureRecognizer();
        addTap.Tapped += OnOpenAddSubjectModalClicked;
        addCard.GestureRecognizers.Add(addTap);

        var plusCircle = new Border
        {
            WidthRequest = isPhonePortrait ? 36 : 42,
            HeightRequest = isPhonePortrait ? 36 : 42,
            StrokeThickness = 0,
            BackgroundColor = isDark ? Color.FromArgb("#1D2446") : Color.FromArgb("#E2E8F0"),
            StrokeShape = new RoundRectangle { CornerRadius = isPhonePortrait ? 18 : 21 },
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Content = new Label
            {
                Text = "+",
                FontSize = isPhonePortrait ? 18 : 20,
                FontAttributes = FontAttributes.Bold,
                TextColor = isDark ? Color.FromArgb("#94A3C6") : Color.FromArgb("#334155"),
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center
            }
        };

        var addTitleLbl = new Label
        {
            Text = "Add Subject",
            FontSize = 13.5,
            FontAttributes = FontAttributes.Bold,
            TextColor = isDark ? Color.FromArgb("#94A3C6") : Color.FromArgb("#475569"),
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        };

        if (isPhonePortrait)
        {
            var hStack = new HorizontalStackLayout
            {
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                Spacing = 12
            };
            hStack.Children.Add(plusCircle);
            hStack.Children.Add(addTitleLbl);
            addCard.Content = hStack;
        }
        else
        {
            var vStack = new VerticalStackLayout
            {
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                Spacing = 10
            };
            vStack.Children.Add(plusCircle);
            vStack.Children.Add(addTitleLbl);
            addCard.Content = vStack;
        }

        SubjectsFlexGrid.Children.Add(addCard);

        // 2. Active Course Cards
        var visibleSubjects = _state.Subjects
            .Where(s => !s.Archived && (string.IsNullOrWhiteSpace(searchFilter) || s.Name.Contains(searchFilter, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var sub in visibleSubjects)
        {
            Color accentColor = ResolveSubjectAccentColor(sub.Color);
            int fileCount = _state.Pdfs.Count(f => f.SubjectId == sub.Id);
            int eventCount = _state.Deadlines.Count(d => d.SubjectId == sub.Id && !d.Completed);
            int subTasks = _state.Tasks.Count(t => t.SubjectId == sub.Id);
            int subDone = _state.Tasks.Count(t => t.SubjectId == sub.Id && t.Completed);
            int pct = subTasks > 0 ? (int)Math.Round(subDone * 100.0 / subTasks) : 0;

            var cardBorder = new Border
            {
                WidthRequest = cardW,
                HeightRequest = isPhonePortrait ? 142 : 150,
                BackgroundColor = isDark ? Color.FromArgb("#E6131830") : Colors.White,
                Stroke = isDark ? Color.FromArgb("#263056") : Color.FromArgb("#CBD5E1"),
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 18 },
                Margin = cardMargin,
                Padding = new Thickness(0)
            };

            var openTap = new TapGestureRecognizer();
            openTap.Tapped += (_, _) => OpenSubject(sub);
            cardBorder.GestureRecognizers.Add(openTap);

            var cardGrid = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition { Height = new GridLength(4) },
                    new RowDefinition { Height = GridLength.Star }
                }
            };

            // Top Inline Accent Bar (Flush with rounded border top)
            cardGrid.Add(new BoxView
            {
                Color = accentColor,
                HeightRequest = 4,
                HorizontalOptions = LayoutOptions.Fill
            }, 0, 0);

            var bodyStack = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12),
                Spacing = 10
            };

            // Header Row: Icon + Course Title + Archive/Delete Quick Action
            var headerGrid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = GridLength.Star },
                    new ColumnDefinition { Width = GridLength.Auto }
                },
                ColumnSpacing = 10
            };

            headerGrid.Add(new Border
            {
                WidthRequest = 40,
                HeightRequest = 40,
                BackgroundColor = accentColor,
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 12 },
                Content = new Label
                {
                    Text = MapSubjectEmoji(sub.Icon),
                    FontSize = 18,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center
                }
            }, 0, 0);

            headerGrid.Add(new Label
            {
                Text = sub.Name,
                FontSize = 15,
                FontAttributes = FontAttributes.Bold,
                TextColor = isDark ? Color.FromArgb("#F8FAFC") : Color.FromArgb("#0F172A"),
                VerticalOptions = LayoutOptions.Center,
                LineBreakMode = LineBreakMode.TailTruncation,
                MaxLines = 2
            }, 1, 0);

            var menuBtn = new Button
            {
                Text = "⋯",
                FontSize = 14,
                WidthRequest = 30,
                HeightRequest = 28,
                Padding = new Thickness(0),
                CornerRadius = 8,
                BackgroundColor = isDark ? Color.FromArgb("#1A2140") : Color.FromArgb("#F1F5F9"),
                TextColor = isDark ? Color.FromArgb("#94A3C6") : Color.FromArgb("#475569")
            };
            menuBtn.Clicked += async (_, _) => await OnSubjectQuickMenuClicked(sub);
            headerGrid.Add(menuBtn, 2, 0);

            bodyStack.Children.Add(headerGrid);

            // Files & Events Pill
            var statsPill = new Border
            {
                BackgroundColor = isDark ? Color.FromArgb("#0D1124") : Color.FromArgb("#F1F5F9"),
                Stroke = isDark ? Color.FromArgb("#1E2648") : Color.FromArgb("#E2E8F0"),
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 10 },
                Padding = new Thickness(10, 5)
            };
            var statsGrid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Star },
                    new ColumnDefinition { Width = GridLength.Auto }
                }
            };
            statsGrid.Add(new Label
            {
                Text = $"📁 {fileCount} Files",
                FontSize = 11,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#38BDF8")
            }, 0, 0);
            statsGrid.Add(new Label
            {
                Text = $"📅 {eventCount} Events",
                FontSize = 11,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#C084FC")
            }, 1, 0);
            statsPill.Content = statsGrid;
            bodyStack.Children.Add(statsPill);

            // Progress Bar Row
            var progGrid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = GridLength.Star }
                },
                ColumnSpacing = 8,
                VerticalOptions = LayoutOptions.Center
            };
            progGrid.Add(new Label
            {
                Text = $"{pct}%",
                FontSize = 10.5,
                FontAttributes = FontAttributes.Bold,
                TextColor = isDark ? Color.FromArgb("#8A95B7") : Color.FromArgb("#64748B")
            }, 0, 0);
            progGrid.Add(new ProgressBar
            {
                Progress = pct / 100.0,
                ProgressColor = accentColor,
                VerticalOptions = LayoutOptions.Center
            }, 1, 0);
            bodyStack.Children.Add(progGrid);

            cardGrid.Add(bodyStack, 0, 1);
            cardBorder.Content = cardGrid;
            SubjectsFlexGrid.Children.Add(cardBorder);
        }
    }

    private void RenderArchivedDrawer()
    {
        ArchivedCoursesDrawerList.Children.Clear();
        var archived = _state.Subjects.Where(s => s.Archived).ToList();
        if (archived.Count == 0)
        {
            ArchivedCoursesDrawerList.Children.Add(new Label
            {
                Text = "No archived courses.",
                FontSize = 11.5,
                TextColor = Color.FromArgb("#687296")
            });
            return;
        }

        foreach (var sub in archived)
        {
            var row = new Border
            {
                BackgroundColor = Color.FromArgb("#161B36"),
                Stroke = Color.FromArgb("#28315A"),
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 10 },
                Padding = new Thickness(10, 8)
            };
            var g = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Star },
                    new ColumnDefinition { Width = GridLength.Auto }
                }
            };
            g.Add(new Label
            {
                Text = $"{MapSubjectEmoji(sub.Icon)} {sub.Name}",
                FontSize = 12.5,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#F8FAFC"),
                VerticalOptions = LayoutOptions.Center
            }, 0, 0);

            var restoreBtn = new Button
            {
                Text = "Restore",
                FontSize = 10.5,
                BackgroundColor = Color.FromArgb("#7C5CFF"),
                TextColor = Colors.White,
                CornerRadius = 8,
                Padding = new Thickness(8, 4)
            };
            restoreBtn.Clicked += (_, _) =>
            {
                sub.Archived = false;
                RenderSubjectsGrid(_currentSearchQuery);
                RenderArchivedDrawer();
                _ = _storage.SaveStateAsync(_state);
                ShowToast($"Restored '{sub.Name}'");
            };
            g.Add(restoreBtn, 1, 0);
            row.Content = g;
            ArchivedCoursesDrawerList.Children.Add(row);
        }
    }

    private async Task OnSubjectQuickMenuClicked(SubjectItem sub)
    {
        string action = await DisplayActionSheet($"Manage '{sub.Name}'", "Cancel", "🗑️ Delete Course", "📂 Open Workspace", "✏️ Rename Course", "📦 Archive Course");
        if (action == "📂 Open Workspace")
        {
            OpenSubject(sub);
        }
        else if (action == "✏️ Rename Course")
        {
            string? newName = await DisplayPromptAsync("Rename Course", "Enter new subject title:", initialValue: sub.Name);
            if (!string.IsNullOrWhiteSpace(newName))
            {
                sub.Name = newName.Trim();
                RenderSubjectsGrid(_currentSearchQuery);
                UpdateDashboardStats();
                _ = _storage.SaveStateAsync(_state);
            }
        }
        else if (action == "📦 Archive Course")
        {
            sub.Archived = true;
            RenderSubjectsGrid(_currentSearchQuery);
            RenderArchivedDrawer();
            UpdateDashboardStats();
            _ = _storage.SaveStateAsync(_state);
            ShowToast($"Archived '{sub.Name}' to left StudyHub drawer");
        }
        else if (action == "🗑️ Delete Course")
        {
            bool confirm = await DisplayAlert("Delete Course", $"Delete '{sub.Name}' and its items?", "Delete", "Cancel");
            if (confirm)
            {
                _state.Subjects.Remove(sub);
                RenderSubjectsGrid(_currentSearchQuery);
                UpdateDashboardStats();
                _ = _storage.SaveStateAsync(_state);
                ShowToast($"Deleted '{sub.Name}'");
            }
        }
    }

    private async void OnOpenAddSubjectModalClicked(object? sender, EventArgs e)
    {
        string? name = await DisplayPromptAsync("Add New Subject", "Enter course or subject name:", "Create", "Cancel", "e.g., Digital Signal Processing");
        if (string.IsNullOrWhiteSpace(name)) return;

        string[] palette = { "#7C5CFF", "#22D3EE", "#10B981", "#F59E0B", "#EC4899", "#3B82F6" };
        string[] icons = { "🎓", "📡", "✈️", "💻", "📐", "🔬" };
        int idx = _state.Subjects.Count % palette.Length;

        var newSub = new SubjectItem
        {
            Id = "sub_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Name = name.Trim(),
            Icon = icons[idx],
            Color = palette[idx],
            Folders = new List<string> { "Lectures", "Tutorials", "Assignments", "Quizzes", "Labs", "References" }
        };

        _state.Subjects.Add(newSub);
        RenderSubjectsGrid(_currentSearchQuery);
        UpdateDashboardStats();
        await _storage.SaveStateAsync(_state);
        ShowToast($"Created subject '{newSub.Name}'");
    }

    // ================= SUBJECT WORKSPACE & TABS =================

    private void OpenSubject(SubjectItem sub)
    {
        _currentSubject = sub;
        _state.CurrentSubjectId = sub.Id;
        HomeDashboardScroll.IsVisible = false;
        SubjectWorkspaceGrid.IsVisible = true;

        UpdateSubjectHeaderInfo();
        BuildSubjectTabsBar();
        SwitchSubjectTab(_activeSubjectTab);
    }

    private void OnBackToHomeClicked(object? sender, EventArgs e)
    {
        _currentSubject = null;
        SubjectWorkspaceGrid.IsVisible = false;
        HomeDashboardScroll.IsVisible = true;
        RenderSubjectsGrid(_currentSearchQuery);
        UpdateDashboardStats();
    }

    private async void OnEditCurrentSubjectClicked(object? sender, EventArgs e)
    {
        if (_currentSubject == null) return;
        string? newName = await DisplayPromptAsync("Edit Subject", "Course title:", initialValue: _currentSubject.Name);
        if (!string.IsNullOrWhiteSpace(newName))
        {
            _currentSubject.Name = newName.Trim();
            UpdateSubjectHeaderInfo();
            await _storage.SaveStateAsync(_state);
        }
    }

    private void UpdateSubjectHeaderInfo()
    {
        if (_currentSubject == null) return;
        LblCurrentSubjectTitle.Text = $"{MapSubjectEmoji(_currentSubject.Icon)} {_currentSubject.Name}";
        int fCount = _state.Pdfs.Count(p => p.SubjectId == _currentSubject.Id);
        int tCount = _state.Tasks.Count(t => t.SubjectId == _currentSubject.Id);
        int cCount = _state.Flashcards.Count(fc => fc.SubjectId == _currentSubject.Id);
        LblCurrentSubjectStats.Text = $"{fCount} Files • {tCount} Tasks • {cCount} Flashcards";
    }

    private void BuildSubjectTabsBar()
    {
        SubjectTabsContainer.Children.Clear();
        (string Key, string Label)[] tabs =
        {
            ("Files", "📁 Files & PDF"),
            ("Notes", "📝 Notes"),
            ("Tasks", "✅ Tasks"),
            ("Flashcards", "🃏 Flashcards"),
            ("Whiteboard", "🎨 Whiteboard"),
            ("Deadlines", "📅 Deadlines"),
            ("Links", "🔗 Links")
        };

        foreach (var t in tabs)
        {
            bool active = string.Equals(_activeSubjectTab, t.Key, StringComparison.OrdinalIgnoreCase);
            var btn = new Button
            {
                Text = t.Label,
                FontSize = 12,
                FontAttributes = FontAttributes.Bold,
                BackgroundColor = active ? Color.FromArgb("#7C5CFF") : Color.FromArgb("#181D38"),
                TextColor = active ? Colors.White : Color.FromArgb("#CBD5E1"),
                CornerRadius = 10,
                Padding = new Thickness(12, 6)
            };
            string tabKey = t.Key;
            btn.Clicked += (_, _) => SwitchSubjectTab(tabKey);
            SubjectTabsContainer.Children.Add(btn);
        }
    }

    private void SwitchSubjectTab(string tabKey)
    {
        _activeSubjectTab = tabKey;
        BuildSubjectTabsBar();

        TabFilesView.IsVisible = tabKey == "Files";
        TabNotesView.IsVisible = tabKey == "Notes";
        TabTasksView.IsVisible = tabKey == "Tasks";
        TabFlashcardsView.IsVisible = tabKey == "Flashcards";
        TabWhiteboardView.IsVisible = tabKey == "Whiteboard";
        TabDeadlinesView.IsVisible = tabKey == "Deadlines";
        TabLinksView.IsVisible = tabKey == "Links";

        if (tabKey == "Files") RenderSubjectFilesTab();
        else if (tabKey == "Notes") LoadSubjectNotesTab();
        else if (tabKey == "Tasks") RenderSubjectTasksTab();
        else if (tabKey == "Flashcards") RenderSubjectFlashcardsTab();
        else if (tabKey == "Deadlines") RenderSubjectDeadlinesTab();
        else if (tabKey == "Links") RenderSubjectLinksTab();
    }

    // ---- Tab 1: Files, Folders & Native PDF Converter ----

    private void RenderSubjectFilesTab()
    {
        if (_currentSubject == null) return;
        FoldersListContainer.Children.Clear();

        foreach (var folder in _currentSubject.Folders)
        {
            var filesInFolder = _state.Pdfs
                .Where(f => f.SubjectId == _currentSubject.Id && string.Equals(f.Folder, folder, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var folderCard = new Border
            {
                BackgroundColor = Color.FromArgb("#141933"),
                Stroke = Color.FromArgb("#26305A"),
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 14 },
                Padding = new Thickness(12)
            };

            var stack = new VerticalStackLayout { Spacing = 8 };
            stack.Children.Add(new Label
            {
                Text = $"📂 {folder} ({filesInFolder.Count})",
                FontSize = 13.5,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#38BDF8")
            });

            if (filesInFolder.Count == 0)
            {
                stack.Children.Add(new Label
                {
                    Text = "No files in this folder yet.",
                    FontSize = 11.5,
                    TextColor = Color.FromArgb("#687296")
                });
            }
            else
            {
                foreach (var file in filesInFolder)
                {
                    var fileRow = new Border
                    {
                        BackgroundColor = Color.FromArgb("#0D1124"),
                        Stroke = Color.FromArgb("#1F284D"),
                        StrokeThickness = 1,
                        StrokeShape = new RoundRectangle { CornerRadius = 10 },
                        Padding = new Thickness(10, 8)
                    };

                    var rowGrid = new Grid
                    {
                        ColumnDefinitions =
                        {
                            new ColumnDefinition { Width = GridLength.Star },
                            new ColumnDefinition { Width = GridLength.Auto },
                            new ColumnDefinition { Width = GridLength.Auto }
                        },
                        ColumnSpacing = 8
                    };

                    rowGrid.Add(new Label
                    {
                        Text = $"📄 {file.OriginalName}",
                        FontSize = 12.5,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = Color.FromArgb("#F8FAFC"),
                        VerticalOptions = LayoutOptions.Center,
                        LineBreakMode = LineBreakMode.TailTruncation
                    }, 0, 0);

                    var openBtn = new Button
                    {
                        Text = "📖 Read",
                        FontSize = 11,
                        FontAttributes = FontAttributes.Bold,
                        BackgroundColor = Color.FromArgb("#7C5CFF"),
                        TextColor = Colors.White,
                        CornerRadius = 8,
                        Padding = new Thickness(10, 4)
                    };
                    openBtn.Clicked += async (_, _) => await OpenStudyFileAsync(file);
                    rowGrid.Add(openBtn, 1, 0);

                    var delBtn = new Button
                    {
                        Text = "🗑️",
                        FontSize = 11,
                        BackgroundColor = Color.FromArgb("#3B182A"),
                        TextColor = Color.FromArgb("#F43F5E"),
                        CornerRadius = 8,
                        Padding = new Thickness(8, 4)
                    };
                    delBtn.Clicked += async (_, _) =>
                    {
                        _storage.DeleteStudyFile(file.Filename);
                        _state.Pdfs.Remove(file);
                        RenderSubjectFilesTab();
                        UpdateSubjectHeaderInfo();
                        await _storage.SaveStateAsync(_state);
                    };
                    rowGrid.Add(delBtn, 2, 0);

                    fileRow.Content = rowGrid;
                    stack.Children.Add(fileRow);
                }
            }

            folderCard.Content = stack;
            FoldersListContainer.Children.Add(folderCard);
        }
    }

    private async void OnAddFolderClicked(object? sender, EventArgs e)
    {
        if (_currentSubject == null) return;
        string? folder = await DisplayPromptAsync("New Folder", "Enter folder name:");
        if (string.IsNullOrWhiteSpace(folder)) return;
        if (!_currentSubject.Folders.Contains(folder.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            _currentSubject.Folders.Add(folder.Trim());
            RenderSubjectFilesTab();
            await _storage.SaveStateAsync(_state);
        }
    }

    private async void OnUploadStudyFileClicked(object? sender, EventArgs e)
    {
        if (_currentSubject == null) return;
        try
        {
            var result = await FilePicker.Default.PickAsync();
            if (result == null) return;

            string folder = await DisplayActionSheet("Select Target Folder", "Cancel", null, _currentSubject.Folders.ToArray());
            if (string.IsNullOrWhiteSpace(folder) || folder == "Cancel") folder = "Lectures";

            string tempPath = await CopyPickedFileToLocalTempAsync(result);
            var item = _storage.ImportStudyFile(tempPath, _currentSubject.Id, folder);
            _state.Pdfs.Add(item);
            RecordStudyActivity();
            RenderSubjectFilesTab();
            UpdateSubjectHeaderInfo();
            await _storage.SaveStateAsync(_state);
            ShowToast($"Uploaded '{item.OriginalName}'");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Upload Error", ex.Message, "OK");
        }
    }

    private async void OnConvertFileToPdfClicked(object? sender, EventArgs e)
    {
        if (_currentSubject == null) return;
        try
        {
            var result = await FilePicker.Default.PickAsync();
            if (result == null) return;

            string tempPath = await CopyPickedFileToLocalTempAsync(result);
            string outDir = _storage.ResolveExportDir(_state);
            var conv = PdfConverterService.ConvertFileToPdf(tempPath, outDir);
            if (!conv.Success)
            {
                await DisplayAlert("PDF Converter", conv.Error, "OK");
                return;
            }

            var imported = _storage.ImportStudyFile(conv.OutputPdfPath, _currentSubject.Id, "Lectures");
            _state.Pdfs.Add(imported);
            RenderSubjectFilesTab();
            UpdateSubjectHeaderInfo();
            await _storage.SaveStateAsync(_state);
            ShowToast($"Converted & added '{imported.OriginalName}'!");
        }
        catch (Exception ex)
        {
            await DisplayAlert("PDF Conversion Error", ex.Message, "OK");
        }
    }

    private static async Task<string> CopyPickedFileToLocalTempAsync(FileResult picked)
    {
        if (!string.IsNullOrWhiteSpace(picked.FullPath) && File.Exists(picked.FullPath))
            return picked.FullPath;

        string tempDir = Path.Combine(FileSystem.CacheDirectory, "StudyHub_Picked");
        Directory.CreateDirectory(tempDir);
        string dest = Path.Combine(tempDir, picked.FileName);
        using var srcStream = await picked.OpenReadAsync();
        using var dstStream = File.Create(dest);
        await srcStream.CopyToAsync(dstStream);
        return dest;
    }

    // ---- In-App Native PDF Reader with Dark Mode ----

    private async Task OpenStudyFileAsync(StudyFileItem file)
    {
        string fullPath = _storage.GetStudyFilePath(file.Filename);
        if (!File.Exists(fullPath))
        {
            await DisplayAlert("File Not Found", $"Could not locate '{file.OriginalName}' on disk.", "OK");
            return;
        }

        _currentPdfFilePath = fullPath;
        LblPdfTitle.Text = file.OriginalName;
        LblPdfSubject.Text = $"{_currentSubject?.Name ?? "Course"} • StudyHub In-App PDF Reader";
        PdfReaderOverlay.IsVisible = true;

        await RenderPdfDocumentPagesAsync(fullPath);
    }

    private async Task RenderPdfDocumentPagesAsync(string fullPath)
    {
        PdfPagesStack.Children.Clear();
        PdfPagesScroll.BackgroundColor = _isPdfDarkMode ? Color.FromArgb("#0B0D18") : Color.FromArgb("#1E2235");

#if ANDROID
        await Task.Run(() =>
        {
            try
            {
                using var pfd = Android.OS.ParcelFileDescriptor.Open(new Java.IO.File(fullPath), Android.OS.ParcelFileMode.ReadOnly);
                if (pfd == null) return;
                using var renderer = new Android.Graphics.Pdf.PdfRenderer(pfd);
                int pageCount = Math.Min(renderer.PageCount, 40);

                for (int i = 0; i < pageCount; i++)
                {
                    using var page = renderer.OpenPage(i);
                    int w = Math.Max(1, page.Width * 2);
                    int h = Math.Max(1, page.Height * 2);
                    using var bmp = Android.Graphics.Bitmap.CreateBitmap(w, h, Android.Graphics.Bitmap.Config.Argb8888!);
                    using var canvas = new Android.Graphics.Canvas(bmp);
                    canvas.DrawColor(Android.Graphics.Color.White);
                    page.Render(bmp, null, null, Android.Graphics.Pdf.PdfRenderMode.ForDisplay);

                    Android.Graphics.Bitmap finalBmp = bmp;
                    Android.Graphics.Bitmap? darkBmp = null;

                    if (_isPdfDarkMode)
                    {
                        darkBmp = Android.Graphics.Bitmap.CreateBitmap(w, h, Android.Graphics.Bitmap.Config.Argb8888!);
                        using var darkCanvas = new Android.Graphics.Canvas(darkBmp);
                        using var paint = new Android.Graphics.Paint();
                        // Smart Dark Paper Matrix: inverts white paper to dark #14161F while keeping text crisp
                        float[] matrix =
                        {
                            -0.88f,  0.00f,  0.00f, 0f, 240f,
                             0.00f, -0.88f,  0.00f, 0f, 242f,
                             0.00f,  0.00f, -0.85f, 0f, 248f,
                             0.00f,  0.00f,  0.00f, 1f,   0f
                        };
                        paint.SetColorFilter(new Android.Graphics.ColorMatrixColorFilter(matrix));
                        darkCanvas.DrawBitmap(bmp, 0, 0, paint);
                        finalBmp = darkBmp;
                    }

                    using var ms = new MemoryStream();
                    finalBmp.Compress(Android.Graphics.Bitmap.CompressFormat.Png!, 92, ms);
                    darkBmp?.Dispose();
                    byte[] pngBytes = ms.ToArray();

                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        var pageBorder = new Border
                        {
                            BackgroundColor = _isPdfDarkMode ? Color.FromArgb("#161822") : Colors.White,
                            Stroke = Color.FromArgb("#2E365E"),
                            StrokeThickness = 1,
                            StrokeShape = new RoundRectangle { CornerRadius = 8 },
                            Padding = new Thickness(4)
                        };
                        pageBorder.Content = new Image
                        {
                            Source = ImageSource.FromStream(() => new MemoryStream(pngBytes)),
                            Aspect = Aspect.AspectFit
                        };
                        PdfPagesStack.Children.Add(pageBorder);
                    });
                }
            }
            catch (Exception ex)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    PdfPagesStack.Children.Add(new Label
                    {
                        Text = $"Could not render PDF pages: {ex.Message}",
                        TextColor = Colors.OrangeRed
                    });
                });
            }
        });
#else
        await Task.CompletedTask;
        PdfPagesStack.Children.Add(new Label
        {
            Text = $"📄 {Path.GetFileName(fullPath)}",
            FontSize = 15,
            FontAttributes = FontAttributes.Bold,
            TextColor = Colors.White
        });
#endif
    }

    private void UpdatePdfDarkModeButtonVisual()
    {
        if (_isPdfDarkMode)
        {
            BtnPdfDarkModeToggle.Text = "☀️ Light";
            BtnPdfDarkModeToggle.TextColor = Color.FromArgb("#FACC15");
            BtnPdfDarkModeToggle.BackgroundColor = Color.FromArgb("#262248");
        }
        else
        {
            BtnPdfDarkModeToggle.Text = "🌙 Dark";
            BtnPdfDarkModeToggle.TextColor = Color.FromArgb("#F8FAFC");
            BtnPdfDarkModeToggle.BackgroundColor = Color.FromArgb("#181D38");
        }
    }

    private async void OnTogglePdfDarkModeClicked(object? sender, EventArgs e)
    {
        _isPdfDarkMode = !_isPdfDarkMode;
        _state.Settings.PdfDarkMode = _isPdfDarkMode;
        UpdatePdfDarkModeButtonVisual();
        await _storage.SaveStateAsync(_state);

        if (!string.IsNullOrWhiteSpace(_currentPdfFilePath) && File.Exists(_currentPdfFilePath))
        {
            await RenderPdfDocumentPagesAsync(_currentPdfFilePath);
        }
    }

    private async void OnOpenPdfExternalClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentPdfFilePath) || !File.Exists(_currentPdfFilePath)) return;
        try
        {
            await Launcher.Default.OpenAsync(new OpenFileRequest
            {
                Title = Path.GetFileName(_currentPdfFilePath),
                File = new ReadOnlyFile(_currentPdfFilePath)
            });
        }
        catch (Exception ex)
        {
            await DisplayAlert("External Viewer", ex.Message, "OK");
        }
    }

    private void OnClosePdfReaderClicked(object? sender, EventArgs e)
    {
        PdfReaderOverlay.IsVisible = false;
        PdfPagesStack.Children.Clear();
    }

    // ---- Tab 2: Notes ----

    private void LoadSubjectNotesTab()
    {
        if (_currentSubject == null) return;
        var note = _state.Notes.FirstOrDefault(n => n.SubjectId == _currentSubject.Id);
        EditorSubjectNotes.Text = note?.Text ?? "";
        UpdateNotesWordCount(EditorSubjectNotes.Text);
    }

    private void OnSubjectNotesTextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateNotesWordCount(e.NewTextValue ?? "");
    }

    private void UpdateNotesWordCount(string text)
    {
        int chars = text.Length;
        int words = string.IsNullOrWhiteSpace(text) ? 0 : text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
        LblNotesWordCount.Text = $"{words} words • {chars} characters";
    }

    private async void OnSaveSubjectNotesClicked(object? sender, EventArgs e)
    {
        if (_currentSubject == null) return;
        var note = _state.Notes.FirstOrDefault(n => n.SubjectId == _currentSubject.Id);
        if (note == null)
        {
            note = new NoteItem
            {
                Id = "note_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                SubjectId = _currentSubject.Id
            };
            _state.Notes.Add(note);
        }
        note.Text = EditorSubjectNotes.Text ?? "";
        note.Date = DateTime.Now.ToString("MMM dd, yyyy HH:mm");
        RecordStudyActivity();
        await _storage.SaveStateAsync(_state);
        ShowToast("Study notes saved ✅");
    }

    // ---- Tab 3: Tasks ----

    private void RenderSubjectTasksTab()
    {
        if (_currentSubject == null) return;
        TasksListContainer.Children.Clear();

        var tasks = _state.Tasks.Where(t => t.SubjectId == _currentSubject.Id).ToList();
        foreach (var task in tasks)
        {
            var row = new Border
            {
                BackgroundColor = Color.FromArgb("#141933"),
                Stroke = Color.FromArgb("#252F58"),
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 10 },
                Padding = new Thickness(10, 6)
            };
            var g = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = GridLength.Star },
                    new ColumnDefinition { Width = GridLength.Auto }
                },
                ColumnSpacing = 8
            };
            var cb = new CheckBox
            {
                IsChecked = task.Completed,
                Color = Color.FromArgb("#10B981")
            };
            cb.CheckedChanged += async (_, args) =>
            {
                task.Completed = args.Value;
                UpdateDashboardStats();
                await _storage.SaveStateAsync(_state);
            };
            g.Add(cb, 0, 0);

            g.Add(new Label
            {
                Text = task.Text,
                FontSize = 13,
                TextColor = task.Completed ? Color.FromArgb("#64748B") : Color.FromArgb("#F8FAFC"),
                TextDecorations = task.Completed ? TextDecorations.Strikethrough : TextDecorations.None,
                VerticalOptions = LayoutOptions.Center
            }, 1, 0);

            var delBtn = new Button
            {
                Text = "✕",
                FontSize = 11,
                WidthRequest = 28,
                HeightRequest = 28,
                Padding = new Thickness(0),
                BackgroundColor = Color.FromArgb("#3B182A"),
                TextColor = Color.FromArgb("#F43F5E"),
                CornerRadius = 7
            };
            delBtn.Clicked += async (_, _) =>
            {
                _state.Tasks.Remove(task);
                RenderSubjectTasksTab();
                UpdateSubjectHeaderInfo();
                UpdateDashboardStats();
                await _storage.SaveStateAsync(_state);
            };
            g.Add(delBtn, 2, 0);
            row.Content = g;
            TasksListContainer.Children.Add(row);
        }
    }

    private async void OnAddTaskClicked(object? sender, EventArgs e)
    {
        if (_currentSubject == null || string.IsNullOrWhiteSpace(EntryNewTask.Text)) return;
        _state.Tasks.Add(new TaskItem
        {
            Id = "task_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SubjectId = _currentSubject.Id,
            Text = EntryNewTask.Text.Trim(),
            Completed = false
        });
        EntryNewTask.Text = "";
        RecordStudyActivity();
        RenderSubjectTasksTab();
        UpdateSubjectHeaderInfo();
        UpdateDashboardStats();
        await _storage.SaveStateAsync(_state);
    }

    // ---- Tab 4: Flashcards ----

    private void RenderSubjectFlashcardsTab()
    {
        if (_currentSubject == null) return;
        var deck = _state.Flashcards.Where(f => f.SubjectId == _currentSubject.Id).ToList();
        if (deck.Count == 0)
        {
            LblFlashcardCounter.Text = "🃏 Flashcards Deck (0 / 0)";
            LblFlashcardSideBadge.Text = "EMPTY DECK";
            LblFlashcardContent.Text = "No flashcards yet. Tap '+ New Card' to create one!";
            return;
        }

        _currentFlashcardIndex = Math.Clamp(_currentFlashcardIndex, 0, deck.Count - 1);
        var card = deck[_currentFlashcardIndex];
        LblFlashcardCounter.Text = $"🃏 Flashcards Deck ({_currentFlashcardIndex + 1} / {deck.Count})";
        LblFlashcardSideBadge.Text = _isFlashcardFlipped ? "ANSWER" : "QUESTION";
        LblFlashcardSideBadge.TextColor = _isFlashcardFlipped ? Color.FromArgb("#10B981") : Color.FromArgb("#22D3EE");
        LblFlashcardContent.Text = _isFlashcardFlipped ? card.Answer : card.Question;
    }

    private async void OnAddFlashcardClicked(object? sender, EventArgs e)
    {
        if (_currentSubject == null) return;
        string? q = await DisplayPromptAsync("New Flashcard", "Enter Question (Front side):");
        if (string.IsNullOrWhiteSpace(q)) return;
        string? a = await DisplayPromptAsync("New Flashcard", "Enter Answer (Back side):");
        if (string.IsNullOrWhiteSpace(a)) return;

        _state.Flashcards.Add(new FlashcardItem
        {
            Id = "fc_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SubjectId = _currentSubject.Id,
            Question = q.Trim(),
            Answer = a.Trim()
        });
        _isFlashcardFlipped = false;
        RenderSubjectFlashcardsTab();
        UpdateSubjectHeaderInfo();
        await _storage.SaveStateAsync(_state);
    }

    private void OnFlipFlashcardTapped(object? sender, EventArgs e) => FlipCurrentFlashcard();
    private void OnFlipFlashcardClicked(object? sender, EventArgs e) => FlipCurrentFlashcard();

    private void FlipCurrentFlashcard()
    {
        _isFlashcardFlipped = !_isFlashcardFlipped;
        RenderSubjectFlashcardsTab();
    }

    private void OnPrevFlashcardClicked(object? sender, EventArgs e)
    {
        if (_currentSubject == null) return;
        int count = _state.Flashcards.Count(f => f.SubjectId == _currentSubject.Id);
        if (count == 0) return;
        _currentFlashcardIndex = (_currentFlashcardIndex - 1 + count) % count;
        _isFlashcardFlipped = false;
        RenderSubjectFlashcardsTab();
    }

    private void OnNextFlashcardClicked(object? sender, EventArgs e)
    {
        if (_currentSubject == null) return;
        int count = _state.Flashcards.Count(f => f.SubjectId == _currentSubject.Id);
        if (count == 0) return;
        _currentFlashcardIndex = (_currentFlashcardIndex + 1) % count;
        _isFlashcardFlipped = false;
        RenderSubjectFlashcardsTab();
    }

    private async void OnDeleteCurrentFlashcardClicked(object? sender, EventArgs e)
    {
        if (_currentSubject == null) return;
        var deck = _state.Flashcards.Where(f => f.SubjectId == _currentSubject.Id).ToList();
        if (deck.Count == 0) return;
        var card = deck[Math.Clamp(_currentFlashcardIndex, 0, deck.Count - 1)];
        _state.Flashcards.Remove(card);
        _isFlashcardFlipped = false;
        RenderSubjectFlashcardsTab();
        UpdateSubjectHeaderInfo();
        await _storage.SaveStateAsync(_state);
    }

    // ---- Tab 5: Interactive Touch Whiteboard ----

    private void OnWhiteboardStartInteraction(object? sender, TouchEventArgs e)
    {
        var pt = e.Touches.FirstOrDefault();
        _activeWbStroke = new WhiteboardStroke
        {
            Color = _currentWbStrokeColor,
            StrokeWidth = 3.5f
        };
        _activeWbStroke.Points.Add(pt);
        _whiteboard.Strokes.Add(_activeWbStroke);
        WhiteboardCanvas.Invalidate();
    }

    private void OnWhiteboardDragInteraction(object? sender, TouchEventArgs e)
    {
        if (_activeWbStroke == null) return;
        var pt = e.Touches.FirstOrDefault();
        _activeWbStroke.Points.Add(pt);
        WhiteboardCanvas.Invalidate();
    }

    private void OnWhiteboardEndInteraction(object? sender, TouchEventArgs e)
    {
        _activeWbStroke = null;
        WhiteboardCanvas.Invalidate();
    }

    private void OnWbColorVioletClicked(object? sender, EventArgs e) => _currentWbStrokeColor = Color.FromArgb("#7C5CFF");
    private void OnWbColorCyanClicked(object? sender, EventArgs e) => _currentWbStrokeColor = Color.FromArgb("#22D3EE");
    private void OnWbColorEmeraldClicked(object? sender, EventArgs e) => _currentWbStrokeColor = Color.FromArgb("#10B981");
    private void OnWbColorAmberClicked(object? sender, EventArgs e) => _currentWbStrokeColor = Color.FromArgb("#F59E0B");
    private void OnWbColorWhiteClicked(object? sender, EventArgs e) => _currentWbStrokeColor = Color.FromArgb("#F8FAFC");

    private void OnWbUndoClicked(object? sender, EventArgs e)
    {
        if (_whiteboard.Strokes.Count > 0)
        {
            _whiteboard.Strokes.RemoveAt(_whiteboard.Strokes.Count - 1);
            WhiteboardCanvas.Invalidate();
        }
    }

    private void OnWbClearClicked(object? sender, EventArgs e)
    {
        _whiteboard.Strokes.Clear();
        WhiteboardCanvas.Invalidate();
    }

    // ---- Tab 6: Deadlines ----

    private void RenderSubjectDeadlinesTab()
    {
        if (_currentSubject == null) return;
        DeadlinesListContainer.Children.Clear();

        var list = _state.Deadlines.Where(d => d.SubjectId == _currentSubject.Id).ToList();
        foreach (var d in list)
        {
            var row = new Border
            {
                BackgroundColor = Color.FromArgb("#141933"),
                Stroke = Color.FromArgb("#252F58"),
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 10 },
                Padding = new Thickness(12, 8)
            };
            var g = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Star },
                    new ColumnDefinition { Width = GridLength.Auto }
                }
            };
            g.Add(new Label
            {
                Text = $"📅 {d.Title}  •  {d.Date} ({d.Type})",
                FontSize = 13,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#F8FAFC"),
                VerticalOptions = LayoutOptions.Center
            }, 0, 0);

            var delBtn = new Button
            {
                Text = "🗑️",
                FontSize = 11,
                BackgroundColor = Color.FromArgb("#3B182A"),
                TextColor = Color.FromArgb("#F43F5E"),
                CornerRadius = 8,
                Padding = new Thickness(8, 4)
            };
            delBtn.Clicked += async (_, _) =>
            {
                _state.Deadlines.Remove(d);
                RenderSubjectDeadlinesTab();
                await _storage.SaveStateAsync(_state);
            };
            g.Add(delBtn, 1, 0);
            row.Content = g;
            DeadlinesListContainer.Children.Add(row);
        }
    }

    private async void OnAddDeadlineClicked(object? sender, EventArgs e)
    {
        if (_currentSubject == null) return;
        string? title = await DisplayPromptAsync("Add Deadline", "Exam or assignment title:");
        if (string.IsNullOrWhiteSpace(title)) return;
        string? date = await DisplayPromptAsync("Deadline Date", "Date (YYYY-MM-DD):", initialValue: DateTime.Today.AddDays(7).ToString("yyyy-MM-dd"));
        if (string.IsNullOrWhiteSpace(date)) return;

        _state.Deadlines.Add(new DeadlineItem
        {
            Id = "dl_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SubjectId = _currentSubject.Id,
            Title = title.Trim(),
            Date = date.Trim(),
            Type = "Exam"
        });
        RenderSubjectDeadlinesTab();
        await _storage.SaveStateAsync(_state);
    }

    // ---- Tab 7: Links ----

    private void RenderSubjectLinksTab()
    {
        if (_currentSubject == null) return;
        LinksListContainer.Children.Clear();

        var links = _state.Links.Where(l => l.SubjectId == _currentSubject.Id).ToList();
        foreach (var l in links)
        {
            var row = new Border
            {
                BackgroundColor = Color.FromArgb("#141933"),
                Stroke = Color.FromArgb("#252F58"),
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 10 },
                Padding = new Thickness(12, 8)
            };
            var g = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Star },
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = GridLength.Auto }
                },
                ColumnSpacing = 8
            };
            g.Add(new Label
            {
                Text = $"🔗 {l.Title}",
                FontSize = 13,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#38BDF8"),
                VerticalOptions = LayoutOptions.Center
            }, 0, 0);

            var openBtn = new Button
            {
                Text = "↗ Open",
                FontSize = 11,
                BackgroundColor = Color.FromArgb("#7C5CFF"),
                TextColor = Colors.White,
                CornerRadius = 8,
                Padding = new Thickness(10, 4)
            };
            openBtn.Clicked += async (_, _) =>
            {
                try { await Launcher.Default.OpenAsync(l.Url); } catch { }
            };
            g.Add(openBtn, 1, 0);

            var delBtn = new Button
            {
                Text = "🗑️",
                FontSize = 11,
                BackgroundColor = Color.FromArgb("#3B182A"),
                TextColor = Color.FromArgb("#F43F5E"),
                CornerRadius = 8,
                Padding = new Thickness(8, 4)
            };
            delBtn.Clicked += async (_, _) =>
            {
                _state.Links.Remove(l);
                RenderSubjectLinksTab();
                await _storage.SaveStateAsync(_state);
            };
            g.Add(delBtn, 2, 0);
            row.Content = g;
            LinksListContainer.Children.Add(row);
        }
    }

    private async void OnAddLinkClicked(object? sender, EventArgs e)
    {
        if (_currentSubject == null) return;
        string? title = await DisplayPromptAsync("Add Resource Link", "Resource title:");
        if (string.IsNullOrWhiteSpace(title)) return;
        string? url = await DisplayPromptAsync("Add Resource Link", "URL (https://...):", initialValue: "https://");
        if (string.IsNullOrWhiteSpace(url)) return;

        _state.Links.Add(new LinkItem
        {
            Id = "lnk_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SubjectId = _currentSubject.Id,
            Title = title.Trim(),
            Url = url.Trim()
        });
        RenderSubjectLinksTab();
        await _storage.SaveStateAsync(_state);
    }

    // ================= FOCUS TIMER (POMODORO) =================

    private void TickPomodoroSecond()
    {
        _sessionSecondsAccumulator++;
        if (_sessionSecondsAccumulator >= 60)
        {
            _sessionSecondsAccumulator = 0;
            _sessionActiveMinutes++;
            UpdateDashboardStats();
        }

        if (!_isPomoRunning) return;

        if (_pomoRemainingSeconds > 0)
        {
            _pomoRemainingSeconds--;
            UpdateTimerUi();
        }
        else
        {
            _isPomoRunning = false;
            _audio.PlayNotificationChime();
            if (_isPomoWorkPhase)
            {
                _state.PomodorosCompleted++;
                RecordStudyActivity();
                _ = _storage.SaveStateAsync(_state);
                ShowToast("🎉 Focus session complete! Time for a break.");
                SwitchPomoPhase(false);
            }
            else
            {
                ShowToast("⚡ Break finished! Ready for next focus session.");
                SwitchPomoPhase(true);
            }
        }
    }

    private void UpdateTimerUi()
    {
        int mins = _pomoRemainingSeconds / 60;
        int secs = _pomoRemainingSeconds % 60;
        string formatted = $"{mins:00}:{secs:00}";

        LblFloatingTimerDigits.Text = formatted;
        LblMinimizedTimerDigits.Text = formatted;
        LblNavTimerDisplay.Text = formatted;
        LblNavTimerDisplayWide.Text = formatted;
        LblZenTimerSub.Text = $"⏱ Focus Timer • {formatted}";

        double ratio = _pomoTotalSeconds > 0 ? Math.Clamp((double)_pomoRemainingSeconds / _pomoTotalSeconds, 0.0, 1.0) : 0.0;
        MinimizedTimerGlowBar.WidthRequest = Math.Max(4.0, 198.0 * ratio);
        Color dotCol = _isPomoRunning ? Color.FromArgb("#10B981") : Color.FromArgb("#F59E0B");
        NavTimerDot.Color = dotCol;
        NavTimerDotWide.Color = dotCol;
    }

    private void OnPomoStartClicked(object? sender, EventArgs e)
    {
        _isPomoRunning = true;
        UpdateTimerUi();
    }

    private void OnPomoPauseTapped(object? sender, EventArgs e)
    {
        _isPomoRunning = false;
        UpdateTimerUi();
    }

    private void OnPomoResetClicked(object? sender, EventArgs e)
    {
        _isPomoRunning = false;
        _pomoRemainingSeconds = _pomoTotalSeconds;
        UpdateTimerUi();
    }

    private void OnPomoWorkModeClicked(object? sender, EventArgs e) => SwitchPomoPhase(true);
    private void OnPomoBreakModeClicked(object? sender, EventArgs e) => SwitchPomoPhase(false);

    private void SwitchPomoPhase(bool workPhase)
    {
        _isPomoRunning = false;
        _isPomoWorkPhase = workPhase;
        int mins = workPhase ? Math.Clamp(_state.Settings.PomoWork, 1, 180) : Math.Clamp(_state.Settings.PomoBreak, 1, 90);
        _pomoTotalSeconds = mins * 60;
        _pomoRemainingSeconds = _pomoTotalSeconds;

        BtnPomoWorkMode.BackgroundColor = workPhase ? Color.FromArgb("#7C5CFF") : Color.FromArgb("#1E2548");
        BtnPomoBreakMode.BackgroundColor = !workPhase ? Color.FromArgb("#10B981") : Color.FromArgb("#151A36");
        UpdateTimerUi();
    }

    private void OnToggleTimerSettingsClicked(object? sender, EventArgs e)
    {
        BdPomoSettingsPanel.IsVisible = !BdPomoSettingsPanel.IsVisible;
    }

    private void OnPomoDurationChanged(object? sender, EventArgs e)
    {
        if (int.TryParse(EntryPomoWorkMin.Text, out int w) && w > 0)
            _state.Settings.PomoWork = Math.Clamp(w, 1, 180);
        if (int.TryParse(EntryPomoBreakMin.Text, out int b) && b > 0)
            _state.Settings.PomoBreak = Math.Clamp(b, 1, 90);

        BtnPomoWorkMode.Text = $"{_state.Settings.PomoWork}m Work";
        BtnPomoBreakMode.Text = $"{_state.Settings.PomoBreak}m Break";
        SwitchPomoPhase(_isPomoWorkPhase);
        _ = _storage.SaveStateAsync(_state);
    }

    private void OnMinimizeTimerClicked(object? sender, EventArgs e)
    {
        BdFloatingTimerExpanded.IsVisible = false;
        BdFloatingTimerMinimized.IsVisible = true;
    }

    private void OnExpandTimerClicked(object? sender, EventArgs e)
    {
        BdFloatingTimerMinimized.IsVisible = false;
        BdFloatingTimerExpanded.IsVisible = true;
    }

    private void OnNavTimerPillTapped(object? sender, EventArgs e)
    {
        FloatingTimerHost.IsVisible = true;
        OnExpandTimerClicked(sender, e);
    }

    private void OnDockBottomLeftClicked(object? sender, EventArgs e) => ApplyTimerDockPosition("bottom-left");
    private void OnDockTopBarClicked(object? sender, EventArgs e) => ApplyTimerDockPosition("top-bar");
    private void OnDockBottomRightClicked(object? sender, EventArgs e) => ApplyTimerDockPosition("bottom-right");

    private void ApplyTimerDockPosition(string dock)
    {
        _state.TimerDock = dock;
        if (dock == "bottom-left")
        {
            FloatingTimerHost.IsVisible = true;
            FloatingTimerHost.HorizontalOptions = LayoutOptions.Start;
            FloatingTimerHost.VerticalOptions = LayoutOptions.End;
        }
        else if (dock == "top-bar")
        {
            FloatingTimerHost.IsVisible = false;
        }
        else
        {
            FloatingTimerHost.IsVisible = true;
            FloatingTimerHost.HorizontalOptions = LayoutOptions.End;
            FloatingTimerHost.VerticalOptions = LayoutOptions.End;
        }
        _ = _storage.SaveStateAsync(_state);
    }

    // ================= AMBIENT SOUND MIXER =================

    private void BuildSoundMixerChannelsUi()
    {
        MixerChannelsContainer.Children.Clear();

        foreach (var ch in _audio.Channels)
        {
            var card = new Border
            {
                BackgroundColor = Color.FromArgb("#161B36"),
                Stroke = Color.FromArgb("#28315A"),
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 14 },
                Padding = new Thickness(12, 10)
            };

            var stack = new VerticalStackLayout { Spacing = 6 };
            var topGrid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Star },
                    new ColumnDefinition { Width = GridLength.Auto }
                }
            };

            topGrid.Add(new Label
            {
                Text = $"{ch.Icon}  {ch.Name}",
                FontSize = 12.5,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#F8FAFC")
            }, 0, 0);

            var pctLabel = new Label
            {
                Text = $"{(int)ch.Volume}%",
                FontSize = 11,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#A78BFA")
            };
            topGrid.Add(pctLabel, 1, 0);
            stack.Children.Add(topGrid);

            stack.Children.Add(new Label
            {
                Text = ch.Description,
                FontSize = 10,
                TextColor = Color.FromArgb("#8A95B7")
            });

            var slider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = ch.Volume,
                MinimumTrackColor = Color.FromArgb("#8B5CF6"),
                MaximumTrackColor = Color.FromArgb("#28315A"),
                ThumbColor = Color.FromArgb("#C4B5FD")
            };
            string chId = ch.Id;
            slider.ValueChanged += (_, args) =>
            {
                int v = (int)Math.Round(args.NewValue);
                pctLabel.Text = $"{v}%";
                _audio.SetChannelVolume(chId, v);
                _state.Settings.MixerLevels[chId] = v;
                UpdateSoundMixerBadge();
            };
            stack.Children.Add(slider);

            card.Content = stack;
            MixerChannelsContainer.Children.Add(card);
        }

        UpdateSoundMixerBadge();
    }

    private void UpdateSoundMixerBadge()
    {
        int active = _audio.GetActiveChannelsCount();
        LblMixerActiveCount.Text = $"{active} active";
        NavMixerActiveDot.IsVisible = _audio.IsActivePlaying();
        BtnMixerPlayPause.Text = _audio.IsActivePlaying() ? "⏸" : "▶";
        BtnMixerMute.Text = _audio.IsMasterMuted ? "🔇" : "🔊";
    }

    private void OnToggleSoundMixerClicked(object? sender, EventArgs e)
    {
        SoundMixerOverlay.IsVisible = !SoundMixerOverlay.IsVisible;
    }

    private void OnMixerMasterPlayPauseClicked(object? sender, EventArgs e)
    {
        _audio.ToggleMasterPlayPause();
        BuildSoundMixerChannelsUi();
    }

    private void OnMixerStopAllClicked(object? sender, EventArgs e)
    {
        _audio.StopAndResetAll();
        BuildSoundMixerChannelsUi();
    }

    private void OnMixerMasterMuteClicked(object? sender, EventArgs e)
    {
        _audio.ToggleMasterMute();
        _state.Settings.MixerMuted = _audio.IsMasterMuted;
        UpdateSoundMixerBadge();
    }

    private void OnMasterVolumeChanged(object? sender, ValueChangedEventArgs e)
    {
        int v = (int)Math.Round(e.NewValue);
        LblMasterVolPct.Text = $"{v}%";
        _audio.SetMasterVolume(v);
        _state.Settings.MixerMaster = v;
    }

    private void OnPresetRainyClicked(object? sender, EventArgs e) { _audio.ApplyPreset("rainy_zen"); BuildSoundMixerChannelsUi(); }
    private void OnPresetCozyClicked(object? sender, EventArgs e) { _audio.ApplyPreset("cozy_hearth"); BuildSoundMixerChannelsUi(); }
    private void OnPresetSanctuaryClicked(object? sender, EventArgs e) { _audio.ApplyPreset("sanctuary"); BuildSoundMixerChannelsUi(); }
    private void OnPresetBrownClicked(object? sender, EventArgs e) { _audio.ApplyPreset("deep_brown"); BuildSoundMixerChannelsUi(); }

    private async void OnImportCustomSoundClicked(object? sender, EventArgs e)
    {
        try
        {
            var picked = await FilePicker.Default.PickAsync();
            if (picked == null) return;
            string localPath = await CopyPickedFileToLocalTempAsync(picked);
            var cs = _storage.ImportCustomSound(localPath);
            _state.CustomSounds.Add(cs);
            _audio.SyncCustomSounds(_state.CustomSounds, _state.Settings.MixerLevels, _state.Settings.MixerMaster);
            BuildSoundMixerChannelsUi();
            await _storage.SaveStateAsync(_state);
            ShowToast($"Imported sound '{cs.Name}' 🎵");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Audio Import", ex.Message, "OK");
        }
    }

    // ================= ZEN MODE (17 ANIMATED WORLDS) =================

    private void OnToggleZenModeClicked(object? sender, EventArgs e)
    {
        bool enterZen = !ZenModeOverlay.IsVisible;
        ZenModeOverlay.IsVisible = enterZen;
        MainShellGrid.IsVisible = !enterZen;
        _backdrop.IsZenMode = enterZen;

        if (enterZen)
        {
            LeftDrawerOverlay.IsVisible = false;
            SettingsModalOverlay.IsVisible = false;
            UpdateZenSceneUi();
            UpdateZenTimerToggleVisual();
        }
        else
        {
            ZenScenePickerModal.IsVisible = false;
            ApplyTimerDockPosition(_state.TimerDock ?? "bottom-right");
        }

        BackdropCanvas.Invalidate();
    }

    private void OnOpenZenSceneModalClicked(object? sender, EventArgs e)
    {
        BuildZenSceneModalList();
        ZenScenePickerModal.IsVisible = true;
    }

    private void OnCloseZenSceneModalClicked(object? sender, EventArgs e)
    {
        ZenScenePickerModal.IsVisible = false;
    }

    private void SelectZenScene(string sceneKey)
    {
        string normalized = NormalizeZenSceneKey(sceneKey);
        _backdrop.ZenScene = normalized;
        _state.Settings.ZenScene = normalized;

        if (!ZenModeOverlay.IsVisible)
        {
            ZenModeOverlay.IsVisible = true;
            MainShellGrid.IsVisible = false;
            _backdrop.IsZenMode = true;
            LeftDrawerOverlay.IsVisible = false;
            SettingsModalOverlay.IsVisible = false;
            UpdateZenTimerToggleVisual();
        }

        UpdateZenSceneUi();
        BuildZenSceneModalList();
        ZenScenePickerModal.IsVisible = false;
        BackdropCanvas.Invalidate();
        _ = _storage.SaveStateAsync(_state);
    }

    private void OnToggleZenTimerClicked(object? sender, EventArgs e)
    {
        FloatingTimerHost.IsVisible = !FloatingTimerHost.IsVisible;
        UpdateZenTimerToggleVisual();
    }

    private void UpdateZenTimerToggleVisual()
    {
        bool shown = FloatingTimerHost.IsVisible;
        BtnZenTimerToggle.BackgroundColor = shown ? Color.FromArgb("#1D3354") : Color.FromArgb("#D811152C");
        BtnZenTimerToggle.BorderColor = shown ? Color.FromArgb("#38BDF8") : Color.FromArgb("#334175");
    }

    // ================= NAVIGATION DRAWER, SEARCH, THEMES & SETTINGS =================

    private void OnToggleLeftDrawerClicked(object? sender, EventArgs e)
    {
        LeftDrawerOverlay.IsVisible = !LeftDrawerOverlay.IsVisible;
    }

    private void OnDrawerHomeClicked(object? sender, EventArgs e)
    {
        LeftDrawerOverlay.IsVisible = false;
        OnBackToHomeClicked(sender, e);
    }

    private void OnGlobalSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        _currentSearchQuery = e.NewTextValue ?? "";
        RenderSubjectsGrid(_currentSearchQuery);
    }

    private void OnGlobalSearchCompleted(object? sender, EventArgs e)
    {
        if (sender is Entry entry)
        {
            _currentSearchQuery = entry.Text ?? "";
            RenderSubjectsGrid(_currentSearchQuery);
        }
    }

    private void OnToggleThemeClicked(object? sender, EventArgs e)
    {
        _backdrop.IsDarkMode = !_backdrop.IsDarkMode;
        _state.Theme = _backdrop.IsDarkMode ? "dark" : "light";
        ApplySurfaceThemeColors();
        BackdropCanvas.Invalidate();
        _ = _storage.SaveStateAsync(_state);
    }

    private void OnOpenSettingsClicked(object? sender, EventArgs e) => SettingsModalOverlay.IsVisible = true;
    private void OnCloseSettingsClicked(object? sender, EventArgs e) => SettingsModalOverlay.IsVisible = false;

    private void SetSurfaceTheme(string themeKey, string label)
    {
        _backdrop.IsDarkMode = true;
        _state.Theme = "dark";
        _backdrop.BgTheme = themeKey;
        _state.Settings.BgTheme = themeKey;
        ApplySurfaceThemeColors();
        BackdropCanvas.Invalidate();
        _ = _storage.SaveStateAsync(_state);
        ShowToast($"Theme: {label}");
    }

    private void OnThemeAuroraClicked(object? sender, EventArgs e) => SetSurfaceTheme("aurora", "Midnight Cyber");
    private void OnThemeNebulaClicked(object? sender, EventArgs e) => SetSurfaceTheme("nebula", "Deep Nebula");
    private void OnThemeAcrylicClicked(object? sender, EventArgs e) => SetSurfaceTheme("acrylic", "Frosted Acrylic");
    private void OnThemeObsidianClicked(object? sender, EventArgs e) => SetSurfaceTheme("obsidian", "Obsidian OLED");

    private void SetParticleEffect(string effectKey)
    {
        _backdrop.ParticleEffect = effectKey;
        _state.Settings.ParticleEffect = effectKey;
        BackdropCanvas.Invalidate();
        _ = _storage.SaveStateAsync(_state);
        ShowToast($"Particles: {effectKey.Replace('_', ' ')}");
    }

    private void OnParticleNetworkClicked(object? sender, EventArgs e) => SetParticleEffect("network");
    private void OnParticleStarsClicked(object? sender, EventArgs e) => SetParticleEffect("fading_stars");
    private void OnParticleSnowClicked(object? sender, EventArgs e) => SetParticleEffect("snow");
    private void OnParticleMatrixClicked(object? sender, EventArgs e) => SetParticleEffect("matrix");
    private void OnParticleFirefliesClicked(object? sender, EventArgs e) => SetParticleEffect("fireflies");
    private void OnParticleSakuraClicked(object? sender, EventArgs e) => SetParticleEffect("sakura");
    private void OnParticleEmbersClicked(object? sender, EventArgs e) => SetParticleEffect("embers");
    private void OnParticleBubblesClicked(object? sender, EventArgs e) => SetParticleEffect("bubbles");
    private void OnParticleNoneClicked(object? sender, EventArgs e) => SetParticleEffect("none");

    private void OnExportBackupClicked(object? sender, EventArgs e)
    {
        try
        {
            string outPath = _storage.ExportBackupFile(_state);
            ShowToast($"Backup saved: {Path.GetFileName(outPath)}");
        }
        catch (Exception ex)
        {
            DisplayAlert("Backup Error", ex.Message, "OK");
        }
    }

    private async void OnImportBackupClicked(object? sender, EventArgs e)
    {
        try
        {
            var picked = await FilePicker.Default.PickAsync();
            if (picked == null) return;
            string localPath = await CopyPickedFileToLocalTempAsync(picked);
            var imported = _storage.ImportBackupFile(localPath);
            if (imported != null)
            {
                _state = imported;
                await _storage.SaveStateAsync(_state);
                ApplySavedSettingsToUi();
                RenderSubjectsGrid(_currentSearchQuery);
                RenderArchivedDrawer();
                UpdateDashboardStats();
                ShowToast("Workspace backup restored ✅");
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Restore Error", ex.Message, "OK");
        }
    }

    private async void OnCheckGithubUpdateClicked(object? sender, EventArgs e)
    {
        _state.Settings.GithubRepoUrl = EntryGithubRepoUrl.Text?.Trim() ?? "";
        await _storage.SaveStateAsync(_state);
        var res = await _updater.CheckForUpdatesAsync(_state.Settings.GithubRepoUrl, _state.Settings.InstalledCommitSha);
        if (!res.Success)
        {
            await DisplayAlert("GitHub Updater", res.ErrorMessage, "OK");
            return;
        }
        string msg = res.IsUpdateAvailable
            ? $"New update available: {res.LatestVersion}\n{res.ReleaseTitle}"
            : $"You are up to date (v{res.CurrentVersion}).";
        await DisplayAlert("GitHub Updater", msg, "OK");
    }

    // ================= HELPERS =================

    private void RecordStudyActivity()
    {
        string key = DateTime.Today.ToString("yyyy-MM-dd");
        _state.Activity[key] = (_state.Activity.TryGetValue(key, out int c) ? c : 0) + 1;
        RenderHeatmap();
    }

    private void ShowToast(string message)
    {
        LblToastText.Text = message;
        ToastBanner.IsVisible = true;
        var timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2.6);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => ToastBanner.IsVisible = false;
        timer.Start();
    }

    private static Color ResolveSubjectAccentColor(string? colorSpec)
    {
        if (string.IsNullOrWhiteSpace(colorSpec)) return Color.FromArgb("#7C5CFF");
        if (colorSpec.StartsWith("#"))
        {
            try { return Color.FromArgb(colorSpec); } catch { }
        }
        if (colorSpec.Contains("cyan") || colorSpec.Contains("sky") || colorSpec.Contains("blue"))
            return Color.FromArgb("#22D3EE");
        if (colorSpec.Contains("emerald") || colorSpec.Contains("green") || colorSpec.Contains("teal"))
            return Color.FromArgb("#10B981");
        if (colorSpec.Contains("amber") || colorSpec.Contains("orange") || colorSpec.Contains("yellow"))
            return Color.FromArgb("#F59E0B");
        if (colorSpec.Contains("rose") || colorSpec.Contains("pink"))
            return Color.FromArgb("#EC4899");
        return Color.FromArgb("#8B5CF6");
    }

    private static string MapSubjectEmoji(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return "🎓";
        if (!icon.StartsWith("fa-", StringComparison.OrdinalIgnoreCase)) return icon;
        return icon.ToLowerInvariant() switch
        {
            var s when s.Contains("satellite") || s.Contains("tower") || s.Contains("wifi") => "📡",
            var s when s.Contains("plane") || s.Contains("jet") || s.Contains("rocket") => "✈️",
            var s when s.Contains("code") || s.Contains("laptop") || s.Contains("microchip") => "💻",
            var s when s.Contains("atom") || s.Contains("flask") => "🔬",
            var s when s.Contains("calculator") || s.Contains("square-root") => "📐",
            _ => "🎓"
        };
    }
}
