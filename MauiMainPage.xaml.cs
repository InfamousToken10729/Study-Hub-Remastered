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

    // Responsive Screen Dimensions (init negative so initial OnSizeAllocated always lays out cleanly)
    private double _pageWidth = -1;
    private double _pageHeight = -1;
    private string _currentSearchQuery = "";

    // Subject Editor Modal State
    private SubjectItem? _editingSubjectTarget;
    private string _editorSelectedIcon = "🎓";
    private string _editorSelectedColor = "#7C5CFF";

    // Calendar & Upcoming Events State
    private DateTime _calDisplayMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _calSelectedDate = DateTime.Today;
    private bool _calRule3Days = true;
    private bool _calRule24Hours = true;
    private bool _calRuleMorning = true;
    private bool _calRuleCustom = false;
    private int _calCustomUnitIdx = 0; // 0 = Hours, 1 = Minutes, 2 = Days
    private bool _homeUpcomingExpanded = false;
    private bool _isInitializingSettingsUi = false;
    private List<SubjectItem> _calSubjectChoices = new();
    private DateTime _lastNotifCheckUtc = DateTime.MinValue;

    // In-App GitHub Updater State
    private UpdateCheckResult? _lastUpdateCheckResult;
    private string _downloadedApkPath = "";
    private bool _isUpdateInstalling = false;

    private static readonly string[] SubjectEmojiPalette =
    {
        "🎓", "📚", "💻", "🔬", "📐", "✈️", "📡", "⚙️",
        "⚛️", "🎨", "📈", "🧠", "🧬", "🌐", "🩺", "🔌"
    };

    private static readonly string[] SubjectColorPalette =
    {
        "#7C5CFF", "#22D3EE", "#10B981", "#F59E0B",
        "#EC4899", "#3B82F6", "#A855F7", "#EF4444"
    };

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
        RenderHomeUpcomingEvents();
        RenderActiveDrawerCourses();
        RenderArchivedDrawer();
        BuildSoundMixerChannelsUi();
        UpdateTimerUi();

#if ANDROID
        StudyHub.Platforms.Android.AndroidCalendarNotifications.BackgroundKeysFired += OnAndroidBackgroundNotificationKeysFired;
#endif
        CheckDueCalendarNotificationsMobile();
        RescheduleAndroidCalendarAlarms();
        StartBackgroundLoops();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        RenderSubjectsGrid(_currentSearchQuery);
        RenderHomeUpcomingEvents();
        RenderActiveDrawerCourses();
        UpdateDashboardStats();
        CheckDueCalendarNotificationsMobile();
        RescheduleAndroidCalendarAlarms();
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width <= 0 || height <= 0) return;

        bool widthChangedSignificantly = _pageWidth <= 0 || Math.Abs(width - _pageWidth) > 4;
        _pageWidth = width;
        _pageHeight = height;

        ApplyResponsiveLayout(width, height);
        if (widthChangedSignificantly)
        {
            Dispatcher.Dispatch(() => RenderSubjectsGrid(_currentSearchQuery));
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

        // 1-Second Focus Timer & Study Activity Tracker + Periodic Calendar Notification Check
        var pomoTimer = Dispatcher.CreateTimer();
        pomoTimer.Interval = TimeSpan.FromSeconds(1);
        pomoTimer.Tick += (_, _) =>
        {
            TickPomodoroSecond();
            if ((DateTime.UtcNow - _lastNotifCheckUtc).TotalSeconds >= 25)
            {
                CheckDueCalendarNotificationsMobile();
            }
        };
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

        _state.Settings.GithubRepoUrl = UserSettings.NormalizeGithubRepoUrl(_state.Settings.GithubRepoUrl);
        EntryGithubRepoUrl.Text = _state.Settings.GithubRepoUrl;

        _state.Settings.CalendarNotifStartHour = CalendarNotificationEngine.NormalizeMorningTimeString(_state.Settings.CalendarNotifStartHour);
        _isInitializingSettingsUi = true;
        SwitchSettingsCalNotifs.IsToggled = _state.Settings.CalendarNotifsEnabled;
        EntrySettingsMorningTime.Text = _state.Settings.CalendarNotifStartHour;
        LblCalMorningBadge.Text = $"Morning reminders at {_state.Settings.CalendarNotifStartHour}";
        _isInitializingSettingsUi = false;

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
            BdHomeUpcomingEvents.BackgroundColor = Color.FromArgb("#F2FFFFFF");
            BdHomeUpcomingEvents.Stroke = Color.FromArgb("#CBD5E1");
            LblHomeUpcomingHeader.TextColor = Color.FromArgb("#0F172A");
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
            BdHomeUpcomingEvents.BackgroundColor = Color.FromArgb(cardBg);
            BdHomeUpcomingEvents.Stroke = Color.FromArgb(strokeCol);
            LblHomeUpcomingHeader.TextColor = Color.FromArgb("#F8FAFC");
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

        string todayKey = DateTime.Today.ToString("yyyy-MM-dd");
        int pomoToday = _state.PomodoroDays.TryGetValue(todayKey, out int pt) ? pt : 0;
        int actToday = _state.Activity.TryGetValue(todayKey, out int at) ? at : 0;
        int totalTasks = _state.Tasks.Count;
        int doneTasks = _state.Tasks.Count(t => t.Completed);

        int ratio;
        if (pomoToday > 0)
        {
            double taskBonus = totalTasks > 0 ? (doneTasks * 20.0 / totalTasks) : 0;
            ratio = (int)Math.Min(100, Math.Round((pomoToday / 4.0) * 100.0 + taskBonus));
        }
        else if (totalTasks > 0)
        {
            ratio = (int)Math.Min(100, Math.Round(doneTasks * 100.0 / totalTasks));
        }
        else
        {
            ratio = Math.Min(100, actToday * 25);
        }

        LblFocusRatio.Text = $"{ratio}%";
        LblFocusRatio.TextColor = ratio >= 100
            ? Color.FromArgb("#10B981")
            : ratio > 0
                ? Color.FromArgb("#38BDF8")
                : Color.FromArgb("#94A3C6");
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
        SubjectsRowsContainer.Children.Clear();
        bool isDark = _backdrop.IsDarkMode;

        double effectiveW = _pageWidth > 40 ? _pageWidth : 390;
        bool isPhonePortrait = effectiveW < 540;
        int colCount = isPhonePortrait ? 1 : (effectiveW < 880 ? 2 : 3);
        double rowCardHeight = isPhonePortrait ? 146 : 152;
        double addCardHeight = isPhonePortrait ? 80 : rowCardHeight;
        var cards = new List<View>();

        // 1. "+ Add Subject" Dashed Card (Compact Horizontal Banner on Phone Portrait, Full Card on Landscape/Tablet)
        var addCard = new Border
        {
            HorizontalOptions = LayoutOptions.Fill,
            HeightRequest = addCardHeight,
            MinimumHeightRequest = addCardHeight,
            BackgroundColor = isDark ? Color.FromArgb("#9911162B") : Color.FromArgb("#E6F8FAFC"),
            Stroke = isDark ? Color.FromArgb("#334170") : Color.FromArgb("#94A3B8"),
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            StrokeShape = new RoundRectangle { CornerRadius = 18 },
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
            InputTransparent = true,
            Content = new Label
            {
                Text = "+",
                FontSize = isPhonePortrait ? 18 : 20,
                FontAttributes = FontAttributes.Bold,
                TextColor = isDark ? Color.FromArgb("#94A3C6") : Color.FromArgb("#334155"),
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                InputTransparent = true
            }
        };

        var addTitleLbl = new Label
        {
            Text = "Add Subject",
            FontSize = 13.5,
            FontAttributes = FontAttributes.Bold,
            TextColor = isDark ? Color.FromArgb("#94A3C6") : Color.FromArgb("#475569"),
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            InputTransparent = true
        };

        if (isPhonePortrait)
        {
            var hStack = new HorizontalStackLayout
            {
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                Spacing = 12,
                InputTransparent = true
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
                Spacing = 10,
                InputTransparent = true
            };
            vStack.Children.Add(plusCircle);
            vStack.Children.Add(addTitleLbl);
            addCard.Content = vStack;
        }

        cards.Add(addCard);

        // 2. Active Course Cards
        var visibleSubjects = _state.Subjects
            .Where(s => !s.Archived && (string.IsNullOrWhiteSpace(searchFilter) || s.Name.Contains(searchFilter, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var sub in visibleSubjects)
        {
            Color accentColor = ResolveSubjectAccentColor(sub.Color);
            int fileCount = _state.Pdfs.Count(f => f.SubjectId == sub.Id);
            int eventCount = _state.Deadlines.Count(d => d.SubjectId == sub.Id && !d.Completed)
                           + _state.CalendarEvents.Count(c => c.SubjectId == sub.Id && !c.Completed);
            int subTasks = _state.Tasks.Count(t => t.SubjectId == sub.Id);
            int subDone = _state.Tasks.Count(t => t.SubjectId == sub.Id && t.Completed);
            int pct = subTasks > 0 ? (int)Math.Round(subDone * 100.0 / subTasks) : 0;

            var capturedSub = sub;
            var cardBorder = new Border
            {
                HorizontalOptions = LayoutOptions.Fill,
                HeightRequest = rowCardHeight,
                MinimumHeightRequest = rowCardHeight,
                BackgroundColor = isDark ? Color.FromArgb("#E6131830") : Colors.White,
                Stroke = isDark ? Color.FromArgb("#263056") : Color.FromArgb("#CBD5E1"),
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 18 },
                Padding = new Thickness(0)
            };

            var openTap = new TapGestureRecognizer();
            openTap.Tapped += (_, _) => OpenSubject(capturedSub);
            cardBorder.GestureRecognizers.Add(openTap);

            var cardGrid = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition { Height = new GridLength(4) },
                    new RowDefinition { Height = GridLength.Star }
                }
            };
            cardGrid.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(() => OpenSubject(capturedSub))
            });

            // Top Inline Accent Bar (Flush with rounded border top)
            cardGrid.Add(new BoxView
            {
                Color = accentColor,
                HeightRequest = 4,
                HorizontalOptions = LayoutOptions.Fill,
                InputTransparent = true
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
                InputTransparent = true,
                Content = new Label
                {
                    Text = MapSubjectEmoji(sub.Icon),
                    FontSize = 18,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    InputTransparent = true
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
                MaxLines = 2,
                InputTransparent = true
            }, 1, 0);

            var menuBtn = new Button
            {
                Text = "⋯",
                FontSize = 14,
                WidthRequest = 32,
                HeightRequest = 30,
                Padding = new Thickness(0),
                CornerRadius = 8,
                BackgroundColor = isDark ? Color.FromArgb("#1A2140") : Color.FromArgb("#F1F5F9"),
                TextColor = isDark ? Color.FromArgb("#94A3C6") : Color.FromArgb("#475569"),
                InputTransparent = false
            };
            menuBtn.Clicked += async (_, _) => await OnSubjectQuickMenuClicked(capturedSub);
            headerGrid.Add(menuBtn, 2, 0);

            bodyStack.Children.Add(headerGrid);

            // Files & Events Pill
            var statsPill = new Border
            {
                BackgroundColor = isDark ? Color.FromArgb("#0D1124") : Color.FromArgb("#F1F5F9"),
                Stroke = isDark ? Color.FromArgb("#1E2648") : Color.FromArgb("#E2E8F0"),
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 10 },
                Padding = new Thickness(10, 5),
                InputTransparent = true
            };
            var statsGrid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Star },
                    new ColumnDefinition { Width = GridLength.Auto }
                },
                InputTransparent = true
            };
            statsGrid.Add(new Label
            {
                Text = $"📁 {fileCount} Files",
                FontSize = 11,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#38BDF8"),
                InputTransparent = true
            }, 0, 0);
            statsGrid.Add(new Label
            {
                Text = $"📅 {eventCount} Events",
                FontSize = 11,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#C084FC"),
                InputTransparent = true
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
                VerticalOptions = LayoutOptions.Center,
                InputTransparent = true
            };
            progGrid.Add(new Label
            {
                Text = $"{pct}%",
                FontSize = 10.5,
                FontAttributes = FontAttributes.Bold,
                TextColor = isDark ? Color.FromArgb("#8A95B7") : Color.FromArgb("#64748B"),
                InputTransparent = true
            }, 0, 0);
            progGrid.Add(new ProgressBar
            {
                Progress = pct / 100.0,
                ProgressColor = accentColor,
                VerticalOptions = LayoutOptions.Center,
                InputTransparent = true
            }, 1, 0);
            bodyStack.Children.Add(progGrid);

            cardGrid.Add(bodyStack, 0, 1);
            cardBorder.Content = cardGrid;
            cards.Add(cardBorder);
        }

        double totalHeight = 0;
        int rowCount = 0;
        for (int i = 0; i < cards.Count; i += colCount)
        {
            rowCount++;
            if (colCount == 1)
            {
                double h = (i == 0) ? addCardHeight : rowCardHeight;
                SubjectsRowsContainer.Children.Add(cards[i]);
                totalHeight += h;
            }
            else
            {
                var rowGrid = new Grid
                {
                    ColumnSpacing = 12,
                    HorizontalOptions = LayoutOptions.Fill,
                    HeightRequest = rowCardHeight,
                    MinimumHeightRequest = rowCardHeight,
                    RowDefinitions =
                    {
                        new RowDefinition { Height = new GridLength(rowCardHeight) }
                    }
                };
                for (int c = 0; c < colCount; c++)
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });

                for (int c = 0; c < colCount && (i + c) < cards.Count; c++)
                    rowGrid.Add(cards[i + c], c, 0);

                SubjectsRowsContainer.Children.Add(rowGrid);
                totalHeight += rowCardHeight;
            }
        }

        if (rowCount > 1)
            totalHeight += SubjectsRowsContainer.Spacing * (rowCount - 1);

        SubjectsRowsContainer.MinimumHeightRequest = totalHeight;
        SubjectsRowsContainer.HeightRequest = totalHeight;
        (SubjectsRowsContainer as IView)?.InvalidateMeasure();
    }

    private void RenderActiveDrawerCourses()
    {
        ActiveCoursesDrawerList.Children.Clear();
        var active = _state.Subjects.Where(s => !s.Archived).ToList();
        if (active.Count == 0)
        {
            ActiveCoursesDrawerList.Children.Add(new Label
            {
                Text = "No active courses yet.",
                FontSize = 11.5,
                TextColor = Color.FromArgb("#687296")
            });
            return;
        }

        foreach (var sub in active)
        {
            var btn = new Button
            {
                Text = $"{MapSubjectEmoji(sub.Icon)} {sub.Name}",
                FontSize = 12.5,
                FontAttributes = FontAttributes.Bold,
                BackgroundColor = Color.FromArgb("#161B36"),
                TextColor = Color.FromArgb("#F8FAFC"),
                BorderColor = ResolveSubjectAccentColor(sub.Color),
                BorderWidth = 1,
                CornerRadius = 10,
                Padding = new Thickness(12, 8)
            };
            var targetSub = sub;
            btn.Clicked += (_, _) =>
            {
                LeftDrawerOverlay.IsVisible = false;
                OpenSubject(targetSub);
            };
            ActiveCoursesDrawerList.Children.Add(btn);
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
                RenderActiveDrawerCourses();
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
        string action = await DisplayActionSheet($"Manage '{sub.Name}'", "Cancel", "🗑️ Delete Course", "📂 Open Workspace", "🎨 Edit Name, Icon & Color", "📦 Archive Course");
        if (action == "📂 Open Workspace")
        {
            OpenSubject(sub);
        }
        else if (action == "🎨 Edit Name, Icon & Color")
        {
            OpenSubjectEditorModal(sub);
        }
        else if (action == "📦 Archive Course")
        {
            sub.Archived = true;
            RenderSubjectsGrid(_currentSearchQuery);
            RenderActiveDrawerCourses();
            RenderArchivedDrawer();
            UpdateDashboardStats();
            _ = _storage.SaveStateAsync(_state);
            ShowToast($"Archived '{sub.Name}' to StudyHub menu");
        }
        else if (action == "🗑️ Delete Course")
        {
            bool confirm = await DisplayAlert("Delete Course", $"Delete '{sub.Name}' and its items?", "Delete", "Cancel");
            if (confirm)
            {
                _state.Subjects.Remove(sub);
                RenderSubjectsGrid(_currentSearchQuery);
                RenderActiveDrawerCourses();
                UpdateDashboardStats();
                _ = _storage.SaveStateAsync(_state);
                ShowToast($"Deleted '{sub.Name}'");
            }
        }
    }

    private void OnOpenAddSubjectModalClicked(object? sender, EventArgs e)
    {
        OpenSubjectEditorModal(null);
    }

    private void OpenSubjectEditorModal(SubjectItem? existingSubject)
    {
        _editingSubjectTarget = existingSubject;
        if (existingSubject != null)
        {
            LblSubjectEditorTitle.Text = "✏️ Edit Subject";
            EntrySubjectEditorName.Text = existingSubject.Name;
            _editorSelectedIcon = MapSubjectEmoji(existingSubject.Icon);
            if (string.IsNullOrWhiteSpace(_editorSelectedIcon)) _editorSelectedIcon = SubjectEmojiPalette[0];
            _editorSelectedColor = existingSubject.Color;
            if (string.IsNullOrWhiteSpace(_editorSelectedColor) || !_editorSelectedColor.StartsWith("#"))
                _editorSelectedColor = SubjectColorPalette[0];
        }
        else
        {
            int idx = _state.Subjects.Count % SubjectColorPalette.Length;
            LblSubjectEditorTitle.Text = "✨ Add New Subject";
            EntrySubjectEditorName.Text = "";
            _editorSelectedIcon = SubjectEmojiPalette[idx];
            _editorSelectedColor = SubjectColorPalette[idx];
        }

        BuildSubjectEditorOptions();
        SubjectEditorModalOverlay.IsVisible = true;
    }

    private void BuildSubjectEditorOptions()
    {
        SubjectEditorIconsFlex.Children.Clear();
        foreach (string icon in SubjectEmojiPalette)
        {
            bool active = string.Equals(icon, _editorSelectedIcon, StringComparison.Ordinal);
            var btn = new Button
            {
                Text = icon,
                FontSize = 19,
                WidthRequest = 44,
                HeightRequest = 44,
                Padding = 0,
                Margin = new Thickness(0, 0, 8, 8),
                CornerRadius = 12,
                BackgroundColor = active ? Color.FromArgb("#2B265E") : Color.FromArgb("#151A36"),
                BorderColor = active ? Color.FromArgb("#7C5CFF") : Color.FromArgb("#28315A"),
                BorderWidth = active ? 2.0 : 1.0
            };
            string pickedIcon = icon;
            btn.Clicked += (_, _) =>
            {
                _editorSelectedIcon = pickedIcon;
                BuildSubjectEditorOptions();
            };
            SubjectEditorIconsFlex.Children.Add(btn);
        }

        SubjectEditorColorsFlex.Children.Clear();
        foreach (string hex in SubjectColorPalette)
        {
            bool active = string.Equals(hex, _editorSelectedColor, StringComparison.OrdinalIgnoreCase);
            var swatch = new Border
            {
                WidthRequest = 36,
                HeightRequest = 36,
                Margin = new Thickness(0, 0, 10, 8),
                BackgroundColor = Color.FromArgb(hex),
                Stroke = active ? Colors.White : Color.FromArgb("#28315A"),
                StrokeThickness = active ? 3.0 : 1.0,
                StrokeShape = new RoundRectangle { CornerRadius = 18 }
            };
            var tap = new TapGestureRecognizer();
            string pickedHex = hex;
            tap.Tapped += (_, _) =>
            {
                _editorSelectedColor = pickedHex;
                BuildSubjectEditorOptions();
            };
            swatch.GestureRecognizers.Add(tap);
            SubjectEditorColorsFlex.Children.Add(swatch);
        }
    }

    private void OnCloseSubjectEditorClicked(object? sender, EventArgs e)
    {
        SubjectEditorModalOverlay.IsVisible = false;
    }

    private async void OnSaveSubjectEditorClicked(object? sender, EventArgs e)
    {
        string name = (EntrySubjectEditorName.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            await DisplayAlert("Subject Name", "Please enter a course or subject name.", "OK");
            return;
        }

        if (_editingSubjectTarget != null)
        {
            _editingSubjectTarget.Name = name;
            _editingSubjectTarget.Icon = _editorSelectedIcon;
            _editingSubjectTarget.Color = _editorSelectedColor;
            SubjectEditorModalOverlay.IsVisible = false;
            UpdateSubjectHeaderInfo();
            RenderSubjectsGrid(_currentSearchQuery);
            RenderActiveDrawerCourses();
            RenderHomeUpcomingEvents();
            UpdateDashboardStats();
            await _storage.SaveStateAsync(_state);
            ShowToast($"Updated '{name}'");
        }
        else
        {
            var newSub = new SubjectItem
            {
                Id = "sub_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Name = name,
                Icon = _editorSelectedIcon,
                Color = _editorSelectedColor,
                Folders = new List<string> { "Lectures", "Tutorials", "Assignments", "Quizzes", "Labs", "References" }
            };

            _state.Subjects.Add(newSub);
            SubjectEditorModalOverlay.IsVisible = false;
            RenderSubjectsGrid(_currentSearchQuery);
            RenderActiveDrawerCourses();
            UpdateDashboardStats();
            await _storage.SaveStateAsync(_state);
            ShowToast($"Created subject '{newSub.Name}'");
        }
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

    private void OnEditCurrentSubjectClicked(object? sender, EventArgs e)
    {
        if (_currentSubject == null) return;
        OpenSubjectEditorModal(_currentSubject);
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

        double viewWidth = _pageWidth > 120 ? (_pageWidth - 28) : 340;
        string ext = Path.GetExtension(fullPath).ToLowerInvariant();
        if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".webp" || ext == ".bmp")
        {
            var imgBorder = new Border
            {
                HorizontalOptions = LayoutOptions.Fill,
                BackgroundColor = _isPdfDarkMode ? Color.FromArgb("#161822") : Colors.White,
                Stroke = Color.FromArgb("#2E365E"),
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 8 },
                Padding = new Thickness(4),
                Content = new Image
                {
                    Source = ImageSource.FromFile(fullPath),
                    WidthRequest = viewWidth,
                    HeightRequest = Math.Round(viewWidth * 1.25),
                    Aspect = Aspect.AspectFit
                }
            };
            PdfPagesStack.Children.Add(imgBorder);
            return;
        }

        var loadingLabel = new Label
        {
            Text = "⏳ Rendering PDF pages...",
            FontSize = 13,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#94A3C6"),
            HorizontalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 18)
        };
        PdfPagesStack.Children.Add(loadingLabel);

#if ANDROID
        await Task.Run(() =>
        {
            try
            {
                using var pfd = Android.OS.ParcelFileDescriptor.Open(new Java.IO.File(fullPath), Android.OS.ParcelFileMode.ReadOnly);
                if (pfd == null)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        PdfPagesStack.Children.Remove(loadingLabel);
                        PdfPagesStack.Children.Add(new Label
                        {
                            Text = "Could not open PDF file descriptor.",
                            TextColor = Colors.OrangeRed,
                            HorizontalOptions = LayoutOptions.Center
                        });
                    });
                    return;
                }

                using var renderer = new Android.Graphics.Pdf.PdfRenderer(pfd);
                int totalPages = renderer.PageCount;
                int pageCount = Math.Min(totalPages, 50);

                if (pageCount == 0)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        PdfPagesStack.Children.Remove(loadingLabel);
                        PdfPagesStack.Children.Add(new Label
                        {
                            Text = "This PDF document contains 0 pages.",
                            TextColor = Color.FromArgb("#CBD5E1"),
                            HorizontalOptions = LayoutOptions.Center
                        });
                    });
                    return;
                }

                bool clearedLoading = false;
                for (int i = 0; i < pageCount; i++)
                {
                    using var page = renderer.OpenPage(i);
                    double rawW = Math.Max(1, page.Width);
                    double rawH = Math.Max(1, page.Height);
                    int targetW = (int)Math.Clamp(viewWidth * 2.0, 720, 1280);
                    int targetH = Math.Max(1, (int)Math.Round(targetW * rawH / rawW));

                    using var bmp = Android.Graphics.Bitmap.CreateBitmap(targetW, targetH, Android.Graphics.Bitmap.Config.Argb8888!);
                    using var canvas = new Android.Graphics.Canvas(bmp);
                    canvas.DrawColor(Android.Graphics.Color.White);
                    page.Render(bmp, new Android.Graphics.Rect(0, 0, targetW, targetH), null, Android.Graphics.Pdf.PdfRenderMode.ForDisplay);

                    Android.Graphics.Bitmap finalBmp = bmp;
                    Android.Graphics.Bitmap? darkBmp = null;

                    if (_isPdfDarkMode)
                    {
                        darkBmp = Android.Graphics.Bitmap.CreateBitmap(targetW, targetH, Android.Graphics.Bitmap.Config.Argb8888!);
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
                    finalBmp.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg!, 90, ms);
                    darkBmp?.Dispose();
                    byte[] imgBytes = ms.ToArray();

                    int pageNum = i + 1;
                    double displayW = Math.Max(240, viewWidth - 8);
                    double displayH = Math.Max(120, Math.Round(displayW * rawH / rawW));

                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (!clearedLoading)
                        {
                            PdfPagesStack.Children.Remove(loadingLabel);
                            clearedLoading = true;
                        }

                        var pageContainer = new VerticalStackLayout { Spacing = 4, HorizontalOptions = LayoutOptions.Fill };
                        var pageBorder = new Border
                        {
                            HorizontalOptions = LayoutOptions.Center,
                            WidthRequest = displayW + 8,
                            HeightRequest = displayH + 8,
                            BackgroundColor = _isPdfDarkMode ? Color.FromArgb("#161822") : Colors.White,
                            Stroke = Color.FromArgb("#2E365E"),
                            StrokeThickness = 1,
                            StrokeShape = new RoundRectangle { CornerRadius = 8 },
                            Padding = new Thickness(4)
                        };
                        pageBorder.Content = new Image
                        {
                            Source = ImageSource.FromStream(() => new MemoryStream(imgBytes, false)),
                            WidthRequest = displayW,
                            HeightRequest = displayH,
                            Aspect = Aspect.AspectFit
                        };
                        pageContainer.Children.Add(pageBorder);
                        pageContainer.Children.Add(new Label
                        {
                            Text = $"Page {pageNum} of {totalPages}",
                            FontSize = 10.5,
                            TextColor = Color.FromArgb("#8A95B7"),
                            HorizontalOptions = LayoutOptions.Center
                        });
                        PdfPagesStack.Children.Add(pageContainer);
                    });
                }

                if (totalPages > pageCount)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        PdfPagesStack.Children.Add(new Label
                        {
                            Text = $"Showing first {pageCount} of {totalPages} pages. Tap external icon (↗) above to view all.",
                            FontSize = 11.5,
                            TextColor = Color.FromArgb("#FACC15"),
                            HorizontalOptions = LayoutOptions.Center,
                            Margin = new Thickness(8)
                        });
                    });
                }
            }
            catch (Exception ex)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    PdfPagesStack.Children.Remove(loadingLabel);
                    PdfPagesStack.Children.Add(new Label
                    {
                        Text = $"Could not render PDF pages: {ex.Message}",
                        TextColor = Colors.OrangeRed,
                        HorizontalOptions = LayoutOptions.Center
                    });
                });
            }
        });
#else
        await Task.CompletedTask;
        PdfPagesStack.Children.Remove(loadingLabel);
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
                RenderHomeUpcomingEvents();
                RescheduleAndroidCalendarAlarms();
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
        RenderHomeUpcomingEvents();
        RescheduleAndroidCalendarAlarms();
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
                string todayKey = DateTime.Today.ToString("yyyy-MM-dd");
                _state.PomodoroDays[todayKey] = (_state.PomodoroDays.TryGetValue(todayKey, out int pc) ? pc : 0) + 1;
                RecordStudyActivity();
                UpdateDashboardStats();
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
        bool show = !SoundMixerOverlay.IsVisible;
        SoundMixerOverlay.IsVisible = show;
        if (show)
        {
            BuildSoundMixerChannelsUi();
            UpdateSoundMixerBadge();
        }
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
        if (_audio == null || _state == null) return;
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
        if (!LeftDrawerOverlay.IsVisible)
        {
            RenderActiveDrawerCourses();
            RenderArchivedDrawer();
        }
        LeftDrawerOverlay.IsVisible = !LeftDrawerOverlay.IsVisible;
    }

    private void OnDrawerHomeClicked(object? sender, EventArgs e)
    {
        LeftDrawerOverlay.IsVisible = false;
        CalendarModalOverlay.IsVisible = false;
        OnBackToHomeClicked(sender, e);
    }

    private void OnDrawerCalendarClicked(object? sender, EventArgs e)
    {
        LeftDrawerOverlay.IsVisible = false;
        OpenCalendarModal();
    }

    private void OnDrawerTimerClicked(object? sender, EventArgs e)
    {
        LeftDrawerOverlay.IsVisible = false;
        FloatingTimerHost.IsVisible = true;
        BdFloatingTimerExpanded.IsVisible = true;
        BdFloatingTimerMinimized.IsVisible = false;
        ShowToast("Focus Timer opened ⏱️");
    }

    private void OnDrawerZenModeClicked(object? sender, EventArgs e)
    {
        LeftDrawerOverlay.IsVisible = false;
        OnToggleZenModeClicked(sender, e);
    }

    private void OnDrawerMixerClicked(object? sender, EventArgs e)
    {
        LeftDrawerOverlay.IsVisible = false;
        OnToggleSoundMixerClicked(sender, e);
    }

    private void OnDrawerToggleThemeClicked(object? sender, EventArgs e)
    {
        LeftDrawerOverlay.IsVisible = false;
        OnToggleThemeClicked(sender, e);
    }

    private void OnDrawerSettingsClicked(object? sender, EventArgs e)
    {
        LeftDrawerOverlay.IsVisible = false;
        OnOpenSettingsClicked(sender, e);
    }

    private void OnDrawerAddSubjectClicked(object? sender, EventArgs e)
    {
        LeftDrawerOverlay.IsVisible = false;
        OpenSubjectEditorModal(null);
    }

    // ================= UPCOMING EVENTS & CALENDAR FEATURE =================

    private void RenderHomeUpcomingEvents()
    {
        var upcoming = CalendarNotificationEngine.GetUpcomingEvents(_state, includeCompleted: false);
        int count = upcoming.Count;
        LblHomeUpcomingCount.Text = count.ToString();
        NavCalendarActiveDot.IsVisible = count > 0;
        BtnDrawerCalendar.Text = count > 0 ? $"📅 Calendar & Upcoming ({count})" : "📅 Calendar & Upcoming Events";

        HomeUpcomingEventsList.Children.Clear();
        bool isDark = _backdrop.IsDarkMode;

        if (count == 0)
        {
            BtnHomeUpcomingToggleMore.IsVisible = false;
            HomeUpcomingEventsList.Children.Add(new Border
            {
                BackgroundColor = isDark ? Color.FromArgb("#0D1124") : Color.FromArgb("#F1F5F9"),
                Stroke = isDark ? Color.FromArgb("#1E2648") : Color.FromArgb("#CBD5E1"),
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 12 },
                Padding = new Thickness(12, 10),
                Content = new Label
                {
                    Text = "No upcoming events scheduled — tap '+ Note' or open Calendar to plan ahead.",
                    FontSize = 11.5,
                    TextColor = isDark ? Color.FromArgb("#8A95B7") : Color.FromArgb("#475569")
                }
            });
            return;
        }

        int limit = _homeUpcomingExpanded ? upcoming.Count : Math.Min(3, upcoming.Count);
        for (int i = 0; i < limit; i++)
        {
            var item = upcoming[i];
            HomeUpcomingEventsList.Children.Add(BuildUpcomingEventCard(item, compact: true));
        }

        if (upcoming.Count > 3)
        {
            BtnHomeUpcomingToggleMore.IsVisible = true;
            BtnHomeUpcomingToggleMore.Text = _homeUpcomingExpanded
                ? "Show Fewer ▴"
                : $"Show All {upcoming.Count} Upcoming Events ▾";
        }
        else
        {
            BtnHomeUpcomingToggleMore.IsVisible = false;
        }
    }

    private void OnToggleHomeUpcomingExpandClicked(object? sender, EventArgs e)
    {
        _homeUpcomingExpanded = !_homeUpcomingExpanded;
        RenderHomeUpcomingEvents();
    }

    private Border BuildUpcomingEventCard(UpcomingEventEntry entry, bool compact)
    {
        bool isDark = _backdrop.IsDarkMode;
        Color badgeColor = Color.FromArgb(entry.CountdownColorHex);

        var card = new Border
        {
            BackgroundColor = isDark ? Color.FromArgb("#0D1124") : Color.FromArgb("#F8FAFC"),
            Stroke = isDark ? Color.FromArgb("#232C54") : Color.FromArgb("#CBD5E1"),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            Padding = new Thickness(10, 8)
        };

        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 9,
            VerticalOptions = LayoutOptions.Center
        };

        row.Add(new Border
        {
            BackgroundColor = badgeColor.WithAlpha(0.18f),
            Stroke = badgeColor,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            Padding = new Thickness(7, 4),
            VerticalOptions = LayoutOptions.Center,
            Content = new Label
            {
                Text = entry.RelativeBadge,
                FontSize = 10,
                FontAttributes = FontAttributes.Bold,
                TextColor = badgeColor
            }
        }, 0, 0);

        var info = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
        info.Children.Add(new Label
        {
            Text = $"{entry.SubjectIcon} {entry.Title}",
            FontSize = 12.5,
            FontAttributes = FontAttributes.Bold,
            TextColor = isDark ? Color.FromArgb("#F8FAFC") : Color.FromArgb("#0F172A"),
            LineBreakMode = LineBreakMode.TailTruncation
        });

        string subLine = $"{entry.DateTimeLocal:ddd, MMM d} • {entry.TimeDisplay} • {entry.SubjectName}";
        if (!string.IsNullOrWhiteSpace(entry.ReminderSummary))
            subLine += $" • 🔔 {entry.ReminderSummary}";

        info.Children.Add(new Label
        {
            Text = subLine,
            FontSize = 10.5,
            TextColor = isDark ? Color.FromArgb("#8A95B7") : Color.FromArgb("#64748B"),
            LineBreakMode = LineBreakMode.TailTruncation
        });

        if (!compact && !string.IsNullOrWhiteSpace(entry.Note))
        {
            info.Children.Add(new Label
            {
                Text = $"📝 {entry.Note}",
                FontSize = 11,
                TextColor = isDark ? Color.FromArgb("#CBD5E1") : Color.FromArgb("#334155"),
                MaxLines = 2,
                LineBreakMode = LineBreakMode.TailTruncation
            });
        }
        row.Add(info, 1, 0);

        if (compact)
        {
            row.Add(new Label
            {
                Text = "›",
                FontSize = 16,
                FontAttributes = FontAttributes.Bold,
                TextColor = isDark ? Color.FromArgb("#687296") : Color.FromArgb("#64748B"),
                VerticalOptions = LayoutOptions.Center
            }, 2, 0);

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                _calSelectedDate = entry.DateTimeLocal.Date;
                _calDisplayMonth = new DateTime(_calSelectedDate.Year, _calSelectedDate.Month, 1);
                OpenCalendarModal();
            };
            card.GestureRecognizers.Add(tap);
        }
        else
        {
            var actions = new HorizontalStackLayout { Spacing = 4, VerticalOptions = LayoutOptions.Center };
            var doneBtn = new Button
            {
                Text = entry.Completed ? "↩" : "✓",
                FontSize = 11,
                WidthRequest = 30,
                HeightRequest = 28,
                Padding = 0,
                BackgroundColor = entry.Completed ? Color.FromArgb("#1E2548") : Color.FromArgb("#13382D"),
                TextColor = entry.Completed ? Color.FromArgb("#CBD5E1") : Color.FromArgb("#10B981"),
                CornerRadius = 8
            };
            doneBtn.Clicked += async (_, _) => await ToggleUpcomingEntryCompletedAsync(entry);
            actions.Children.Add(doneBtn);

            var delBtn = new Button
            {
                Text = "🗑️",
                FontSize = 11,
                WidthRequest = 30,
                HeightRequest = 28,
                Padding = 0,
                BackgroundColor = Color.FromArgb("#3B182A"),
                TextColor = Color.FromArgb("#F43F5E"),
                CornerRadius = 8
            };
            delBtn.Clicked += async (_, _) => await DeleteUpcomingEntryAsync(entry);
            actions.Children.Add(delBtn);

            row.Add(actions, 2, 0);

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                _calSelectedDate = entry.DateTimeLocal.Date;
                _calDisplayMonth = new DateTime(_calSelectedDate.Year, _calSelectedDate.Month, 1);
                RefreshCalendarModalViews();
            };
            card.GestureRecognizers.Add(tap);
        }

        card.Content = row;
        return card;
    }

    private void OnOpenCalendarModalClicked(object? sender, EventArgs e) => OpenCalendarModal();

    private void OnOpenCalendarAddNoteClicked(object? sender, EventArgs e)
    {
        _calSelectedDate = DateTime.Today;
        _calDisplayMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        OpenCalendarModal();
    }

    private void OpenCalendarModal()
    {
        _calSubjectChoices = _state.Subjects.Where(s => !s.Archived).ToList();
        var pickerItems = new List<string> { "General / Personal" };
        pickerItems.AddRange(_calSubjectChoices.Select(s => $"{MapSubjectEmoji(s.Icon)} {s.Name}"));
        PickerCalSubject.ItemsSource = pickerItems;
        PickerCalSubject.SelectedIndex = 0;

        LblCalMorningBadge.Text = $"Morning reminders at {CalendarNotificationEngine.NormalizeMorningTimeString(_state.Settings.CalendarNotifStartHour)}";
        UpdateCalendarRuleButtonsUi();
        RefreshCalendarModalViews();
        CalendarModalOverlay.IsVisible = true;
    }

    private void OnCloseCalendarModalClicked(object? sender, EventArgs e)
    {
        CalendarModalOverlay.IsVisible = false;
    }

    private async void OnOpenCalMorningQuickConfigClicked(object? sender, EventArgs e)
    {
        string current = CalendarNotificationEngine.NormalizeMorningTimeString(_state.Settings.CalendarNotifStartHour);
        string? res = await DisplayPromptAsync(
            "Morning Notification Time",
            "Receive morning reminders when you open your phone at or after (HH:mm, e.g. 07:00 or 08:00):",
            "Save",
            "Cancel",
            "08:00",
            initialValue: current);
        if (string.IsNullOrWhiteSpace(res)) return;

        await SaveMorningTimeSettingAsync(res);
        RefreshCalendarModalViews();
        UpdateCalendarRuleButtonsUi();
    }

    private void OnCalPrevMonthClicked(object? sender, EventArgs e)
    {
        _calDisplayMonth = _calDisplayMonth.AddMonths(-1);
        RefreshCalendarModalViews();
    }

    private void OnCalNextMonthClicked(object? sender, EventArgs e)
    {
        _calDisplayMonth = _calDisplayMonth.AddMonths(1);
        RefreshCalendarModalViews();
    }

    private void OnCalTodayClicked(object? sender, EventArgs e)
    {
        _calSelectedDate = DateTime.Today;
        _calDisplayMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        RefreshCalendarModalViews();
    }

    private void OnCalToggleAll4Clicked(object? sender, EventArgs e)
    {
        bool allCurrentlyOn = _calRule3Days && _calRule24Hours && _calRuleMorning && _calRuleCustom;
        bool nextState = !allCurrentlyOn;
        _calRule3Days = nextState;
        _calRule24Hours = nextState;
        _calRuleMorning = nextState;
        _calRuleCustom = nextState;
        UpdateCalendarRuleButtonsUi();
    }

    private void OnCalRule3DaysClicked(object? sender, EventArgs e) { _calRule3Days = !_calRule3Days; UpdateCalendarRuleButtonsUi(); }
    private void OnCalRule24HoursClicked(object? sender, EventArgs e) { _calRule24Hours = !_calRule24Hours; UpdateCalendarRuleButtonsUi(); }
    private void OnCalRuleMorningClicked(object? sender, EventArgs e) { _calRuleMorning = !_calRuleMorning; UpdateCalendarRuleButtonsUi(); }
    private void OnCalRuleCustomClicked(object? sender, EventArgs e) { _calRuleCustom = !_calRuleCustom; UpdateCalendarRuleButtonsUi(); }

    private void OnCalCycleCustomUnitClicked(object? sender, EventArgs e)
    {
        _calCustomUnitIdx = (_calCustomUnitIdx + 1) % 3;
        BtnCalCustomUnit.Text = _calCustomUnitIdx switch
        {
            1 => "Minutes Before ▾",
            2 => "Days Before ▾",
            _ => "Hours Before ▾"
        };
    }

    private void UpdateCalendarRuleButtonsUi()
    {
        static void StyleRuleBtn(Button b, bool active, string activeText, string inactiveText)
        {
            b.Text = active ? $"✓ {activeText}" : $"○ {inactiveText}";
            b.BackgroundColor = active ? Color.FromArgb("#7C5CFF") : Color.FromArgb("#1B2142");
            b.TextColor = active ? Colors.White : Color.FromArgb("#CBD5E1");
        }

        string m = CalendarNotificationEngine.NormalizeMorningTimeString(_state.Settings.CalendarNotifStartHour);
        StyleRuleBtn(BtnCalRule3Days, _calRule3Days, "3 Days Before", "3 Days Before");
        StyleRuleBtn(BtnCalRule24Hours, _calRule24Hours, "24 Hours Before", "24 Hours Before");
        StyleRuleBtn(BtnCalRuleMorning, _calRuleMorning, $"Morning ({m})", $"Morning ({m})");
        StyleRuleBtn(BtnCalRuleCustom, _calRuleCustom, "Custom", "Custom");

        bool all4 = _calRule3Days && _calRule24Hours && _calRuleMorning && _calRuleCustom;
        BtnCalSelectAll4.Text = all4 ? "✓ All 4 Active" : "✓ Select All 4";
        BtnCalSelectAll4.BackgroundColor = all4 ? Color.FromArgb("#10B981") : Color.FromArgb("#232D58");
        BtnCalSelectAll4.TextColor = Colors.White;

        BdCalCustomOffsetRow.IsVisible = _calRuleCustom;
    }

    private void RefreshCalendarModalViews()
    {
        LblCalMonthTitle.Text = _calDisplayMonth.ToString("MMMM yyyy");
        LblCalSelectedDayHeader.Text = $"Selected: {_calSelectedDate:ddd, MMM d, yyyy}";
        LblCalMorningBadge.Text = $"Morning reminders at {CalendarNotificationEngine.NormalizeMorningTimeString(_state.Settings.CalendarNotifStartHour)}";

        var allUpcoming = CalendarNotificationEngine.GetUpcomingEvents(_state, includeCompleted: true);
        var datesWithEvents = new HashSet<string>(
            allUpcoming.Where(u => !u.Completed).Select(u => u.DateKey),
            StringComparer.Ordinal);

        // Render 6x7 Month Grid
        CalendarMonthGrid.Children.Clear();
        CalendarMonthGrid.RowDefinitions.Clear();
        CalendarMonthGrid.ColumnDefinitions.Clear();
        for (int c = 0; c < 7; c++) CalendarMonthGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        for (int r = 0; r < 6; r++) CalendarMonthGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });

        DateTime firstOfMonth = new DateTime(_calDisplayMonth.Year, _calDisplayMonth.Month, 1);
        int startCol = (int)firstOfMonth.DayOfWeek;
        DateTime cursor = firstOfMonth.AddDays(-startCol);

        for (int r = 0; r < 6; r++)
        {
            for (int c = 0; c < 7; c++)
            {
                DateTime dayDate = cursor;
                string dayKey = dayDate.ToString("yyyy-MM-dd");
                bool isCurrentMonth = dayDate.Month == _calDisplayMonth.Month;
                bool isSelected = dayDate.Date == _calSelectedDate.Date;
                bool isToday = dayDate.Date == DateTime.Today;
                bool hasEvents = datesWithEvents.Contains(dayKey);

                Color bg = isSelected
                    ? Color.FromArgb("#7C5CFF")
                    : isToday
                        ? Color.FromArgb("#1D2D58")
                        : Color.FromArgb("#0E1328");
                Color borderCol = isSelected
                    ? Color.FromArgb("#C4B5FD")
                    : isToday
                        ? Color.FromArgb("#38BDF8")
                        : Color.FromArgb("#1F264A");

                var cell = new Border
                {
                    BackgroundColor = bg,
                    Stroke = borderCol,
                    StrokeThickness = (isSelected || isToday) ? 1.5 : 1.0,
                    StrokeShape = new RoundRectangle { CornerRadius = 9 },
                    Padding = new Thickness(2),
                    Opacity = isCurrentMonth ? 1.0 : 0.42
                };

                var cellStack = new VerticalStackLayout
                {
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    Spacing = 2
                };
                cellStack.Children.Add(new Label
                {
                    Text = dayDate.Day.ToString(),
                    FontSize = 12,
                    FontAttributes = (isSelected || isToday) ? FontAttributes.Bold : FontAttributes.None,
                    TextColor = isSelected ? Colors.White : (isToday ? Color.FromArgb("#38BDF8") : Color.FromArgb("#E2E8F0")),
                    HorizontalOptions = LayoutOptions.Center
                });

                if (hasEvents)
                {
                    cellStack.Children.Add(new BoxView
                    {
                        WidthRequest = 6,
                        HeightRequest = 6,
                        CornerRadius = 3,
                        Color = isSelected ? Color.FromArgb("#FACC15") : Color.FromArgb("#10B981"),
                        HorizontalOptions = LayoutOptions.Center
                    });
                }

                cell.Content = cellStack;
                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) =>
                {
                    _calSelectedDate = dayDate;
                    if (dayDate.Month != _calDisplayMonth.Month || dayDate.Year != _calDisplayMonth.Year)
                        _calDisplayMonth = new DateTime(dayDate.Year, dayDate.Month, 1);
                    RefreshCalendarModalViews();
                };
                cell.GestureRecognizers.Add(tap);
                CalendarMonthGrid.Add(cell, c, r);

                cursor = cursor.AddDays(1);
            }
        }

        // Selected Day Notes List
        CalSelectedDayEventsList.Children.Clear();
        string selKey = _calSelectedDate.ToString("yyyy-MM-dd");
        var dayItems = allUpcoming.Where(u => u.DateKey == selKey).ToList();
        if (dayItems.Count == 0)
        {
            CalSelectedDayEventsList.Children.Add(new Label
            {
                Text = "No notes or events on this day yet. Add one below!",
                FontSize = 11.5,
                TextColor = Color.FromArgb("#687296")
            });
        }
        else
        {
            foreach (var item in dayItems)
                CalSelectedDayEventsList.Children.Add(BuildUpcomingEventCard(item, compact: false));
        }

        // Upcoming Events List inside Calendar
        var upcomingOnly = CalendarNotificationEngine.GetUpcomingEvents(_state, includeCompleted: false);
        LblCalUpcomingTotalCount.Text = $"{upcomingOnly.Count} upcoming";
        CalUpcomingEventsList.Children.Clear();
        if (upcomingOnly.Count == 0)
        {
            CalUpcomingEventsList.Children.Add(new Label
            {
                Text = "No upcoming events scheduled.",
                FontSize = 11.5,
                TextColor = Color.FromArgb("#687296")
            });
        }
        else
        {
            foreach (var item in upcomingOnly)
                CalUpcomingEventsList.Children.Add(BuildUpcomingEventCard(item, compact: false));
        }
    }

    private async void OnCalSaveEventClicked(object? sender, EventArgs e)
    {
        string title = (EntryCalTitle.Text ?? "").Trim();
        string note = (EditorCalNote.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(note))
        {
            await DisplayAlert("Calendar Note", "Please enter an event title or note for this day.", "OK");
            return;
        }
        if (string.IsNullOrWhiteSpace(title)) title = "Calendar Note";

        var parsedTime = CalendarNotificationEngine.ParseTimeOfDay(EntryCalEventTime.Text, new TimeSpan(9, 0, 0));
        string eventTime = $"{parsedTime.Hours:D2}:{parsedTime.Minutes:D2}";

        string subjectId = "";
        if (PickerCalSubject.SelectedIndex > 0 && PickerCalSubject.SelectedIndex - 1 < _calSubjectChoices.Count)
            subjectId = _calSubjectChoices[PickerCalSubject.SelectedIndex - 1].Id;

        int customOffsetMins = 120;
        if (_calRuleCustom)
        {
            int rawAmount = int.TryParse(EntryCalCustomMinutes.Text, out int v) && v > 0 ? v : 2;
            customOffsetMins = _calCustomUnitIdx switch
            {
                1 => rawAmount,
                2 => rawAmount * 1440,
                _ => rawAmount * 60
            };
        }

        var ev = new CalendarEventItem
        {
            Id = "cal_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Title = title,
            Note = note,
            Date = _calSelectedDate.ToString("yyyy-MM-dd"),
            EventTime = eventTime,
            SubjectId = subjectId,
            Notify3DaysBefore = _calRule3Days,
            Notify24HoursBefore = _calRule24Hours,
            NotifyMorningOf = _calRuleMorning,
            NotifyCustom = _calRuleCustom,
            CustomOffsetMinutes = customOffsetMins,
            CreatedAt = DateTime.UtcNow.ToString("O")
        };

        CalendarNotificationEngine.SuppressPreExistingPastTriggers(ev, _state, DateTime.Now);
        _state.CalendarEvents.Add(ev);

        EntryCalTitle.Text = "";
        EditorCalNote.Text = "";

        RefreshCalendarModalViews();
        RenderHomeUpcomingEvents();
        RenderSubjectsGrid(_currentSearchQuery);
        CheckDueCalendarNotificationsMobile();
        RescheduleAndroidCalendarAlarms();
        await _storage.SaveStateAsync(_state);
        ShowToast($"Saved calendar note for {_calSelectedDate:MMM d} 📅");
    }

    private async Task ToggleUpcomingEntryCompletedAsync(UpcomingEventEntry entry)
    {
        if (entry.SourceCalendarEvent != null)
            entry.SourceCalendarEvent.Completed = !entry.SourceCalendarEvent.Completed;
        else if (entry.SourceDeadline != null)
            entry.SourceDeadline.Completed = !entry.SourceDeadline.Completed;

        RefreshCalendarModalViews();
        RenderHomeUpcomingEvents();
        RenderSubjectsGrid(_currentSearchQuery);
        RescheduleAndroidCalendarAlarms();
        await _storage.SaveStateAsync(_state);
    }

    private async Task DeleteUpcomingEntryAsync(UpcomingEventEntry entry)
    {
        if (entry.SourceCalendarEvent != null)
            _state.CalendarEvents.Remove(entry.SourceCalendarEvent);
        else if (entry.SourceDeadline != null)
            _state.Deadlines.Remove(entry.SourceDeadline);

        RefreshCalendarModalViews();
        RenderHomeUpcomingEvents();
        RenderSubjectsGrid(_currentSearchQuery);
        RescheduleAndroidCalendarAlarms();
        await _storage.SaveStateAsync(_state);
        ShowToast("Removed event");
    }

    private async void OnSettingsCalNotifToggled(object? sender, ToggledEventArgs e)
    {
        if (_isInitializingSettingsUi) return;
        _state.Settings.CalendarNotifsEnabled = e.Value;
        await _storage.SaveStateAsync(_state);
        RescheduleAndroidCalendarAlarms();
        ShowToast(e.Value ? "Calendar alerts enabled 🔔" : "Calendar alerts paused 🔕");
    }

    private async void OnSettingsMorningTimeCompleted(object? sender, EventArgs e)
    {
        await SaveMorningTimeSettingAsync(EntrySettingsMorningTime.Text);
    }

    private async void OnMorningPreset0700Clicked(object? sender, EventArgs e) => await SaveMorningTimeSettingAsync("07:00");
    private async void OnMorningPreset0800Clicked(object? sender, EventArgs e) => await SaveMorningTimeSettingAsync("08:00");
    private async void OnMorningPreset0900Clicked(object? sender, EventArgs e) => await SaveMorningTimeSettingAsync("09:00");

    private async Task SaveMorningTimeSettingAsync(string? rawTime)
    {
        string normalized = CalendarNotificationEngine.NormalizeMorningTimeString(rawTime);
        _state.Settings.CalendarNotifStartHour = normalized;
        _isInitializingSettingsUi = true;
        EntrySettingsMorningTime.Text = normalized;
        _isInitializingSettingsUi = false;
        LblCalMorningBadge.Text = $"Morning reminders at {normalized}";
        await _storage.SaveStateAsync(_state);
        UpdateCalendarRuleButtonsUi();
        CheckDueCalendarNotificationsMobile();
        RescheduleAndroidCalendarAlarms();
        ShowToast($"Morning reminder time set to {normalized} ⏰");
    }

    private void CheckDueCalendarNotificationsMobile()
    {
        _lastNotifCheckUtc = DateTime.UtcNow;
#if ANDROID
        try
        {
            var fired = StudyHub.Platforms.Android.AndroidCalendarNotifications.CheckAndDispatchDue(
                Android.App.Application.Context, _state, _storage);
            if (fired.Count > 0)
            {
                _ = _storage.SaveStateAsync(_state);
                var first = fired[0];
                MainThread.BeginInvokeOnMainThread(() =>
                    ShowToast($"🔔 {first.Title} ({first.RuleLabel})"));
            }
        }
        catch { }
#endif
    }

    private void RescheduleAndroidCalendarAlarms()
    {
#if ANDROID
        try
        {
            StudyHub.Platforms.Android.AndroidCalendarNotifications.RescheduleAll(
                Android.App.Application.Context, _state);
        }
        catch { }
#endif
    }

#if ANDROID
    private void OnAndroidBackgroundNotificationKeysFired(List<string> keys)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _state.FiredNotificationKeys ??= new List<string>();
            var set = new HashSet<string>(_state.FiredNotificationKeys, StringComparer.Ordinal);
            bool changed = false;
            foreach (var k in keys)
            {
                if (set.Add(k))
                {
                    _state.FiredNotificationKeys.Add(k);
                    changed = true;
                }
            }
            if (changed)
            {
                _ = _storage.SaveStateAsync(_state);
            }
        });
    }
#endif

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
                RenderHomeUpcomingEvents();
                RenderActiveDrawerCourses();
                RenderArchivedDrawer();
                UpdateDashboardStats();
                CheckDueCalendarNotificationsMobile();
                RescheduleAndroidCalendarAlarms();
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
        string normalizedUrl = UserSettings.NormalizeGithubRepoUrl(EntryGithubRepoUrl.Text);
        EntryGithubRepoUrl.Text = normalizedUrl;
        _state.Settings.GithubRepoUrl = normalizedUrl;
        await _storage.SaveStateAsync(_state);

        BdMobileUpdateStatusCard.IsVisible = true;
        LblMobileUpdateStatusTitle.Text = "🔄 Checking GitHub for updates...";
        LblMobileUpdateStatusTitle.TextColor = Color.FromArgb("#38BDF8");
        LblMobileUpdateStatusNotes.Text = $"Querying {normalizedUrl}...";
        ProgMobileUpdateDownload.IsVisible = false;
        ProgMobileUpdateDownload.Progress = 0;
        BtnMobileInstallUpdateNow.IsVisible = false;

        ShowToast("Checking GitHub for updates...");
        var res = await _updater.CheckForUpdatesAsync(normalizedUrl, _state.Settings.InstalledCommitSha);
        _lastUpdateCheckResult = res;

        if (!res.Success)
        {
            LblMobileUpdateStatusTitle.Text = "❌ Update Check Failed";
            LblMobileUpdateStatusTitle.TextColor = Color.FromArgb("#F43F5E");
            LblMobileUpdateStatusNotes.Text = res.ErrorMessage;
            await DisplayAlert("GitHub Updater", res.ErrorMessage, "OK");
            return;
        }

        if (res.IsUpdateAvailable)
        {
            LblMobileUpdateStatusTitle.Text = $"🎉 Update Available: v{res.LatestVersion}";
            LblMobileUpdateStatusTitle.TextColor = Color.FromArgb("#10B981");
            LblMobileUpdateStatusNotes.Text = $"{res.ReleaseTitle}\n{res.ReleaseNotes}".Trim();

            if (!string.IsNullOrWhiteSpace(res.DownloadUrl))
            {
                BtnMobileInstallUpdateNow.IsVisible = true;
                BtnMobileInstallUpdateNow.IsEnabled = true;
                BtnMobileInstallUpdateNow.Text = "⬇️ Download & Install Update In-App";

                string msg = $"New update available: {res.LatestVersion}\n{res.ReleaseTitle}\n\n{res.ReleaseNotes}\n\nDownload and install directly inside Study Hub?";
                bool installNow = await DisplayAlert("GitHub Updater", msg, "Install Now", "Later");
                if (installNow)
                {
                    await StartInAppUpdateInstallAsync();
                }
            }
            else
            {
                await DisplayAlert("GitHub Updater", $"New update found ({res.LatestVersion}), but no .apk installer was found in Release assets or repository root.", "OK");
            }
        }
        else
        {
            LblMobileUpdateStatusTitle.Text = $"✅ Up to Date (v{res.CurrentVersion})";
            LblMobileUpdateStatusTitle.TextColor = Color.FromArgb("#10B981");
            string commitInfo = string.IsNullOrWhiteSpace(res.LatestCommitSha)
                ? "You have the latest version."
                : $"Latest check: {res.ReleaseTitle}";
            LblMobileUpdateStatusNotes.Text = commitInfo;

            if (!string.IsNullOrWhiteSpace(res.DownloadUrl))
            {
                BtnMobileInstallUpdateNow.IsVisible = true;
                BtnMobileInstallUpdateNow.IsEnabled = true;
                BtnMobileInstallUpdateNow.Text = "🔄 Reinstall Latest Build In-App";
            }

            await DisplayAlert("GitHub Updater", $"You are up to date (v{res.CurrentVersion}).\n{commitInfo}", "OK");
        }
    }

    private async void OnMobileInstallUpdateNowClicked(object? sender, EventArgs e)
    {
        await StartInAppUpdateInstallAsync();
    }

    private async Task StartInAppUpdateInstallAsync()
    {
        if (_isUpdateInstalling) return;
        if (_lastUpdateCheckResult == null || string.IsNullOrWhiteSpace(_lastUpdateCheckResult.DownloadUrl))
        {
            ShowToast("Please check GitHub for updates first.");
            return;
        }

        _isUpdateInstalling = true;
        BtnMobileInstallUpdateNow.IsEnabled = false;
        BtnCheckGithubUpdate.IsEnabled = false;
        BdMobileUpdateStatusCard.IsVisible = true;
        ProgMobileUpdateDownload.IsVisible = true;

        try
        {
#if ANDROID
            // If APK was already downloaded and user just granted Unknown Apps permission, launch installer immediately
            if (!string.IsNullOrWhiteSpace(_downloadedApkPath) && File.Exists(_downloadedApkPath))
            {
                LblMobileUpdateStatusTitle.Text = "📦 Launching Android Installer...";
                LblMobileUpdateStatusNotes.Text = "Installing downloaded StudyHub update...";
                var (instOk, instErr) = _updater.LaunchAndroidApkInstaller(_downloadedApkPath);
                if (instOk)
                {
                    ShowToast("Android package installer opened 📦");
                    return;
                }
                if (instErr == "PERMISSION_NEEDED")
                {
                    LblMobileUpdateStatusTitle.Text = "🔐 Permission Required";
                    LblMobileUpdateStatusTitle.TextColor = Color.FromArgb("#F59E0B");
                    LblMobileUpdateStatusNotes.Text = "Allow 'Install unknown apps' for Study Hub, then tap Install again.";
                    await DisplayAlert("Install Permission Required", "Please enable 'Allow from this source' in Android Settings, then return and tap the Install button again (no re-download needed).", "OK");
                    return;
                }
                // Otherwise fall through to fresh download
                _downloadedApkPath = "";
            }
#endif

            ProgMobileUpdateDownload.Progress = 0.05;
            LblMobileUpdateStatusTitle.Text = "⬇️ Downloading Update...";
            LblMobileUpdateStatusTitle.TextColor = Color.FromArgb("#38BDF8");

            var progress = new Progress<(int percent, string status)>(p =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    ProgMobileUpdateDownload.Progress = Math.Clamp(p.percent / 100.0, 0.0, 1.0);
                    LblMobileUpdateStatusNotes.Text = $"{p.status} ({p.percent}%)";
                });
            });

            var (ok, error) = await _updater.DownloadAndApplyUpdateAsync(_lastUpdateCheckResult, progress);
            if (!ok)
            {
                LblMobileUpdateStatusTitle.Text = "❌ Update Download Failed";
                LblMobileUpdateStatusTitle.TextColor = Color.FromArgb("#F43F5E");
                LblMobileUpdateStatusNotes.Text = error;
                await DisplayAlert("Update Failed", error, "OK");
                return;
            }

            if (!string.IsNullOrWhiteSpace(_lastUpdateCheckResult.LatestCommitSha))
            {
                _state.Settings.InstalledCommitSha = _lastUpdateCheckResult.LatestCommitSha;
                await _storage.SaveStateAsync(_state);
            }

#if ANDROID
            _downloadedApkPath = _updater.LastDownloadedApkPath;
            LblMobileUpdateStatusTitle.Text = "📦 Installing Update...";
            LblMobileUpdateStatusTitle.TextColor = Color.FromArgb("#10B981");
            LblMobileUpdateStatusNotes.Text = "Opening Android package installer...";

            var (launched, launchErr) = _updater.LaunchAndroidApkInstaller(_downloadedApkPath);
            if (launched)
            {
                ShowToast("Follow the Android prompt to finish updating 🚀");
            }
            else if (launchErr == "PERMISSION_NEEDED")
            {
                LblMobileUpdateStatusTitle.Text = "🔐 Permission Required";
                LblMobileUpdateStatusTitle.TextColor = Color.FromArgb("#F59E0B");
                LblMobileUpdateStatusNotes.Text = "Enable 'Allow from this source' in Android Settings, then tap Install again.";
                BtnMobileInstallUpdateNow.Text = "📦 Install Downloaded APK";
                await DisplayAlert("Allow Unknown Apps", "Android Settings was opened so you can allow Study Hub to install updates. Toggle 'Allow from this source', come back, and tap 'Install Downloaded APK'.", "OK");
            }
            else
            {
                LblMobileUpdateStatusTitle.Text = "⚠️ Could Not Launch Installer";
                LblMobileUpdateStatusTitle.TextColor = Color.FromArgb("#F59E0B");
                LblMobileUpdateStatusNotes.Text = launchErr;
                await DisplayAlert("Installer Error", $"Could not open Android package installer: {launchErr}", "OK");
            }
#else
            LblMobileUpdateStatusTitle.Text = "🚀 Restarting to Apply Update...";
            ShowToast("Restarting Study Hub to apply update...");
            Application.Current?.Quit();
#endif
        }
        catch (Exception ex)
        {
            LblMobileUpdateStatusTitle.Text = "❌ Update Error";
            LblMobileUpdateStatusTitle.TextColor = Color.FromArgb("#F43F5E");
            LblMobileUpdateStatusNotes.Text = ex.Message;
            await DisplayAlert("Update Error", ex.Message, "OK");
        }
        finally
        {
            _isUpdateInstalling = false;
            BtnMobileInstallUpdateNow.IsEnabled = true;
            BtnCheckGithubUpdate.IsEnabled = true;
        }
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
