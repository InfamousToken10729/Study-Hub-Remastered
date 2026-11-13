using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using StudyHub.Models;
using StudyHub.Services;
using Path = System.IO.Path;

namespace StudyHub
{
    public partial class MainWindow : Window
    {
        private readonly StorageService _storage;
        private readonly GitHubUpdateService _updater;
        private readonly AudioStudioService _audio;

        private AppStateData _state;
        private SubjectItem? _currentSubject;
        private string _activeTab = "Files";
        private StudyFileItem? _splitDoc;

        // Timers & Unified VSync Animation Loop
        private readonly DispatcherTimer _clockTimer;
        private readonly DispatcherTimer _pomoTimer;
        private readonly DispatcherTimer _toastTimer;
        private readonly DispatcherTimer _sliderSaveTimer;
        private readonly Stopwatch _renderStopwatch = new();
        private bool _isRenderLoopAttached = false;
        private bool _isClosing = false;
        private bool _perfSkipToggle = false;
        private TimeSpan _lastRenderTimestamp = TimeSpan.MinValue;

        // Pomodoro State
        private int _pomoSecondsRemaining = 25 * 60;
        private int _pomoPhaseTotalSeconds = 25 * 60;
        private bool _isPomoRunning = false;
        private bool _isPomoBreak = false;
        private bool _isTimerMinimized = false;
        private bool _isPomoSettingsOpen = false;
        private bool _pomoMouseDown = false;
        private bool _isDraggingPomo = false;
        private Point _pomoDragStartPoint;
        private string _pomoDragHoverZone = "bottom-right";
        private int _sessionStartTimeUnix = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Zen & Window State
        private bool _isFullscreen = false;
        private bool _is24HourClock = false;
        private bool _isDarkMode = true;
        private string _currentZenScene = "north_pole";
        private readonly List<string> _zenCustomVideos = new();
        private int _zenCurrentVideoIdx = 0;
        private int _lastClockSecond = -1;
        private bool _lastClockWas24 = false;
        private string _lastClockStyle = "";
        private readonly Random _rand = new();

        // Global Ambient Particle Engine State (zero-layout transform-only nodes)
        private class ParticleEngineItem
        {
            public FrameworkElement Element = null!;
            public TranslateTransform Translate = null!;
            public TranslateTransform? SpriteShift;
            public ScaleTransform? Scale;
            public double X, Y, Vx, Vy, R, MaxR, Phase, Speed;
            public Color BaseColor;
        }
        private class MeshSegmentItem
        {
            public Rectangle Rect = null!;
            public MatrixTransform Transform = null!;
        }
        private readonly List<ParticleEngineItem> _activeGlobalParticles = new();
        private readonly List<MeshSegmentItem> _activeMeshLines = new();
        private BitmapSource? _matrixGlyphStripBitmap;
        private string _currentParticleEffect = "network";

        // Selected Icon & Color for Course Creation
        private string _selectedModalIcon = "🎓";
        private string _selectedModalColor = "#7C5CFF";
        private string? _editingSubjectId = null;

        // Independent Collapsed Folders Tracker (each folder state is isolated & preserved)
        private readonly HashSet<string> _collapsedFolderKeys = new(StringComparer.OrdinalIgnoreCase);

        // Currently Open PDF Document in In-App Reader
        private StudyFileItem? _currentInAppPdfFile = null;
        private string? _currentInAppPdfFullPath = null;
        private string? _activePdfFrameSrc = null;
        private int _pdfNavCounter = 0;
        private Microsoft.Web.WebView2.Core.CoreWebView2Environment? _pdfWebViewEnv = null;
        private bool _isPdfDarkMode = false;

        // Flashcards deck state
        private int _flashcardIndex = 0;
        private bool _flashcardFlipped = false;

        // Guard flag against recursive slider updates when rendering Sound Mixer
        private bool _isRenderingMixer = false;

        public MainWindow()
        {
            InitializeComponent();

            _storage = new StorageService();
            _updater = new GitHubUpdateService(_storage.BaseDir);
            _audio = new AudioStudioService(_storage.SoundsDir);
            _audio.PlaybackStateChanged += Audio_PlaybackStateChanged;

            _state = _storage.LoadState();
            _state.Normalize();

            if (MainWhiteboardCanvas != null)
            {
                MainWhiteboardCanvas.DefaultDrawingAttributes.Color = Color.FromRgb(124, 92, 255);
            }

            // Real-time Top Bar Clock Timer (250ms cadence with change-detection prevents second skips and Zen HUD flicker)
            _clockTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(250) };
            _clockTimer.Tick += (s, e) => UpdateLiveClock();
            _clockTimer.Start();
            UpdateLiveClock(force: true);

            // Focus Pomodoro Timer
            _pomoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _pomoTimer.Tick += PomoTimer_Tick;

            // Toast Auto-hide Timer
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _toastTimer.Tick += (s, e) => { ToastBorder.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };

            // Debounced Slider Persistence Timer (prevents disk thrash during drags)
            _sliderSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _sliderSaveTimer.Tick += (s, e) => { _sliderSaveTimer.Stop(); SaveState(countActivity: false); };

            StateChanged += (s, e) => UpdateRenderLoopState();
            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Apply saved launch mode
                if (string.Equals(_state.Settings?.LaunchWindowMode, "fullscreen", StringComparison.OrdinalIgnoreCase))
                {
                    SetFullscreen(true);
                }

                if (!string.IsNullOrWhiteSpace(_state.Settings?.GithubRepoUrl))
                {
                    TxtSettingsGithubUrl.Text = _state.Settings.GithubRepoUrl;
                }

                ChkSettingsAutoCheckOnLaunch.IsChecked = _state.Settings?.AutoCheckUpdates ?? true;
                _is24HourClock = string.Equals(_state.Settings?.TimeFormat, "24h", StringComparison.OrdinalIgnoreCase);
                UpdateLiveClock(force: true);

                // Normalize surface theme vs dark/light toggle
                bool startInDark = !string.Equals(_state.Theme, "light", StringComparison.OrdinalIgnoreCase);
                if (string.Equals(_state.Settings?.BgTheme, "light", StringComparison.OrdinalIgnoreCase))
                {
                    startInDark = false;
                    _state.Settings!.BgTheme = "aurora";
                }
                else if (string.Equals(_state.Settings?.BgTheme, "dark", StringComparison.OrdinalIgnoreCase))
                {
                    _state.Settings!.BgTheme = "aurora";
                }

                // Synchronize Ambient Audio Studio channels & master settings BEFORE UI rendering
                _audio.IsMasterMuted = _state.Settings?.MixerMuted ?? false;
                _audio.SyncCustomSounds(_state.CustomSounds, _state.Settings?.MixerLevels, _state.Settings?.MixerMaster ?? 80);
                _isRenderingMixer = true;
                SliderMasterVolume.Value = _state.Settings?.MixerMaster ?? 80;
                _isRenderingMixer = false;

                // Initialize Pomodoro timer duration from persisted preferences
                int savedWork = (_state.Settings?.PomoWork > 0) ? _state.Settings.PomoWork : 25;
                int savedBreak = (_state.Settings?.PomoBreak > 0) ? _state.Settings.PomoBreak : 5;
                _pomoSecondsRemaining = savedWork * 60;
                _pomoPhaseTotalSeconds = _pomoSecondsRemaining;
                if (TxtPomoWorkInput != null) TxtPomoWorkInput.Text = savedWork.ToString();
                if (TxtPomoBreakInput != null) TxtPomoBreakInput.Text = savedBreak.ToString();

                // Build Core UI with persisted theme (without double-triggering BgTheme or inflating activity on boot)
                ApplyTheme(startInDark, saveToDisk: false);
                ApplyTimerDockPosition(saveToDisk: false);
                UpdatePomoDisplay();

                // Initialize Particle Effects & Zen Scenes (Surface theme already applied via ApplyTheme)
                _isPdfDarkMode = _state.Settings?.PdfDarkMode ?? false;
                UpdatePdfDarkModeButtonUi();
                ApplyParticleEffect(_state.Settings?.ParticleEffect ?? "network", skipToast: true, saveToDisk: false);
                RefreshZenCustomVideos();
                ApplyZenScene(_state.Settings?.ZenScene ?? "north_pole", saveToDisk: false);
                UpdateZenClockAndHud(force: true);
                RenderControlCenterThemesAndEffects();
                UpdateControlCenterUiState();
                UpdateRenderLoopState();

                // Auto-check GitHub for updates on launch if configured
                if (_state.Settings?.AutoCheckUpdates != false &&
                    !string.IsNullOrWhiteSpace(_state.Settings?.GithubRepoUrl))
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1800);
                        await Dispatcher.InvokeAsync(async () =>
                        {
                            await CheckForGithubUpdatesAsync(silentOnNoUpdate: true);
                        });
                    });
                }
            }
            catch (Exception ex)
            {
                ShowToast($"Startup issue: {ex.Message}");
            }
        }

        // ================= CLOCK & POMODORO =================

        private void UpdateLiveClock(bool force = false)
        {
            var now = DateTime.Now;
            string clockStyle = _state?.Settings?.ClockStyle ?? "digital";
            if (!force &&
                now.Second == _lastClockSecond &&
                _is24HourClock == _lastClockWas24 &&
                string.Equals(clockStyle, _lastClockStyle, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _lastClockSecond = now.Second;
            _lastClockWas24 = _is24HourClock;
            _lastClockStyle = clockStyle;

            string ampm = now.Hour >= 12 ? "PM" : "AM";
            string timeStr = _is24HourClock ? now.ToString("HH:mm:ss") : $"{now:hh:mm:ss} {ampm}";
            string dateStr = now.ToString("ddd, MMM d");
            if (TxtNavClockTime != null && !string.Equals(TxtNavClockTime.Text, timeStr, StringComparison.Ordinal))
                TxtNavClockTime.Text = timeStr;
            if (TxtNavClockDate != null && !string.Equals(TxtNavClockDate.Text, dateStr, StringComparison.Ordinal))
                TxtNavClockDate.Text = dateStr;
            if (TxtPdfClockTime != null && !string.Equals(TxtPdfClockTime.Text, timeStr, StringComparison.Ordinal))
                TxtPdfClockTime.Text = timeStr;
            if (TxtPdfClockDate != null && !string.Equals(TxtPdfClockDate.Text, dateStr, StringComparison.Ordinal))
                TxtPdfClockDate.Text = dateStr;

            UpdateZenClockAndHud(now, force);

            // Update Dynamic Greeting Badge in Welcome Banner
            int h = now.Hour;
            bool useSunVector = false;
            string icon = "⚡";
            string greeting = "NIGHT OWL MODE 🌙";
            if (h < 5)
            {
                icon = "🌙";
                greeting = "LATE NIGHT GRIND ⚡";
            }
            else if (h < 12)
            {
                useSunVector = true;
                icon = "";
                greeting = "EARLY BIRD FOCUS 🚀";
            }
            else if (h < 17)
            {
                useSunVector = true;
                icon = "";
                greeting = "AFTERNOON SPRINT ⚡";
            }
            else if (h < 21)
            {
                icon = "✨";
                greeting = "EVENING STUDY 💡";
            }
            else
            {
                icon = "⚡";
                greeting = "NIGHT OWL MODE 🌙";
            }

            if (GridGreetingSunIcon != null) GridGreetingSunIcon.Visibility = useSunVector ? Visibility.Visible : Visibility.Collapsed;
            if (TxtGreetingIcon != null)
            {
                TxtGreetingIcon.Text = icon;
                TxtGreetingIcon.Visibility = useSunVector ? Visibility.Collapsed : Visibility.Visible;
            }
            if (TxtGreetingBadge != null && !string.Equals(TxtGreetingBadge.Text, greeting, StringComparison.Ordinal))
                TxtGreetingBadge.Text = greeting;
        }

        private void NavClockBorder_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _is24HourClock = !_is24HourClock;
            if (_state?.Settings != null)
            {
                _state.Settings.TimeFormat = _is24HourClock ? "24h" : "12h";
                SaveState(countActivity: false);
                UpdateControlCenterUiState();
            }
            UpdateLiveClock(force: true);
            ShowToast(_is24HourClock ? "24-hour time format enabled 🕒" : "12-hour time format enabled 🕒");
        }

        private void PomoTimer_Tick(object? sender, EventArgs e)
        {
            if (_pomoSecondsRemaining > 0)
            {
                _pomoSecondsRemaining--;
                UpdatePomoDisplay();
            }
            else
            {
                _pomoTimer.Stop();
                _isPomoRunning = false;
                _audio.PlayNotificationChime();

                if (!_isPomoBreak)
                {
                    _state.PomodorosCompleted++;
                    SaveState();
                    int brkMins = _state.Settings?.PomoBreak > 0 ? _state.Settings.PomoBreak : 5;
                    ShowToast($"Focus session complete! Take a {brkMins}-minute break 🏆");
                    _isPomoBreak = true;
                    _pomoSecondsRemaining = brkMins * 60;
                    _pomoPhaseTotalSeconds = _pomoSecondsRemaining;
                }
                else
                {
                    ShowToast("Break ended! Ready to focus? 🚀");
                    _isPomoBreak = false;
                    int wrkMins = _state.Settings?.PomoWork > 0 ? _state.Settings.PomoWork : 25;
                    _pomoSecondsRemaining = wrkMins * 60;
                    _pomoPhaseTotalSeconds = _pomoSecondsRemaining;
                }

                UpdatePomoDisplay();
            }
        }

        private void UpdatePomoDisplay()
        {
            int m = _pomoSecondsRemaining / 60;
            int s = _pomoSecondsRemaining % 60;
            string formatted = $"{m:00}:{s:00}";

            if (TxtNavTimerDisplay != null) TxtNavTimerDisplay.Text = formatted;
            if (TxtFloatingTimerDisplay != null) TxtFloatingTimerDisplay.Text = formatted;
            if (TxtPomoMinTime != null) TxtPomoMinTime.Text = formatted;
            if (TxtAnalogSubReadout != null) TxtAnalogSubReadout.Text = formatted;
            if (TxtDropdownTimerDisplay != null) TxtDropdownTimerDisplay.Text = formatted;
            if (TxtPdfTimerDisplay != null) TxtPdfTimerDisplay.Text = formatted;
            UpdateZenTimerReadout();

            var dotBrush = _isPomoRunning
                ? new SolidColorBrush(Color.FromRgb(16, 185, 129))
                : (_isPomoBreak ? new SolidColorBrush(Color.FromRgb(34, 211, 238)) : new SolidColorBrush(Color.FromRgb(244, 63, 94)));
            if (NavTimerStatusDot != null) NavTimerStatusDot.Fill = dotBrush;
            if (PdfTimerStatusDot != null) PdfTimerStatusDot.Fill = dotBrush;
            if (PomoMinDot != null) PomoMinDot.Fill = dotBrush;

            string statusText = _isPomoRunning
                ? (_isPomoBreak ? "BREAK TIME • RECHARGE" : "FOCUSING • KEEP GOING")
                : (_pomoSecondsRemaining < _pomoPhaseTotalSeconds ? "PAUSED" : (_isPomoBreak ? "BREAK READY" : "READY TO FOCUS!"));
            if (TxtFloatingTimerStatus != null) TxtFloatingTimerStatus.Text = statusText;
            if (TxtDropdownTimerStatus != null) TxtDropdownTimerStatus.Text = statusText;

            bool isAnalog = string.Equals(_state?.Settings?.ClockStyle, "analog", StringComparison.OrdinalIgnoreCase);
            if (TxtFloatingTimerDisplay != null && PomoAnalogContainer != null)
            {
                TxtFloatingTimerDisplay.Visibility = isAnalog ? Visibility.Collapsed : Visibility.Visible;
                PomoAnalogContainer.Visibility = isAnalog ? Visibility.Visible : Visibility.Collapsed;
            }
            if (isAnalog) UpdateAnalogDial();

            UpdateMinimizedProgressBar();
        }

        private void UpdateAnalogDial()
        {
            if (AnalogProgressArc == null || AnalogMinuteRotate == null || AnalogSecondRotate == null) return;
            double fraction = _pomoPhaseTotalSeconds > 0
                ? Math.Clamp((double)_pomoSecondsRemaining / _pomoPhaseTotalSeconds, 0.0, 1.0)
                : 0.0;

            AnalogMinuteRotate.Angle = (_pomoSecondsRemaining / 3600.0) * 360.0;
            AnalogSecondRotate.Angle = (_pomoSecondsRemaining % 60) * 6.0;

            double cx = 60, cy = 60, r = 54;
            if (fraction <= 0.001)
            {
                AnalogProgressArc.Data = Geometry.Empty;
                return;
            }
            double sweepDeg = Math.Min(359.9, fraction * 360.0);
            double endRad = (-90.0 + sweepDeg) * Math.PI / 180.0;
            Point startPt = new(cx, cy - r);
            Point endPt = new(cx + r * Math.Cos(endRad), cy + r * Math.Sin(endRad));
            bool isLarge = sweepDeg > 180.0;

            var fig = new PathFigure { StartPoint = startPt, IsClosed = false };
            fig.Segments.Add(new ArcSegment(endPt, new Size(r, r), 0, isLarge, SweepDirection.Clockwise, true));
            var geom = new PathGeometry();
            geom.Figures.Add(fig);
            AnalogProgressArc.Data = geom;
        }

        private void UpdateMinimizedProgressBar()
        {
            if (PomoMinimizedBarWrap == null || PomoMinimizedBarFill == null || PomoClipGrid == null) return;
            PomoMinimizedBarWrap.Visibility = _isTimerMinimized ? Visibility.Visible : Visibility.Collapsed;

            double trackWidth = PomoClipGrid.ActualWidth > 10 ? PomoClipGrid.ActualWidth : 286;
            double fraction = _pomoPhaseTotalSeconds > 0
                ? Math.Clamp((double)_pomoSecondsRemaining / _pomoPhaseTotalSeconds, 0.0, 1.0)
                : 0.0;
            double activeWidth = Math.Max(0, trackWidth * fraction);
            PomoMinimizedBarFill.Width = activeWidth;
            if (PomoMinimizedBarAura != null)
            {
                PomoMinimizedBarAura.Width = activeWidth;
            }
        }

        private void PomoClipGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (PomoClipGrid != null)
            {
                PomoClipGrid.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 21.5, 21.5);
                UpdateMinimizedProgressBar();
            }
        }

        private void BtnTimerStart_Click(object sender, RoutedEventArgs e)
        {
            if (_pomoPhaseTotalSeconds <= 0) _pomoPhaseTotalSeconds = Math.Max(60, _pomoSecondsRemaining);
            _isPomoRunning = true;
            _pomoTimer.Start();
            UpdatePomoDisplay();
            ShowToast("Timer started! Stay focused 🎯");
        }

        private void BtnTimerPause_Click(object sender, RoutedEventArgs e)
        {
            _isPomoRunning = false;
            _pomoTimer.Stop();
            UpdatePomoDisplay();
            ShowToast("Timer paused");
        }

        private void BtnTimerReset_Click(object sender, RoutedEventArgs e)
        {
            _isPomoRunning = false;
            _pomoTimer.Stop();
            _isPomoBreak = false;
            int wrk = _state.Settings?.PomoWork > 0 ? _state.Settings.PomoWork : 25;
            _pomoSecondsRemaining = wrk * 60;
            _pomoPhaseTotalSeconds = _pomoSecondsRemaining;
            UpdatePomoDisplay();
            ShowToast("Timer reset ↺");
        }

        private void BtnPresetWork25_Click(object sender, RoutedEventArgs e)
        {
            _isPomoRunning = false;
            _pomoTimer.Stop();
            _isPomoBreak = false;
            _pomoSecondsRemaining = 25 * 60;
            _pomoPhaseTotalSeconds = _pomoSecondsRemaining;
            UpdatePomoDisplay();
        }

        private void BtnPresetBreak5_Click(object sender, RoutedEventArgs e)
        {
            _isPomoRunning = false;
            _pomoTimer.Stop();
            _isPomoBreak = true;
            int brk = _state.Settings?.PomoBreak > 0 ? _state.Settings.PomoBreak : 5;
            _pomoSecondsRemaining = brk * 60;
            _pomoPhaseTotalSeconds = _pomoSecondsRemaining;
            UpdatePomoDisplay();
        }

        private void BtnDropdownWorkPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && int.TryParse(btn.Tag as string, out int mins) && mins > 0)
            {
                _isPomoRunning = false;
                _pomoTimer.Stop();
                _isPomoBreak = false;
                _state.Settings.PomoWork = mins;
                if (TxtPomoWorkInput != null) TxtPomoWorkInput.Text = mins.ToString();
                _pomoSecondsRemaining = mins * 60;
                _pomoPhaseTotalSeconds = _pomoSecondsRemaining;
                SaveState(countActivity: false);
                UpdatePomoDisplay();
                ShowToast($"Focus duration set to {mins} min ⏱️");
            }
        }

        private void BtnPomoSettingsToggle_Click(object sender, RoutedEventArgs e)
        {
            if (PomoSettingsPanel == null) return;
            _isPomoSettingsOpen = !_isPomoSettingsOpen;
            if (_isPomoSettingsOpen && _isTimerMinimized)
            {
                SetTimerMinimized(false);
            }
            if (_isPomoSettingsOpen)
            {
                int w = _state.Settings?.PomoWork > 0 ? _state.Settings.PomoWork : 25;
                int b = _state.Settings?.PomoBreak > 0 ? _state.Settings.PomoBreak : 5;
                if (TxtPomoWorkInput != null) TxtPomoWorkInput.Text = w.ToString();
                if (TxtPomoBreakInput != null) TxtPomoBreakInput.Text = b.ToString();
                UpdateTimerDockButtonsUi();
            }
            PomoSettingsPanel.Visibility = _isPomoSettingsOpen ? Visibility.Visible : Visibility.Collapsed;
            UpdateTimerHeaderIconColors();
        }

        private void BtnPomoQuickPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string raw)
            {
                var parts = raw.Split(',');
                if (parts.Length == 2 && TxtPomoWorkInput != null && TxtPomoBreakInput != null)
                {
                    TxtPomoWorkInput.Text = parts[0];
                    TxtPomoBreakInput.Text = parts[1];
                }
            }
        }

        private void BtnStepWorkDown_Click(object sender, RoutedEventArgs e)
        {
            int val = int.TryParse(TxtPomoWorkInput?.Text, out int v) ? v : 25;
            val = Math.Max(1, val - 5);
            if (TxtPomoWorkInput != null) TxtPomoWorkInput.Text = val.ToString();
        }

        private void BtnStepWorkUp_Click(object sender, RoutedEventArgs e)
        {
            int val = int.TryParse(TxtPomoWorkInput?.Text, out int v) ? v : 25;
            val = Math.Min(180, val + 5);
            if (TxtPomoWorkInput != null) TxtPomoWorkInput.Text = val.ToString();
        }

        private void BtnStepBreakDown_Click(object sender, RoutedEventArgs e)
        {
            int val = int.TryParse(TxtPomoBreakInput?.Text, out int v) ? v : 5;
            val = Math.Max(1, val - 1);
            if (TxtPomoBreakInput != null) TxtPomoBreakInput.Text = val.ToString();
        }

        private void BtnStepBreakUp_Click(object sender, RoutedEventArgs e)
        {
            int val = int.TryParse(TxtPomoBreakInput?.Text, out int v) ? v : 5;
            val = Math.Min(60, val + 1);
            if (TxtPomoBreakInput != null) TxtPomoBreakInput.Text = val.ToString();
        }

        private void BtnSavePomoSettings_Click(object sender, RoutedEventArgs e)
        {
            int w = int.TryParse(TxtPomoWorkInput?.Text, out int wv) ? Math.Clamp(wv, 1, 180) : 25;
            int b = int.TryParse(TxtPomoBreakInput?.Text, out int bv) ? Math.Clamp(bv, 1, 60) : 5;

            if (TxtPomoWorkInput != null) TxtPomoWorkInput.Text = w.ToString();
            if (TxtPomoBreakInput != null) TxtPomoBreakInput.Text = b.ToString();

            _state.Settings.PomoWork = w;
            _state.Settings.PomoBreak = b;
            SaveState(countActivity: false);

            if (!_isPomoRunning)
            {
                _pomoSecondsRemaining = (_isPomoBreak ? b : w) * 60;
                _pomoPhaseTotalSeconds = _pomoSecondsRemaining;
                UpdatePomoDisplay();
            }

            _isPomoSettingsOpen = false;
            if (PomoSettingsPanel != null) PomoSettingsPanel.Visibility = Visibility.Collapsed;
            UpdateTimerHeaderIconColors();
            ShowToast($"Timer saved: {w}m focus / {b}m break ✅");
        }

        private void BtnPomoCollapseToggle_Click(object sender, RoutedEventArgs e)
        {
            SetTimerMinimized(!_isTimerMinimized);
        }

        private void SetTimerMinimized(bool minimized)
        {
            _isTimerMinimized = minimized;
            if (minimized && _isPomoSettingsOpen)
            {
                _isPomoSettingsOpen = false;
                if (PomoSettingsPanel != null) PomoSettingsPanel.Visibility = Visibility.Collapsed;
            }

            if (PomoContentPanel != null)
            {
                PomoContentPanel.Visibility = minimized ? Visibility.Collapsed : Visibility.Visible;
            }
            if (PomoMinTimeChip != null)
            {
                PomoMinTimeChip.Visibility = minimized ? Visibility.Visible : Visibility.Collapsed;
            }
            if (PomoBodyStack != null)
            {
                PomoBodyStack.Margin = minimized ? new Thickness(16, 11, 16, 13) : new Thickness(18, 14, 18, 16);
            }
            if (IconPomoChevronPath != null)
            {
                IconPomoChevronPath.Data = Geometry.Parse(minimized ? "M 6,15 L 12,9 L 18,15" : "M 6,9 L 12,15 L 18,9");
            }
            UpdateTimerHeaderIconColors();
            UpdateMinimizedProgressBar();
        }

        private void UpdateTimerHeaderIconColors()
        {
            var muted = _isDarkMode ? new SolidColorBrush(Color.FromRgb(148, 163, 184)) : new SolidColorBrush(Color.FromRgb(100, 116, 139));
            if (IconPomoGearPath != null)
            {
                IconPomoGearPath.Fill = _isPomoSettingsOpen ? new SolidColorBrush(Color.FromRgb(167, 139, 250)) : muted;
            }
            if (IconPomoChevronPath != null)
            {
                IconPomoChevronPath.Stroke = muted;
            }
        }

        private void BtnSetDockPosition_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string pos && !string.IsNullOrWhiteSpace(pos))
            {
                SetTimerDockPosition(pos);
            }
        }

        private void SetTimerDockPosition(string dockPos, bool saveToDisk = true)
        {
            string normalized = dockPos switch
            {
                "bottom-left" => "bottom-left",
                "top-bar" or "docked" => "top-bar",
                _ => "bottom-right"
            };
            _state.TimerDock = normalized;
            if (saveToDisk) SaveState(countActivity: false);
            ApplyTimerDockPosition(saveToDisk: false);

            string label = normalized switch
            {
                "bottom-left" => "Focus Timer moved to Bottom Left ↙",
                "top-bar" => "Focus Timer moved to Top Bar ⬆",
                _ => "Focus Timer moved to Bottom Right ↘"
            };
            ShowToast(label);
        }

        private void ApplyTimerDockPosition(bool saveToDisk = true)
        {
            if (_state == null || FloatingPomodoroWidget == null || NavTimerPill == null) return;

            string dock = _state.TimerDock ?? "bottom-right";
            if (dock == "docked") dock = "top-bar";
            if (dock != "bottom-left" && dock != "bottom-right" && dock != "top-bar") dock = "bottom-right";
            _state.TimerDock = dock;
            if (saveToDisk) SaveState(countActivity: false);

            if (PomoDragTransform != null)
            {
                PomoDragTransform.X = 0;
                PomoDragTransform.Y = 0;
            }

            if (dock == "top-bar")
            {
                FloatingPomodoroWidget.Visibility = Visibility.Collapsed;
                NavTimerPill.Visibility = Visibility.Visible;
            }
            else
            {
                if (TopBarTimerDropdownGrid != null) TopBarTimerDropdownGrid.Visibility = Visibility.Collapsed;
                NavTimerPill.Visibility = Visibility.Collapsed;
                FloatingPomodoroWidget.Visibility = Visibility.Visible;

                if (dock == "bottom-left")
                {
                    FloatingPomodoroWidget.HorizontalAlignment = HorizontalAlignment.Left;
                    FloatingPomodoroWidget.VerticalAlignment = VerticalAlignment.Bottom;
                    FloatingPomodoroWidget.Margin = new Thickness(24, 0, 0, 24);
                }
                else
                {
                    FloatingPomodoroWidget.HorizontalAlignment = HorizontalAlignment.Right;
                    FloatingPomodoroWidget.VerticalAlignment = VerticalAlignment.Bottom;
                    FloatingPomodoroWidget.Margin = new Thickness(0, 0, 24, 24);
                }
            }

            UpdateTimerDockButtonsUi();
        }

        private void UpdateTimerDockButtonsUi()
        {
            string dock = _state?.TimerDock ?? "bottom-right";
            if (dock == "docked") dock = "top-bar";

            void StyleBtn(Button? b, bool active)
            {
                if (b == null) return;
                if (active)
                {
                    var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
                    g.GradientStops.Add(new GradientStop(Color.FromRgb(124, 92, 255), 0.0));
                    g.GradientStops.Add(new GradientStop(Color.FromRgb(99, 102, 241), 1.0));
                    b.Background = g;
                    b.Foreground = Brushes.White;
                }
                else
                {
                    b.Background = Brushes.Transparent;
                    b.Foreground = _isDarkMode
                        ? new SolidColorBrush(Color.FromRgb(148, 163, 184))
                        : new SolidColorBrush(Color.FromRgb(100, 116, 139));
                }
            }

            StyleBtn(BtnDockLeft, dock == "bottom-left");
            StyleBtn(BtnDockTop, dock == "top-bar");
            StyleBtn(BtnDockRight, dock == "bottom-right");

            if (BtnDropdownDockLeft != null && BtnDropdownDockTop != null && BtnDropdownDockRight != null)
            {
                var brandStyle = (Style)FindResource("BrandButtonStyle");
                var secStyle = (Style)FindResource("SecondaryButtonStyle");
                BtnDropdownDockLeft.Style = (dock == "bottom-left") ? brandStyle : secStyle;
                BtnDropdownDockTop.Style = (dock == "top-bar") ? brandStyle : secStyle;
                BtnDropdownDockRight.Style = (dock == "bottom-right") ? brandStyle : secStyle;
            }
        }

        private void NavTimerPill_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (TopBarTimerDropdownGrid == null) return;
            TopBarTimerDropdownGrid.Visibility = (TopBarTimerDropdownGrid.Visibility == Visibility.Visible)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void BtnCloseTopBarTimerDropdown_Click(object sender, RoutedEventArgs e)
        {
            if (TopBarTimerDropdownGrid != null) TopBarTimerDropdownGrid.Visibility = Visibility.Collapsed;
        }

        private void TopBarTimerBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (TopBarTimerDropdownGrid != null) TopBarTimerDropdownGrid.Visibility = Visibility.Collapsed;
        }

        private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T parent) return parent;
                child = VisualTreeHelper.GetParent(child);
            }
            return null;
        }

        private void PomoHeaderRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (FindVisualParent<Button>(e.OriginalSource as DependencyObject) != null) return;
            _pomoMouseDown = true;
            _isDraggingPomo = false;
            _pomoDragStartPoint = e.GetPosition(RootMainGrid);
        }

        private void PomoHeaderRow_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_pomoMouseDown || e.LeftButton != MouseButtonState.Pressed || RootMainGrid == null) return;

            Point current = e.GetPosition(RootMainGrid);
            double dx = current.X - _pomoDragStartPoint.X;
            double dy = current.Y - _pomoDragStartPoint.Y;

            if (!_isDraggingPomo)
            {
                if (Math.Sqrt(dx * dx + dy * dy) < 12) return;
                _isDraggingPomo = true;
                PomoHeaderRow.CaptureMouse();
                if (TimerSnapOverlayGrid != null) TimerSnapOverlayGrid.Visibility = Visibility.Visible;
            }

            if (PomoDragTransform != null)
            {
                PomoDragTransform.X = dx;
                PomoDragTransform.Y = dy;
            }

            double w = RootMainGrid.ActualWidth > 0 ? RootMainGrid.ActualWidth : 1440;
            double h = RootMainGrid.ActualHeight > 0 ? RootMainGrid.ActualHeight : 900;

            _pomoDragHoverZone = current.Y < h * 0.30
                ? "top-bar"
                : (current.X < w * 0.45 ? "bottom-left" : "bottom-right");

            void HighlightZone(Border? bd, bool hot)
            {
                if (bd == null) return;
                bd.Background = hot ? new SolidColorBrush(Color.FromArgb(95, 124, 92, 255)) : new SolidColorBrush(Color.FromArgb(24, 124, 92, 255));
                bd.BorderBrush = hot ? new SolidColorBrush(Color.FromRgb(34, 211, 238)) : new SolidColorBrush(Color.FromArgb(100, 124, 92, 255));
            }
            HighlightZone(SnapZoneTop, _pomoDragHoverZone == "top-bar");
            HighlightZone(SnapZoneBottomLeft, _pomoDragHoverZone == "bottom-left");
            HighlightZone(SnapZoneBottomRight, _pomoDragHoverZone == "bottom-right");
        }

        private void PomoHeaderRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_pomoMouseDown) return;
            _pomoMouseDown = false;
            bool wasDragging = _isDraggingPomo;
            _isDraggingPomo = false;

            if (PomoHeaderRow.IsMouseCaptured) PomoHeaderRow.ReleaseMouseCapture();
            if (TimerSnapOverlayGrid != null) TimerSnapOverlayGrid.Visibility = Visibility.Collapsed;
            if (PomoDragTransform != null)
            {
                PomoDragTransform.X = 0;
                PomoDragTransform.Y = 0;
            }

            if (wasDragging)
            {
                SetTimerDockPosition(_pomoDragHoverZone);
            }
        }

        // ================= RETRACTABLE NAVIGATION DRAWER HANDLERS =================

        private void BtnNavMenu_Click(object sender, RoutedEventArgs e)
        {
            AppNavigationDrawerGrid.Visibility = Visibility.Visible;
        }

        private void BtnCloseNavDrawer_Click(object sender, RoutedEventArgs e)
        {
            AppNavigationDrawerGrid.Visibility = Visibility.Collapsed;
        }

        private void NavDrawerBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
        {
            AppNavigationDrawerGrid.Visibility = Visibility.Collapsed;
        }

        private void BtnDrawerSubjects_Click(object sender, RoutedEventArgs e)
        {
            AppNavigationDrawerGrid.Visibility = Visibility.Collapsed;
            GoToHome();
        }

        private void BtnDrawerTimer_Click(object sender, RoutedEventArgs e)
        {
            AppNavigationDrawerGrid.Visibility = Visibility.Collapsed;
            if (_state?.TimerDock == "top-bar")
            {
                if (TopBarTimerDropdownGrid != null)
                {
                    TopBarTimerDropdownGrid.Visibility = (TopBarTimerDropdownGrid.Visibility == Visibility.Visible) ? Visibility.Collapsed : Visibility.Visible;
                }
            }
            else
            {
                FloatingPomodoroWidget.Visibility = (FloatingPomodoroWidget.Visibility == Visibility.Visible) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private void BtnDrawerZen_Click(object sender, RoutedEventArgs e)
        {
            AppNavigationDrawerGrid.Visibility = Visibility.Collapsed;
            BtnNavZen_Click(sender, e);
        }

        private void BtnDrawerMixer_Click(object sender, RoutedEventArgs e)
        {
            AppNavigationDrawerGrid.Visibility = Visibility.Collapsed;
            BtnNavSoundMixer_Click(sender, e);
        }

        private void BtnDrawerTheme_Click(object sender, RoutedEventArgs e)
        {
            BtnNavTheme_Click(sender, e);
        }

        private void BtnDrawerSettings_Click(object sender, RoutedEventArgs e)
        {
            AppNavigationDrawerGrid.Visibility = Visibility.Collapsed;
            SettingsModalGrid.Visibility = Visibility.Visible;
        }

        private void BtnDrawerAddSubject_Click(object sender, RoutedEventArgs e)
        {
            AppNavigationDrawerGrid.Visibility = Visibility.Collapsed;
            BtnOpenAddSubjectModal_Click(sender, e);
        }

        private void BtnToggleDrawerArchived_Click(object sender, RoutedEventArgs e)
        {
            bool isVis = (DrawerArchivedStackPanel.Visibility == Visibility.Visible);
            DrawerArchivedStackPanel.Visibility = isVis ? Visibility.Collapsed : Visibility.Visible;
            TxtDrawerArchivedChevron.Text = isVis ? "▼" : "▲";
        }

        // ================= NAVIGATION & VIEW SWITCHING =================

        private void BtnBrandHome_Click(object sender, RoutedEventArgs e)
        {
            AppNavigationDrawerGrid.Visibility = (AppNavigationDrawerGrid.Visibility == Visibility.Visible)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void BtnBackToHome_Click(object sender, RoutedEventArgs e)
        {
            GoToHome();
        }

        private void GoToHome()
        {
            _currentSubject = null;
            SubjectDetailGrid.Visibility = Visibility.Collapsed;
            HomeViewScrollViewer.Visibility = Visibility.Visible;
            UpdateDashboardStats();
            RenderSubjects();
        }

        private void OpenSubject(SubjectItem subject)
        {
            _currentSubject = subject;
            HomeViewScrollViewer.Visibility = Visibility.Collapsed;
            SubjectDetailGrid.Visibility = Visibility.Visible;

            TxtActiveSubjectTitle.Text = subject.Name;
            TxtSubjectDetailIcon.Text = CleanIconText(subject.Icon);
            BdSubjectIconBox.Background = ParseColorBrush(subject.Color);

            // Default to Files tab
            SwitchWorkspaceTab("Files");
            UpdateSubjectDetailStats();
        }

        private void UpdateSubjectDetailStats()
        {
            if (_currentSubject == null) return;
            string subId = _currentSubject.Id;

            int pdfs = _state.Pdfs.Count(p => p.SubjectId == subId);
            int vids = _state.Videos.Count(v => v.SubjectId == subId);
            int recs = _state.Recordings.Count(r => r.SubjectId == subId);
            int notes = _state.Notes.Count(n => n.SubjectId == subId);
            int tasks = _state.Tasks.Count(t => t.SubjectId == subId);
            int links = _state.Links.Count(l => l.SubjectId == subId);
            int deads = _state.Deadlines.Count(d => d.SubjectId == subId);
            int cards = _state.Flashcards.Count(c => c.SubjectId == subId);

            TxtActiveSubjectStats.Text = $"{pdfs} Files • {vids} Videos • {recs} Recordings • {tasks} Tasks • {deads} Deadlines";

            BadgeTabFiles.Text = pdfs.ToString();
            BadgeTabVideos.Text = vids.ToString();
            BadgeTabRecordings.Text = recs.ToString();
            BadgeTabNotes.Text = notes.ToString();
            BadgeTabTasks.Text = tasks.ToString();
            BadgeTabLinks.Text = links.ToString();
            BadgeTabDeadlines.Text = deads.ToString();
            BadgeTabFlashcards.Text = cards.ToString();
        }

        private void TabBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tabName)
            {
                SwitchWorkspaceTab(tabName);
            }
        }

        private void SwitchWorkspaceTab(string tabName)
        {
            _activeTab = tabName;

            // Hide all sub-workspaces
            TabFilesWorkspace.Visibility = Visibility.Collapsed;
            TabSplitWorkspace.Visibility = Visibility.Collapsed;
            TabVideosWorkspace.Visibility = Visibility.Collapsed;
            TabRecordingsWorkspace.Visibility = Visibility.Collapsed;
            TabNotesWorkspace.Visibility = Visibility.Collapsed;
            TabTasksWorkspace.Visibility = Visibility.Collapsed;
            TabLinksWorkspace.Visibility = Visibility.Collapsed;
            TabDeadlinesWorkspace.Visibility = Visibility.Collapsed;
            TabFlashcardsWorkspace.Visibility = Visibility.Collapsed;
            TabWhiteboardWorkspace.Visibility = Visibility.Collapsed;

            // Highlight buttons
            var buttons = new[] { TabBtnFiles, TabBtnVideos, TabBtnRecordings, TabBtnNotes, TabBtnTasks, TabBtnLinks, TabBtnDeadlines, TabBtnFlashcards, TabBtnWhiteboard };
            foreach (var b in buttons)
            {
                b.Style = (string.Equals(b.Tag as string, tabName, StringComparison.OrdinalIgnoreCase))
                    ? (Style)FindResource("BrandButtonStyle")
                    : (Style)FindResource("SecondaryButtonStyle");
            }

            // Show active workspace
            switch (tabName)
            {
                case "Files":
                    TabFilesWorkspace.Visibility = Visibility.Visible;
                    RenderFoldersAndFiles();
                    break;
                case "Split":
                    TabSplitWorkspace.Visibility = Visibility.Visible;
                    RenderSplitScreen();
                    break;
                case "Videos":
                    TabVideosWorkspace.Visibility = Visibility.Visible;
                    RenderVideosList();
                    break;
                case "Recordings":
                    TabRecordingsWorkspace.Visibility = Visibility.Visible;
                    RenderRecordingsList();
                    break;
                case "Notes":
                    TabNotesWorkspace.Visibility = Visibility.Visible;
                    LoadSubjectNotes();
                    break;
                case "Tasks":
                    TabTasksWorkspace.Visibility = Visibility.Visible;
                    RenderTasksList();
                    break;
                case "Links":
                    TabLinksWorkspace.Visibility = Visibility.Visible;
                    RenderLinksList();
                    break;
                case "Deadlines":
                    TabDeadlinesWorkspace.Visibility = Visibility.Visible;
                    RenderDeadlinesList();
                    break;
                case "Flashcards":
                    TabFlashcardsWorkspace.Visibility = Visibility.Visible;
                    RenderFlashcards();
                    break;
                case "Whiteboard":
                    TabWhiteboardWorkspace.Visibility = Visibility.Visible;
                    break;
            }
        }

        // ================= SUBJECTS DASHBOARD RENDERING =================

        private void RenderSubjects()
        {
            SubjectsWrapPanel.Children.Clear();

            // 1. Dash-bordered "Add Subject" Card
            var addCardGrid = new Grid
            {
                Width = 320,
                Height = 205,
                Margin = new Thickness(10),
                Cursor = Cursors.Hand
            };

            var dashedRect = new System.Windows.Shapes.Rectangle
            {
                RadiusX = 24,
                RadiusY = 24,
                Stroke = GetAddCardStrokeBrush(),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Fill = GetAddCardSurfaceBrush()
            };
            addCardGrid.Children.Add(dashedRect);

            var addSp = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var addCircle = new Border
            {
                Width = 58,
                Height = 58,
                CornerRadius = new CornerRadius(29),
                Background = _isDarkMode ? new SolidColorBrush(Color.FromArgb(180, 26, 33, 66)) : new SolidColorBrush(Color.FromRgb(238, 242, 246)),
                Margin = new Thickness(0, 0, 0, 12),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var plusTxt = new TextBlock
            {
                Text = "+",
                FontSize = 26,
                FontWeight = FontWeights.Bold,
                Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(148, 163, 200)) : new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            addCircle.Child = plusTxt;
            addSp.Children.Add(addCircle);

            var addLbl = new TextBlock
            {
                Text = "Add Subject",
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(148, 163, 200)) : new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            addSp.Children.Add(addLbl);

            addCardGrid.Children.Add(addSp);

            addCardGrid.MouseEnter += (s, e) =>
            {
                dashedRect.Stroke = new SolidColorBrush(Color.FromRgb(124, 92, 255));
                dashedRect.Fill = GetAddCardHoverSurfaceBrush();
                addCircle.Background = _isDarkMode ? new SolidColorBrush(Color.FromArgb(220, 40, 48, 92)) : new SolidColorBrush(Color.FromRgb(226, 232, 240));
                addLbl.Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(248, 250, 252)) : new SolidColorBrush(Color.FromRgb(15, 23, 42));
                plusTxt.Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(248, 250, 252)) : new SolidColorBrush(Color.FromRgb(15, 23, 42));
            };
            addCardGrid.MouseLeave += (s, e) =>
            {
                dashedRect.Stroke = GetAddCardStrokeBrush();
                dashedRect.Fill = GetAddCardSurfaceBrush();
                addCircle.Background = _isDarkMode ? new SolidColorBrush(Color.FromArgb(180, 26, 33, 66)) : new SolidColorBrush(Color.FromRgb(238, 242, 246));
                addLbl.Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(148, 163, 200)) : new SolidColorBrush(Color.FromRgb(100, 116, 139));
                plusTxt.Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(148, 163, 200)) : new SolidColorBrush(Color.FromRgb(100, 116, 139));
            };
            addCardGrid.MouseDown += (s, e) => { BtnOpenAddSubjectModal_Click(s, e); };
            SubjectsWrapPanel.Children.Add(addCardGrid);

            // 2. Dynamic Course Cards
            var active = _state.Subjects.Where(s => !s.Archived).ToList();

            foreach (var sub in active)
            {
                var currentSub = sub;
                int fileCount = _state.Pdfs.Count(p => p.SubjectId == currentSub.Id);
                int deadlineCount = _state.Deadlines.Count(d => d.SubjectId == currentSub.Id);
                var subTasks = _state.Tasks.Where(t => t.SubjectId == currentSub.Id).ToList();
                int pct = subTasks.Count > 0 ? (int)Math.Round((double)subTasks.Count(t => t.Completed) / subTasks.Count * 100) : 0;

                Color accentBaseColor = ExtractPrimaryAccentColor(currentSub.Color);
                Brush restingCardBorder = GetCardBorderBrush();

                var cardBorder = new Border
                {
                    Width = 320,
                    Height = 205,
                    Background = GetCardSurfaceBrush(),
                    BorderBrush = restingCardBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(24),
                    Margin = new Thickness(10),
                    Cursor = Cursors.Hand,
                    Effect = _isDarkMode
                        ? new System.Windows.Media.Effects.DropShadowEffect
                        {
                            Color = Color.FromRgb(2, 6, 23),
                            BlurRadius = 22,
                            ShadowDepth = 6,
                            Opacity = 0.28
                        }
                        : new System.Windows.Media.Effects.DropShadowEffect
                        {
                            Color = Color.FromRgb(15, 23, 42),
                            BlurRadius = 18,
                            ShadowDepth = 3,
                            Opacity = 0.06
                        }
                };

                // Inner Grid clipped identically to Border's exact interior 23.5px corner radius so the top gradient stripe follows the rounded border 100% flush!
                var clipGrid = new Grid
                {
                    Background = Brushes.Transparent,
                    Clip = new RectangleGeometry(new Rect(0, 0, 318, 203), 23.5, 23.5)
                };
                clipGrid.SizeChanged += (s, ev) =>
                {
                    clipGrid.Clip = new RectangleGeometry(new Rect(0, 0, ev.NewSize.Width, ev.NewSize.Height), 23.5, 23.5);
                };

                // Ambient top-right radial glow (cleanly clipped inside card bounds)
                var cornerGlowBrush = new RadialGradientBrush
                {
                    GradientOrigin = new Point(0.5, 0.5),
                    Center = new Point(0.5, 0.5),
                    RadiusX = 0.5,
                    RadiusY = 0.5
                };
                cornerGlowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(72, accentBaseColor.R, accentBaseColor.G, accentBaseColor.B), 0.0));
                cornerGlowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(24, accentBaseColor.R, accentBaseColor.G, accentBaseColor.B), 0.55));
                cornerGlowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, accentBaseColor.R, accentBaseColor.G, accentBaseColor.B), 1.0));

                var cornerGlow = new Ellipse
                {
                    Width = 175,
                    Height = 175,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, -56, -52, 0),
                    Fill = cornerGlowBrush,
                    Opacity = _isDarkMode ? 0.50 : 0.35,
                    IsHitTestVisible = false
                };
                clipGrid.Children.Add(cornerGlow);

                // Top Accent Stripe — full-bleed inside clipped interior so left/right curved ends are mathematically flush with Border
                var accentLine = new Border
                {
                    Height = 5,
                    VerticalAlignment = VerticalAlignment.Top,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Background = ParseColorBrush(currentSub.Color, horizontal: true),
                    IsHitTestVisible = false
                };
                clipGrid.Children.Add(accentLine);

                // Card Interior Container
                var innerSp = new StackPanel { Margin = new Thickness(18, 19, 18, 14) };

                // Header with Icon & Title
                var headerGrid = new Grid();
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var iconBox = new Border
                {
                    Width = 50,
                    Height = 50,
                    CornerRadius = new CornerRadius(15),
                    Background = ParseColorBrush(currentSub.Color, horizontal: false),
                    Margin = new Thickness(0, 0, 12, 0),
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        Color = accentBaseColor,
                        BlurRadius = 12,
                        ShadowDepth = 2,
                        Opacity = 0.35
                    }
                };
                var iconText = new TextBlock
                {
                    Text = CleanIconText(currentSub.Icon),
                    FontSize = 22,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                iconBox.Child = iconText;
                Grid.SetColumn(iconBox, 0);
                headerGrid.Children.Add(iconBox);

                var titleText = new TextBlock
                {
                    Text = currentSub.Name,
                    FontWeight = FontWeights.Bold,
                    FontSize = 17,
                    Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(248, 250, 252)) : new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 46,
                    MaxWidth = 220,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(titleText, 1);
                headerGrid.Children.Add(titleText);

                innerSp.Children.Add(headerGrid);

                // Counters Pill: 📁 {n} Files  |  📅 {n} Events
                var statsBorder = new Border
                {
                    Background = GetSubtleSurfaceBrush(),
                    BorderBrush = restingCardBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 14, 0, 14)
                };
                var statsGrid = new Grid();
                statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var filesTxt = new TextBlock
                {
                    Text = $"📁 {fileCount} Files",
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(56, 189, 248)) : new SolidColorBrush(Color.FromRgb(2, 132, 199))
                };
                Grid.SetColumn(filesTxt, 0);
                statsGrid.Children.Add(filesTxt);

                var eventsTxt = new TextBlock
                {
                    Text = $"📅 {deadlineCount} Events",
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(192, 132, 252)) : new SolidColorBrush(Color.FromRgb(124, 58, 237)),
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                Grid.SetColumn(eventsTxt, 1);
                statsGrid.Children.Add(eventsTxt);

                statsBorder.Child = statsGrid;
                innerSp.Children.Add(statsBorder);

                // Progress Bar Section
                var progGrid = new Grid();
                progGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                progGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var pctTxt = new TextBlock
                {
                    Text = $"{pct}%",
                    FontSize = 11.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = (pct == 100)
                        ? new SolidColorBrush(Color.FromRgb(16, 185, 129))
                        : (_isDarkMode ? new SolidColorBrush(Color.FromRgb(138, 149, 183)) : new SolidColorBrush(Color.FromRgb(100, 116, 139))),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0)
                };
                Grid.SetColumn(pctTxt, 0);
                progGrid.Children.Add(pctTxt);

                var pbTrack = new Border
                {
                    Height = 6.5,
                    Background = _isDarkMode ? new SolidColorBrush(Color.FromArgb(145, 30, 38, 70)) : new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                    CornerRadius = new CornerRadius(3.25),
                    ClipToBounds = true,
                    VerticalAlignment = VerticalAlignment.Center
                };
                double fillWidth = (320 - 36 - 44) * (pct / 100.0);
                var pbFill = new Border
                {
                    Height = 6.5,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = Math.Max(0, fillWidth),
                    Background = ParseColorBrush(currentSub.Color, horizontal: true),
                    CornerRadius = new CornerRadius(3.25)
                };
                pbTrack.Child = pbFill;
                Grid.SetColumn(pbTrack, 1);
                progGrid.Children.Add(pbTrack);

                innerSp.Children.Add(progGrid);
                clipGrid.Children.Add(innerSp);

                // Floating Top-Right Hover Actions (Archive, Edit, Delete) inside clipGrid
                var actionsStack = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 12, 12, 0),
                    Visibility = Visibility.Collapsed,
                    Opacity = 0.0
                };
                Panel.SetZIndex(actionsStack, 5);

                var btnArchive = new Button
                {
                    Content = "📦",
                    Width = 28,
                    Height = 28,
                    FontSize = 11.5,
                    Background = _isDarkMode ? new SolidColorBrush(Color.FromArgb(220, 42, 33, 24)) : new SolidColorBrush(Color.FromRgb(254, 243, 199)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(120, 245, 158, 11)),
                    Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11)),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 0, 4, 0),
                    Cursor = Cursors.Hand,
                    ToolTip = "Archive course"
                };
                btnArchive.Template = CreatePillActionButtonTemplate(14);
                btnArchive.Click += (s, e) =>
                {
                    e.Handled = true;
                    currentSub.Archived = true;
                    SaveState();
                    RenderSubjects();
                    RenderArchivedSubjects();
                    ShowToast("Course archived 📦");
                };
                actionsStack.Children.Add(btnArchive);

                var btnEdit = new Button
                {
                    Content = "✏️",
                    Width = 28,
                    Height = 28,
                    FontSize = 11.5,
                    Background = _isDarkMode ? new SolidColorBrush(Color.FromArgb(220, 28, 35, 68)) : new SolidColorBrush(Color.FromRgb(241, 245, 249)),
                    BorderBrush = _isDarkMode ? new SolidColorBrush(Color.FromArgb(120, 129, 140, 248)) : new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                    Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(226, 232, 240)) : new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 0, 4, 0),
                    Cursor = Cursors.Hand,
                    ToolTip = "Edit course details"
                };
                btnEdit.Template = CreatePillActionButtonTemplate(14);
                btnEdit.Click += (s, e) =>
                {
                    e.Handled = true;
                    _editingSubjectId = currentSub.Id;
                    _selectedModalIcon = currentSub.Icon;
                    _selectedModalColor = currentSub.Color;
                    TxtSubjectModalHeader.Text = "Edit Course";
                    TxtModalSubjectName.Text = currentSub.Name;
                    InitSubjectModalPickers();
                    SubjectModalGrid.Visibility = Visibility.Visible;
                };
                actionsStack.Children.Add(btnEdit);

                var delBtn = new Button
                {
                    Content = "🗑",
                    Width = 28,
                    Height = 28,
                    FontSize = 11.5,
                    Background = _isDarkMode ? new SolidColorBrush(Color.FromArgb(220, 56, 22, 36)) : new SolidColorBrush(Color.FromRgb(255, 228, 230)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(140, 244, 63, 94)),
                    Foreground = new SolidColorBrush(Color.FromRgb(244, 63, 94)),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(0),
                    Cursor = Cursors.Hand,
                    ToolTip = "Delete course"
                };
                delBtn.Template = CreatePillActionButtonTemplate(14);
                delBtn.Click += (s, e) =>
                {
                    e.Handled = true;
                    if (MessageBox.Show($"Permanently delete course '{currentSub.Name}'?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    {
                        _state.Subjects.Remove(currentSub);
                        SaveState();
                        RenderSubjects();
                        ShowToast("Course deleted 🗑️");
                    }
                };
                actionsStack.Children.Add(delBtn);

                clipGrid.Children.Add(actionsStack);
                cardBorder.Child = clipGrid;

                // Smooth hover effects
                cardBorder.MouseEnter += (s, e) =>
                {
                    actionsStack.Visibility = Visibility.Visible;
                    actionsStack.Opacity = 1.0;
                    cornerGlow.Opacity = _isDarkMode ? 0.90 : 0.65;
                    cardBorder.BorderBrush = new SolidColorBrush(accentBaseColor);
                };
                cardBorder.MouseLeave += (s, e) =>
                {
                    actionsStack.Visibility = Visibility.Collapsed;
                    actionsStack.Opacity = 0.0;
                    cornerGlow.Opacity = _isDarkMode ? 0.50 : 0.35;
                    cardBorder.BorderBrush = restingCardBorder;
                };
                cardBorder.MouseDown += (s, e) => { OpenSubject(currentSub); };

                SubjectsWrapPanel.Children.Add(cardBorder);
            }
        }

        private static ControlTemplate CreatePillActionButtonTemplate(double radius)
        {
            var template = new ControlTemplate(typeof(Button));
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            borderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            borderFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));

            var cpFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            cpFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cpFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.AppendChild(cpFactory);

            template.VisualTree = borderFactory;
            return template;
        }

        private void RenderArchivedSubjects()
        {
            if (DrawerArchivedStackPanel == null) return;
            DrawerArchivedStackPanel.Children.Clear();
            var archived = _state.Subjects.Where(s => s.Archived).ToList();
            if (TxtDrawerArchivedBadge != null) TxtDrawerArchivedBadge.Text = $"({archived.Count})";

            if (archived.Count == 0)
            {
                var emptyTxt = new TextBlock
                {
                    Text = "No archived courses",
                    FontSize = 11.5,
                    Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(104, 114, 150)) : new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(4, 4, 4, 4)
                };
                DrawerArchivedStackPanel.Children.Add(emptyTxt);
                return;
            }

            foreach (var sub in archived)
            {
                var currentSub = sub;
                var card = new Border
                {
                    Background = _isDarkMode ? new SolidColorBrush(Color.FromRgb(17, 21, 42)) : new SolidColorBrush(Color.FromRgb(241, 245, 249)),
                    BorderBrush = _isDarkMode ? new SolidColorBrush(Color.FromRgb(31, 38, 72)) : new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(10, 8, 10, 8)
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var iconBox = new Border
                {
                    Width = 32,
                    Height = 32,
                    CornerRadius = new CornerRadius(8),
                    Background = ParseColorBrush(currentSub.Color),
                    Margin = new Thickness(0, 0, 10, 0)
                };
                iconBox.Child = new TextBlock
                {
                    Text = CleanIconText(currentSub.Icon),
                    FontSize = 14,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(iconBox, 0);
                grid.Children.Add(iconBox);

                var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                titleStack.Children.Add(new TextBlock
                {
                    Text = currentSub.Name,
                    FontWeight = FontWeights.Bold,
                    FontSize = 12.5,
                    Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(244, 246, 255)) : new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                Grid.SetColumn(titleStack, 1);
                grid.Children.Add(titleStack);

                var btnStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

                var restoreBtn = new Button
                {
                    Content = "Restore",
                    FontSize = 10.5,
                    FontWeight = FontWeights.Bold,
                    Style = (Style)FindResource("SecondaryButtonStyle"),
                    Padding = new Thickness(8, 3, 8, 3),
                    Margin = new Thickness(0, 0, 4, 0),
                    ToolTip = "Restore course to active workspace"
                };
                restoreBtn.Click += (s, e) =>
                {
                    currentSub.Archived = false;
                    SaveState();
                    RenderSubjects();
                    RenderArchivedSubjects();
                    ShowToast($"Restored '{currentSub.Name}' to workspace 🚀");
                };
                btnStack.Children.Add(restoreBtn);

                var delBtn = new Button
                {
                    Content = "🗑",
                    FontSize = 11,
                    Style = (Style)FindResource("SecondaryButtonStyle"),
                    Padding = new Thickness(6, 3, 6, 3),
                    ToolTip = "Permanently delete"
                };
                delBtn.Click += (s, e) =>
                {
                    if (MessageBox.Show($"Permanently delete archived course '{currentSub.Name}'?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    {
                        _state.Subjects.Remove(currentSub);
                        SaveState();
                        RenderSubjects();
                        RenderArchivedSubjects();
                        ShowToast($"Deleted '{currentSub.Name}' 🗑️");
                    }
                };
                btnStack.Children.Add(delBtn);

                Grid.SetColumn(btnStack, 2);
                grid.Children.Add(btnStack);

                card.Child = grid;
                DrawerArchivedStackPanel.Children.Add(card);
            }
        }

        // ================= FOLDERS & FILES WORKSPACE =================

        private void RenderFoldersAndFiles()
        {
            FoldersStackPanel.Children.Clear();
            if (_currentSubject == null) return;

            string filter = TxtFilterFiles.Text.Trim().ToLowerInvariant();

            // Default folders if none
            if (_currentSubject.Folders == null || _currentSubject.Folders.Count == 0)
            {
                _currentSubject.Folders = new List<string> { "Lectures", "Tutorials", "Assignments", "Quizzes", "Labs", "References" };
            }

            foreach (var folder in _currentSubject.Folders)
            {
                string folderName = folder;
                string folderKey = $"{_currentSubject.Id}_{folderName}";
                var filesInFolder = _state.Pdfs.Where(p => p.SubjectId == _currentSubject.Id &&
                    (string.Equals(p.Folder, folderName, StringComparison.OrdinalIgnoreCase) ||
                     (string.IsNullOrEmpty(p.Folder) && folderName == "Lectures"))).ToList();

                if (!string.IsNullOrEmpty(filter))
                {
                    filesInFolder = filesInFolder.Where(f => (f.OriginalName ?? "").ToLowerInvariant().Contains(filter)).ToList();
                }

                bool isExpanded = !_collapsedFolderKeys.Contains(folderKey);

                var folderContainer = new Border
                {
                    Background = GetCardSurfaceBrush(),
                    BorderBrush = GetCardBorderBrush(),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(16),
                    Margin = new Thickness(0, 0, 0, 14),
                    Padding = new Thickness(16)
                };

                var sp = new StackPanel();

                // Folder Header
                var headerGrid = new Grid { Cursor = Cursors.Hand };
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var leftStack = new StackPanel { Orientation = Orientation.Horizontal };
                var folderIcon = new TextBlock
                {
                    Text = isExpanded ? "📂" : "📁",
                    FontSize = 18,
                    Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11)),
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var folderTitle = new TextBlock
                {
                    Text = folderName,
                    FontSize = 15,
                    FontWeight = FontWeights.Bold,
                    Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(244, 246, 255)) : new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var countBadge = new Border
                {
                    Background = _isDarkMode ? new SolidColorBrush(Color.FromRgb(32, 39, 74)) : new SolidColorBrush(Color.FromRgb(241, 245, 249)),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(8, 2, 8, 2),
                    Margin = new Thickness(12, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                countBadge.Child = new TextBlock
                {
                    Text = $"{filesInFolder.Count} files",
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(154, 163, 194)) : new SolidColorBrush(Color.FromRgb(100, 116, 139))
                };

                leftStack.Children.Add(folderIcon);
                leftStack.Children.Add(folderTitle);
                leftStack.Children.Add(countBadge);
                Grid.SetColumn(leftStack, 0);
                headerGrid.Children.Add(leftStack);

                // Right controls (Upload to folder, expand chevron)
                var rightStack = new StackPanel { Orientation = Orientation.Horizontal };
                var btnUploadToFolder = new Button
                {
                    Content = "☁️ Upload Here",
                    Style = (Style)FindResource("SecondaryButtonStyle"),
                    Padding = new Thickness(10, 4, 10, 4),
                    Margin = new Thickness(0, 0, 8, 0)
                };
                btnUploadToFolder.Click += (s, e) =>
                {
                    e.Handled = true;
                    UploadFilesDirectly(folderName);
                };
                var chevronTxt = new TextBlock
                {
                    Text = isExpanded ? "▲" : "▼",
                    FontSize = 12,
                    Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(154, 163, 194)) : new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                    VerticalAlignment = VerticalAlignment.Center
                };

                rightStack.Children.Add(btnUploadToFolder);
                rightStack.Children.Add(chevronTxt);
                Grid.SetColumn(rightStack, 1);
                headerGrid.Children.Add(rightStack);

                sp.Children.Add(headerGrid);

                // In-place Collapsible Content Container (avoids full reload and never affects other folders!)
                var filesContentContainer = new StackPanel
                {
                    Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed
                };

                headerGrid.MouseDown += (s, e) =>
                {
                    e.Handled = true;
                    string key = $"{_currentSubject.Id}_{folderName}";
                    bool willCollapse = (filesContentContainer.Visibility == Visibility.Visible);
                    if (willCollapse)
                    {
                        filesContentContainer.Visibility = Visibility.Collapsed;
                        _collapsedFolderKeys.Add(key);
                        chevronTxt.Text = "▼";
                        folderIcon.Text = "📁";
                    }
                    else
                    {
                        filesContentContainer.Visibility = Visibility.Visible;
                        _collapsedFolderKeys.Remove(key);
                        chevronTxt.Text = "▲";
                        folderIcon.Text = "📂";
                    }
                };

                if (filesInFolder.Count == 0)
                {
                    var emptyBox = new Border
                    {
                        Background = GetSubtleSurfaceBrush(),
                        BorderBrush = GetCardBorderBrush(),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(12),
                        Padding = new Thickness(18),
                        Margin = new Thickness(0, 14, 0, 0)
                    };
                    emptyBox.Child = new TextBlock
                    {
                        Text = $"No files in {folderName}. Click 'Upload Here' to add lectures or assignments.",
                        FontSize = 12,
                        Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(104, 114, 150)) : new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                        TextAlignment = TextAlignment.Center
                    };
                    filesContentContainer.Children.Add(emptyBox);
                }
                else
                {
                    var filesWrap = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };

                    foreach (var file in filesInFolder)
                    {
                        var f = file;
                        var fileCard = new Border
                        {
                            Width = 230,
                            Background = GetSubtleSurfaceBrush(),
                            BorderBrush = GetCardBorderBrush(),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(14),
                            Padding = new Thickness(14),
                            Margin = new Thickness(0, 0, 12, 12)
                        };

                        var fsp = new StackPanel();
                        var iconEmoji = GetFileEmoji(f.OriginalName);

                        fsp.Children.Add(new TextBlock
                        {
                            Text = iconEmoji,
                            FontSize = 28,
                            Foreground = GetFileIconBrush(f.OriginalName),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Margin = new Thickness(0, 0, 0, 8)
                        });
                        fsp.Children.Add(new TextBlock
                        {
                            Text = f.OriginalName,
                            FontWeight = FontWeights.Bold,
                            FontSize = 13,
                            Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(244, 246, 255)) : new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                            TextAlignment = TextAlignment.Center,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            Margin = new Thickness(0, 0, 0, 12)
                        });

                        // Action buttons
                        var btnOpen = new Button
                        {
                            Content = "📖 Open",
                            Style = (Style)FindResource("BrandButtonStyle"),
                            Padding = new Thickness(0, 6, 0, 6),
                            Margin = new Thickness(0, 0, 0, 6)
                        };
                        btnOpen.Click += (s, e) => OpenStudyFile(f);
                        fsp.Children.Add(btnOpen);

                        var btnGrid = new Grid();
                        btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                        var btnSplit = new Button
                        {
                            Content = "◫ Split",
                            Style = (Style)FindResource("SecondaryButtonStyle"),
                            Padding = new Thickness(0, 4, 0, 4),
                            Margin = new Thickness(0, 0, 4, 0),
                            ToolTip = "Open in Split Screen with Notes"
                        };
                        btnSplit.Click += (s, e) =>
                        {
                            _splitDoc = f;
                            SwitchWorkspaceTab("Split");
                        };
                        Grid.SetColumn(btnSplit, 0);
                        btnGrid.Children.Add(btnSplit);

                        var btnDel = new Button
                        {
                            Content = "🗑",
                            Style = (Style)FindResource("SecondaryButtonStyle"),
                            Padding = new Thickness(0, 4, 0, 4),
                            Margin = new Thickness(4, 0, 0, 0),
                            ToolTip = "Delete file"
                        };
                        btnDel.Click += (s, e) =>
                        {
                            if (MessageBox.Show($"Delete file '{f.OriginalName}'?", "Confirm Delete", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                            {
                                _state.Pdfs.Remove(f);
                                _storage.DeleteStudyFile(f.Filename);
                                SaveState();
                                RenderFoldersAndFiles();
                                UpdateSubjectDetailStats();
                                ShowToast("File deleted! 🗑️");
                            }
                        };
                        Grid.SetColumn(btnDel, 1);
                        btnGrid.Children.Add(btnDel);

                        fsp.Children.Add(btnGrid);
                        fileCard.Child = fsp;
                        filesWrap.Children.Add(fileCard);
                    }

                    filesContentContainer.Children.Add(filesWrap);
                }

                sp.Children.Add(filesContentContainer);
                folderContainer.Child = sp;
                FoldersStackPanel.Children.Add(folderContainer);
            }
        }

        private void BtnToggleExpandAllFolders_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSubject?.Folders == null) return;

            bool anyCollapsed = _currentSubject.Folders.Any(f => _collapsedFolderKeys.Contains($"{_currentSubject.Id}_{f}"));
            if (anyCollapsed)
            {
                foreach (var f in _currentSubject.Folders)
                {
                    _collapsedFolderKeys.Remove($"{_currentSubject.Id}_{f}");
                }
                TxtExpandAllBtn.Text = "⊟ Collapse All";
            }
            else
            {
                foreach (var f in _currentSubject.Folders)
                {
                    _collapsedFolderKeys.Add($"{_currentSubject.Id}_{f}");
                }
                TxtExpandAllBtn.Text = "⊞ Expand All";
            }
            RenderFoldersAndFiles();
        }

        private void TxtFilterFiles_TextChanged(object sender, TextChangedEventArgs e)
        {
            TxtFilterFilesPlaceholder.Visibility = string.IsNullOrEmpty(TxtFilterFiles.Text) ? Visibility.Visible : Visibility.Collapsed;
            RenderFoldersAndFiles();
        }

        private void BtnPromptNewFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSubject == null) return;
            string input = Microsoft.VisualBasic.Interaction.InputBox("Enter new folder name (e.g. Labs, Practice Exams):", "New Folder", "Assignments");
            if (!string.IsNullOrWhiteSpace(input))
            {
                string clean = input.Trim();
                if (!_currentSubject.Folders.Contains(clean))
                {
                    _currentSubject.Folders.Add(clean);
                    _collapsedFolderKeys.Remove($"{_currentSubject.Id}_{clean}");
                    SaveState();
                    RenderFoldersAndFiles();
                    ShowToast($"Folder '{clean}' created! 📁");
                }
            }
        }

        private void BtnUploadFiles_Click(object sender, RoutedEventArgs e)
        {
            UploadFilesDirectly("Lectures");
        }

        private void UploadFilesDirectly(string targetFolder)
        {
            if (_currentSubject == null) return;

            var dlg = new OpenFileDialog
            {
                Multiselect = true,
                Title = $"Select files to upload into {targetFolder}",
                Filter = "All Supported Files|*.pdf;*.docx;*.pptx;*.xlsx;*.txt;*.png;*.jpg;*.zip|All Files (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true && dlg.FileNames.Length > 0)
            {
                foreach (var srcPath in dlg.FileNames)
                {
                    try
                    {
                        string safeName = Path.GetFileName(srcPath);
                        string savedDiskName = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{safeName}";
                        string destPath = _storage.GetStudyFilePath(savedDiskName);

                        File.Copy(srcPath, destPath, true);

                        _state.Pdfs.Add(new StudyFileItem
                        {
                            Id = "file_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "_" + _rand.Next(1000, 9999),
                            SubjectId = _currentSubject.Id,
                            OriginalName = safeName,
                            Filename = savedDiskName,
                            Folder = targetFolder
                        });
                    }
                    catch (Exception ex)
                    {
                        ShowToast($"Error uploading file: {ex.Message}");
                    }
                }

                _collapsedFolderKeys.Remove($"{_currentSubject.Id}_{targetFolder}");
                SaveState();
                RenderFoldersAndFiles();
                UpdateSubjectDetailStats();
                ShowToast($"{dlg.FileNames.Length} file(s) uploaded to {targetFolder}! ✅");
            }
        }

        private async void OpenStudyFile(StudyFileItem file)
        {
            string fullPath = _storage.GetStudyFilePath(file.Filename);
            if (!File.Exists(fullPath))
            {
                ShowToast($"File not found on disk: {file.OriginalName}");
                return;
            }

            if (string.Equals(_state.Settings?.PdfOpenMode, "external", StringComparison.OrdinalIgnoreCase) ||
                !file.Filename.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
                    ShowToast($"Opening {file.OriginalName}... 🚀");
                }
                catch (Exception ex)
                {
                    ShowToast($"Could not open file: {ex.Message}");
                }
                return;
            }

            await OpenInAppPdfReaderAsync(file, fullPath);
        }

        private static string BuildPdfViewerHtml(string pdfFrameSrc, bool darkMode)
        {
            string safeSrc = System.Net.WebUtility.HtmlEncode(pdfFrameSrc);
            string frameClass = darkMode ? "pdf-dark" : "";
            string bodyBg = darkMode ? "#0d0f17" : "#f1f5f9";
            return $@"<!DOCTYPE html>
<html>
<head>
<meta charset=""utf-8""/>
<style>
  html, body {{
    margin: 0;
    padding: 0;
    width: 100%;
    height: 100%;
    overflow: hidden;
    background: {bodyBg};
  }}
  #pdfFrame {{
    width: 100%;
    height: 100%;
    border: none;
    display: block;
    transition: filter 0.15s ease;
  }}
  #pdfFrame.pdf-dark {{
    filter: invert(0.91) hue-rotate(180deg) brightness(0.95) contrast(1.08);
    background-color: #121212;
  }}
</style>
<script>
  window.setPdfDarkMode = function(enabled) {{
    var f = document.getElementById('pdfFrame');
    if (f) f.classList.toggle('pdf-dark', !!enabled);
    if (document.body) document.body.style.background = enabled ? '#0d0f17' : '#f1f5f9';
  }};
</script>
</head>
<body>
  <iframe id=""pdfFrame"" class=""{frameClass}"" src=""{safeSrc}""></iframe>
</body>
</html>";
        }

        private void UpdatePdfDarkModeButtonUi()
        {
            if (GridPdfSunIcon != null) GridPdfSunIcon.Visibility = _isPdfDarkMode ? Visibility.Visible : Visibility.Collapsed;
            if (GridPdfMoonIcon != null) GridPdfMoonIcon.Visibility = _isPdfDarkMode ? Visibility.Collapsed : Visibility.Visible;
            if (TxtPdfDarkLabel != null) TxtPdfDarkLabel.Text = _isPdfDarkMode ? "Light Mode" : "Dark Mode";

            if (BtnPdfToggleDarkMode != null)
            {
                if (_isPdfDarkMode)
                {
                    BtnPdfToggleDarkMode.Background = new SolidColorBrush(Color.FromArgb(42, 245, 158, 11));
                    BtnPdfToggleDarkMode.BorderBrush = new SolidColorBrush(Color.FromArgb(145, 245, 158, 11));
                    BtnPdfToggleDarkMode.Foreground = new SolidColorBrush(Color.FromRgb(252, 211, 77));
                    BtnPdfToggleDarkMode.ToolTip = "Switch PDF paper to Light Mode";
                }
                else
                {
                    BtnPdfToggleDarkMode.ClearValue(Button.BackgroundProperty);
                    BtnPdfToggleDarkMode.ClearValue(Button.BorderBrushProperty);
                    BtnPdfToggleDarkMode.ClearValue(Button.ForegroundProperty);
                    BtnPdfToggleDarkMode.ToolTip = "Invert PDF paper to Dark Mode";
                }
            }
        }

        private async Task ApplyPdfDarkModeToWebViewAsync()
        {
            if (PdfWebView?.CoreWebView2 == null) return;
            try
            {
                // Keep Edge PDF chrome light before CSS filter so Dark Mode inverts toolbar & page consistently.
                PdfWebView.CoreWebView2.Profile.PreferredColorScheme =
                    Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme.Light;
                string flag = _isPdfDarkMode ? "true" : "false";
                await PdfWebView.CoreWebView2.ExecuteScriptAsync($"if (window.setPdfDarkMode) window.setPdfDarkMode({flag});");
            }
            catch { }
        }

        private void PdfWebView_WebResourceRequested(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebResourceRequestedEventArgs e)
        {
            if (_pdfWebViewEnv == null || string.IsNullOrEmpty(_activePdfFrameSrc)) return;
            if (!e.Request.Uri.StartsWith("https://studyhub.ui.local/", StringComparison.OrdinalIgnoreCase)) return;

            string html = BuildPdfViewerHtml(_activePdfFrameSrc, _isPdfDarkMode);
            var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(html));
            e.Response = _pdfWebViewEnv.CreateWebResourceResponse(
                ms,
                200,
                "OK",
                "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
        }

        private async void PdfWebView_NavigationCompleted(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
        {
            if (string.IsNullOrEmpty(_activePdfFrameSrc) || PdfViewerOverlayGrid.Visibility != Visibility.Visible) return;
            int navId = _pdfNavCounter;
            await ApplyPdfDarkModeToWebViewAsync();
            await Task.Delay(180);
            if (_pdfNavCounter == navId)
            {
                BdPdfLoader.Visibility = Visibility.Collapsed;
            }
        }

        private async Task OpenInAppPdfReaderAsync(StudyFileItem file, string fullPath)
        {
            _currentInAppPdfFile = file;
            _currentInAppPdfFullPath = fullPath;
            _isPdfDarkMode = _state?.Settings?.PdfDarkMode ?? _isPdfDarkMode;
            UpdatePdfDarkModeButtonUi();

            TxtPdfViewerTitle.Text = file.OriginalName;
            TxtPdfViewerSubject.Text = _currentSubject?.Name ?? "Course Document";

            PdfViewerOverlayGrid.Visibility = Visibility.Visible;
            BdPdfLoader.Visibility = Visibility.Visible;
            int navId = ++_pdfNavCounter;

            try
            {
                if (PdfWebView.CoreWebView2 == null)
                {
                    string dataFolder = _storage.WebViewDataDir;
                    _pdfWebViewEnv = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                        userDataFolder: dataFolder);
                    await PdfWebView.EnsureCoreWebView2Async(_pdfWebViewEnv);

                    PdfWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                    PdfWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                    PdfWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(6, 8, 20);

                    PdfWebView.CoreWebView2.AddWebResourceRequestedFilter(
                        "https://studyhub.ui.local/*",
                        Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
                    PdfWebView.CoreWebView2.WebResourceRequested += PdfWebView_WebResourceRequested;
                    PdfWebView.CoreWebView2.NavigationCompleted += PdfWebView_NavigationCompleted;
                }

                string pdfDir = Path.GetDirectoryName(fullPath) ?? _storage.FilesDir;
                string pdfFileName = Path.GetFileName(fullPath);

                PdfWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "studyhub.pdf.local",
                    pdfDir,
                    Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
                PdfWebView.CoreWebView2.Profile.PreferredColorScheme =
                    Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme.Light;

                _activePdfFrameSrc = $"https://studyhub.pdf.local/{Uri.EscapeDataString(pdfFileName)}#toolbar=1&navpanes=0";
                PdfWebView.CoreWebView2.Navigate($"https://studyhub.ui.local/__viewer.html?v={navId}");
            }
            catch (Exception ex)
            {
                ShowToast($"In-app PDF viewer error: {ex.Message}. Opening externally...");
                Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
                PdfViewerOverlayGrid.Visibility = Visibility.Collapsed;
                BdPdfLoader.Visibility = Visibility.Collapsed;
            }
            finally
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2200);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (_pdfNavCounter == navId) BdPdfLoader.Visibility = Visibility.Collapsed;
                    });
                });
            }
        }

        private void BtnClosePdfViewer_Click(object sender, RoutedEventArgs e)
        {
            ClosePdfViewer();
        }

        private void ClosePdfViewer()
        {
            _pdfNavCounter++;
            _activePdfFrameSrc = null;
            PdfViewerOverlayGrid.Visibility = Visibility.Collapsed;
            BdPdfLoader.Visibility = Visibility.Collapsed;
            try
            {
                PdfWebView.CoreWebView2?.Navigate("about:blank");
            }
            catch { }
            _currentInAppPdfFile = null;
            _currentInAppPdfFullPath = null;
        }

        private async void BtnPdfToggleDarkMode_Click(object sender, RoutedEventArgs e)
        {
            _isPdfDarkMode = !_isPdfDarkMode;
            if (_state?.Settings != null)
            {
                _state.Settings.PdfDarkMode = _isPdfDarkMode;
                SaveState(countActivity: false);
            }

            UpdatePdfDarkModeButtonUi();
            ShowToast(_isPdfDarkMode ? "PDF Dark Mode enabled 🌙" : "PDF Light Mode enabled ☀️");
            await ApplyPdfDarkModeToWebViewAsync();
        }

        private void BtnPdfSplitWithNotes_Click(object sender, RoutedEventArgs e)
        {
            if (_currentInAppPdfFile != null)
            {
                _splitDoc = _currentInAppPdfFile;
                ClosePdfViewer();
                SwitchWorkspaceTab("Split");
                ShowToast("Split screen mode activated! ◫");
            }
        }

        private void BtnPdfOpenExternal_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_currentInAppPdfFullPath) && File.Exists(_currentInAppPdfFullPath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(_currentInAppPdfFullPath) { UseShellExecute = true });
                    ShowToast("Opened in external application! 🚀");
                }
                catch (Exception ex)
                {
                    ShowToast($"Could not open external app: {ex.Message}");
                }
            }
        }

        private void BtnPdfDownload_Click(object sender, RoutedEventArgs e)
        {
            if (_currentInAppPdfFile == null || string.IsNullOrEmpty(_currentInAppPdfFullPath) || !File.Exists(_currentInAppPdfFullPath))
                return;

            var dlg = new SaveFileDialog
            {
                FileName = _currentInAppPdfFile.OriginalName,
                Filter = "PDF Document (*.pdf)|*.pdf|All Files (*.*)|*.*",
                Title = "Save Copy of PDF"
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    File.Copy(_currentInAppPdfFullPath, dlg.FileName, true);
                    ShowToast("PDF saved successfully! 💾");
                }
                catch (Exception ex)
                {
                    ShowToast($"Error saving file: {ex.Message}");
                }
            }
        }

        // ================= SPLIT SCREEN WORKSPACE =================

        private void RenderSplitScreen()
        {
            if (_splitDoc != null)
            {
                TxtSplitDocTitle.Text = _splitDoc.OriginalName;
            }
            else
            {
                TxtSplitDocTitle.Text = "No document selected";
            }

            // Load saved notes for this course
            LoadSubjectNotesToTextBox(TxtSplitNotes);
        }

        private void BtnOpenSplitDocExternal_Click(object sender, RoutedEventArgs e)
        {
            if (_splitDoc != null) OpenStudyFile(_splitDoc);
        }

        private void BtnSaveSplitNotes_Click(object sender, RoutedEventArgs e)
        {
            SaveNotesFromTextBox(TxtSplitNotes);
        }

        // ================= VIDEOS STUDIO =================

        private void RenderVideosList()
        {
            VideosListStackPanel.Children.Clear();
            if (_currentSubject == null) return;

            var vids = _state.Videos.Where(v => v.SubjectId == _currentSubject.Id).ToList();

            if (vids.Count == 0)
            {
                VideosListStackPanel.Children.Add(new TextBlock
                {
                    Text = "No video lectures added yet. Click '+ Add' to import lecture videos.",
                    FontSize = 12,
                    Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(104, 114, 150)) : new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            foreach (var v in vids)
            {
                var vidItem = v;
                var btn = new Button
                {
                    Style = (Style)FindResource("SecondaryButtonStyle"),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 0, 0, 6)
                };
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock
                {
                    Text = $"🎬 {vidItem.Title}",
                    FontWeight = FontWeights.Bold,
                    Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(244, 246, 255)) : new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                sp.Children.Add(new TextBlock
                {
                    Text = vidItem.Date,
                    FontSize = 10.5,
                    Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(154, 163, 194)) : new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                    Margin = new Thickness(0, 2, 0, 0)
                });
                btn.Content = sp;

                btn.Click += (s, e) =>
                {
                    string path = _storage.GetStudyFilePath(vidItem.Filename);
                    if (File.Exists(path))
                    {
                        StudioVideoPlayer.Source = new Uri(path, UriKind.Absolute);
                        StudioVideoPlayer.Play();
                        ShowToast($"Playing {vidItem.Title} ▶️");
                    }
                };
                VideosListStackPanel.Children.Add(btn);
            }
        }

        private void BtnUploadVideo_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSubject == null) return;
            var dlg = new OpenFileDialog
            {
                Title = "Select Lecture Video",
                Filter = "Video Files|*.mp4;*.mkv;*.avi;*.mov;*.wmv|All Files (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                string safe = Path.GetFileName(dlg.FileName);
                string diskName = $"vid_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{safe}";
                string dest = _storage.GetStudyFilePath(diskName);
                File.Copy(dlg.FileName, dest, true);

                _state.Videos.Add(new MediaTrackItem
                {
                    Id = "vid_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    SubjectId = _currentSubject.Id,
                    Title = safe,
                    Filename = diskName,
                    Date = DateTime.Now.ToString("yyyy-MM-dd")
                });

                SaveState();
                RenderVideosList();
                UpdateSubjectDetailStats();
                ShowToast("Video added! 🎬");
            }
        }

        private void BtnVideoPlay_Click(object sender, RoutedEventArgs e) { StudioVideoPlayer.Play(); }
        private void BtnVideoPause_Click(object sender, RoutedEventArgs e) { StudioVideoPlayer.Pause(); }
        private void BtnVideoStop_Click(object sender, RoutedEventArgs e) { StudioVideoPlayer.Stop(); }

        private void VideoSeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (StudioVideoPlayer.NaturalDuration.HasTimeSpan)
            {
                double total = StudioVideoPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                double target = (VideoSeekSlider.Value / 100.0) * total;
                StudioVideoPlayer.Position = TimeSpan.FromSeconds(target);
            }
        }

        // ================= RECORDINGS WORKSPACE =================

        private void RenderRecordingsList()
        {
            RecordingsListStackPanel.Children.Clear();
            if (_currentSubject == null) return;

            var recs = _state.Recordings.Where(r => r.SubjectId == _currentSubject.Id).ToList();
            if (recs.Count == 0)
            {
                RecordingsListStackPanel.Children.Add(new TextBlock
                {
                    Text = "No voice recordings yet. Click 'Import Audio' to add lecture recordings.",
                    FontSize = 12,
                    Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(104, 114, 150)) : new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            foreach (var r in recs)
            {
                var recItem = r;
                var btn = new Button
                {
                    Style = (Style)FindResource("SecondaryButtonStyle"),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 0, 0, 6)
                };
                btn.Content = $"🎙️ {recItem.Title} ({recItem.Date})";
                btn.Click += (s, e) =>
                {
                    string path = _storage.GetStudyFilePath(recItem.Filename);
                    if (File.Exists(path))
                    {
                        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                        ShowToast($"Playing {recItem.Title} 🎵");
                    }
                };
                RecordingsListStackPanel.Children.Add(btn);
            }
        }

        private void BtnImportRecordingFile_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSubject == null) return;
            var dlg = new OpenFileDialog
            {
                Title = "Select Lecture Audio Recording",
                Filter = "Audio Files|*.mp3;*.wav;*.m4a;*.flac;*.aac;*.wma|All Files (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                string title = string.IsNullOrWhiteSpace(TxtRecordTitle.Text) ? Path.GetFileNameWithoutExtension(dlg.FileName) : TxtRecordTitle.Text.Trim();
                string diskName = $"rec_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Path.GetFileName(dlg.FileName)}";
                string dest = _storage.GetStudyFilePath(diskName);
                File.Copy(dlg.FileName, dest, true);

                _state.Recordings.Add(new MediaTrackItem
                {
                    Id = "rec_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    SubjectId = _currentSubject.Id,
                    Title = title,
                    Filename = diskName,
                    Date = DateTime.Now.ToString("yyyy-MM-dd")
                });

                TxtRecordTitle.Text = "";
                SaveState();
                RenderRecordingsList();
                UpdateSubjectDetailStats();
                ShowToast("Voice recording added! 🎙️");
            }
        }

        // ================= NOTES WORKSPACE =================

        private void LoadSubjectNotes()
        {
            LoadSubjectNotesToTextBox(TxtSubjectNotes);
        }

        private void LoadSubjectNotesToTextBox(TextBox tb)
        {
            if (_currentSubject == null) return;
            var note = _state.Notes.Find(n => n.SubjectId == _currentSubject.Id);
            tb.Text = note != null ? (note.Text ?? "") : "";
            UpdateNotesStats();
        }

        private void SaveNotesFromTextBox(TextBox tb)
        {
            if (_currentSubject == null) return;
            var note = _state.Notes.Find(n => n.SubjectId == _currentSubject.Id);
            if (note == null)
            {
                note = new NoteItem
                {
                    Id = "note_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    SubjectId = _currentSubject.Id,
                    Date = DateTime.Now.ToString("yyyy-MM-dd")
                };
                _state.Notes.Add(note);
            }

            note.Text = tb.Text;
            note.Date = DateTime.Now.ToString("yyyy-MM-dd");
            SaveState();
            UpdateSubjectDetailStats();
            ShowToast("Notes saved successfully! 💾");
        }

        private void BtnSaveNotes_Click(object sender, RoutedEventArgs e)
        {
            SaveNotesFromTextBox(TxtSubjectNotes);
        }

        private void TxtSubjectNotes_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateNotesStats();
        }

        private void UpdateNotesStats()
        {
            string txt = TxtSubjectNotes.Text ?? "";
            int words = txt.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
            TxtNotesWordCount.Text = $"{words} words • {txt.Length} characters";
        }

        private void BtnExportNotesTxt_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSubject == null) return;
            var dlg = new SaveFileDialog
            {
                FileName = $"{_currentSubject.Name}_Notes.txt",
                Filter = "Text Documents (*.txt)|*.txt"
            };
            if (dlg.ShowDialog() == true)
            {
                File.WriteAllText(dlg.FileName, TxtSubjectNotes.Text);
                ShowToast("Notes exported to TXT! 📄");
            }
        }

        // ================= TASKS WORKSPACE =================

        private void RenderTasksList()
        {
            TasksListStackPanel.Children.Clear();
            if (_currentSubject == null) return;

            var tasks = _state.Tasks.Where(t => t.SubjectId == _currentSubject.Id).ToList();
            if (tasks.Count == 0)
            {
                TasksListStackPanel.Children.Add(new TextBlock
                {
                    Text = "No tasks yet! Type a task above and press Enter or 'Add Task'.",
                    FontSize = 12.5,
                    Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(104, 114, 150)) : new SolidColorBrush(Color.FromRgb(100, 116, 139))
                });
                return;
            }

            foreach (var task in tasks)
            {
                var t = task;
                var border = new Border
                {
                    Background = _isDarkMode ? new SolidColorBrush(Color.FromRgb(17, 21, 44)) : new SolidColorBrush(Color.FromRgb(248, 250, 252)),
                    BorderBrush = _isDarkMode ? new SolidColorBrush(Color.FromRgb(40, 48, 86)) : new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(14, 10, 14, 10),
                    Margin = new Thickness(0, 0, 0, 8)
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var cb = new CheckBox
                {
                    IsChecked = t.Completed,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0)
                };
                cb.Click += (s, e) =>
                {
                    t.Completed = cb.IsChecked ?? false;
                    SaveState();
                    RenderTasksList();
                    UpdateSubjectDetailStats();
                };
                Grid.SetColumn(cb, 0);
                grid.Children.Add(cb);

                var txt = new TextBlock
                {
                    Text = t.Text,
                    FontSize = 13.5,
                    Foreground = t.Completed
                        ? (_isDarkMode ? new SolidColorBrush(Color.FromRgb(104, 114, 150)) : new SolidColorBrush(Color.FromRgb(148, 163, 184)))
                        : (_isDarkMode ? new SolidColorBrush(Color.FromRgb(244, 246, 255)) : new SolidColorBrush(Color.FromRgb(15, 23, 42))),
                    TextDecorations = t.Completed ? TextDecorations.Strikethrough : null,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(txt, 1);
                grid.Children.Add(txt);

                var delBtn = new Button
                {
                    Content = "✕",
                    Style = (Style)FindResource("SecondaryButtonStyle"),
                    Padding = new Thickness(6, 2, 6, 2),
                    FontSize = 11
                };
                delBtn.Click += (s, e) =>
                {
                    _state.Tasks.Remove(t);
                    SaveState();
                    RenderTasksList();
                    UpdateSubjectDetailStats();
                };
                Grid.SetColumn(delBtn, 2);
                grid.Children.Add(delBtn);

                border.Child = grid;
                TasksListStackPanel.Children.Add(border);
            }
        }

        private void BtnAddTask_Click(object sender, RoutedEventArgs e)
        {
            AddTaskFromInput();
        }

        private void TxtNewTaskTitle_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) AddTaskFromInput();
        }

        private void AddTaskFromInput()
        {
            if (_currentSubject == null) return;
            string title = TxtNewTaskTitle.Text.Trim();
            if (string.IsNullOrEmpty(title)) return;

            _state.Tasks.Add(new TaskItem
            {
                Id = "task_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                SubjectId = _currentSubject.Id,
                Text = title,
                Completed = false
            });

            TxtNewTaskTitle.Text = "";
            SaveState();
            RenderTasksList();
            UpdateSubjectDetailStats();
            ShowToast("Task added! ✅");
        }

        // ================= LINKS WORKSPACE =================

        private void RenderLinksList()
        {
            LinksListStackPanel.Children.Clear();
            if (_currentSubject == null) return;

            var links = _state.Links.Where(l => l.SubjectId == _currentSubject.Id).ToList();
            if (links.Count == 0)
            {
                LinksListStackPanel.Children.Add(new TextBlock
                {
                    Text = "No links added yet. Add websites or references above.",
                    FontSize = 12,
                    Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(104, 114, 150)) : new SolidColorBrush(Color.FromRgb(100, 116, 139))
                });
                return;
            }

            foreach (var link in links)
            {
                var l = link;
                var border = new Border
                {
                    Background = _isDarkMode ? new SolidColorBrush(Color.FromRgb(17, 21, 44)) : new SolidColorBrush(Color.FromRgb(248, 250, 252)),
                    BorderBrush = _isDarkMode ? new SolidColorBrush(Color.FromRgb(40, 48, 86)) : new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(14, 10, 14, 10),
                    Margin = new Thickness(0, 0, 0, 8)
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var sp = new StackPanel();
                sp.Children.Add(new TextBlock
                {
                    Text = $"🔗 {l.Title}",
                    FontWeight = FontWeights.Bold,
                    Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(34, 211, 238)) : new SolidColorBrush(Color.FromRgb(2, 132, 199)),
                    FontSize = 13.5
                });
                sp.Children.Add(new TextBlock
                {
                    Text = l.Url,
                    Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(154, 163, 194)) : new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                    FontSize = 11.5,
                    Margin = new Thickness(0, 2, 0, 0)
                });
                Grid.SetColumn(sp, 0);
                grid.Children.Add(sp);

                var actions = new StackPanel { Orientation = Orientation.Horizontal };
                var btnOpen = new Button { Content = "Open ↗", Style = (Style)FindResource("BrandButtonStyle"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
                btnOpen.Click += (s, e) =>
                {
                    try { Process.Start(new ProcessStartInfo(l.Url) { UseShellExecute = true }); } catch { }
                };
                var btnDel = new Button { Content = "✕", Style = (Style)FindResource("SecondaryButtonStyle"), Padding = new Thickness(6, 2, 6, 2) };
                btnDel.Click += (s, e) =>
                {
                    _state.Links.Remove(l);
                    SaveState();
                    RenderLinksList();
                    UpdateSubjectDetailStats();
                };
                actions.Children.Add(btnOpen);
                actions.Children.Add(btnDel);
                Grid.SetColumn(actions, 1);
                grid.Children.Add(actions);

                border.Child = grid;
                LinksListStackPanel.Children.Add(border);
            }
        }

        private void BtnAddLink_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSubject == null) return;
            string title = TxtLinkTitle.Text.Trim();
            string url = TxtLinkUrl.Text.Trim();
            if (string.IsNullOrEmpty(url)) return;
            if (!url.StartsWith("http://") && !url.StartsWith("https://")) url = "https://" + url;
            if (string.IsNullOrEmpty(title)) title = url;

            _state.Links.Add(new LinkItem
            {
                Id = "link_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                SubjectId = _currentSubject.Id,
                Title = title,
                Url = url
            });

            TxtLinkTitle.Text = "";
            TxtLinkUrl.Text = "";
            SaveState();
            RenderLinksList();
            UpdateSubjectDetailStats();
            ShowToast("Link added! 🔗");
        }

        // ================= DEADLINES WORKSPACE =================

        private void RenderDeadlinesList()
        {
            DeadlinesListStackPanel.Children.Clear();
            if (_currentSubject == null) return;

            var deadlines = _state.Deadlines.Where(d => d.SubjectId == _currentSubject.Id).ToList();
            if (deadlines.Count == 0)
            {
                DeadlinesListStackPanel.Children.Add(new TextBlock
                {
                    Text = "No deadlines yet! Schedule your quizzes and exams above.",
                    FontSize = 12,
                    Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(104, 114, 150)) : new SolidColorBrush(Color.FromRgb(100, 116, 139))
                });
                return;
            }

            foreach (var d in deadlines)
            {
                var dl = d;
                var border = new Border
                {
                    Background = _isDarkMode ? new SolidColorBrush(Color.FromRgb(17, 21, 44)) : new SolidColorBrush(Color.FromRgb(248, 250, 252)),
                    BorderBrush = _isDarkMode ? new SolidColorBrush(Color.FromRgb(40, 48, 86)) : new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(14, 10, 14, 10),
                    Margin = new Thickness(0, 0, 0, 8)
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var sp = new StackPanel();
                sp.Children.Add(new TextBlock
                {
                    Text = $"📅 {dl.Title}",
                    FontWeight = FontWeights.Bold,
                    Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(244, 246, 255)) : new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                    FontSize = 13.5
                });
                sp.Children.Add(new TextBlock
                {
                    Text = $"Due: {dl.Date} at {dl.Time}",
                    Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(244, 114, 182)) : new SolidColorBrush(Color.FromRgb(219, 39, 119)),
                    FontSize = 11.5,
                    Margin = new Thickness(0, 2, 0, 0)
                });
                Grid.SetColumn(sp, 0);
                grid.Children.Add(sp);

                var delBtn = new Button { Content = "✕", Style = (Style)FindResource("SecondaryButtonStyle"), Padding = new Thickness(6, 2, 6, 2) };
                delBtn.Click += (s, e) =>
                {
                    _state.Deadlines.Remove(dl);
                    SaveState();
                    RenderDeadlinesList();
                    UpdateSubjectDetailStats();
                };
                Grid.SetColumn(delBtn, 1);
                grid.Children.Add(delBtn);

                border.Child = grid;
                DeadlinesListStackPanel.Children.Add(border);
            }
        }

        private void BtnAddDeadline_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSubject == null) return;
            string title = TxtDeadlineTitle.Text.Trim();
            if (string.IsNullOrEmpty(title)) return;
            string date = DpDeadlineDate.SelectedDate?.ToString("yyyy-MM-dd") ?? DateTime.Now.AddDays(7).ToString("yyyy-MM-dd");

            _state.Deadlines.Add(new DeadlineItem
            {
                Id = "dl_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                SubjectId = _currentSubject.Id,
                Title = title,
                Date = date,
                Time = "23:59",
                Type = "Assignment"
            });

            TxtDeadlineTitle.Text = "";
            SaveState();
            RenderDeadlinesList();
            UpdateSubjectDetailStats();
            ShowToast("Deadline added! 📅");
        }

        // ================= FLASHCARDS WORKSPACE =================

        private void RenderFlashcards()
        {
            if (_currentSubject == null) return;
            var cards = _state.Flashcards.Where(c => c.SubjectId == _currentSubject.Id).ToList();

            if (cards.Count == 0)
            {
                TxtFlashcardContent.Text = "No flashcards in this deck yet.\nClick 'New Card' below to create one!";
                return;
            }

            if (_flashcardIndex >= cards.Count) _flashcardIndex = 0;
            var current = cards[_flashcardIndex];

            if (_flashcardFlipped)
            {
                TxtFlashcardContent.Text = $"💡 Answer:\n\n{current.Answer}";
                BdFlashcard.BorderBrush = new SolidColorBrush(Color.FromRgb(34, 211, 238));
            }
            else
            {
                TxtFlashcardContent.Text = $"❓ Question ({_flashcardIndex + 1}/{cards.Count}):\n\n{current.Question}";
                BdFlashcard.BorderBrush = new SolidColorBrush(Color.FromRgb(124, 92, 255));
            }
        }

        private void BdFlashcard_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _flashcardFlipped = !_flashcardFlipped;
            RenderFlashcards();
        }

        private void BtnNextFlashcard_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSubject == null) return;
            var cards = _state.Flashcards.Where(c => c.SubjectId == _currentSubject.Id).ToList();
            if (cards.Count > 0)
            {
                _flashcardIndex = (_flashcardIndex + 1) % cards.Count;
                _flashcardFlipped = false;
                RenderFlashcards();
            }
        }

        private void BtnPrevFlashcard_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSubject == null) return;
            var cards = _state.Flashcards.Where(c => c.SubjectId == _currentSubject.Id).ToList();
            if (cards.Count > 0)
            {
                _flashcardIndex = (_flashcardIndex - 1 + cards.Count) % cards.Count;
                _flashcardFlipped = false;
                RenderFlashcards();
            }
        }

        private void BtnAddFlashcardPrompt_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSubject == null) return;
            string q = Microsoft.VisualBasic.Interaction.InputBox("Enter Question / Prompt:", "New Flashcard", "What is...");
            if (string.IsNullOrWhiteSpace(q)) return;
            string a = Microsoft.VisualBasic.Interaction.InputBox("Enter Answer:", "New Flashcard Answer", "");
            if (string.IsNullOrWhiteSpace(a)) return;

            _state.Flashcards.Add(new FlashcardItem
            {
                Id = "fc_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                SubjectId = _currentSubject.Id,
                Question = q.Trim(),
                Answer = a.Trim()
            });

            SaveState();
            _flashcardIndex = _state.Flashcards.Count(c => c.SubjectId == _currentSubject.Id) - 1;
            _flashcardFlipped = false;
            RenderFlashcards();
            UpdateSubjectDetailStats();
            ShowToast("Flashcard created! 🎴");
        }

        // ================= WHITEBOARD WORKSPACE (WPF Native InkCanvas) =================

        private void BtnInkPen_Click(object sender, RoutedEventArgs e)
        {
            MainWhiteboardCanvas.EditingMode = InkCanvasEditingMode.Ink;
            MainWhiteboardCanvas.DefaultDrawingAttributes.IsHighlighter = false;
            ShowToast("Pen tool selected 🖊️");
        }

        private void BtnInkHighlighter_Click(object sender, RoutedEventArgs e)
        {
            MainWhiteboardCanvas.EditingMode = InkCanvasEditingMode.Ink;
            MainWhiteboardCanvas.DefaultDrawingAttributes.IsHighlighter = true;
            ShowToast("Highlighter selected 🖍️");
        }

        private void BtnInkEraser_Click(object sender, RoutedEventArgs e)
        {
            MainWhiteboardCanvas.EditingMode = InkCanvasEditingMode.EraseByStroke;
            ShowToast("Eraser tool selected ⌫");
        }

        private void ColorSwatch_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border b && b.Tag is string hex)
            {
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(hex);
                    MainWhiteboardCanvas.DefaultDrawingAttributes.Color = color;
                    MainWhiteboardCanvas.EditingMode = InkCanvasEditingMode.Ink;
                }
                catch { }
            }
        }

        private void SliderInkSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MainWhiteboardCanvas != null)
            {
                double s = SliderInkSize.Value;
                MainWhiteboardCanvas.DefaultDrawingAttributes.Width = s;
                MainWhiteboardCanvas.DefaultDrawingAttributes.Height = s;
            }
        }

        private void BtnClearWhiteboard_Click(object sender, RoutedEventArgs e)
        {
            MainWhiteboardCanvas.Strokes.Clear();
            ShowToast("Canvas cleared ⌫");
        }

        private void BtnSaveWhiteboardPng_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                FileName = $"Whiteboard_{DateTime.Now:yyyyMMdd_HHmm}.png",
                Filter = "PNG Image (*.png)|*.png"
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    int w = (int)MainWhiteboardCanvas.ActualWidth;
                    int h = (int)MainWhiteboardCanvas.ActualHeight;
                    if (w <= 0 || h <= 0) { w = 1200; h = 800; }

                    var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Default);
                    rtb.Render(MainWhiteboardCanvas);

                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(rtb));

                    using var fs = File.OpenWrite(dlg.FileName);
                    encoder.Save(fs);
                    ShowToast("Whiteboard drawing saved as PNG! 💾");
                }
                catch (Exception ex)
                {
                    ShowToast($"Error saving image: {ex.Message}");
                }
            }
        }

        // ================= AMBIENT SOUND STUDIO / MIXER =================

        private void BtnNavSoundMixer_Click(object sender, RoutedEventArgs e)
        {
            SoundMixerDrawerGrid.Visibility = (SoundMixerDrawerGrid.Visibility == Visibility.Visible) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void BtnCloseSoundMixer_Click(object sender, RoutedEventArgs e)
        {
            SoundMixerDrawerGrid.Visibility = Visibility.Collapsed;
        }

        private void SoundMixerBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
        {
            SoundMixerDrawerGrid.Visibility = Visibility.Collapsed;
        }

        private void Audio_PlaybackStateChanged()
        {
            Dispatcher.Invoke(() =>
            {
                UpdateMixerUIState();
            });
        }

        private void UpdateMixerUIState()
        {
            if (BtnMasterAudioPlay == null) return;

            bool isPlaying = _audio.IsActivePlaying();
            int activeCount = _audio.GetActiveChannelsCount();

            NavMixerDot.Visibility = isPlaying ? Visibility.Visible : Visibility.Collapsed;

            if (IconMasterPlayTriangle != null) IconMasterPlayTriangle.Visibility = isPlaying ? Visibility.Collapsed : Visibility.Visible;
            if (IconMasterPauseBars != null) IconMasterPauseBars.Visibility = isPlaying ? Visibility.Visible : Visibility.Collapsed;
            BtnMasterAudioPlay.Background = isPlaying
                ? new SolidColorBrush(Color.FromRgb(245, 158, 11)) // #F59E0B Amber/Orange when playing
                : new SolidColorBrush(Color.FromRgb(16, 185, 129)); // #10B981 Emerald when ready/paused

            if (BtnMasterAudioMute != null && TxtMasterMuteIcon != null)
            {
                bool muted = _audio.IsMasterMuted;
                TxtMasterMuteIcon.Text = muted ? "🔇" : "🔊";
                BtnMasterAudioMute.Background = muted
                    ? new SolidColorBrush(Color.FromRgb(244, 63, 94))  // #F43F5E Rose when muted
                    : new SolidColorBrush(Color.FromRgb(124, 92, 255)); // #7C5CFF Brand purple when unmuted
            }

            if (TxtMasterVolLabel != null)
            {
                TxtMasterVolLabel.Text = _audio.IsMasterMuted ? "Muted" : $"{(int)_audio.MasterVolume}%";
                TxtMasterVolLabel.Foreground = _audio.IsMasterMuted
                    ? new SolidColorBrush(Color.FromRgb(244, 63, 94))
                    : (_isDarkMode ? new SolidColorBrush(Color.FromRgb(167, 139, 250)) : new SolidColorBrush(Color.FromRgb(124, 58, 237)));
            }

            if (BdMixerActiveBadge != null && TxtMixerActiveBadge != null)
            {
                BdMixerActiveBadge.Visibility = activeCount > 0 ? Visibility.Visible : Visibility.Collapsed;
                TxtMixerActiveBadge.Text = $"{activeCount} active";
            }
        }

        private void RenderCustomSoundPresets()
        {
            if (MixerCustomPresetsStackPanel == null) return;
            MixerCustomPresetsStackPanel.Children.Clear();

            if (_state?.SavedSoundPresets == null || _state.SavedSoundPresets.Count == 0) return;

            foreach (var preset in _state.SavedSoundPresets.ToList())
            {
                var p = preset;
                var pillBorder = new Border
                {
                    Background = _isDarkMode ? new SolidColorBrush(Color.FromRgb(29, 34, 64)) : new SolidColorBrush(Color.FromRgb(241, 245, 249)),
                    BorderBrush = _isDarkMode ? new SolidColorBrush(Color.FromRgb(56, 65, 110)) : new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(9),
                    Margin = new Thickness(0, 0, 6, 0),
                    Padding = new Thickness(9, 4, 6, 4)
                };

                var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

                var lblBtn = new Button
                {
                    Content = $"🎛️ {p.Name}",
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(226, 232, 240)) : new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Padding = new Thickness(0, 1, 6, 1),
                    Cursor = Cursors.Hand,
                    ToolTip = "Apply saved mix preset"
                };
                lblBtn.Click += (s, e) =>
                {
                    _audio.ApplyCustomPreset(p.Levels);
                    if (_state?.Settings != null)
                    {
                        _state.Settings.MixerMuted = _audio.IsMasterMuted;
                        _state.Settings.MixerMaster = (int)_audio.MasterVolume;
                        _isRenderingMixer = true;
                        SliderMasterVolume.Value = _audio.MasterVolume;
                        _isRenderingMixer = false;
                    }
                    if (_state?.Settings?.MixerLevels != null)
                    {
                        foreach (var ch in _audio.Channels)
                        {
                            _state.Settings.MixerLevels[ch.Id] = (int)ch.Volume;
                        }
                        SaveState();
                    }
                    RenderSoundMixerChannels();
                    UpdateMixerUIState();
                    ShowToast($"Applied mix '{p.Name}' 🎚️");
                };
                sp.Children.Add(lblBtn);

                var delBtn = new Button
                {
                    Content = "✕",
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(148, 163, 184)) : new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Padding = new Thickness(3, 0, 2, 0),
                    Cursor = Cursors.Hand,
                    ToolTip = "Delete saved mix"
                };
                delBtn.Click += (s, e) =>
                {
                    _state.SavedSoundPresets.Remove(p);
                    SaveState();
                    RenderCustomSoundPresets();
                    ShowToast($"Removed preset '{p.Name}'");
                };
                sp.Children.Add(delBtn);

                pillBorder.Child = sp;
                MixerCustomPresetsStackPanel.Children.Add(pillBorder);
            }
        }

        private void RenderSoundMixerChannels()
        {
            if (MixerChannelsStackPanel == null) return;
            _isRenderingMixer = true;
            try
            {
                MixerChannelsStackPanel.Children.Clear();
                var studioSliderStyle = (Style)FindResource("StudioSliderStyle");

                foreach (var ch in _audio.Channels.ToList())
                {
                    var channel = ch;
                    bool isChannelActive = channel.Volume > 0 && !channel.IsMuted;

                    var card = new Border
                    {
                        Background = _isDarkMode
                            ? (isChannelActive ? new SolidColorBrush(Color.FromRgb(26, 31, 62)) : new SolidColorBrush(Color.FromRgb(20, 24, 48)))
                            : (isChannelActive ? new SolidColorBrush(Color.FromRgb(245, 243, 255)) : new SolidColorBrush(Color.FromRgb(255, 255, 255))),
                        BorderBrush = _isDarkMode
                            ? (isChannelActive ? new SolidColorBrush(Color.FromRgb(88, 68, 168)) : new SolidColorBrush(Color.FromRgb(38, 46, 82)))
                            : (isChannelActive ? new SolidColorBrush(Color.FromRgb(196, 181, 253)) : new SolidColorBrush(Color.FromRgb(226, 232, 240))),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(16),
                        Padding = new Thickness(14, 12, 14, 12),
                        Margin = new Thickness(0, 0, 0, 10)
                    };

                    var sp = new StackPanel();

                    // Top row: Icon + Title + (Custom delete) + Volume %
                    var headerGrid = new Grid();
                    headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    titleRow.Children.Add(new TextBlock
                    {
                        Text = channel.Icon,
                        FontSize = 16,
                        Foreground = GetSoundChannelIconBrush(channel.Id),
                        Margin = new Thickness(0, 0, 10, 0),
                        VerticalAlignment = VerticalAlignment.Top
                    });

                    var textCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    textCol.Children.Add(new TextBlock
                    {
                        Text = channel.Name,
                        FontSize = 13,
                        FontWeight = FontWeights.Bold,
                        Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(248, 250, 252)) : new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });

                    if (!string.IsNullOrWhiteSpace(channel.Description))
                    {
                        textCol.Children.Add(new TextBlock
                        {
                            Text = channel.Description,
                            FontSize = 11,
                            Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(138, 149, 183)) : new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                            Margin = new Thickness(0, 2, 0, 0),
                            TextTrimming = TextTrimming.CharacterEllipsis
                        });
                    }
                    titleRow.Children.Add(textCol);
                    Grid.SetColumn(titleRow, 0);
                    headerGrid.Children.Add(titleRow);

                    var rightStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 0, 0, 0) };

                    if (channel.IsCustom)
                    {
                        var delCustomBtn = new Button
                        {
                            Content = "🗑",
                            FontSize = 11,
                            Background = Brushes.Transparent,
                            BorderThickness = new Thickness(0),
                            Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(138, 149, 183)) : new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                            Padding = new Thickness(4, 0, 4, 0),
                            Margin = new Thickness(0, 0, 6, 0),
                            Cursor = Cursors.Hand,
                            ToolTip = "Remove imported sound"
                        };
                        delCustomBtn.Click += (s, e) =>
                        {
                            _audio.RemoveCustomSound(channel.Id);
                            var csItem = _state.CustomSounds.Find(x => x.Id == channel.Id);
                            if (csItem != null) _state.CustomSounds.Remove(csItem);
                            _state.Settings?.MixerLevels?.Remove(channel.Id);
                            SaveState();
                            RenderSoundMixerChannels();
                            UpdateMixerUIState();
                            ShowToast($"Removed '{channel.Name}'");
                        };
                        rightStack.Children.Add(delCustomBtn);
                    }

                    var volLabel = new TextBlock
                    {
                        Text = $"{(int)channel.Volume}%",
                        FontSize = 12,
                        FontWeight = FontWeights.Bold,
                        FontFamily = new FontFamily("Consolas, Courier New"),
                        Foreground = isChannelActive
                            ? (_isDarkMode ? new SolidColorBrush(Color.FromRgb(167, 139, 250)) : new SolidColorBrush(Color.FromRgb(124, 58, 237)))
                            : (_isDarkMode ? new SolidColorBrush(Color.FromRgb(138, 149, 183)) : new SolidColorBrush(Color.FromRgb(100, 116, 139))),
                        VerticalAlignment = VerticalAlignment.Center,
                        MinWidth = 34,
                        TextAlignment = TextAlignment.Right
                    };
                    rightStack.Children.Add(volLabel);

                    Grid.SetColumn(rightStack, 1);
                    headerGrid.Children.Add(rightStack);
                    sp.Children.Add(headerGrid);

                    var slider = new Slider
                    {
                        Minimum = 0,
                        Maximum = 100,
                        Value = channel.Volume,
                        Style = studioSliderStyle,
                        Margin = new Thickness(0, 8, 0, 0)
                    };
                    slider.ValueChanged += (s, e) =>
                    {
                        if (_isRenderingMixer) return;
                        double v = slider.Value;
                        _audio.SetChannelVolume(channel.Id, v);

                        bool nowActive = channel.Volume > 0 && !channel.IsMuted;
                        volLabel.Text = $"{(int)v}%";
                        volLabel.Foreground = nowActive
                            ? (_isDarkMode ? new SolidColorBrush(Color.FromRgb(167, 139, 250)) : new SolidColorBrush(Color.FromRgb(124, 58, 237)))
                            : (_isDarkMode ? new SolidColorBrush(Color.FromRgb(138, 149, 183)) : new SolidColorBrush(Color.FromRgb(100, 116, 139)));

                        card.Background = _isDarkMode
                            ? (nowActive ? new SolidColorBrush(Color.FromRgb(26, 31, 62)) : new SolidColorBrush(Color.FromRgb(20, 24, 48)))
                            : (nowActive ? new SolidColorBrush(Color.FromRgb(245, 243, 255)) : new SolidColorBrush(Color.FromRgb(255, 255, 255)));
                        card.BorderBrush = _isDarkMode
                            ? (nowActive ? new SolidColorBrush(Color.FromRgb(88, 68, 168)) : new SolidColorBrush(Color.FromRgb(38, 46, 82)))
                            : (nowActive ? new SolidColorBrush(Color.FromRgb(196, 181, 253)) : new SolidColorBrush(Color.FromRgb(226, 232, 240)));

                        if (_state?.Settings?.MixerLevels != null)
                        {
                            _state.Settings.MixerLevels[channel.Id] = (int)v;
                            _state.Settings.MixerMuted = _audio.IsMasterMuted;
                            SaveStateDebounced();
                        }
                        UpdateMixerUIState();
                    };
                    sp.Children.Add(slider);

                    card.Child = sp;
                    MixerChannelsStackPanel.Children.Add(card);
                }
            }
            finally
            {
                _isRenderingMixer = false;
            }
        }

        private void BtnMasterAudioPlay_Click(object sender, RoutedEventArgs e)
        {
            _audio.ToggleMasterPlayPause();
            bool isPlaying = _audio.IsActivePlaying();

            if (_state?.Settings != null)
            {
                _state.Settings.MixerMuted = _audio.IsMasterMuted;
                _state.Settings.MixerMaster = (int)_audio.MasterVolume;
                _isRenderingMixer = true;
                SliderMasterVolume.Value = _audio.MasterVolume;
                _isRenderingMixer = false;
            }
            if (_state?.Settings?.MixerLevels != null)
            {
                foreach (var ch in _audio.Channels)
                {
                    _state.Settings.MixerLevels[ch.Id] = (int)ch.Volume;
                }
                SaveState();
            }
            RenderSoundMixerChannels();
            UpdateMixerUIState();
            ShowToast(isPlaying ? "Ambient Sound Studio playing ▶️" : "Ambient Sound Studio paused ⏸️");
        }

        private void BtnMasterAudioReset_Click(object sender, RoutedEventArgs e)
        {
            _audio.StopAndResetAll();
            if (_state?.Settings?.MixerLevels != null)
            {
                foreach (var key in _state.Settings.MixerLevels.Keys.ToList())
                {
                    _state.Settings.MixerLevels[key] = 0;
                }
                SaveState();
            }
            RenderSoundMixerChannels();
            UpdateMixerUIState();
            ShowToast("All ambient channels reset ⏹️");
        }

        private void BtnMasterAudioMute_Click(object sender, RoutedEventArgs e)
        {
            _audio.ToggleMasterMute();
            if (_state?.Settings != null)
            {
                _state.Settings.MixerMuted = _audio.IsMasterMuted;
                SaveState();
            }
            UpdateMixerUIState();
            ShowToast(_audio.IsMasterMuted ? "Audio Mixer muted 🔇" : "Audio Mixer unmuted 🔊");
        }

        private void BtnSoundPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string presetTag && !string.IsNullOrEmpty(presetTag))
            {
                _audio.ApplyPreset(presetTag);
                if (_state?.Settings != null)
                {
                    _state.Settings.MixerMuted = _audio.IsMasterMuted;
                    _state.Settings.MixerMaster = (int)_audio.MasterVolume;
                    _isRenderingMixer = true;
                    SliderMasterVolume.Value = _audio.MasterVolume;
                    _isRenderingMixer = false;
                }
                if (_state?.Settings?.MixerLevels != null)
                {
                    foreach (var ch in _audio.Channels)
                    {
                        _state.Settings.MixerLevels[ch.Id] = (int)ch.Volume;
                    }
                    SaveState();
                }
                RenderSoundMixerChannels();
                UpdateMixerUIState();
                ShowToast("Preset soundscape applied 🎚️");
            }
        }

        private void BtnSaveSoundPreset_Click(object sender, RoutedEventArgs e)
        {
            var activeLevels = new Dictionary<string, int>();
            foreach (var ch in _audio.Channels)
            {
                if (ch.Volume > 0)
                {
                    activeLevels[ch.Id] = (int)ch.Volume;
                }
            }

            if (activeLevels.Count == 0)
            {
                ShowToast("Raise at least one channel volume before saving a mix!");
                return;
            }

            string name = Microsoft.VisualBasic.Interaction.InputBox(
                "Enter a name for this custom sound mix (e.g. Deep Focus, Rainy Study):",
                "Save Mix Preset",
                "My Focus Mix");
            if (string.IsNullOrWhiteSpace(name)) return;

            _state.SavedSoundPresets.Add(new SoundPresetItem
            {
                Id = "mix_" + Guid.NewGuid().ToString("N")[..8],
                Name = name.Trim(),
                Levels = activeLevels
            });
            SaveState();
            RenderCustomSoundPresets();
            ShowToast($"Saved custom mix '{name.Trim()}' 💾");
        }

        private void SliderMasterVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isRenderingMixer || _audio == null) return;
            _audio.SetMasterVolume(SliderMasterVolume.Value);
            if (_state?.Settings != null)
            {
                _state.Settings.MixerMaster = (int)SliderMasterVolume.Value;
                _state.Settings.MixerMuted = _audio.IsMasterMuted;
                SaveStateDebounced();
            }
            UpdateMixerUIState();
        }

        private void MixerPresetsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (MixerPresetsScrollViewer != null)
            {
                MixerPresetsScrollViewer.ScrollToHorizontalOffset(MixerPresetsScrollViewer.HorizontalOffset - (e.Delta * 0.6));
                e.Handled = true;
            }
        }

        // ================= ZEN STUDY MODE (F10) =================

        private void BtnNavZen_Click(object sender, RoutedEventArgs e)
        {
            ToggleZenMode();
        }

        private void ToggleZenMode()
        {
            bool isZen = ZenModeOverlayGrid.Visibility == Visibility.Visible;
            if (isZen)
            {
                CloseZenSceneDropdown();
                StopZenCustomMedia();
                ZenModeOverlayGrid.Visibility = Visibility.Collapsed;
                ZenStudyOverlayCard.Visibility = Visibility.Collapsed;
                TxtZenStudyViewBtn.Text = "Show Study View";
                HomeViewScrollViewer.Visibility = (_currentSubject == null) ? Visibility.Visible : Visibility.Collapsed;
                SubjectDetailGrid.Visibility = (_currentSubject != null) ? Visibility.Visible : Visibility.Collapsed;
                UpdateRenderLoopState();
            }
            else
            {
                HomeViewScrollViewer.Visibility = Visibility.Collapsed;
                SubjectDetailGrid.Visibility = Visibility.Collapsed;
                ZenModeOverlayGrid.Visibility = Visibility.Visible;
                ZenModeOverlayGrid.Focus();

                UpdateZenClockAndHud(force: true);
                ApplyZenBackdropSource();
                UpdateRenderLoopState();
                ShowToast("Zen Study Mode active. Press Esc or F10 to exit 🧘");
            }
        }

        private void BtnExitZen_Click(object sender, RoutedEventArgs e)
        {
            ToggleZenMode();
        }

        private void BtnZenToggleMixer_Click(object sender, RoutedEventArgs e)
        {
            SoundMixerDrawerGrid.Visibility = Visibility.Visible;
        }

        private record GlobalThemeOption(string Id, string Name, string Desc, string GradientPreview, string Icon);
        private record ParticleOption(string Id, string Name, string Desc, string GradientPreview, string Icon);
        private record ZenSceneOption(string Id, string Name, string Badge, string GradientPreview, string Icon);

        private static readonly List<GlobalThemeOption> GLOBAL_THEMES = new()
        {
            new("aurora", "Dynamic Gradients", "Shifting aurora mesh", "#7C5CFF,#22D3EE,#F472B6", "✨"),
            new("particles", "Deep Nebula", "Velvet indigo & violet", "#050713,#4F46E5,#9333EA", "🌌"),
            new("acrylic", "Frosted Acrylic", "Deep translucent glass", "#1E3A8A,#0E7490,#22D3EE", "💠"),
            new("daydusk", "Day / Dusk Cycle", "Live solar sky evolution", "#F59E0B,#F43F5E,#311042", "🌅"),
            new("minimal", "Flat Minimalist", "Zero-distraction matte", "#475569,#1E293B", "🔲")
        };

        private static readonly List<ParticleOption> PARTICLE_MODES = new()
        {
            new("network", "Network Mesh", "Connected constellation nodes", "#4F46E5,#06B6D4", "🌐"),
            new("cosmic", "Cosmic Dust", "Twinkling stars & nebula dust", "#7E22CE,#C026D3,#F59E0B", "⭐"),
            new("matrix", "Digital Rain", "Falling cyber glyph streams", "#059669,#0D9488,#0284C7", "💻"),
            new("orbs", "Floating Orbs", "Soft glowing bokeh spheres", "#EC4899,#8B5CF6,#38BDF8", "🔮"),
            new("lava_lamp", "Lava Lamp", "Rising & morphing glowing wax blobs", "#F43F5E,#F97316,#7E22CE", "🔥"),
            new("meteor", "Meteor", "Shooting stars & night sky", "#F59E0B,#EA580C,#311042", "🌠"),
            new("fading_stars", "Fading Stars", "Appearing & fading starlight", "#7C3AED,#6366F1,#38BDF8", "🌟"),
            new("water_ripple", "Water Ripple", "Expanding concentric water rings", "#06B6D4,#0284C7,#1D4ED8", "💧"),
            new("disable", "Disable", "Static background only", "#64748B,#334155", "🚫")
        };

        private static readonly List<ZenSceneOption> ZEN_SCENES_LIST = new()
        {
            new("north_pole", "Northern Lights", "North Pole & Ice Mountains", "#10B981,#06B6D4,#312E81", "🌌"),
            new("far_space", "Far Space", "Deep Cosmos, Multi-Galaxies & Quasar", "#6B21A8,#C026D3,#020617", "🚀"),
            new("solar_system", "Solar System", "Planets, Moons, Asteroid & Kuiper Belts", "#F59E0B,#EA580C,#020617", "☀️"),
            new("moon_night", "Moon Night", "Full Cratered Moon, Clouds & Fireflies", "#1E293B,#312E81,#38BDF8", "🌙"),
            new("cyber_city", "Cyber City", "Neon Megastructures & Sky Traffic", "#C026D3,#6B21A8,#22D3EE", "🌆"),
            new("nuclear_wasteland", "Nuclear Wasteland", "Irradiated Skies, Cooling Tower & Glowing Fallout", "#B45309,#3F6212,#1C1917", "☢️"),
            new("wasteland", "Wasteland", "Barren Desolation, Dust Devils & Weathered Mesas", "#92400E,#292524,#0C0A09", "💨"),
            new("city_wasteland", "City Wasteland", "Overgrown Ruined Metropolis & Derelict Towers", "#022C22,#0F172A,#1C1917", "🏙️"),
            new("desert", "Desert Dunes", "Sweeping Sand Dunes & Starlit Oasis", "#F59E0B,#EA580C,#581C87", "🏜️"),
            new("lake_forest", "Lake Forest", "Mirror Alpine Lake & Pine Woods", "#047857,#0D9488,#020617", "🌲"),
            new("shipwreck", "Shipwreck Cove", "Sunken Galleon, Sunbeams & Reef", "#0E7490,#115E59,#020617", "⚓"),
            new("ocean_life", "Ocean Life", "Underwater Reef, Fish Schools & Caustics", "#0284C7,#1D4ED8,#020617", "🐠"),
            new("snowy_peaks", "Snowy Mountain Tops", "High Summits & Cloud Sea", "#7DD3FC,#2563EB,#0F172A", "❄️"),
            new("city_skyline", "City Skyline", "Night Metropolis & Illuminated Towers", "#4338CA,#1E293B,#F59E0B", "🌃"),
            new("city_shoreline", "City Shoreline", "Harbor Skyline & Water Reflections", "#06B6D4,#C026D3,#0F172A", "🌉"),
            new("sunset", "Sunset", "Horizon Glow, Mountains & Birds", "#F43F5E,#F97316,#FBBF24", "🌅"),
            new("rainy_forest", "Rainy Forest", "Misty Pine Woodland & Rainfall", "#0F766E,#065F46,#0F172A", "🌧️"),
            new("custom", "Custom Video", "Local Video / Animation Loop", "#7C3AED,#9333EA,#06B6D4", "🎬"),
            new("none", "None", "Static Focus Backdrop", "#334155,#0F172A", "🚫")
        };

        private static string GetZenSceneIconPathData(string sceneId) => sceneId switch
        {
            "north_pole" => "M 2,19 L 8.5,8 L 13,14 L 16.5,9.5 L 22,19 Z M 19,3.5 L 19.8,5.2 L 21.5,6 L 19.8,6.8 L 19,8.5 L 18.2,6.8 L 16.5,6 L 18.2,5.2 Z",
            "far_space" => "M 12,2.5 C 15,5 16,8.5 16,13 L 19,16.5 L 15.5,17.5 L 14,15.5 L 10,15.5 L 8.5,17.5 L 5,16.5 L 8,13 C 8,8.5 9,5 12,2.5 Z M 12,17 L 14,20.5 L 12,22 L 10,20.5 Z",
            "solar_system" => "M 12,7.5 A 4.5,4.5 0 1,1 11.99,7.5 Z M 11,1.5 L 13,1.5 L 13,4.8 L 11,4.8 Z M 11,19.2 L 13,19.2 L 13,22.5 L 11,22.5 Z M 1.5,11 L 4.8,11 L 4.8,13 L 1.5,13 Z M 19.2,11 L 22.5,11 L 22.5,13 L 19.2,13 Z M 4.2,5.6 L 5.6,4.2 L 7.9,6.5 L 6.5,7.9 Z M 16.1,17.5 L 17.5,16.1 L 19.8,18.4 L 18.4,19.8 Z M 4.2,18.4 L 6.5,16.1 L 7.9,17.5 L 5.6,19.8 Z M 16.1,6.5 L 18.4,4.2 L 19.8,5.6 L 17.5,7.9 Z",
            "moon_night" => "M 13.5,3 A 9,9 0 1,0 20.5,16.5 A 6.5,6.5 0 0,1 13.5,3 Z M 18,4 L 18.6,5.4 L 20,6 L 18.6,6.6 L 18,8 L 17.4,6.6 L 16,6 L 17.4,5.4 Z",
            "cyber_city" => "M 3,20 L 3,9 L 6,9 L 6,6 L 9,6 L 9,20 Z M 10.5,20 L 10.5,3 L 14.5,3 L 14.5,20 Z M 16,20 L 16,10.5 L 21,10.5 L 21,20 Z",
            "nuclear_wasteland" => "M 12,9.8 A 2.2,2.2 0 1,1 11.99,9.8 Z M 10.1,8.7 L 7,3.3 A 10,10 0 0,1 17,3.3 L 13.9,8.7 A 3.8,3.8 0 0,0 10.1,8.7 Z M 15.8,12 L 22,12 A 10,10 0 0,1 17,20.7 L 13.9,15.3 A 3.8,3.8 0 0,0 15.8,12 Z M 10.1,15.3 L 7,20.7 A 10,10 0 0,1 2,12 L 8.2,12 A 3.8,3.8 0 0,0 10.1,15.3 Z",
            "wasteland" => "M 3,6.5 L 14,6.5 A 2,2 0 1,0 14,2.5 L 14,4.2 A 0.4,0.4 0 1,1 14,5 L 3,5 Z M 2,12 L 19,12 A 2.4,2.4 0 1,0 19,7.2 L 19,9.2 A 0.4,0.4 0 1,1 19,10 L 2,10 Z M 4,15 L 14.5,15 A 2.4,2.4 0 1,1 14.5,19.8 L 14.5,17.8 A 0.4,0.4 0 1,0 14.5,17 L 4,17 Z",
            "city_wasteland" => "M 4,20 L 4,10 L 7,12 L 9,7 L 12,11 L 12,20 Z M 13.5,20 L 13.5,4 L 17,6 L 20,4.5 L 20,20 Z",
            "desert" => "M 7.5,12 A 4.5,4.5 0 0,1 16.5,12 Z M 2,18.5 Q 8,12.5 14,16.5 Q 18,14 22,18.5 L 22,20.5 L 2,20.5 Z",
            "lake_forest" => "M 8.5,3 L 3,12 L 6,12 L 2.5,18 L 7.5,18 L 7.5,21 L 9.5,21 L 9.5,18 L 14.5,18 L 11,12 L 14,12 Z M 16.5,7 L 12.5,14 L 14.8,14 L 12,19 L 15.8,19 L 15.8,21 L 17.2,21 L 17.2,19 L 21,19 L 18.2,14 L 20.5,14 Z",
            "shipwreck" => "M 12,3 A 2,2 0 1,1 11.99,3 Z M 11,6 L 13,6 L 13,8 L 16,8 L 16,10 L 13,10 L 13,17.5 C 16,17 18.5,14.5 18.5,12 L 16.5,12 L 19.5,8.5 L 22,12 L 20.3,12 C 20,16.8 16.5,20.5 12,21 C 7.5,20.5 4,16.8 3.7,12 L 2,12 L 4.5,8.5 L 7.5,12 L 5.5,12 C 5.5,14.5 8,17 11,17.5 L 11,10 L 8,10 L 8,8 L 11,8 Z",
            "ocean_life" => "M 2.5,8 L 7,12 L 2.5,16 Z M 6.5,12 C 9.5,6 17,6.5 21.5,12 C 17,17.5 9.5,18 6.5,12 Z",
            "snowy_peaks" => "M 11,2 L 13,2 L 13,10.2 L 19.8,6.2 L 20.8,8 L 14,12 L 20.8,16 L 19.8,17.8 L 13,13.8 L 13,22 L 11,22 L 11,13.8 L 4.2,17.8 L 3.2,16 L 10,12 L 3.2,8 L 4.2,6.2 L 11,10.2 Z",
            "city_skyline" => "M 3,20 L 3,10 L 8,10 L 8,20 Z M 9.5,20 L 9.5,6 L 11.5,6 L 11.5,3 L 12.5,3 L 12.5,6 L 14.5,6 L 14.5,20 Z M 16,20 L 16,8.5 L 21,8.5 L 21,20 Z",
            "city_shoreline" => "M 2,15 L 5,15 L 5,5 L 7,5 L 7,15 L 17,15 L 17,5 L 19,5 L 19,15 L 22,15 L 22,17 L 2,17 Z M 7,7 C 9.5,12.5 14.5,12.5 17,7 L 17,9 C 14.5,14 9.5,14 7,9 Z M 3,19 L 21,19 L 21,21 L 3,21 Z",
            "sunset" => "M 6,13.5 A 6,6 0 0,1 18,13.5 Z M 2,15.5 L 22,15.5 L 22,17.5 L 2,17.5 Z M 5,19.5 L 19,19.5 L 19,21.5 L 5,21.5 Z",
            "rainy_forest" => "M 7,13 A 3.5,3.5 0 0,1 6.5,6.2 A 5,5 0 0,1 16,6.8 A 3.3,3.3 0 0,1 17.5,13 Z M 7,15.5 L 9,15.5 L 7.5,20.5 L 5.5,20.5 Z M 11.5,15.5 L 13.5,15.5 L 12,20.5 L 10,20.5 Z M 16,15.5 L 18,15.5 L 16.5,20.5 L 14.5,20.5 Z",
            "custom" => "M 2,7 A 2,2 0 0,1 4,5 L 14,5 A 2,2 0 0,1 16,7 L 16,17 A 2,2 0 0,1 14,19 L 4,19 A 2,2 0 0,1 2,17 Z M 17,9.5 L 22,6.5 L 22,17.5 L 17,14.5 Z",
            "none" => "M 12,2 A 10,10 0 1,1 11.99,2 Z M 12,4.6 A 7.4,7.4 0 1,0 12.01,4.6 Z M 5.8,16.6 L 16.6,5.8 L 18.2,7.4 L 7.4,18.2 Z",
            _ => "M 2,19 L 8.5,8 L 13,14 L 16.5,9.5 L 22,19 Z"
        };

        private void BtnZenToggleStudyView_Click(object sender, RoutedEventArgs e)
        {
            bool isVis = ZenStudyOverlayCard.Visibility == Visibility.Visible;
            ZenStudyOverlayCard.Visibility = isVis ? Visibility.Collapsed : Visibility.Visible;
            TxtZenStudyViewBtn.Text = isVis ? "Show Study View" : "Hide Study View";

            if (!isVis)
            {
                TxtZenStudyCourseTitle.Text = _currentSubject != null
                    ? $"📖 {_currentSubject.Name} — Active Course Study View"
                    : "📖 Study Workspace — Quick Notes";
                TxtZenStudyNotes.Text = TxtSubjectNotes.Text;
            }
        }

        private void BtnCloseZenStudyCard_Click(object sender, RoutedEventArgs e)
        {
            ZenStudyOverlayCard.Visibility = Visibility.Collapsed;
            TxtZenStudyViewBtn.Text = "Show Study View";
        }

        private void UpdateZenTimerReadout()
        {
            if (TxtZenTimerDisplay == null) return;
            int m = _pomoSecondsRemaining / 60;
            int s = _pomoSecondsRemaining % 60;
            string prefix = _isPomoRunning
                ? (_isPomoBreak ? "☕ Break" : "🔥 Focusing")
                : (_isPomoBreak ? "☕ Break" : "Focus Timer");
            string nextText = $"{prefix} • {m:00}:{s:00}";
            if (!string.Equals(TxtZenTimerDisplay.Text, nextText, StringComparison.Ordinal))
            {
                TxtZenTimerDisplay.Text = nextText;
            }
        }

        private void UpdateZenClockAndHud(DateTime? explicitNow = null, bool force = false)
        {
            var now = explicitNow ?? DateTime.Now;
            bool isAnalog = string.Equals(_state?.Settings?.ClockStyle, "analog", StringComparison.OrdinalIgnoreCase);

            if (ZenDigitalClockWrap != null && ZenAnalogClockWrap != null)
            {
                var digVis = isAnalog ? Visibility.Collapsed : Visibility.Visible;
                var anaVis = isAnalog ? Visibility.Visible : Visibility.Collapsed;
                if (ZenDigitalClockWrap.Visibility != digVis) ZenDigitalClockWrap.Visibility = digVis;
                if (ZenAnalogClockWrap.Visibility != anaVis) ZenAnalogClockWrap.Visibility = anaVis;
            }

            string mainStr = _is24HourClock ? now.ToString("HH:mm") : now.ToString("hh:mm");
            string ampmStr = _is24HourClock ? "" : (now.Hour >= 12 ? "PM" : "AM");

            if (TxtZenClockTime != null && (force || !string.Equals(TxtZenClockTime.Text, mainStr, StringComparison.Ordinal)))
            {
                TxtZenClockTime.Text = mainStr;
            }
            if (TxtZenClockAmPm != null)
            {
                var ampmVis = _is24HourClock ? Visibility.Collapsed : Visibility.Visible;
                if (TxtZenClockAmPm.Visibility != ampmVis) TxtZenClockAmPm.Visibility = ampmVis;
                if (force || !string.Equals(TxtZenClockAmPm.Text, ampmStr, StringComparison.Ordinal))
                {
                    TxtZenClockAmPm.Text = ampmStr;
                }
            }

            if (isAnalog)
            {
                double sec = now.Second;
                double min = now.Minute + sec / 60.0;
                double hr = (now.Hour % 12) + min / 60.0;
                if (ZenAnalogHourRotate != null) ZenAnalogHourRotate.Angle = (hr / 12.0) * 360.0;
                if (ZenAnalogMinuteRotate != null) ZenAnalogMinuteRotate.Angle = (min / 60.0) * 360.0;
                if (ZenAnalogSecondRotate != null) ZenAnalogSecondRotate.Angle = (sec / 60.0) * 360.0;
                if (TxtZenAnalogSubText != null)
                {
                    string sub = _is24HourClock ? mainStr : $"{mainStr} {ampmStr}";
                    if (!string.Equals(TxtZenAnalogSubText.Text, sub, StringComparison.Ordinal))
                        TxtZenAnalogSubText.Text = sub;
                }
            }

            UpdateZenTimerReadout();
        }

        private void BtnZenSceneDropdown_Click(object sender, RoutedEventArgs e)
        {
            if (ZenSceneDropdownOverlay == null) return;
            if (ZenSceneDropdownOverlay.Visibility == Visibility.Visible)
            {
                CloseZenSceneDropdown();
            }
            else
            {
                RenderZenSceneDropdown();
                PositionZenSceneDropdownMenu();
                ZenSceneDropdownOverlay.Visibility = Visibility.Visible;
            }
        }

        private void ZenSceneDropdownBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
        {
            CloseZenSceneDropdown();
        }

        private void CloseZenSceneDropdown()
        {
            if (ZenSceneDropdownOverlay != null)
            {
                ZenSceneDropdownOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void PositionZenSceneDropdownMenu()
        {
            if (BdZenSceneDropdownMenu == null || BtnZenSceneDropdown == null || ZenModeOverlayGrid == null) return;
            double hostW = ZenModeOverlayGrid.ActualWidth > 0 ? ZenModeOverlayGrid.ActualWidth : (ActualWidth > 0 ? ActualWidth : 1440);
            double hostH = ZenModeOverlayGrid.ActualHeight > 0 ? ZenModeOverlayGrid.ActualHeight : (ActualHeight > 0 ? ActualHeight : 900);
            double menuW = BdZenSceneDropdownMenu.Width > 0 ? BdZenSceneDropdownMenu.Width : 282;

            try
            {
                Point anchorRightBottom = BtnZenSceneDropdown.TranslatePoint(
                    new Point(BtnZenSceneDropdown.ActualWidth, BtnZenSceneDropdown.ActualHeight),
                    ZenModeOverlayGrid);
                double left = anchorRightBottom.X - menuW;
                double top = anchorRightBottom.Y + 8;
                if (left + menuW > hostW - 14) left = hostW - menuW - 14;
                if (left < 14) left = 14;
                if (top < 14) top = 62;
                if (top + 360 > hostH - 14) top = Math.Max(14, hostH - 414);
                BdZenSceneDropdownMenu.Margin = new Thickness(left, top, 0, 0);
            }
            catch
            {
                BdZenSceneDropdownMenu.Margin = new Thickness(Math.Max(14, hostW - menuW - 24), 62, 0, 0);
            }
        }

        private void RenderZenSceneDropdown()
        {
            if (SpZenDropdownItems == null) return;
            SpZenDropdownItems.Children.Clear();
            string curZen = _currentZenScene ?? _state?.Settings?.ZenScene ?? "north_pole";

            var hoverBg = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
            hoverBg.Freeze();
            var activeBg = new SolidColorBrush(Color.FromRgb(124, 58, 237));
            activeBg.Freeze();
            var inactiveSub = new SolidColorBrush(Color.FromRgb(148, 163, 184));
            inactiveSub.Freeze();
            var activeSub = new SolidColorBrush(Color.FromArgb(215, 255, 255, 255));
            activeSub.Freeze();
            var inactiveTitle = new SolidColorBrush(Color.FromRgb(226, 232, 240));
            inactiveTitle.Freeze();
            var amberCheck = new SolidColorBrush(Color.FromRgb(253, 224, 71));
            amberCheck.Freeze();

            foreach (var sc in ZEN_SCENES_LIST)
            {
                bool active = string.Equals(sc.Id, curZen, StringComparison.OrdinalIgnoreCase);

                var rowBd = new Border
                {
                    Background = active ? activeBg : Brushes.Transparent,
                    CornerRadius = new CornerRadius(11),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 1, 0, 2),
                    Cursor = Cursors.Hand
                };
                if (!active)
                {
                    rowBd.MouseEnter += (_, _) => rowBd.Background = hoverBg;
                    rowBd.MouseLeave += (_, _) => rowBd.Background = Brushes.Transparent;
                }

                var rowGrid = new Grid();
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var tile = new Border
                {
                    Width = 28,
                    Height = 28,
                    CornerRadius = new CornerRadius(8),
                    Background = ParseTailwindPreviewBrush(sc.GradientPreview),
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                tile.Child = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse(GetZenSceneIconPathData(sc.Id)),
                    Fill = Brushes.White,
                    Width = 13,
                    Height = 13,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(tile, 0);
                rowGrid.Children.Add(tile);

                var textCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                textCol.Children.Add(new TextBlock
                {
                    Text = sc.Name,
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = active ? Brushes.White : inactiveTitle,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                textCol.Children.Add(new TextBlock
                {
                    Text = sc.Badge,
                    FontSize = 10,
                    FontWeight = FontWeights.Medium,
                    Foreground = active ? activeSub : inactiveSub,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 1, 0, 0)
                });
                Grid.SetColumn(textCol, 1);
                rowGrid.Children.Add(textCol);

                if (active)
                {
                    var chk = new System.Windows.Shapes.Path
                    {
                        Data = Geometry.Parse("M 4,12 L 9.5,17.5 L 20,6.5"),
                        Stroke = amberCheck,
                        StrokeThickness = 2.4,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        Width = 12,
                        Height = 12,
                        Stretch = Stretch.Uniform,
                        Margin = new Thickness(8, 0, 2, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(chk, 2);
                    rowGrid.Children.Add(chk);
                }

                rowBd.Child = rowGrid;
                string targetId = sc.Id;
                rowBd.MouseLeftButtonUp += (_, args) =>
                {
                    args.Handled = true;
                    ApplyZenScene(targetId);
                    CloseZenSceneDropdown();
                };
                SpZenDropdownItems.Children.Add(rowBd);
            }
        }

        private void ApplyZenScene(string sceneId, bool saveToDisk = true)
        {
            if (_state?.Settings == null) return;
            var found = ZEN_SCENES_LIST.Find(z => string.Equals(z.Id, sceneId, StringComparison.OrdinalIgnoreCase)) ?? ZEN_SCENES_LIST[0];
            _currentZenScene = found.Id;
            _state.Settings.ZenScene = found.Id;
            if (saveToDisk) SaveState();

            if (TxtActiveZenSceneBadge != null) TxtActiveZenSceneBadge.Text = found.Name;
            if (TxtZenSceneLabel != null) TxtZenSceneLabel.Text = $"{found.Name} • {found.Badge}";
            if (TxtZenDropdownName != null) TxtZenDropdownName.Text = found.Name;
            if (ZenDropdownBtnIconPath != null)
            {
                try { ZenDropdownBtnIconPath.Data = Geometry.Parse(GetZenSceneIconPathData(found.Id)); } catch { }
            }

            if (WpZenCards != null) RenderControlCenterZenCards();
            if (ZenSceneDropdownOverlay != null && ZenSceneDropdownOverlay.Visibility == Visibility.Visible)
                RenderZenSceneDropdown();

            ApplyZenBackdropSource();
        }

        private void StopZenCustomMedia()
        {
            try
            {
                if (ZenCustomVideoPlayer != null)
                {
                    ZenCustomVideoPlayer.Stop();
                    ZenCustomVideoPlayer.Source = null;
                    ZenCustomVideoPlayer.Visibility = Visibility.Collapsed;
                }
                if (ZenCustomImageView != null)
                {
                    ZenCustomImageView.Source = null;
                    ZenCustomImageView.Visibility = Visibility.Collapsed;
                }
            }
            catch { }
        }

        private void RefreshZenCustomVideos(bool notify = false)
        {
            _zenCustomVideos.Clear();
            string folder = (_state?.Settings?.ZenVideoFolder ?? "").Trim();
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    ".mp4", ".webm", ".mov", ".avi", ".wmv", ".mkv", ".m4v", ".gif", ".webp", ".png", ".jpg", ".jpeg"
                };
                try
                {
                    foreach (var f in Directory.GetFiles(folder).OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
                    {
                        if (exts.Contains(Path.GetExtension(f)))
                        {
                            _zenCustomVideos.Add(f);
                        }
                    }
                }
                catch { }
            }

            if (notify)
            {
                if (_zenCustomVideos.Count > 0)
                {
                    ShowToast($"Loaded {_zenCustomVideos.Count} custom Zen video(s)! 🎬");
                }
                else
                {
                    ShowToast("No videos/images found in folder — Northern Lights fallback active.");
                }
            }
        }

        private void ActivateBuiltinZenFallback(ZenSceneOption found, string? reason = null)
        {
            StopZenCustomMedia();
            if (ZenSceneRenderer != null)
            {
                ZenSceneRenderer.Visibility = Visibility.Visible;
                string renderId = (found.Id == "custom") ? "north_pole" : found.Id;
                ZenSceneRenderer.SetScene(renderId);
            }

            bool isCustomScene = found.Id == "custom";
            if (BtnZenPickVideoFolder != null) BtnZenPickVideoFolder.Visibility = isCustomScene ? Visibility.Visible : Visibility.Collapsed;
            if (BtnZenPrevVideo != null) BtnZenPrevVideo.Visibility = Visibility.Collapsed;
            if (BtnZenNextVideo != null) BtnZenNextVideo.Visibility = Visibility.Collapsed;
            if (ZenCustomPromptBox != null)
            {
                ZenCustomPromptBox.Visibility = (isCustomScene && _zenCustomVideos.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
            }

            if (TxtZenSceneLabel != null)
            {
                if (isCustomScene)
                {
                    string baseLabel = "Northern Lights • North Pole & Ice Mountains";
                    TxtZenSceneLabel.Text = string.IsNullOrEmpty(reason) ? baseLabel : $"{baseLabel} ({reason})";
                }
                else
                {
                    TxtZenSceneLabel.Text = $"{found.Name} • {found.Badge}";
                }
            }

            UpdateRenderLoopState();
        }

        private void ApplyZenBackdropSource()
        {
            var found = ZEN_SCENES_LIST.Find(z => string.Equals(z.Id, _currentZenScene, StringComparison.OrdinalIgnoreCase)) ?? ZEN_SCENES_LIST[0];
            if (found.Id != "custom")
            {
                ActivateBuiltinZenFallback(found, null);
                return;
            }

            if (_zenCustomVideos.Count == 0)
            {
                RefreshZenCustomVideos();
            }

            if (_zenCustomVideos.Count == 0)
            {
                ActivateBuiltinZenFallback(found, "Select a video folder");
                return;
            }

            if (ZenCustomPromptBox != null) ZenCustomPromptBox.Visibility = Visibility.Collapsed;
            if (BtnZenPickVideoFolder != null) BtnZenPickVideoFolder.Visibility = Visibility.Visible;

            bool multi = _zenCustomVideos.Count > 1;
            if (BtnZenPrevVideo != null) BtnZenPrevVideo.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
            if (BtnZenNextVideo != null) BtnZenNextVideo.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;

            _zenCurrentVideoIdx = ((_zenCurrentVideoIdx % _zenCustomVideos.Count) + _zenCustomVideos.Count) % _zenCustomVideos.Count;
            string mediaPath = _zenCustomVideos[_zenCurrentVideoIdx];
            if (!File.Exists(mediaPath))
            {
                RefreshZenCustomVideos();
                ActivateBuiltinZenFallback(found, "File missing");
                return;
            }

            if (TxtZenSceneLabel != null)
            {
                TxtZenSceneLabel.Text = $"Custom Video ({_zenCurrentVideoIdx + 1}/{_zenCustomVideos.Count}) • {Path.GetFileName(mediaPath)}";
            }

            string ext = Path.GetExtension(mediaPath).ToLowerInvariant();
            bool isImage = ext is ".gif" or ".webp" or ".png" or ".jpg" or ".jpeg";

            try
            {
                if (isImage)
                {
                    if (ZenCustomVideoPlayer != null)
                    {
                        ZenCustomVideoPlayer.Stop();
                        ZenCustomVideoPlayer.Source = null;
                        ZenCustomVideoPlayer.Visibility = Visibility.Collapsed;
                    }
                    if (ZenSceneRenderer != null) ZenSceneRenderer.Visibility = Visibility.Collapsed;
                    if (ZenCustomImageView != null)
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(mediaPath, UriKind.Absolute);
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        ZenCustomImageView.Source = bmp;
                        ZenCustomImageView.Visibility = Visibility.Visible;
                    }
                }
                else
                {
                    if (ZenCustomImageView != null)
                    {
                        ZenCustomImageView.Source = null;
                        ZenCustomImageView.Visibility = Visibility.Collapsed;
                    }
                    if (ZenSceneRenderer != null) ZenSceneRenderer.Visibility = Visibility.Collapsed;
                    if (ZenCustomVideoPlayer != null)
                    {
                        ZenCustomVideoPlayer.Visibility = Visibility.Visible;
                        ZenCustomVideoPlayer.IsMuted = _audio?.IsMasterMuted ?? false;
                        ZenCustomVideoPlayer.Volume = Math.Clamp((_state?.Settings?.MixerMaster ?? 80) / 100.0 * 0.65, 0.0, 1.0);
                        ZenCustomVideoPlayer.Source = new Uri(mediaPath, UriKind.Absolute);
                        ZenCustomVideoPlayer.Play();
                    }
                }
            }
            catch
            {
                ActivateBuiltinZenFallback(found, "Unsupported codec fallback");
            }

            UpdateRenderLoopState();
        }

        private void BtnZenPrevVideo_Click(object sender, RoutedEventArgs e)
        {
            if (_zenCustomVideos.Count <= 1) return;
            _zenCurrentVideoIdx = (_zenCurrentVideoIdx - 1 + _zenCustomVideos.Count) % _zenCustomVideos.Count;
            ApplyZenBackdropSource();
        }

        private void BtnZenNextVideo_Click(object sender, RoutedEventArgs e)
        {
            if (_zenCustomVideos.Count <= 1) return;
            _zenCurrentVideoIdx = (_zenCurrentVideoIdx + 1) % _zenCustomVideos.Count;
            ApplyZenBackdropSource();
        }

        private void BtnBrowseZenVideoFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select Folder for Zen Mode Videos / Animations"
            };
            if (!string.IsNullOrWhiteSpace(_state?.Settings?.ZenVideoFolder) && Directory.Exists(_state.Settings.ZenVideoFolder))
            {
                dlg.InitialDirectory = _state.Settings.ZenVideoFolder;
            }

            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            {
                _state.Settings.ZenVideoFolder = dlg.FolderName;
                _zenCurrentVideoIdx = 0;
                RefreshZenCustomVideos(notify: true);
                ApplyZenScene("custom", saveToDisk: true);
            }
        }

        private void ZenCustomVideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            if (ZenCustomVideoPlayer != null && ZenCustomVideoPlayer.Visibility == Visibility.Visible)
            {
                ZenCustomVideoPlayer.Position = TimeSpan.Zero;
                ZenCustomVideoPlayer.Play();
            }
        }

        private void ZenCustomVideoPlayer_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
        {
            var found = ZEN_SCENES_LIST.Find(z => z.Id == "custom") ?? ZEN_SCENES_LIST[0];
            ActivateBuiltinZenFallback(found, "Video decode fallback");
            ShowToast("Unable to decode video format — Northern Lights fallback active.");
        }

        private void UpdateRenderLoopState()
        {
            bool canRun = !_isClosing && WindowState != WindowState.Minimized;
            bool needsZen = canRun
                && ZenModeOverlayGrid?.Visibility == Visibility.Visible
                && ZenSceneRenderer?.Visibility == Visibility.Visible
                && !string.Equals(ZenSceneRenderer.ActiveSceneId, "none", StringComparison.OrdinalIgnoreCase);

            bool needsParticles = canRun
                && ZenModeOverlayGrid?.Visibility != Visibility.Visible
                && _isDarkMode
                && GlobalThemeCanvas?.Visibility == Visibility.Visible
                && !string.Equals(_currentParticleEffect, "disable", StringComparison.OrdinalIgnoreCase)
                && _activeGlobalParticles.Count > 0;

            bool needLoop = needsZen || needsParticles;
            if (needLoop && !_isRenderLoopAttached)
            {
                _isRenderLoopAttached = true;
                _lastRenderTimestamp = TimeSpan.MinValue;
                _perfSkipToggle = false;
                _renderStopwatch.Restart();
                CompositionTarget.Rendering += OnCompositionRendering;
            }
            else if (!needLoop && _isRenderLoopAttached)
            {
                _isRenderLoopAttached = false;
                CompositionTarget.Rendering -= OnCompositionRendering;
                _renderStopwatch.Stop();
            }
        }

        private void OnCompositionRendering(object? sender, EventArgs e)
        {
            if (_isClosing || WindowState == WindowState.Minimized)
            {
                UpdateRenderLoopState();
                return;
            }

            if (e is RenderingEventArgs ra)
            {
                if (ra.RenderingTime == _lastRenderTimestamp)
                    return;
                _lastRenderTimestamp = ra.RenderingTime;
            }

            if (!_renderStopwatch.IsRunning)
            {
                _renderStopwatch.Restart();
                return;
            }

            if (_state?.Settings?.PerfThrottle == true)
            {
                _perfSkipToggle = !_perfSkipToggle;
                if (_perfSkipToggle)
                    return;
            }

            double dt = Math.Clamp(_renderStopwatch.Elapsed.TotalSeconds, 0.001, 0.08);
            _renderStopwatch.Restart();

            if (ZenModeOverlayGrid?.Visibility == Visibility.Visible)
            {
                if (ZenSceneRenderer != null && ZenSceneRenderer.Visibility == Visibility.Visible)
                {
                    ZenSceneRenderer.StepFrame(dt);
                }
            }
            else if (_isDarkMode &&
                     GlobalThemeCanvas?.Visibility == Visibility.Visible &&
                     !string.Equals(_currentParticleEffect, "disable", StringComparison.OrdinalIgnoreCase))
            {
                StepGlobalParticles(dt);
            }
        }

        private void ZenModeOverlayGrid_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (ZenSceneDropdownOverlay != null && ZenSceneDropdownOverlay.Visibility == Visibility.Visible)
                {
                    CloseZenSceneDropdown();
                    e.Handled = true;
                    return;
                }
                ToggleZenMode();
                e.Handled = true;
            }
            else if (e.Key == Key.F10)
            {
                ToggleZenMode();
                e.Handled = true;
            }
        }

        // ================= GLOBAL APP THEME & PARTICLE ENGINE =================

        private string GetActiveBgTheme()
        {
            string raw = _state?.Settings?.BgTheme ?? "aurora";
            var valid = GLOBAL_THEMES.Find(t => string.Equals(t.Id, raw, StringComparison.OrdinalIgnoreCase));
            return valid?.Id ?? "aurora";
        }

        private Brush GetCardSurfaceBrush()
        {
            string theme = GetActiveBgTheme();
            if (_isDarkMode)
            {
                return theme switch
                {
                    "acrylic" => new SolidColorBrush(Color.FromArgb(136, 16, 24, 52)),   // True translucent frosted sapphire-teal glass (Image 2)
                    "particles" => new SolidColorBrush(Color.FromArgb(176, 14, 17, 38)), // Translucent obsidian-indigo glass letting nebula orbs shine
                    "daydusk" => new SolidColorBrush(Color.FromArgb(190, 20, 20, 44)),   // Dusk translucent surface
                    "minimal" => new SolidColorBrush(Color.FromRgb(17, 24, 39)),         // Pure matte slate (#111827)
                    _ => new SolidColorBrush(Color.FromArgb(198, 19, 24, 48))            // Aurora translucent surface
                };
            }
            return theme switch
            {
                "acrylic" => new SolidColorBrush(Color.FromArgb(168, 255, 255, 255)),
                "minimal" => new SolidColorBrush(Color.FromRgb(255, 255, 255)),
                _ => new SolidColorBrush(Color.FromArgb(222, 255, 255, 255))
            };
        }

        private Brush GetCardBorderBrush()
        {
            string theme = GetActiveBgTheme();
            if (_isDarkMode)
            {
                return theme switch
                {
                    "acrylic" => new SolidColorBrush(Color.FromArgb(64, 190, 216, 255)),
                    "particles" => new SolidColorBrush(Color.FromArgb(46, 139, 149, 255)),
                    "daydusk" => new SolidColorBrush(Color.FromArgb(42, 255, 255, 255)),
                    "minimal" => new SolidColorBrush(Color.FromRgb(31, 41, 55)),
                    _ => new SolidColorBrush(Color.FromArgb(46, 132, 148, 224))
                };
            }
            return theme switch
            {
                "acrylic" => new SolidColorBrush(Color.FromArgb(215, 226, 232, 240)),
                _ => new SolidColorBrush(Color.FromRgb(226, 232, 240))
            };
        }

        private Brush GetSubtleSurfaceBrush()
        {
            string theme = GetActiveBgTheme();
            if (_isDarkMode)
            {
                return theme switch
                {
                    "acrylic" => new SolidColorBrush(Color.FromArgb(136, 9, 16, 36)),
                    "particles" => new SolidColorBrush(Color.FromArgb(165, 9, 11, 26)),
                    "minimal" => new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                    _ => new SolidColorBrush(Color.FromArgb(175, 11, 15, 34))
                };
            }
            return theme switch
            {
                "acrylic" => new SolidColorBrush(Color.FromArgb(156, 248, 250, 252)),
                _ => new SolidColorBrush(Color.FromRgb(248, 250, 252))
            };
        }

        private Brush GetAddCardSurfaceBrush()
        {
            string theme = GetActiveBgTheme();
            if (_isDarkMode)
            {
                return theme switch
                {
                    "acrylic" => new SolidColorBrush(Color.FromArgb(95, 14, 22, 46)),
                    "particles" => new SolidColorBrush(Color.FromArgb(115, 11, 14, 34)),
                    "minimal" => new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                    _ => new SolidColorBrush(Color.FromArgb(145, 14, 18, 38))
                };
            }
            return theme switch
            {
                "acrylic" => new SolidColorBrush(Color.FromArgb(140, 248, 250, 252)),
                _ => new SolidColorBrush(Color.FromArgb(196, 248, 250, 252))
            };
        }

        private Brush GetAddCardHoverSurfaceBrush()
        {
            string theme = GetActiveBgTheme();
            if (_isDarkMode)
            {
                return theme switch
                {
                    "acrylic" => new SolidColorBrush(Color.FromArgb(145, 22, 34, 68)),
                    "particles" => new SolidColorBrush(Color.FromArgb(160, 20, 24, 54)),
                    _ => new SolidColorBrush(Color.FromArgb(190, 20, 25, 52))
                };
            }
            return new SolidColorBrush(Color.FromArgb(228, 241, 245, 249));
        }

        private Brush GetAddCardStrokeBrush()
        {
            if (!_isDarkMode) return new SolidColorBrush(Color.FromRgb(203, 213, 225));
            return GetActiveBgTheme() switch
            {
                "acrylic" => new SolidColorBrush(Color.FromArgb(92, 148, 184, 240)),
                "particles" => new SolidColorBrush(Color.FromArgb(84, 129, 140, 248)),
                _ => new SolidColorBrush(Color.FromRgb(42, 52, 84))
            };
        }

        private static RadialGradientBrush MakeGlowOrb(Point center, double rx, double ry, params (Color c, double off)[] stops)
        {
            var gsc = new GradientStopCollection();
            foreach (var (c, off) in stops) gsc.Add(new GradientStop(c, off));
            return new RadialGradientBrush(gsc)
            {
                Center = center,
                GradientOrigin = center,
                RadiusX = rx,
                RadiusY = ry
            };
        }

        public void ApplyGlobalBgTheme(string themeId, bool saveToDisk = true)
        {
            if (_state?.Settings == null) return;
            var valid = GLOBAL_THEMES.Find(t => string.Equals(t.Id, themeId, StringComparison.OrdinalIgnoreCase)) ?? GLOBAL_THEMES[0];
            _state.Settings.BgTheme = valid.Id;
            if (saveToDisk) SaveState();

            if (BgBaseLayer != null && BgGlow1 != null && BgGlow2 != null && BgGlow3 != null)
            {
                if (_isDarkMode)
                {
                    switch (valid.Id)
                    {
                        case "aurora": // Dynamic Gradients: violet top-left, cyan/emerald top-right, rose-magenta lower over #080A18
                            BgBaseLayer.Background = new SolidColorBrush(Color.FromRgb(8, 10, 24));
                            BgGlow1.Background = MakeGlowOrb(new Point(0.06, 0.06), 0.62, 0.64,
                                (Color.FromArgb(92, 124, 92, 255), 0.0),
                                (Color.FromArgb(34, 124, 92, 255), 0.52),
                                (Color.FromArgb(0, 124, 92, 255), 1.0));
                            BgGlow2.Background = MakeGlowOrb(new Point(0.94, 0.12), 0.56, 0.62,
                                (Color.FromArgb(72, 34, 211, 238), 0.0),
                                (Color.FromArgb(24, 16, 185, 129), 0.52),
                                (Color.FromArgb(0, 34, 211, 238), 1.0));
                            BgGlow3.Background = MakeGlowOrb(new Point(0.50, 0.98), 0.68, 0.60,
                                (Color.FromArgb(68, 244, 114, 182), 0.0),
                                (Color.FromArgb(22, 192, 132, 252), 0.55),
                                (Color.FromArgb(0, 244, 114, 182), 1.0));
                            break;

                        case "particles": // Deep Nebula (Image 3): true space-black edges with distinct circular glowing indigo & purple orbs
                            BgBaseLayer.Background = new SolidColorBrush(Color.FromRgb(4, 5, 16)); // #040510 pure space black
                            BgGlow1.Background = MakeGlowOrb(new Point(0.16, 0.22), 0.36, 0.48,
                                (Color.FromArgb(118, 79, 70, 229), 0.0),
                                (Color.FromArgb(52, 67, 56, 202), 0.45),
                                (Color.FromArgb(14, 49, 46, 129), 0.78),
                                (Color.FromArgb(0, 4, 5, 16), 1.0));
                            BgGlow2.Background = MakeGlowOrb(new Point(0.79, 0.77), 0.34, 0.46,
                                (Color.FromArgb(128, 126, 58, 242), 0.0),
                                (Color.FromArgb(58, 109, 40, 217), 0.44),
                                (Color.FromArgb(14, 76, 29, 149), 0.78),
                                (Color.FromArgb(0, 4, 5, 16), 1.0));
                            BgGlow3.Background = Brushes.Transparent;
                            break;

                        case "acrylic": // Frosted Acrylic (Image 2): rich royal sapphire-violet top-left fading into luminous deep cyan-teal bottom-right
                            BgBaseLayer.Background = new LinearGradientBrush(
                                new GradientStopCollection
                                {
                                    new GradientStop(Color.FromRgb(18, 28, 72), 0.0),
                                    new GradientStop(Color.FromRgb(11, 22, 48), 0.48),
                                    new GradientStop(Color.FromRgb(4, 52, 68), 1.0)
                                },
                                new Point(0, 0), new Point(1, 1));
                            BgGlow1.Background = MakeGlowOrb(new Point(0.06, 0.10), 0.64, 0.66,
                                (Color.FromArgb(136, 86, 74, 214), 0.0),
                                (Color.FromArgb(56, 62, 96, 204), 0.46),
                                (Color.FromArgb(0, 11, 22, 48), 1.0));
                            BgGlow2.Background = MakeGlowOrb(new Point(0.94, 0.90), 0.64, 0.68,
                                (Color.FromArgb(155, 14, 165, 186), 0.0),
                                (Color.FromArgb(68, 8, 110, 140), 0.46),
                                (Color.FromArgb(0, 4, 52, 68), 1.0));
                            BgGlow3.Background = MakeGlowOrb(new Point(0.40, 0.24), 0.52, 0.46,
                                (Color.FromArgb(46, 56, 130, 224), 0.0),
                                (Color.FromArgb(0, 11, 22, 48), 1.0));
                            break;

                        case "daydusk": // Live solar sky evolution (Dark)
                            {
                                int hr = DateTime.Now.Hour;
                                Color c1, c2, c3, orb1, orb2;
                                if (hr >= 5 && hr < 10)
                                {
                                    c1 = Color.FromRgb(42, 16, 60); c2 = Color.FromRgb(64, 24, 110); c3 = Color.FromRgb(15, 23, 42);
                                    orb1 = Color.FromArgb(72, 244, 63, 94); orb2 = Color.FromArgb(54, 245, 158, 11);
                                }
                                else if (hr >= 10 && hr < 17)
                                {
                                    c1 = Color.FromRgb(12, 45, 72); c2 = Color.FromRgb(30, 27, 75); c3 = Color.FromRgb(9, 13, 22);
                                    orb1 = Color.FromArgb(74, 56, 189, 248); orb2 = Color.FromArgb(52, 99, 102, 241);
                                }
                                else if (hr >= 17 && hr < 20)
                                {
                                    c1 = Color.FromRgb(67, 20, 7); c2 = Color.FromRgb(59, 7, 100); c3 = Color.FromRgb(9, 11, 26);
                                    orb1 = Color.FromArgb(84, 249, 115, 22); orb2 = Color.FromArgb(62, 236, 72, 153);
                                }
                                else
                                {
                                    c1 = Color.FromRgb(9, 11, 26); c2 = Color.FromRgb(30, 27, 75); c3 = Color.FromRgb(2, 6, 23);
                                    orb1 = Color.FromArgb(58, 99, 102, 241); orb2 = Color.FromArgb(42, 56, 189, 248);
                                }

                                BgBaseLayer.Background = new LinearGradientBrush(
                                    new GradientStopCollection { new(c1, 0.0), new(c2, 0.50), new(c3, 1.0) },
                                    new Point(0, 0), new Point(1, 1));
                                BgGlow1.Background = MakeGlowOrb(new Point(0.14, 0.14), 0.60, 0.60,
                                    (orb1, 0.0), (Color.FromArgb(0, c2.R, c2.G, c2.B), 1.0));
                                BgGlow2.Background = MakeGlowOrb(new Point(0.86, 0.80), 0.58, 0.58,
                                    (orb2, 0.0), (Color.FromArgb(0, c3.R, c3.G, c3.B), 1.0));
                                BgGlow3.Background = Brushes.Transparent;
                            }
                            break;

                        case "minimal":
                        default:
                            BgBaseLayer.Background = new SolidColorBrush(Color.FromRgb(11, 15, 25)); // #0B0F19
                            BgGlow1.Background = Brushes.Transparent;
                            BgGlow2.Background = Brushes.Transparent;
                            BgGlow3.Background = Brushes.Transparent;
                            break;
                    }
                }
                else
                {
                    switch (valid.Id)
                    {
                        case "aurora": // Light Dynamic Gradients
                            BgBaseLayer.Background = new SolidColorBrush(Color.FromRgb(245, 246, 251));
                            BgGlow1.Background = MakeGlowOrb(new Point(0.05, 0.05), 0.62, 0.62,
                                (Color.FromArgb(56, 124, 92, 255), 0.0),
                                (Color.FromArgb(20, 124, 92, 255), 0.55),
                                (Color.FromArgb(0, 124, 92, 255), 1.0));
                            BgGlow2.Background = MakeGlowOrb(new Point(0.95, 0.10), 0.58, 0.60,
                                (Color.FromArgb(46, 34, 211, 238), 0.0),
                                (Color.FromArgb(14, 34, 211, 238), 0.55),
                                (Color.FromArgb(0, 34, 211, 238), 1.0));
                            BgGlow3.Background = MakeGlowOrb(new Point(0.50, 1.02), 0.65, 0.58,
                                (Color.FromArgb(38, 244, 114, 182), 0.0),
                                (Color.FromArgb(0, 244, 114, 182), 1.0));
                            break;

                        case "particles": // Light Deep Nebula (soft lavender/violet clouds over airy indigo wash)
                            BgBaseLayer.Background = new SolidColorBrush(Color.FromRgb(238, 242, 255)); // #EEF2FF
                            BgGlow1.Background = MakeGlowOrb(new Point(0.15, 0.16), 0.58, 0.62,
                                (Color.FromArgb(60, 99, 102, 241), 0.0),
                                (Color.FromArgb(20, 99, 102, 241), 0.50),
                                (Color.FromArgb(0, 238, 242, 255), 1.0));
                            BgGlow2.Background = MakeGlowOrb(new Point(0.84, 0.80), 0.56, 0.62,
                                (Color.FromArgb(52, 168, 85, 247), 0.0),
                                (Color.FromArgb(18, 168, 85, 247), 0.50),
                                (Color.FromArgb(0, 238, 242, 255), 1.0));
                            BgGlow3.Background = Brushes.Transparent;
                            break;

                        case "acrylic": // Light Frosted Acrylic (pastel sapphire -> ocean aqua -> soft violet glass canvas)
                            BgBaseLayer.Background = new LinearGradientBrush(
                                new GradientStopCollection
                                {
                                    new GradientStop(Color.FromRgb(236, 240, 253), 0.0),
                                    new GradientStop(Color.FromRgb(236, 246, 252), 0.52),
                                    new GradientStop(Color.FromRgb(228, 247, 250), 1.0)
                                },
                                new Point(0, 0), new Point(1, 1));
                            BgGlow1.Background = MakeGlowOrb(new Point(0.06, 0.08), 0.66, 0.66,
                                (Color.FromArgb(84, 99, 102, 241), 0.0),
                                (Color.FromArgb(30, 124, 92, 255), 0.50),
                                (Color.FromArgb(0, 236, 240, 253), 1.0));
                            BgGlow2.Background = MakeGlowOrb(new Point(0.94, 0.90), 0.66, 0.66,
                                (Color.FromArgb(72, 6, 182, 212), 0.0),
                                (Color.FromArgb(28, 34, 211, 238), 0.50),
                                (Color.FromArgb(0, 228, 247, 250), 1.0));
                            BgGlow3.Background = MakeGlowOrb(new Point(0.65, 0.25), 0.55, 0.52,
                                (Color.FromArgb(40, 244, 114, 182), 0.0),
                                (Color.FromArgb(0, 236, 246, 252), 1.0));
                            break;

                        case "daydusk": // Light Day/Dusk Cycle
                            {
                                int hr = DateTime.Now.Hour;
                                Color c1, c2, c3, orb1, orb2;
                                if (hr >= 5 && hr < 10)
                                {
                                    c1 = Color.FromRgb(254, 243, 199); c2 = Color.FromRgb(251, 207, 232); c3 = Color.FromRgb(224, 231, 255);
                                    orb1 = Color.FromArgb(64, 251, 191, 36); orb2 = Color.FromArgb(48, 244, 114, 182);
                                }
                                else if (hr >= 10 && hr < 17)
                                {
                                    c1 = Color.FromRgb(224, 242, 254); c2 = Color.FromRgb(240, 249, 255); c3 = Color.FromRgb(237, 233, 254);
                                    orb1 = Color.FromArgb(60, 56, 189, 248); orb2 = Color.FromArgb(48, 129, 140, 248);
                                }
                                else if (hr >= 17 && hr < 20)
                                {
                                    c1 = Color.FromRgb(254, 215, 170); c2 = Color.FromRgb(249, 168, 212); c3 = Color.FromRgb(196, 181, 253);
                                    orb1 = Color.FromArgb(68, 251, 146, 60); orb2 = Color.FromArgb(54, 192, 132, 252);
                                }
                                else
                                {
                                    c1 = Color.FromRgb(224, 231, 255); c2 = Color.FromRgb(221, 214, 254); c3 = Color.FromRgb(241, 245, 249);
                                    orb1 = Color.FromArgb(52, 129, 140, 248); orb2 = Color.FromArgb(38, 168, 85, 247);
                                }

                                BgBaseLayer.Background = new LinearGradientBrush(
                                    new GradientStopCollection { new(c1, 0.0), new(c2, 0.50), new(c3, 1.0) },
                                    new Point(0, 0), new Point(1, 1));
                                BgGlow1.Background = MakeGlowOrb(new Point(0.15, 0.14), 0.60, 0.60,
                                    (orb1, 0.0), (Color.FromArgb(0, c2.R, c2.G, c2.B), 1.0));
                                BgGlow2.Background = MakeGlowOrb(new Point(0.85, 0.82), 0.58, 0.58,
                                    (orb2, 0.0), (Color.FromArgb(0, c3.R, c3.G, c3.B), 1.0));
                                BgGlow3.Background = Brushes.Transparent;
                            }
                            break;

                        case "minimal":
                        default:
                            BgBaseLayer.Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)); // #F8FAFC
                            BgGlow1.Background = Brushes.Transparent;
                            BgGlow2.Background = Brushes.Transparent;
                            BgGlow3.Background = Brushes.Transparent;
                            break;
                    }
                }
            }

            ApplySurfacePalette();
            if (WpThemeCards != null) RenderControlCenterThemeCards();
        }

        private void ApplySurfacePalette()
        {
            string theme = GetActiveBgTheme();
            bool isAcrylic = theme == "acrylic";
            bool isParticles = theme == "particles";
            bool isMinimal = theme == "minimal";

            // Dynamic Application Resources so Modals, Folders, Circular Buttons, Inputs match active surface theme
            if (Application.Current?.Resources != null)
            {
                if (_isDarkMode)
                {
                    Application.Current.Resources["AppBgBrush"] = new SolidColorBrush(isMinimal ? Color.FromRgb(11, 15, 25) : isParticles ? Color.FromRgb(5, 7, 19) : Color.FromRgb(9, 11, 26));
                    Application.Current.Resources["NavBgBrush"] = new SolidColorBrush(isAcrylic ? Color.FromArgb(204, 14, 22, 46) : isMinimal ? Color.FromRgb(17, 24, 39) : Color.FromRgb(17, 21, 44));
                    Application.Current.Resources["CardBgBrush"] = GetCardSurfaceBrush();
                    Application.Current.Resources["CardHoverBgBrush"] = new SolidColorBrush(isAcrylic ? Color.FromArgb(204, 28, 42, 78) : Color.FromRgb(32, 39, 74));
                    Application.Current.Resources["SubtleBgBrush"] = GetSubtleSurfaceBrush();
                    Application.Current.Resources["InputBgBrush"] = new SolidColorBrush(isAcrylic ? Color.FromArgb(180, 10, 17, 38) : Color.FromRgb(14, 18, 37));
                    Application.Current.Resources["BorderBrushTheme"] = GetCardBorderBrush();
                    Application.Current.Resources["PrimaryTextBrush"] = new SolidColorBrush(Color.FromRgb(248, 250, 252));
                    Application.Current.Resources["SecondaryTextBrush"] = new SolidColorBrush(Color.FromRgb(148, 163, 198));
                    Application.Current.Resources["MutedTextBrush"] = new SolidColorBrush(Color.FromRgb(104, 116, 150));
                    Application.Current.Resources["SliderTrackBgBrush"] = new SolidColorBrush(Color.FromRgb(44, 53, 93));
                }
                else
                {
                    Application.Current.Resources["AppBgBrush"] = new SolidColorBrush(Color.FromRgb(246, 247, 251));
                    Application.Current.Resources["NavBgBrush"] = new SolidColorBrush(isAcrylic ? Color.FromArgb(214, 255, 255, 255) : Color.FromRgb(255, 255, 255));
                    Application.Current.Resources["CardBgBrush"] = GetCardSurfaceBrush();
                    Application.Current.Resources["CardHoverBgBrush"] = new SolidColorBrush(Color.FromRgb(241, 245, 249));
                    Application.Current.Resources["SubtleBgBrush"] = GetSubtleSurfaceBrush();
                    Application.Current.Resources["InputBgBrush"] = new SolidColorBrush(Color.FromRgb(255, 255, 255));
                    Application.Current.Resources["BorderBrushTheme"] = GetCardBorderBrush();
                    Application.Current.Resources["PrimaryTextBrush"] = new SolidColorBrush(Color.FromRgb(15, 23, 42));
                    Application.Current.Resources["SecondaryTextBrush"] = new SolidColorBrush(Color.FromRgb(71, 85, 105));
                    Application.Current.Resources["MutedTextBrush"] = new SolidColorBrush(Color.FromRgb(148, 163, 184));
                    Application.Current.Resources["SliderTrackBgBrush"] = new SolidColorBrush(Color.FromRgb(203, 213, 225));
                }
            }

            // Top Navigation Bar
            if (TopNavBorder != null)
            {
                if (_isDarkMode)
                {
                    TopNavBorder.Background = theme switch
                    {
                        "acrylic" => new SolidColorBrush(Color.FromArgb(168, 11, 18, 42)),
                        "particles" => new SolidColorBrush(Color.FromArgb(188, 9, 11, 26)),
                        "daydusk" => new SolidColorBrush(Color.FromArgb(190, 15, 16, 36)),
                        "minimal" => new SolidColorBrush(Color.FromRgb(13, 18, 30)),
                        _ => new SolidColorBrush(Color.FromArgb(206, 13, 16, 36))
                    };
                }
                else
                {
                    TopNavBorder.Background = theme switch
                    {
                        "acrylic" => new SolidColorBrush(Color.FromArgb(182, 255, 255, 255)),
                        "minimal" => new SolidColorBrush(Color.FromRgb(255, 255, 255)),
                        _ => new SolidColorBrush(Color.FromArgb(212, 255, 255, 255))
                    };
                }
                TopNavBorder.BorderBrush = GetCardBorderBrush();
            }

            // Home Welcome Banner
            if (BdBanner != null)
            {
                if (_isDarkMode)
                {
                    if (isAcrylic)
                    {
                        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                        g.GradientStops.Add(new GradientStop(Color.FromArgb(138, 22, 32, 68), 0.0));
                        g.GradientStops.Add(new GradientStop(Color.FromArgb(130, 10, 42, 62), 1.0));
                        BdBanner.Background = g;
                    }
                    else
                    {
                        BdBanner.Background = GetCardSurfaceBrush();
                    }
                }
                else
                {
                    if (isMinimal)
                    {
                        BdBanner.Background = new SolidColorBrush(Color.FromRgb(255, 255, 255));
                    }
                    else if (isAcrylic)
                    {
                        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
                        g.GradientStops.Add(new GradientStop(Color.FromArgb(184, 248, 247, 255), 0.0));
                        g.GradientStops.Add(new GradientStop(Color.FromArgb(172, 255, 255, 255), 0.5));
                        g.GradientStops.Add(new GradientStop(Color.FromArgb(184, 238, 252, 254), 1.0));
                        BdBanner.Background = g;
                    }
                    else
                    {
                        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
                        g.GradientStops.Add(new GradientStop(Color.FromArgb(228, 245, 243, 255), 0.0));
                        g.GradientStops.Add(new GradientStop(Color.FromArgb(232, 255, 255, 255), 0.5));
                        g.GradientStops.Add(new GradientStop(Color.FromArgb(228, 240, 253, 250), 1.0));
                        BdBanner.Background = g;
                    }
                }
                BdBanner.BorderBrush = GetCardBorderBrush();
            }

            if (BdUptimeCapsule != null)
            {
                BdUptimeCapsule.Background = GetSubtleSurfaceBrush();
                BdUptimeCapsule.BorderBrush = GetCardBorderBrush();
            }
            if (BdHeatmapCard != null)
            {
                BdHeatmapCard.Background = GetSubtleSurfaceBrush();
                BdHeatmapCard.BorderBrush = GetCardBorderBrush();
            }
            if (FloatingPomodoroWidget != null)
            {
                FloatingPomodoroWidget.Background = _isDarkMode
                    ? (isAcrylic ? new SolidColorBrush(Color.FromArgb(178, 14, 26, 48)) : isParticles ? new SolidColorBrush(Color.FromArgb(196, 16, 19, 42)) : isMinimal ? new SolidColorBrush(Color.FromRgb(17, 24, 39)) : new SolidColorBrush(Color.FromArgb(212, 20, 24, 48)))
                    : (isAcrylic ? new SolidColorBrush(Color.FromArgb(192, 255, 255, 255)) : new SolidColorBrush(Color.FromRgb(255, 255, 255)));
                FloatingPomodoroWidget.BorderBrush = _isDarkMode && isAcrylic
                    ? new SolidColorBrush(Color.FromArgb(62, 56, 189, 248))
                    : GetCardBorderBrush();
            }

            // Let chosen Global Surface Theme show seamlessly behind Subject Workspaces too
            if (SubjectDetailGrid != null) SubjectDetailGrid.Background = Brushes.Transparent;
            if (SubjectWorkspaceGrid != null) SubjectWorkspaceGrid.Background = Brushes.Transparent;
            if (BdSubjectDetailHeader != null)
            {
                BdSubjectDetailHeader.Background = GetCardSurfaceBrush();
                BdSubjectDetailHeader.BorderBrush = GetCardBorderBrush();
            }
            if (BdSubjectDetailTabs != null)
            {
                BdSubjectDetailTabs.Background = GetSubtleSurfaceBrush();
                BdSubjectDetailTabs.BorderBrush = GetCardBorderBrush();
            }
            if (BdFilesActionBar != null)
            {
                BdFilesActionBar.Background = GetCardSurfaceBrush();
                BdFilesActionBar.BorderBrush = GetCardBorderBrush();
            }

            // Refresh Subject cards & Folder cards so translucency updates live on theme change
            if (SubjectsWrapPanel != null && _state != null) RenderSubjects();
            if (_currentSubject != null) RenderFoldersAndFiles();
        }

        private static readonly string MatrixGlyphPool = "0123456789ABCDEFλπΩ";

        private BitmapSource EnsureMatrixGlyphStrip()
        {
            if (_matrixGlyphStripBitmap != null) return _matrixGlyphStripBitmap;

            const double cellW = 24.0;
            const double cellH = 28.0;
            int n = MatrixGlyphPool.Length;
            int pxW = (int)(n * cellW * 2.0);
            int pxH = (int)(cellH * 2.0);

            var typeface = new Typeface(new FontFamily("Consolas, Courier New"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var fg = new SolidColorBrush(Color.FromArgb(185, 16, 185, 129));
            fg.Freeze();

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                for (int i = 0; i < n; i++)
                {
                    var ft = new FormattedText(
                        MatrixGlyphPool[i].ToString(),
                        System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        typeface,
                        20.0,
                        fg,
                        2.0);
                    double ox = i * cellW + Math.Max(0, (cellW - ft.Width) * 0.5);
                    double oy = Math.Max(0, (cellH - ft.Height) * 0.5);
                    dc.DrawText(ft, new Point(ox, oy));
                }
            }

            var strip = new RenderTargetBitmap(pxW, pxH, 192.0, 192.0, PixelFormats.Pbgra32);
            strip.Render(dv);
            strip.Freeze();

            _matrixGlyphStripBitmap = strip;
            return strip;
        }

        public void ApplyParticleEffect(string effectId, bool skipToast = false, bool saveToDisk = true)
        {
            if (_state?.Settings == null) return;
            var valid = PARTICLE_MODES.Find(p => p.Id == effectId) ?? PARTICLE_MODES[0];
            _currentParticleEffect = valid.Id;
            _state.Settings.ParticleEffect = valid.Id;
            if (saveToDisk) SaveState();

            if (TxtActiveParticleBadge != null) TxtActiveParticleBadge.Text = valid.Name;

            if (GlobalThemeCanvas == null) return;
            GlobalThemeCanvas.Children.Clear();
            _activeGlobalParticles.Clear();
            _activeMeshLines.Clear();

            if (valid.Id == "disable")
            {
                UpdateRenderLoopState();
                if (WpParticleCards != null) RenderControlCenterParticleCards();
                if (!skipToast) ShowToast("Particle layer disabled.");
                return;
            }

            double w = Math.Max(ActualWidth, 1440);
            double h = Math.Max(ActualHeight, 900);

            switch (valid.Id)
            {
                case "network":
                    {
                        var nodeBrush = new SolidColorBrush(Color.FromArgb(200, 34, 211, 238));
                        nodeBrush.Freeze();
                        var meshBrush = new SolidColorBrush(Color.FromArgb(40, 124, 92, 255));
                        meshBrush.Freeze();

                        // 35 constellation nodes positioned via TranslateTransform (zero layout invalidation)
                        for (int i = 0; i < 35; i++)
                        {
                            double sz = _rand.Next(4, 8);
                            double x = _rand.NextDouble() * w;
                            double y = _rand.NextDouble() * h;
                            var tt = new TranslateTransform(x, y);
                            var el = new Ellipse
                            {
                                Width = sz,
                                Height = sz,
                                Fill = nodeBrush,
                                RenderTransform = tt,
                                IsHitTestVisible = false
                            };
                            Canvas.SetLeft(el, 0);
                            Canvas.SetTop(el, 0);
                            GlobalThemeCanvas.Children.Add(el);

                            _activeGlobalParticles.Add(new ParticleEngineItem
                            {
                                Element = el,
                                Translate = tt,
                                X = x, Y = y,
                                Vx = (_rand.NextDouble() - 0.5) * 1.2,
                                Vy = (_rand.NextDouble() - 0.5) * 1.2,
                                BaseColor = Color.FromRgb(34, 211, 238)
                            });
                        }

                        // Pre-create 28 unit-quad mesh segments driven purely via MatrixTransform + Opacity
                        for (int i = 0; i < 28; i++)
                        {
                            var mt = new MatrixTransform();
                            var rect = new Rectangle
                            {
                                Width = 1.0,
                                Height = 1.0,
                                Fill = meshBrush,
                                RenderTransform = mt,
                                Opacity = 0.0,
                                IsHitTestVisible = false
                            };
                            Canvas.SetLeft(rect, 0);
                            Canvas.SetTop(rect, 0);
                            GlobalThemeCanvas.Children.Add(rect);
                            _activeMeshLines.Add(new MeshSegmentItem { Rect = rect, Transform = mt });
                        }
                        break;
                    }

                case "cosmic":
                    {
                        var goldBrush = new SolidColorBrush(Color.FromArgb(180, 245, 158, 11));
                        goldBrush.Freeze();
                        var violetBrush = new SolidColorBrush(Color.FromArgb(180, 168, 85, 247));
                        violetBrush.Freeze();

                        // 50 twinkling cosmic dust specks
                        for (int i = 0; i < 50; i++)
                        {
                            int sz = _rand.Next(3, 7);
                            bool isGold = _rand.Next(0, 3) == 0;
                            var col = isGold ? Color.FromRgb(245, 158, 11) : Color.FromRgb(168, 85, 247);
                            double x = _rand.NextDouble() * w;
                            double y = _rand.NextDouble() * h;
                            var tt = new TranslateTransform(x, y);
                            var dot = new Ellipse
                            {
                                Width = sz,
                                Height = sz,
                                Fill = isGold ? goldBrush : violetBrush,
                                RenderTransform = tt,
                                IsHitTestVisible = false
                            };
                            Canvas.SetLeft(dot, 0);
                            Canvas.SetTop(dot, 0);
                            GlobalThemeCanvas.Children.Add(dot);

                            _activeGlobalParticles.Add(new ParticleEngineItem
                            {
                                Element = dot,
                                Translate = tt,
                                X = x, Y = y,
                                Vx = (_rand.NextDouble() - 0.5) * 0.4,
                                Vy = -(_rand.NextDouble() * 0.5 + 0.2),
                                Phase = _rand.NextDouble() * Math.PI * 2,
                                BaseColor = col
                            });
                        }
                        break;
                    }

                case "matrix":
                    {
                        // Pre-baked glyph sprite strip shifted horizontally via TranslateTransform (0 Measure/Arrange churn)
                        var stripBmp = EnsureMatrixGlyphStrip();
                        int cols = Math.Max(1, (int)(w / 40));
                        for (int i = 0; i < cols; i++)
                        {
                            double glyphSz = _rand.Next(13, 20);
                            double cw = glyphSz * 1.2;
                            double ch = glyphSz * 1.4;
                            double x = i * 40 + _rand.Next(0, 10);
                            double y = _rand.NextDouble() * -h;
                            var tt = new TranslateTransform(x, y);
                            var spriteShift = new TranslateTransform(-_rand.Next(MatrixGlyphPool.Length) * cw, 0);

                            var host = new Canvas
                            {
                                Width = cw,
                                Height = ch,
                                ClipToBounds = true,
                                RenderTransform = tt,
                                IsHitTestVisible = false
                            };
                            var img = new Image
                            {
                                Source = stripBmp,
                                Width = cw * MatrixGlyphPool.Length,
                                Height = ch,
                                Stretch = Stretch.Fill,
                                RenderTransform = spriteShift,
                                IsHitTestVisible = false
                            };
                            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                            Canvas.SetLeft(img, 0);
                            Canvas.SetTop(img, 0);
                            host.Children.Add(img);

                            Canvas.SetLeft(host, 0);
                            Canvas.SetTop(host, 0);
                            GlobalThemeCanvas.Children.Add(host);

                            _activeGlobalParticles.Add(new ParticleEngineItem
                            {
                                Element = host,
                                Translate = tt,
                                SpriteShift = spriteShift,
                                X = x, Y = y,
                                R = cw,
                                Speed = _rand.NextDouble() * 6.0 + 3.0
                            });
                        }
                        break;
                    }

                case "orbs":
                    {
                        var orbPink = new RadialGradientBrush(
                            Color.FromArgb(40, 244, 114, 182),
                            Color.FromArgb(0, 244, 114, 182));
                        orbPink.Freeze();
                        var orbCyan = new RadialGradientBrush(
                            Color.FromArgb(40, 34, 211, 238),
                            Color.FromArgb(0, 34, 211, 238));
                        orbCyan.Freeze();

                        // Floating soft bokeh spheres
                        for (int i = 0; i < 12; i++)
                        {
                            int sz = _rand.Next(60, 120);
                            double x = _rand.NextDouble() * w;
                            double y = _rand.NextDouble() * h;
                            var tt = new TranslateTransform(x, y);
                            var orb = new Ellipse
                            {
                                Width = sz,
                                Height = sz,
                                Fill = (_rand.Next(0, 2) == 0) ? orbPink : orbCyan,
                                RenderTransform = tt,
                                IsHitTestVisible = false
                            };
                            Canvas.SetLeft(orb, 0);
                            Canvas.SetTop(orb, 0);
                            GlobalThemeCanvas.Children.Add(orb);

                            _activeGlobalParticles.Add(new ParticleEngineItem
                            {
                                Element = orb,
                                Translate = tt,
                                X = x, Y = y,
                                Vx = (_rand.NextDouble() - 0.5) * 0.8,
                                Vy = (_rand.NextDouble() - 0.5) * 0.8,
                                R = sz
                            });
                        }
                        break;
                    }

                case "lava_lamp":
                    {
                        var lavaRose = new RadialGradientBrush(
                            Color.FromArgb(50, 244, 63, 94),
                            Color.FromArgb(0, 244, 63, 94));
                        lavaRose.Freeze();
                        var lavaAmber = new RadialGradientBrush(
                            Color.FromArgb(50, 245, 158, 11),
                            Color.FromArgb(0, 245, 158, 11));
                        lavaAmber.Freeze();

                        // Rising morphing wax blobs
                        for (int i = 0; i < 9; i++)
                        {
                            int sz = _rand.Next(70, 130);
                            double x = _rand.NextDouble() * w;
                            double y = _rand.NextDouble() * h;
                            var tt = new TranslateTransform(x, y);
                            var blob = new Ellipse
                            {
                                Width = sz,
                                Height = sz,
                                Fill = (_rand.Next(0, 2) == 0) ? lavaRose : lavaAmber,
                                RenderTransform = tt,
                                IsHitTestVisible = false
                            };
                            Canvas.SetLeft(blob, 0);
                            Canvas.SetTop(blob, 0);
                            GlobalThemeCanvas.Children.Add(blob);

                            _activeGlobalParticles.Add(new ParticleEngineItem
                            {
                                Element = blob,
                                Translate = tt,
                                X = x, Y = y,
                                Speed = -(_rand.NextDouble() * 1.5 + 0.5),
                                R = sz
                            });
                        }
                        break;
                    }

                case "meteor":
                    {
                        var meteorBrush = new SolidColorBrush(Color.FromArgb(160, 245, 158, 11));
                        meteorBrush.Freeze();

                        // Diagonal shooting stars
                        for (int i = 0; i < 5; i++)
                        {
                            double x = _rand.NextDouble() * w;
                            double y = _rand.NextDouble() * -h;
                            var tt = new TranslateTransform(x, y);
                            var met = new System.Windows.Shapes.Line
                            {
                                X1 = 0, Y1 = 0,
                                X2 = 60, Y2 = 30,
                                Stroke = meteorBrush,
                                StrokeThickness = 2.0,
                                RenderTransform = tt,
                                IsHitTestVisible = false
                            };
                            Canvas.SetLeft(met, 0);
                            Canvas.SetTop(met, 0);
                            GlobalThemeCanvas.Children.Add(met);

                            _activeGlobalParticles.Add(new ParticleEngineItem
                            {
                                Element = met,
                                Translate = tt,
                                X = x, Y = y,
                                Vx = -(_rand.NextDouble() * 10 + 12),
                                Vy = _rand.NextDouble() * 6 + 7
                            });
                        }
                        break;
                    }

                case "water_ripple":
                    {
                        var rippleBrush = new SolidColorBrush(Color.FromArgb(120, 34, 211, 238));
                        rippleBrush.Freeze();

                        // Expanding concentric water rings scaled purely via ScaleTransform + TranslateTransform (zero Width/Height churn)
                        for (int i = 0; i < 6; i++)
                        {
                            double maxR = _rand.Next(90, 160);
                            double maxD = maxR * 2.0;
                            double r = 10.0;
                            double x = _rand.NextDouble() * w;
                            double y = _rand.NextDouble() * h;
                            double s = r / maxR;

                            var st = new ScaleTransform(s, s);
                            var tt = new TranslateTransform(x - maxR, y - maxR);
                            var tg = new TransformGroup();
                            tg.Children.Add(st);
                            tg.Children.Add(tt);

                            var ring = new Ellipse
                            {
                                Width = maxD,
                                Height = maxD,
                                Stroke = rippleBrush,
                                StrokeThickness = 1.5,
                                RenderTransformOrigin = new Point(0.5, 0.5),
                                RenderTransform = tg,
                                Opacity = Math.Max(0, (1.0 - s) * 0.7),
                                IsHitTestVisible = false
                            };
                            Canvas.SetLeft(ring, 0);
                            Canvas.SetTop(ring, 0);
                            GlobalThemeCanvas.Children.Add(ring);

                            _activeGlobalParticles.Add(new ParticleEngineItem
                            {
                                Element = ring,
                                Translate = tt,
                                Scale = st,
                                X = x, Y = y,
                                R = r,
                                MaxR = maxR,
                                Speed = _rand.NextDouble() * 1.5 + 1.2
                            });
                        }
                        break;
                    }

                case "fading_stars":
                default:
                    {
                        // Appearing & fading starlight with 4-point cross-star flares
                        Color[] starColors = new[]
                        {
                            Color.FromRgb(56, 189, 248),  // Cyan / Sky (#38BDF8)
                            Color.FromRgb(192, 132, 252), // Lavender / Purple (#C084FC)
                            Color.FromRgb(251, 191, 36),  // Amber / Gold (#FBBF24)
                            Color.FromRgb(255, 255, 255), // Pure White (#FFFFFF)
                            Color.FromRgb(244, 114, 182), // Rose Pink (#F472B6)
                            Color.FromRgb(45, 212, 191)   // Emerald / Mint (#2DD4BF)
                        };
                        var starBrushes = new SolidColorBrush[starColors.Length];
                        for (int c = 0; c < starColors.Length; c++)
                        {
                            starBrushes[c] = new SolidColorBrush(starColors[c]);
                            starBrushes[c].Freeze();
                        }

                        for (int i = 0; i < 75; i++)
                        {
                            int ci = i % starColors.Length;
                            var col = starColors[ci];
                            var brush = starBrushes[ci];
                            bool isCrossFlare = (i % 3 == 0);
                            double r = _rand.NextDouble() * 3.0 + 1.5;
                            double x = _rand.NextDouble() * w;
                            double y = _rand.NextDouble() * h;
                            double alpha = _rand.NextDouble() * 0.8 + 0.1;
                            double fadeSpeed = _rand.NextDouble() * 0.018 + 0.008;
                            double fadeDir = (_rand.Next(0, 2) == 0) ? 1.0 : -1.0;
                            var tt = new TranslateTransform(x, y);

                            FrameworkElement el;
                            if (isCrossFlare)
                            {
                                el = new TextBlock
                                {
                                    Text = (_rand.Next(0, 3) == 0) ? "✧" : "✦",
                                    FontSize = _rand.Next(12, 21),
                                    Foreground = brush,
                                    Opacity = alpha,
                                    RenderTransform = tt,
                                    IsHitTestVisible = false
                                };
                            }
                            else
                            {
                                double sz = r * 2.0;
                                el = new Ellipse
                                {
                                    Width = sz,
                                    Height = sz,
                                    Fill = brush,
                                    Opacity = alpha,
                                    RenderTransform = tt,
                                    IsHitTestVisible = false
                                };
                            }

                            Canvas.SetLeft(el, 0);
                            Canvas.SetTop(el, 0);
                            GlobalThemeCanvas.Children.Add(el);

                            _activeGlobalParticles.Add(new ParticleEngineItem
                            {
                                Element = el,
                                Translate = tt,
                                X = x,
                                Y = y,
                                R = r,
                                Phase = alpha,     // Current alpha
                                Speed = fadeSpeed, // Fade speed
                                Vy = fadeDir,      // 1.0 or -1.0
                                BaseColor = col
                            });
                        }
                        break;
                    }
            }

            UpdateRenderLoopState();
            RenderControlCenterParticleCards();
            if (!skipToast) ShowToast($"Particle effect applied: {valid.Name} ✨");
        }

        private void StepGlobalParticles(double dtSeconds)
        {
            double w = Math.Max(ActualWidth, 1440);
            double h = Math.Max(ActualHeight, 900);
            double f = dtSeconds / 0.04; // Preserve original 25 Hz displacement velocity cleanly at any VSync rate

            if (_currentParticleEffect == "network")
            {
                for (int i = 0; i < _activeGlobalParticles.Count; i++)
                {
                    var p = _activeGlobalParticles[i];
                    p.X += p.Vx * f;
                    p.Y += p.Vy * f;
                    if (p.X < 0) { p.X = 0; p.Vx = Math.Abs(p.Vx); }
                    else if (p.X > w) { p.X = w; p.Vx = -Math.Abs(p.Vx); }
                    if (p.Y < 0) { p.Y = 0; p.Vy = Math.Abs(p.Vy); }
                    else if (p.Y > h) { p.Y = h; p.Vy = -Math.Abs(p.Vy); }
                    p.Translate.X = p.X;
                    p.Translate.Y = p.Y;
                }

                int lineIdx = 0;
                for (int i = 0; i < _activeGlobalParticles.Count && lineIdx < _activeMeshLines.Count; i++)
                {
                    var pi = _activeGlobalParticles[i];
                    double x1 = pi.X + 3.0;
                    double y1 = pi.Y + 3.0;
                    for (int j = i + 1; j < _activeGlobalParticles.Count && lineIdx < _activeMeshLines.Count; j++)
                    {
                        var pj = _activeGlobalParticles[j];
                        double dx = (pj.X + 3.0) - x1;
                        double dy = (pj.Y + 3.0) - y1;
                        double distSq = dx * dx + dy * dy;

                        if (distSq < 14400.0) // 120 * 120
                        {
                            double dist = Math.Sqrt(distSq);
                            double invDist = dist > 0.001 ? 1.0 / dist : 0.0;
                            double nx = -dy * invDist;
                            double ny = dx * invDist;

                            var seg = _activeMeshLines[lineIdx++];
                            seg.Transform.Matrix = new Matrix(dx, dy, nx, ny, x1 - 0.5 * nx, y1 - 0.5 * ny);
                            seg.Rect.Opacity = (1.0 - dist / 120.0) * 0.45;
                        }
                    }
                }
                for (; lineIdx < _activeMeshLines.Count; lineIdx++)
                {
                    _activeMeshLines[lineIdx].Rect.Opacity = 0.0;
                }
            }
            else if (_currentParticleEffect == "matrix")
            {
                double glyphSwapProb = 1.0 - Math.Pow(0.7, f);
                int poolLen = MatrixGlyphPool.Length;
                for (int i = 0; i < _activeGlobalParticles.Count; i++)
                {
                    var p = _activeGlobalParticles[i];
                    p.Y += p.Speed * f;
                    if (p.Y > h)
                    {
                        p.Y = -25;
                        p.X = _rand.NextDouble() * w;
                    }
                    if (p.SpriteShift != null && _rand.NextDouble() < glyphSwapProb)
                    {
                        p.SpriteShift.X = -_rand.Next(poolLen) * p.R;
                    }
                    p.Translate.X = p.X;
                    p.Translate.Y = p.Y;
                }
            }
            else if (_currentParticleEffect == "water_ripple")
            {
                for (int i = 0; i < _activeGlobalParticles.Count; i++)
                {
                    var p = _activeGlobalParticles[i];
                    p.R += p.Speed * f;
                    if (p.R > p.MaxR)
                    {
                        p.R = 4.0;
                        p.X = _rand.NextDouble() * w;
                        p.Y = _rand.NextDouble() * h;
                    }
                    double s = p.R / p.MaxR;
                    if (p.Scale != null)
                    {
                        p.Scale.ScaleX = s;
                        p.Scale.ScaleY = s;
                    }
                    p.Translate.X = p.X - p.MaxR;
                    p.Translate.Y = p.Y - p.MaxR;
                    p.Element.Opacity = Math.Max(0.0, (1.0 - s) * 0.7);
                }
            }
            else if (_currentParticleEffect == "fading_stars")
            {
                for (int i = 0; i < _activeGlobalParticles.Count; i++)
                {
                    var p = _activeGlobalParticles[i];
                    p.Phase += p.Vy * p.Speed * f;
                    if (p.Phase >= 1.0)
                    {
                        p.Phase = 1.0;
                        p.Vy = -1.0;
                    }
                    else if (p.Phase <= 0.05)
                    {
                        p.Phase = 0.05;
                        p.Vy = 1.0;
                        p.X = _rand.NextDouble() * w;
                        p.Y = _rand.NextDouble() * h;
                        p.Translate.X = p.X;
                        p.Translate.Y = p.Y;
                    }
                    p.Element.Opacity = p.Phase;
                }
            }
            else if (_currentParticleEffect == "cosmic")
            {
                for (int i = 0; i < _activeGlobalParticles.Count; i++)
                {
                    var p = _activeGlobalParticles[i];
                    p.Phase += (p.Speed > 0 ? p.Speed : 0.04) * f;
                    p.Element.Opacity = 0.2 + 0.7 * (0.5 + 0.5 * Math.Sin(p.Phase));
                    p.X += p.Vx * f;
                    p.Y += p.Vy * f;
                    if (p.Y < -10) p.Y = h + 10;
                    p.Translate.X = p.X;
                    p.Translate.Y = p.Y;
                }
            }
            else if (_currentParticleEffect == "lava_lamp")
            {
                for (int i = 0; i < _activeGlobalParticles.Count; i++)
                {
                    var p = _activeGlobalParticles[i];
                    p.Y += p.Speed * f;
                    if (p.Y < -p.R)
                    {
                        p.Y = h + p.R;
                        p.X = _rand.NextDouble() * w;
                    }
                    p.Translate.X = p.X;
                    p.Translate.Y = p.Y;
                }
            }
            else
            {
                for (int i = 0; i < _activeGlobalParticles.Count; i++)
                {
                    var p = _activeGlobalParticles[i];
                    p.X += p.Vx * f;
                    p.Y += p.Vy * f;
                    if (p.X < -120 || p.X > w + 120 || p.Y < -120 || p.Y > h + 120)
                    {
                        p.X = _rand.NextDouble() * w;
                        p.Y = _rand.NextDouble() * -50;
                    }
                    p.Translate.X = p.X;
                    p.Translate.Y = p.Y;
                }
            }
        }

        // ================= CONTROL CENTER RENDERERS =================

        private void RenderControlCenterThemesAndEffects()
        {
            RenderControlCenterThemeCards();
            RenderControlCenterParticleCards();
            RenderControlCenterZenCards();
        }

        private void RenderControlCenterThemeCards()
        {
            if (WpThemeCards == null) return;
            WpThemeCards.Children.Clear();
            string curTheme = _state.Settings?.BgTheme ?? "aurora";

            foreach (var t in GLOBAL_THEMES)
            {
                bool active = string.Equals(t.Id, curTheme, StringComparison.OrdinalIgnoreCase);
                var bd = new Border
                {
                    Background = active
                        ? new SolidColorBrush(Color.FromArgb(40, 124, 92, 255))
                        : (_isDarkMode ? new SolidColorBrush(Color.FromRgb(22, 27, 44)) : new SolidColorBrush(Color.FromRgb(248, 250, 252))),
                    BorderBrush = active
                        ? new SolidColorBrush(Color.FromRgb(124, 92, 255))
                        : (_isDarkMode ? new SolidColorBrush(Color.FromRgb(40, 48, 86)) : new SolidColorBrush(Color.FromRgb(226, 232, 240))),
                    BorderThickness = new Thickness(active ? 2 : 1),
                    CornerRadius = new CornerRadius(14),
                    Padding = new Thickness(10),
                    Margin = new Thickness(4),
                    Cursor = Cursors.Hand
                };

                var sp = new StackPanel();

                // Gradient Banner Box
                var banner = new Border
                {
                    Height = 44,
                    CornerRadius = new CornerRadius(10),
                    Margin = new Thickness(0, 0, 0, 8),
                    Background = ParseTailwindPreviewBrush(t.GradientPreview)
                };
                banner.Child = new TextBlock
                {
                    Text = t.Icon,
                    FontSize = 18,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                sp.Children.Add(banner);

                // Title & Checkmark
                var gridTitle = new Grid();
                gridTitle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                gridTitle.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var txtTitle = new TextBlock
                {
                    Text = t.Name,
                    FontWeight = FontWeights.Bold,
                    FontSize = 12,
                    Foreground = _isDarkMode ? Brushes.White : new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                    TextWrapping = TextWrapping.Wrap
                };
                Grid.SetColumn(txtTitle, 0);
                gridTitle.Children.Add(txtTitle);

                if (active)
                {
                    var chk = new TextBlock
                    {
                        Text = "✓",
                        FontWeight = FontWeights.Black,
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.FromRgb(124, 92, 255)),
                        Margin = new Thickness(4, 0, 0, 0)
                    };
                    Grid.SetColumn(chk, 1);
                    gridTitle.Children.Add(chk);
                }

                sp.Children.Add(gridTitle);

                var txtDesc = new TextBlock
                {
                    Text = t.Desc,
                    FontSize = 10,
                    Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(138, 149, 183)) : new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                    Margin = new Thickness(0, 2, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                sp.Children.Add(txtDesc);

                bd.Child = sp;
                string tid = t.Id;
                bd.MouseDown += (s, e) => ApplyGlobalBgTheme(tid);
                WpThemeCards.Children.Add(bd);
            }
        }

        private void RenderControlCenterParticleCards()
        {
            if (WpParticleCards == null) return;
            WpParticleCards.Children.Clear();
            string curEffect = _state.Settings?.ParticleEffect ?? "network";

            foreach (var p in PARTICLE_MODES)
            {
                bool active = string.Equals(p.Id, curEffect, StringComparison.OrdinalIgnoreCase);
                var bd = new Border
                {
                    Background = active
                        ? new SolidColorBrush(Color.FromArgb(40, 34, 211, 238))
                        : (_isDarkMode ? new SolidColorBrush(Color.FromRgb(22, 27, 44)) : new SolidColorBrush(Color.FromRgb(248, 250, 252))),
                    BorderBrush = active
                        ? new SolidColorBrush(Color.FromRgb(34, 211, 238))
                        : (_isDarkMode ? new SolidColorBrush(Color.FromRgb(40, 48, 86)) : new SolidColorBrush(Color.FromRgb(226, 232, 240))),
                    BorderThickness = new Thickness(active ? 2 : 1),
                    CornerRadius = new CornerRadius(14),
                    Padding = new Thickness(12),
                    Margin = new Thickness(4),
                    Cursor = Cursors.Hand
                };

                var sp = new StackPanel();

                // Gradient Banner Box
                var banner = new Border
                {
                    Height = 44,
                    CornerRadius = new CornerRadius(10),
                    Margin = new Thickness(0, 0, 0, 8),
                    Background = ParseTailwindPreviewBrush(p.GradientPreview)
                };
                banner.Child = new TextBlock
                {
                    Text = p.Icon,
                    FontSize = 18,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                sp.Children.Add(banner);

                // Title & Checkmark
                var gridTitle = new Grid();
                gridTitle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                gridTitle.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var txtTitle = new TextBlock
                {
                    Text = p.Name,
                    FontWeight = FontWeights.Bold,
                    FontSize = 12.5,
                    Foreground = _isDarkMode ? Brushes.White : new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(txtTitle, 0);
                gridTitle.Children.Add(txtTitle);

                if (active)
                {
                    var chk = new TextBlock
                    {
                        Text = "✓",
                        FontWeight = FontWeights.Black,
                        FontSize = 12.5,
                        Foreground = new SolidColorBrush(Color.FromRgb(34, 211, 238)),
                        Margin = new Thickness(4, 0, 0, 0)
                    };
                    Grid.SetColumn(chk, 1);
                    gridTitle.Children.Add(chk);
                }

                sp.Children.Add(gridTitle);

                var txtDesc = new TextBlock
                {
                    Text = p.Desc,
                    FontSize = 10.5,
                    Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(138, 149, 183)) : new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                    Margin = new Thickness(0, 2, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                sp.Children.Add(txtDesc);

                bd.Child = sp;
                string pid = p.Id;
                bd.MouseDown += (s, e) => ApplyParticleEffect(pid);
                WpParticleCards.Children.Add(bd);
            }
        }

        private void RenderControlCenterZenCards()
        {
            if (WpZenCards == null) return;
            WpZenCards.Children.Clear();
            string curZen = _state.Settings?.ZenScene ?? "north_pole";

            foreach (var z in ZEN_SCENES_LIST)
            {
                bool active = string.Equals(z.Id, curZen, StringComparison.OrdinalIgnoreCase);
                var bd = new Border
                {
                    Background = active
                        ? new SolidColorBrush(Color.FromArgb(40, 245, 158, 11))
                        : (_isDarkMode ? new SolidColorBrush(Color.FromRgb(22, 27, 44)) : new SolidColorBrush(Color.FromRgb(248, 250, 252))),
                    BorderBrush = active
                        ? new SolidColorBrush(Color.FromRgb(245, 158, 11))
                        : (_isDarkMode ? new SolidColorBrush(Color.FromRgb(40, 48, 86)) : new SolidColorBrush(Color.FromRgb(226, 232, 240))),
                    BorderThickness = new Thickness(active ? 2 : 1),
                    CornerRadius = new CornerRadius(14),
                    Padding = new Thickness(10),
                    Margin = new Thickness(4),
                    Cursor = Cursors.Hand
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                // Icon Squircle Box (Crisp Vector Icon matching Zen Dropdown & Reference)
                var iconBox = new Border
                {
                    Width = 36, Height = 36,
                    CornerRadius = new CornerRadius(10),
                    Margin = new Thickness(0, 0, 10, 0),
                    Background = ParseTailwindPreviewBrush(z.GradientPreview)
                };
                iconBox.Child = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse(GetZenSceneIconPathData(z.Id)),
                    Fill = Brushes.White,
                    Width = 15,
                    Height = 15,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(iconBox, 0);
                grid.Children.Add(iconBox);

                // Text Stack
                var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var headerGrid = new Grid();
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var txtTitle = new TextBlock
                {
                    Text = z.Name,
                    FontWeight = FontWeights.Bold,
                    FontSize = 12,
                    Foreground = _isDarkMode ? Brushes.White : new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(txtTitle, 0);
                headerGrid.Children.Add(txtTitle);

                if (active)
                {
                    var chk = new TextBlock
                    {
                        Text = "✓",
                        FontWeight = FontWeights.Black,
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11)),
                        Margin = new Thickness(4, 0, 0, 0)
                    };
                    Grid.SetColumn(chk, 1);
                    headerGrid.Children.Add(chk);
                }

                sp.Children.Add(headerGrid);

                var txtDesc = new TextBlock
                {
                    Text = z.Badge,
                    FontSize = 10,
                    Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(138, 149, 183)) : new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                    Margin = new Thickness(0, 2, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                sp.Children.Add(txtDesc);

                Grid.SetColumn(sp, 1);
                grid.Children.Add(sp);

                bd.Child = grid;
                string zid = z.Id;
                bd.MouseDown += (s, e) => ApplyZenScene(zid);
                WpZenCards.Children.Add(bd);
            }
        }

        private static Brush ParseTailwindPreviewBrush(string commaSeparatedHex)
        {
            try
            {
                var parts = commaSeparatedHex.Split(',');
                if (parts.Length == 1) return (SolidColorBrush)new BrushConverter().ConvertFromString(parts[0]);

                var stops = new GradientStopCollection();
                for (int i = 0; i < parts.Length; i++)
                {
                    double offset = (double)i / (parts.Length - 1);
                    var col = (Color)ColorConverter.ConvertFromString(parts[i].Trim());
                    stops.Add(new GradientStop(col, offset));
                }
                return new LinearGradientBrush(stops, new Point(0, 0), new Point(1, 1));
            }
            catch
            {
                return new SolidColorBrush(Color.FromRgb(124, 92, 255));
            }
        }

        // ================= CONTROL CENTER TABS NAVIGATION =================

        private void BtnTabStorage_Click(object sender, RoutedEventArgs e) => SwitchSettingsTab("storage");
        private void BtnTabAudio_Click(object sender, RoutedEventArgs e) => SwitchSettingsTab("audio");
        private void BtnTabDisplay_Click(object sender, RoutedEventArgs e) => SwitchSettingsTab("display");
        private void BtnTabData_Click(object sender, RoutedEventArgs e) => SwitchSettingsTab("data");
        private void BtnTabSystem_Click(object sender, RoutedEventArgs e) => SwitchSettingsTab("system");

        private void SwitchSettingsTab(string tab)
        {
            SpartStorage.Visibility = (tab == "storage") ? Visibility.Visible : Visibility.Collapsed;
            SpartAudio.Visibility = (tab == "audio") ? Visibility.Visible : Visibility.Collapsed;
            SpartDisplay.Visibility = (tab == "display") ? Visibility.Visible : Visibility.Collapsed;
            SpartData.Visibility = (tab == "data") ? Visibility.Visible : Visibility.Collapsed;
            SpartSystem.Visibility = (tab == "system") ? Visibility.Visible : Visibility.Collapsed;

            var activeStyle = (Style)FindResource("SettingsTabButtonActiveStyle");
            var inactiveStyle = (Style)FindResource("SettingsTabButtonStyle");

            BtnTabStorage.Style = (tab == "storage") ? activeStyle : inactiveStyle;
            BtnTabAudio.Style = (tab == "audio") ? activeStyle : inactiveStyle;
            BtnTabDisplay.Style = (tab == "display") ? activeStyle : inactiveStyle;
            BtnTabData.Style = (tab == "data") ? activeStyle : inactiveStyle;
            BtnTabSystem.Style = (tab == "system") ? activeStyle : inactiveStyle;

            TxtSettingsHeaderTitle.Text = tab switch
            {
                "storage" => "File Storage & Paths",
                "audio" => "Audio & Sound Studio",
                "display" => "Interface & Display",
                "data" => "Data Management",
                "system" => "System Utilities",
                _ => "Control Center"
            };
        }

        private void UpdateControlCenterUiState()
        {
            TxtPathBaseData.Text = _storage.BaseDir;
            TxtPathStudyFiles.Text = _storage.FilesDir;
            TxtPathSounds.Text = _storage.SoundsDir;

            bool is24 = string.Equals(_state.Settings?.TimeFormat, "24h", StringComparison.OrdinalIgnoreCase);
            BtnTimeFormat12.Style = is24 ? (Style)FindResource("SecondaryButtonStyle") : (Style)FindResource("BrandButtonStyle");
            BtnTimeFormat24.Style = is24 ? (Style)FindResource("BrandButtonStyle") : (Style)FindResource("SecondaryButtonStyle");

            bool isAnalog = string.Equals(_state.Settings?.ClockStyle, "analog", StringComparison.OrdinalIgnoreCase);
            BtnClockStyleDigital.Style = isAnalog ? (Style)FindResource("SecondaryButtonStyle") : (Style)FindResource("BrandButtonStyle");
            BtnClockStyleAnalog.Style = isAnalog ? (Style)FindResource("BrandButtonStyle") : (Style)FindResource("SecondaryButtonStyle");

            bool isFs = string.Equals(_state.Settings?.LaunchWindowMode, "fullscreen", StringComparison.OrdinalIgnoreCase);
            BtnLaunchWindowed.Style = isFs ? (Style)FindResource("SecondaryButtonStyle") : (Style)FindResource("BrandButtonStyle");
            BtnLaunchFullscreen.Style = isFs ? (Style)FindResource("BrandButtonStyle") : (Style)FindResource("SecondaryButtonStyle");

            bool inApp = !string.Equals(_state.Settings?.PdfOpenMode, "external", StringComparison.OrdinalIgnoreCase);
            RbPdfInApp.IsChecked = inApp;
            RbPdfExternal.IsChecked = !inApp;

            ChkAudioChimes.IsChecked = _state.Settings?.Notifications ?? true;
            ChkStartupBoot.IsChecked = _state.Settings?.Startup ?? false;
            ChkPerfThrottle.IsChecked = _state.Settings?.PerfThrottle ?? false;
        }

        private void BtnClockStyleDigital_Click(object sender, RoutedEventArgs e)
        {
            _state.Settings.ClockStyle = "digital";
            SaveState();
            UpdateControlCenterUiState();
            UpdatePomoDisplay();
            UpdateLiveClock(force: true);
            ShowToast("Clock style: Monospace Digital");
        }

        private void BtnClockStyleAnalog_Click(object sender, RoutedEventArgs e)
        {
            _state.Settings.ClockStyle = "analog";
            SaveState();
            UpdateControlCenterUiState();
            UpdatePomoDisplay();
            UpdateLiveClock(force: true);
            ShowToast("Clock style: Analog Dial");
        }

        private void BtnTimeFormat12_Click(object sender, RoutedEventArgs e)
        {
            _is24HourClock = false;
            _state.Settings.TimeFormat = "12h";
            SaveState();
            UpdateControlCenterUiState();
            UpdateLiveClock(force: true);
            ShowToast("Time format: 12-Hour (AM/PM)");
        }

        private void BtnTimeFormat24_Click(object sender, RoutedEventArgs e)
        {
            _is24HourClock = true;
            _state.Settings.TimeFormat = "24h";
            SaveState();
            UpdateControlCenterUiState();
            UpdateLiveClock(force: true);
            ShowToast("Time format: 24-Hour Railway");
        }

        private void BtnLaunchWindowed_Click(object sender, RoutedEventArgs e)
        {
            _state.Settings.LaunchWindowMode = "windowed";
            SaveState();
            UpdateControlCenterUiState();
            ShowToast("Default launch: Windowed");
        }

        private void BtnLaunchFullscreen_Click(object sender, RoutedEventArgs e)
        {
            _state.Settings.LaunchWindowMode = "fullscreen";
            SaveState();
            UpdateControlCenterUiState();
            ShowToast("Default launch: Fullscreen");
        }

        private void RbPdfMode_Checked(object sender, RoutedEventArgs e)
        {
            if (_state?.Settings == null) return;
            _state.Settings.PdfOpenMode = (RbPdfExternal.IsChecked == true) ? "external" : "in_app";
            SaveState();
        }

        private void BtnOpenBaseFolder_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(_storage.BaseDir) { UseShellExecute = true }); } catch { }
        }

        private void BtnOpenFilesFolder_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(_storage.FilesDir) { UseShellExecute = true }); } catch { }
        }

        private void BtnOpenSoundsFolder_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(_storage.SoundsDir) { UseShellExecute = true }); } catch { }
        }

        private void BtnUploadCustomSound_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select Ambient White Noise / Focus Audio",
                Filter = "Audio Files (*.mp3;*.wav;*.ogg;*.m4a)|*.mp3;*.wav;*.ogg;*.m4a|All Files (*.*)|*.*",
                Multiselect = true
            };
            if (dlg.ShowDialog() == true && dlg.FileNames.Length > 0)
            {
                foreach (var file in dlg.FileNames)
                {
                    try
                    {
                        string safeName = Path.GetFileName(file);
                        string savedDiskName = $"snd_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{safeName}";
                        string dest = Path.Combine(_storage.SoundsDir, savedDiskName);
                        File.Copy(file, dest, true);

                        _state.CustomSounds.Add(new CustomSoundItem
                        {
                            Id = "custom_" + Guid.NewGuid().ToString("N")[..8],
                            Name = Path.GetFileNameWithoutExtension(safeName),
                            Filename = savedDiskName,
                            OriginalName = safeName,
                            AddedAt = DateTime.UtcNow.ToString("o")
                        });
                    }
                    catch { }
                }
                SaveState();
                _audio.SyncCustomSounds(_state.CustomSounds, _state.Settings?.MixerLevels, _state.Settings?.MixerMaster ?? 80);
                RenderSoundMixerChannels();
                UpdateMixerUIState();
                ShowToast($"{dlg.FileNames.Length} audio file(s) imported into Sound Studio! 🎵");
            }
        }

        private void ChkAudioChimes_Changed(object sender, RoutedEventArgs e)
        {
            if (_state?.Settings == null) return;
            _state.Settings.Notifications = ChkAudioChimes.IsChecked == true;
            SaveState();
        }

        private void ChkStartupBoot_Changed(object sender, RoutedEventArgs e)
        {
            if (_state?.Settings == null) return;
            _state.Settings.Startup = ChkStartupBoot.IsChecked == true;
            SaveState();
        }

        private void ChkPerfThrottle_Changed(object sender, RoutedEventArgs e)
        {
            if (_state?.Settings == null) return;
            _state.Settings.PerfThrottle = ChkPerfThrottle.IsChecked == true;
            _perfSkipToggle = false;
            SaveState();
        }

        private void BtnFactoryReset_Click(object sender, RoutedEventArgs e)
        {
            string confirm = Microsoft.VisualBasic.Interaction.InputBox(
                "Type RESET to permanently wipe all subjects, notes, files, sounds, and restore factory defaults:",
                "Confirm Factory Reset", "");
            if (string.Equals(confirm.Trim(), "RESET", StringComparison.OrdinalIgnoreCase))
            {
                _state.Subjects.Clear();
                _state.Pdfs.Clear();
                _state.Notes.Clear();
                _state.Tasks.Clear();
                _state.Deadlines.Clear();
                _state.Flashcards.Clear();
                _state.Links.Clear();
                _state.Videos.Clear();
                _state.Recordings.Clear();
                _state.Settings = new UserSettings();
                _isPdfDarkMode = _state.Settings.PdfDarkMode;
                UpdatePdfDarkModeButtonUi();
                SaveState();

                RenderSubjects();
                RenderArchivedSubjects();
                ApplyGlobalBgTheme("aurora");
                ApplyParticleEffect("network", skipToast: true);
                ApplyZenScene("north_pole");
                UpdateControlCenterUiState();
                ShowToast("StudyHub has been reset to factory defaults! 🔄");
            }
        }

        // ================= SETTINGS & GITHUB UPDATER =================

        private void BtnNavSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsModalGrid.Visibility = Visibility.Visible;
        }

        private void BtnCloseSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsModalGrid.Visibility = Visibility.Collapsed;
        }

        private void SettingsBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
        {
            SettingsModalGrid.Visibility = Visibility.Collapsed;
        }

        private async void BtnCheckGithubUpdates_Click(object sender, RoutedEventArgs e)
        {
            await CheckForGithubUpdatesAsync(silentOnNoUpdate: false);
        }

        private async Task CheckForGithubUpdatesAsync(bool silentOnNoUpdate)
        {
            string url = TxtSettingsGithubUrl.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                if (!silentOnNoUpdate) ShowToast("Please enter a valid GitHub repository URL");
                return;
            }

            _state.Settings.GithubRepoUrl = url;
            _state.Settings.AutoCheckUpdates = ChkSettingsAutoCheckOnLaunch.IsChecked ?? true;
            SaveState();

            BdUpdateResultBox.Visibility = Visibility.Visible;
            TxtUpdateStatusHeader.Text = "Checking GitHub Releases...";
            TxtUpdateStatusHeader.Foreground = new SolidColorBrush(Color.FromRgb(34, 211, 238));
            TxtUpdateStatusDetails.Text = $"Connecting to {url}...";
            BtnInstallUpdateNow.Visibility = Visibility.Collapsed;

            try
            {
                var result = await _updater.CheckForUpdatesAsync(url);
                if (result.Success)
                {
                    if (result.IsUpdateAvailable)
                    {
                        TxtUpdateStatusHeader.Text = $"🎉 New Version Available: {result.LatestVersion}!";
                        TxtUpdateStatusHeader.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                        TxtUpdateStatusDetails.Text = $"Release: {result.ReleaseTitle}\n{result.ReleaseNotes}";
                        BtnInstallUpdateNow.Visibility = Visibility.Visible;
                        ShowToast($"New StudyHub update found: {result.LatestVersion}! 🚀");
                    }
                    else
                    {
                        TxtUpdateStatusHeader.Text = $"✅ StudyHub is Up to Date (v{result.CurrentVersion})";
                        TxtUpdateStatusHeader.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                        TxtUpdateStatusDetails.Text = $"Latest commit: {result.LatestCommitSha}. No updates pending.";
                        if (!silentOnNoUpdate) ShowToast("You are on the latest version of StudyHub!");
                    }
                }
                else
                {
                    TxtUpdateStatusHeader.Text = "Update Check Failed";
                    TxtUpdateStatusHeader.Foreground = new SolidColorBrush(Color.FromRgb(244, 63, 94));
                    TxtUpdateStatusDetails.Text = result.ErrorMessage;
                }
            }
            catch (Exception ex)
            {
                TxtUpdateStatusHeader.Text = "Error";
                TxtUpdateStatusDetails.Text = ex.Message;
            }
        }

        private async void BtnInstallUpdateNow_Click(object sender, RoutedEventArgs e)
        {
            ShowToast("Downloading update from GitHub... ⏳");
            BtnInstallUpdateNow.IsEnabled = false;

            try
            {
                string url = TxtSettingsGithubUrl.Text.Trim();
                var result = await _updater.CheckForUpdatesAsync(url);
                if (result.IsUpdateAvailable && !string.IsNullOrEmpty(result.DownloadUrl))
                {
                    var progress = new Progress<(int percent, string status)>(p =>
                    {
                        TxtUpdateStatusDetails.Text = $"{p.status} ({p.percent}%)";
                    });

                    var (ok, error) = await _updater.DownloadAndApplyUpdateAsync(result, progress);
                    if (ok)
                    {
                        Application.Current.Shutdown();
                    }
                    else
                    {
                        ShowToast($"Update failed: {error}");
                    }
                }
            }
            catch (Exception ex)
            {
                ShowToast($"Error installing update: {ex.Message}");
            }
            finally
            {
                BtnInstallUpdateNow.IsEnabled = true;
            }
        }

        private void BtnExportJsonBackup_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                FileName = $"StudyHub_Backup_{DateTime.Now:yyyyMMdd}.json",
                Filter = "JSON Files (*.json)|*.json"
            };
            if (dlg.ShowDialog() == true)
            {
                string json = _storage.LoadRawStateJson();
                File.WriteAllText(dlg.FileName, json);
                ShowToast("Backup exported successfully! 💾");
            }
        }

        private void BtnImportJsonBackup_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "JSON Files (*.json)|*.json"
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    string json = File.ReadAllText(dlg.FileName);
                    _storage.SaveRawStateJsonAsync(json).GetAwaiter().GetResult();
                    _state = _storage.LoadState();
                    RenderSubjects();
                    ShowToast("Backup restored! ✅");
                }
                catch (Exception ex)
                {
                    ShowToast($"Error importing backup: {ex.Message}");
                }
            }
        }

        // ================= ADD / EDIT SUBJECT MODAL =================

        private void BtnOpenAddSubjectModal_Click(object sender, RoutedEventArgs e)
        {
            _editingSubjectId = null;
            _selectedModalIcon = "🎓";
            _selectedModalColor = "from-violet-600 via-purple-500 to-fuchsia-500";
            TxtSubjectModalHeader.Text = "Add New Course";
            TxtModalSubjectName.Text = "";
            InitSubjectModalPickers();
            SubjectModalGrid.Visibility = Visibility.Visible;
        }

        private void BtnEditCurrentSubject_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSubject == null) return;
            _editingSubjectId = _currentSubject.Id;
            _selectedModalIcon = CleanIconText(_currentSubject.Icon);
            _selectedModalColor = string.IsNullOrWhiteSpace(_currentSubject.Color) ? "from-violet-600 via-purple-500 to-fuchsia-500" : _currentSubject.Color;
            TxtSubjectModalHeader.Text = "Edit Course";
            TxtModalSubjectName.Text = _currentSubject.Name;
            InitSubjectModalPickers();
            SubjectModalGrid.Visibility = Visibility.Visible;
        }

        private void InitSubjectModalPickers()
        {
            IconPickerWrapPanel.Children.Clear();
            string curIcon = CleanIconText(_selectedModalIcon);
            var icons = new[] { "🎓", "📚", "💻", "🔬", "📐", "✈️", "⚙️", "⚛️", "🎨", "📈" };
            foreach (var ic in icons)
            {
                bool isSel = string.Equals(ic, curIcon, StringComparison.Ordinal);
                var b = new Border
                {
                    Width = 36,
                    Height = 36,
                    CornerRadius = new CornerRadius(10),
                    Background = _isDarkMode ? new SolidColorBrush(Color.FromRgb(24, 29, 56)) : new SolidColorBrush(Color.FromRgb(241, 245, 249)),
                    BorderBrush = isSel ? new SolidColorBrush(Color.FromRgb(124, 92, 255)) : null,
                    BorderThickness = new Thickness(isSel ? 2 : 0),
                    Margin = new Thickness(0, 0, 8, 8),
                    Cursor = Cursors.Hand
                };
                b.Child = new TextBlock
                {
                    Text = ic,
                    FontSize = 16,
                    Foreground = _isDarkMode ? new SolidColorBrush(Color.FromRgb(248, 250, 252)) : new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                b.MouseDown += (s, e) =>
                {
                    _selectedModalIcon = ic;
                    foreach (Border el in IconPickerWrapPanel.Children)
                    {
                        el.BorderBrush = null;
                        el.BorderThickness = new Thickness(0);
                    }
                    b.BorderBrush = new SolidColorBrush(Color.FromRgb(124, 92, 255));
                    b.BorderThickness = new Thickness(2);
                };
                IconPickerWrapPanel.Children.Add(b);
            }

            ColorPickerWrapPanel.Children.Clear();
            var colors = new[]
            {
                "from-violet-600 via-purple-500 to-fuchsia-500",
                "from-sky-500 via-cyan-400 to-teal-400",
                "from-emerald-500 via-teal-400 to-cyan-500",
                "from-amber-500 via-orange-500 to-rose-500",
                "from-rose-500 via-pink-500 to-fuchsia-500",
                "from-indigo-500 via-blue-500 to-cyan-400",
                "from-fuchsia-500 via-purple-500 to-indigo-500",
                "from-lime-500 via-emerald-500 to-teal-500"
            };
            foreach (var col in colors)
            {
                var c = col;
                bool isSel = string.Equals(c, _selectedModalColor, StringComparison.OrdinalIgnoreCase);
                var b = new Border
                {
                    Width = 32,
                    Height = 32,
                    CornerRadius = new CornerRadius(16),
                    Background = ParseColorBrush(c, horizontal: false),
                    BorderBrush = isSel ? (_isDarkMode ? Brushes.White : new SolidColorBrush(Color.FromRgb(15, 23, 42))) : null,
                    BorderThickness = new Thickness(isSel ? 2.5 : 0),
                    Margin = new Thickness(0, 0, 8, 8),
                    Cursor = Cursors.Hand
                };
                b.MouseDown += (s, e) =>
                {
                    _selectedModalColor = c;
                    foreach (Border el in ColorPickerWrapPanel.Children)
                    {
                        el.BorderBrush = null;
                        el.BorderThickness = new Thickness(0);
                    }
                    b.BorderBrush = _isDarkMode ? Brushes.White : new SolidColorBrush(Color.FromRgb(15, 23, 42));
                    b.BorderThickness = new Thickness(2.5);
                };
                ColorPickerWrapPanel.Children.Add(b);
            }
        }

        private void BtnCloseSubjectModal_Click(object sender, RoutedEventArgs e)
        {
            SubjectModalGrid.Visibility = Visibility.Collapsed;
        }

        private void SubjectModalBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
        {
            SubjectModalGrid.Visibility = Visibility.Collapsed;
        }

        private void BtnSaveSubjectModal_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtModalSubjectName.Text.Trim();
            if (string.IsNullOrEmpty(name)) return;

            if (_editingSubjectId != null)
            {
                var existing = _state.Subjects.Find(s => s.Id == _editingSubjectId);
                if (existing != null)
                {
                    existing.Name = name;
                    existing.Icon = _selectedModalIcon;
                    existing.Color = _selectedModalColor;
                    if (_currentSubject?.Id == existing.Id)
                    {
                        TxtActiveSubjectTitle.Text = name;
                        TxtSubjectDetailIcon.Text = _selectedModalIcon;
                        BdSubjectIconBox.Background = ParseColorBrush(_selectedModalColor);
                    }
                }
            }
            else
            {
                var newSub = new SubjectItem
                {
                    Id = "sub_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Name = name,
                    Icon = _selectedModalIcon,
                    Color = _selectedModalColor,
                    Folders = new List<string> { "Lectures", "Tutorials", "Assignments", "Quizzes", "Labs", "References" }
                };
                _state.Subjects.Add(newSub);
            }

            SaveState();
            RenderSubjects();
            SubjectModalGrid.Visibility = Visibility.Collapsed;
            ShowToast("Course saved! 🎓");
        }

        private void BtnArchiveCurrentSubject_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSubject == null) return;
            _currentSubject.Archived = true;
            SaveState();
            GoToHome();
            RenderArchivedSubjects();
            ShowToast("Course archived 📦");
        }

        // ================= SEARCH & KEYBOARD SHORTCUTS =================

        private void TxtGlobalSearch_GotFocus(object sender, RoutedEventArgs e)
        {
            TxtGlobalSearchPlaceholder.Visibility = Visibility.Collapsed;
        }

        private void TxtGlobalSearch_LostFocus(object sender, RoutedEventArgs e)
        {
            TxtGlobalSearchPlaceholder.Visibility = string.IsNullOrEmpty(TxtGlobalSearch.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void TxtGlobalSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                string q = TxtGlobalSearch.Text.Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(q))
                {
                    var match = _state.Subjects.Find(s => s.Name.ToLowerInvariant().Contains(q));
                    if (match != null)
                    {
                        OpenSubject(match);
                        ShowToast($"Jumped to {match.Name} 🔍");
                    }
                    else
                    {
                        ShowToast($"No matching course found for '{q}'");
                    }
                }
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F10)
            {
                ToggleZenMode();
                e.Handled = true;
            }
            else if (e.Key == Key.F11)
            {
                SetFullscreen(!_isFullscreen);
                e.Handled = true;
            }
            else if (e.Key == Key.M && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                SoundMixerDrawerGrid.Visibility = (SoundMixerDrawerGrid.Visibility == Visibility.Visible) ? Visibility.Collapsed : Visibility.Visible;
                e.Handled = true;
            }
            else if (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                TxtGlobalSearch.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (PdfViewerOverlayGrid.Visibility == Visibility.Visible) ClosePdfViewer();
                else if (TopBarTimerDropdownGrid != null && TopBarTimerDropdownGrid.Visibility == Visibility.Visible) TopBarTimerDropdownGrid.Visibility = Visibility.Collapsed;
                else if (ZenSceneDropdownOverlay != null && ZenSceneDropdownOverlay.Visibility == Visibility.Visible) CloseZenSceneDropdown();
                else if (ZenModeOverlayGrid.Visibility == Visibility.Visible) ToggleZenMode();
                else if (SettingsModalGrid.Visibility == Visibility.Visible) SettingsModalGrid.Visibility = Visibility.Collapsed;
                else if (SoundMixerDrawerGrid.Visibility == Visibility.Visible) SoundMixerDrawerGrid.Visibility = Visibility.Collapsed;
                else if (SubjectModalGrid.Visibility == Visibility.Visible) SubjectModalGrid.Visibility = Visibility.Collapsed;
                else if (_currentSubject != null) GoToHome();
            }
        }

        private void BtnNavTheme_Click(object sender, RoutedEventArgs e)
        {
            ApplyTheme(!_isDarkMode);
            ShowToast(_isDarkMode ? "Dark Theme applied 🌙" : "Daylight Theme applied ✨");
        }

        private void ApplyTheme(bool isDark, bool saveToDisk = true)
        {
            _isDarkMode = isDark;
            if (GridThemeSunIcon != null) GridThemeSunIcon.Visibility = isDark ? Visibility.Visible : Visibility.Collapsed;
            if (GridThemeMoonIcon != null) GridThemeMoonIcon.Visibility = isDark ? Visibility.Collapsed : Visibility.Visible;
            if (BtnNavTheme != null)
            {
                BtnNavTheme.ToolTip = isDark ? "Switch to Light Theme" : "Switch to Dark Theme";
            }

            // 1. Global Window & Particle Layer Visibility
            Background = isDark ? new SolidColorBrush(Color.FromRgb(8, 10, 24)) : new SolidColorBrush(Color.FromRgb(245, 246, 251));

            if (GlobalThemeCanvas != null)
            {
                GlobalThemeCanvas.Visibility = isDark ? Visibility.Visible : Visibility.Collapsed;
            }
            UpdateRenderLoopState();

            // 2. Top Nav & Text Chromatic Tokens
            if (TxtBrandTitle != null)
            {
                TxtBrandTitle.Foreground = isDark ? new SolidColorBrush(Color.FromRgb(248, 250, 252)) : new SolidColorBrush(Color.FromRgb(15, 23, 42));
            }
            if (NavClockBorder != null)
            {
                NavClockBorder.Background = isDark ? new SolidColorBrush(Color.FromArgb(204, 18, 23, 46)) : new SolidColorBrush(Color.FromArgb(220, 241, 245, 249));
                NavClockBorder.BorderBrush = isDark ? new SolidColorBrush(Color.FromArgb(80, 72, 92, 148)) : new SolidColorBrush(Color.FromRgb(226, 232, 240));
                TxtNavClockTime.Foreground = isDark ? new SolidColorBrush(Color.FromRgb(248, 250, 252)) : new SolidColorBrush(Color.FromRgb(15, 23, 42));
                TxtNavClockDate.Foreground = isDark ? new SolidColorBrush(Color.FromRgb(138, 149, 183)) : new SolidColorBrush(Color.FromRgb(100, 116, 139));
            }
            if (NavTimerPill != null)
            {
                NavTimerPill.Background = isDark ? new SolidColorBrush(Color.FromArgb(215, 38, 19, 36)) : new SolidColorBrush(Color.FromArgb(228, 253, 242, 248));
                NavTimerPill.BorderBrush = isDark ? new SolidColorBrush(Color.FromRgb(74, 28, 54)) : new SolidColorBrush(Color.FromRgb(251, 207, 232));
                TxtNavTimerDisplay.Foreground = isDark ? new SolidColorBrush(Color.FromRgb(244, 114, 182)) : new SolidColorBrush(Color.FromRgb(219, 39, 119));
            }
            if (NavSearchPill != null)
            {
                NavSearchPill.Background = isDark ? new SolidColorBrush(Color.FromArgb(204, 18, 23, 46)) : new SolidColorBrush(Color.FromArgb(220, 241, 245, 249));
                NavSearchPill.BorderBrush = isDark ? new SolidColorBrush(Color.FromArgb(80, 72, 92, 148)) : new SolidColorBrush(Color.FromRgb(226, 232, 240));
                TxtGlobalSearch.Foreground = isDark ? new SolidColorBrush(Color.FromRgb(248, 250, 252)) : new SolidColorBrush(Color.FromRgb(15, 23, 42));
                TxtGlobalSearchPlaceholder.Foreground = isDark ? new SolidColorBrush(Color.FromRgb(138, 149, 183)) : new SolidColorBrush(Color.FromRgb(148, 163, 184));
            }
            if (BdNavSearchShortcut != null && TxtNavSearchShortcut != null)
            {
                BdNavSearchShortcut.Background = isDark ? new SolidColorBrush(Color.FromRgb(34, 40, 70)) : new SolidColorBrush(Color.FromRgb(255, 255, 255));
                BdNavSearchShortcut.BorderBrush = isDark ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(226, 232, 240));
                BdNavSearchShortcut.BorderThickness = new Thickness(isDark ? 0 : 1);
                TxtNavSearchShortcut.Foreground = isDark ? new SolidColorBrush(Color.FromRgb(138, 149, 183)) : new SolidColorBrush(Color.FromRgb(100, 116, 139));
            }

            // 3. Home View Foregrounds & Accents
            if (BdGreetingTag != null)
            {
                BdGreetingTag.Background = isDark ? new SolidColorBrush(Color.FromArgb(215, 29, 23, 53)) : new SolidColorBrush(Color.FromRgb(238, 242, 255));
                BdGreetingTag.BorderBrush = isDark ? new SolidColorBrush(Color.FromRgb(59, 43, 99)) : new SolidColorBrush(Color.FromRgb(199, 210, 254));
            }
            if (TxtGreetingBadge != null)
            {
                TxtGreetingBadge.Foreground = isDark ? new SolidColorBrush(Color.FromRgb(192, 132, 252)) : new SolidColorBrush(Color.FromRgb(99, 102, 241));
            }
            if (TxtWelcomeYour != null)
            {
                TxtWelcomeYour.Foreground = isDark ? new SolidColorBrush(Color.FromRgb(248, 250, 252)) : new SolidColorBrush(Color.FromRgb(15, 23, 42));
            }
            if (TxtWelcomeRocket != null)
            {
                TxtWelcomeRocket.Foreground = isDark ? new SolidColorBrush(Color.FromRgb(248, 250, 252)) : new SolidColorBrush(Color.FromRgb(15, 23, 42));
            }
            if (TxtHomeSubtitle != null)
            {
                TxtHomeSubtitle.Foreground = isDark ? new SolidColorBrush(Color.FromRgb(148, 163, 198)) : new SolidColorBrush(Color.FromRgb(71, 85, 105));
            }
            if (BdUptimeClockBox != null)
            {
                BdUptimeClockBox.Background = isDark ? new SolidColorBrush(Color.FromRgb(12, 43, 54)) : new SolidColorBrush(Color.FromRgb(224, 242, 254));
            }
            if (BdUptimeCrownBox != null)
            {
                BdUptimeCrownBox.Background = isDark ? new SolidColorBrush(Color.FromRgb(52, 39, 18)) : new SolidColorBrush(Color.FromRgb(254, 243, 199));
            }
            if (BdUptimeDivider != null)
            {
                BdUptimeDivider.Background = isDark ? new SolidColorBrush(Color.FromRgb(32, 42, 74)) : new SolidColorBrush(Color.FromRgb(226, 232, 240));
            }
            if (TxtUptimeActiveLabel != null)
            {
                TxtUptimeActiveLabel.Foreground = isDark ? new SolidColorBrush(Color.FromRgb(148, 163, 198)) : new SolidColorBrush(Color.FromRgb(71, 85, 105));
            }
            if (TxtUptimeTopLabel != null)
            {
                TxtUptimeTopLabel.Foreground = isDark ? new SolidColorBrush(Color.FromRgb(148, 163, 198)) : new SolidColorBrush(Color.FromRgb(71, 85, 105));
            }
            if (TxtHomeSessionUptime != null)
            {
                TxtHomeSessionUptime.Foreground = isDark ? new SolidColorBrush(Color.FromRgb(248, 250, 252)) : new SolidColorBrush(Color.FromRgb(15, 23, 42));
            }
            if (TxtHomeTopSubject != null)
            {
                TxtHomeTopSubject.Foreground = isDark ? new SolidColorBrush(Color.FromRgb(248, 250, 252)) : new SolidColorBrush(Color.FromRgb(15, 23, 42));
            }
            if (BdHeatmapDivider != null)
            {
                BdHeatmapDivider.Background = isDark ? new SolidColorBrush(Color.FromRgb(32, 42, 74)) : new SolidColorBrush(Color.FromRgb(226, 232, 240));
            }
            if (TxtHeatmapTitle != null)
            {
                TxtHeatmapTitle.Foreground = isDark ? new SolidColorBrush(Color.FromRgb(248, 250, 252)) : new SolidColorBrush(Color.FromRgb(51, 65, 85));
            }
            if (TxtFocusRatioLabel != null)
            {
                TxtFocusRatioLabel.Foreground = isDark ? new SolidColorBrush(Color.FromRgb(148, 163, 198)) : new SolidColorBrush(Color.FromRgb(71, 85, 105));
            }
            if (TxtHomeFocusRatio != null)
            {
                TxtHomeFocusRatio.Foreground = isDark ? new SolidColorBrush(Color.FromRgb(248, 250, 252)) : new SolidColorBrush(Color.FromRgb(15, 23, 42));
            }
            if (BdSectionIconBox != null)
            {
                BdSectionIconBox.Background = isDark ? new SolidColorBrush(Color.FromRgb(32, 24, 62)) : new SolidColorBrush(Color.FromRgb(238, 242, 255));
            }
            if (TxtSectionTitle != null)
            {
                TxtSectionTitle.Foreground = isDark ? new SolidColorBrush(Color.FromRgb(248, 250, 252)) : new SolidColorBrush(Color.FromRgb(15, 23, 42));
            }

            // 4. Floating Timer Text, Controls & Drawers
            if (TxtFloatingTimerHeader != null)
            {
                TxtFloatingTimerHeader.Foreground = isDark ? new SolidColorBrush(Color.FromRgb(248, 250, 252)) : new SolidColorBrush(Color.FromRgb(15, 23, 42));
            }
            var timerGradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            if (isDark)
            {
                timerGradient.GradientStops.Add(new GradientStop(Color.FromRgb(96, 165, 250), 0.0));
                timerGradient.GradientStops.Add(new GradientStop(Color.FromRgb(34, 211, 238), 0.52));
                timerGradient.GradientStops.Add(new GradientStop(Color.FromRgb(129, 140, 248), 1.0));
            }
            else
            {
                timerGradient.GradientStops.Add(new GradientStop(Color.FromRgb(79, 70, 229), 0.0));
                timerGradient.GradientStops.Add(new GradientStop(Color.FromRgb(2, 132, 199), 0.52));
                timerGradient.GradientStops.Add(new GradientStop(Color.FromRgb(6, 182, 212), 1.0));
            }
            if (TxtFloatingTimerDisplay != null) TxtFloatingTimerDisplay.Foreground = timerGradient;
            if (TxtDropdownTimerDisplay != null) TxtDropdownTimerDisplay.Foreground = timerGradient;

            if (PomoMinTimeChip != null && TxtPomoMinTime != null)
            {
                PomoMinTimeChip.Background = isDark ? new SolidColorBrush(Color.FromArgb(200, 26, 34, 66)) : new SolidColorBrush(Color.FromRgb(241, 245, 249));
                PomoMinTimeChip.BorderBrush = isDark ? new SolidColorBrush(Color.FromRgb(51, 65, 117)) : new SolidColorBrush(Color.FromRgb(203, 213, 225));
                TxtPomoMinTime.Foreground = isDark ? new SolidColorBrush(Color.FromRgb(56, 189, 248)) : new SolidColorBrush(Color.FromRgb(79, 70, 229));
            }
            if (PomoSettingsPanel != null)
            {
                PomoSettingsPanel.Background = isDark ? new SolidColorBrush(Color.FromRgb(16, 21, 45)) : new SolidColorBrush(Color.FromRgb(248, 250, 252));
                PomoSettingsPanel.BorderBrush = isDark ? new SolidColorBrush(Color.FromRgb(42, 51, 92)) : new SolidColorBrush(Color.FromRgb(226, 232, 240));
            }
            if (BdWorkStepperCard != null && BdBreakStepperCard != null && TxtPomoWorkInput != null && TxtPomoBreakInput != null)
            {
                var stepBg = isDark ? new SolidColorBrush(Color.FromRgb(22, 29, 61)) : new SolidColorBrush(Color.FromRgb(255, 255, 255));
                var stepBrd = isDark ? new SolidColorBrush(Color.FromRgb(42, 53, 98)) : new SolidColorBrush(Color.FromRgb(226, 232, 240));
                BdWorkStepperCard.Background = stepBg;
                BdWorkStepperCard.BorderBrush = stepBrd;
                BdBreakStepperCard.Background = stepBg;
                BdBreakStepperCard.BorderBrush = stepBrd;
                var numFg = isDark ? new SolidColorBrush(Color.FromRgb(248, 250, 252)) : new SolidColorBrush(Color.FromRgb(15, 23, 42));
                TxtPomoWorkInput.Foreground = numFg;
                TxtPomoBreakInput.Foreground = numFg;
                var lblFg = isDark ? new SolidColorBrush(Color.FromRgb(148, 163, 184)) : new SolidColorBrush(Color.FromRgb(100, 116, 139));
                if (TxtWorkStepperLbl != null) TxtWorkStepperLbl.Foreground = lblFg;
                if (TxtBreakStepperLbl != null) TxtBreakStepperLbl.Foreground = lblFg;
                if (TxtPomoSettingsLabel != null) TxtPomoSettingsLabel.Foreground = lblFg;
            }
            if (BdDockTrack != null)
            {
                BdDockTrack.Background = isDark ? new SolidColorBrush(Color.FromRgb(22, 29, 61)) : new SolidColorBrush(Color.FromRgb(241, 245, 249));
                BdDockTrack.BorderBrush = isDark ? new SolidColorBrush(Color.FromRgb(42, 53, 98)) : new SolidColorBrush(Color.FromRgb(203, 213, 225));
            }
            if (BtnTimerReset != null && IconTimerResetPath != null)
            {
                BtnTimerReset.Background = isDark ? new SolidColorBrush(Color.FromRgb(37, 44, 78)) : new SolidColorBrush(Color.FromRgb(241, 245, 249));
                BtnTimerReset.BorderBrush = isDark ? new SolidColorBrush(Color.FromRgb(51, 60, 102)) : new SolidColorBrush(Color.FromRgb(203, 213, 225));
                IconTimerResetPath.Fill = isDark ? new SolidColorBrush(Color.FromRgb(226, 232, 240)) : new SolidColorBrush(Color.FromRgb(51, 65, 85));
            }
            if (TxtFloatingTimerStatus != null)
            {
                TxtFloatingTimerStatus.Foreground = isDark ? new SolidColorBrush(Color.FromRgb(148, 163, 184)) : new SolidColorBrush(Color.FromRgb(100, 116, 139));
            }
            if (BdTopBarTimerDropdown != null)
            {
                BdTopBarTimerDropdown.Background = isDark ? new SolidColorBrush(Color.FromRgb(20, 24, 52)) : new SolidColorBrush(Color.FromRgb(255, 255, 255));
                BdTopBarTimerDropdown.BorderBrush = isDark ? new SolidColorBrush(Color.FromRgb(40, 48, 86)) : new SolidColorBrush(Color.FromRgb(226, 232, 240));
            }
            if (PomoMinimizedBarTrack != null)
            {
                PomoMinimizedBarTrack.Background = isDark ? new SolidColorBrush(Color.FromRgb(37, 46, 82)) : new SolidColorBrush(Color.FromRgb(203, 213, 225));
            }
            if (PomoMinimizedBarAura != null)
            {
                PomoMinimizedBarAura.Opacity = isDark ? 1.0 : 0.65;
            }
            if (TxtDropdownHeader != null) TxtDropdownHeader.Foreground = isDark ? new SolidColorBrush(Color.FromRgb(248, 250, 252)) : new SolidColorBrush(Color.FromRgb(15, 23, 42));
            if (TxtDropdownTimerStatus != null) TxtDropdownTimerStatus.Foreground = isDark ? new SolidColorBrush(Color.FromRgb(148, 163, 184)) : new SolidColorBrush(Color.FromRgb(100, 116, 139));
            UpdateTimerHeaderIconColors();
            UpdateTimerDockButtonsUi();
            if (BdNavDrawerPanel != null)
            {
                BdNavDrawerPanel.Background = isDark ? new SolidColorBrush(Color.FromRgb(14, 18, 37)) : new SolidColorBrush(Color.FromRgb(255, 255, 255));
                BdNavDrawerPanel.BorderBrush = isDark ? new SolidColorBrush(Color.FromRgb(32, 39, 74)) : new SolidColorBrush(Color.FromRgb(226, 232, 240));
            }
            if (BdSoundMixerPanel != null)
            {
                BdSoundMixerPanel.Background = isDark ? new SolidColorBrush(Color.FromRgb(17, 21, 44)) : new SolidColorBrush(Color.FromRgb(255, 255, 255));
                BdSoundMixerPanel.BorderBrush = isDark ? new SolidColorBrush(Color.FromRgb(40, 48, 86)) : new SolidColorBrush(Color.FromRgb(226, 232, 240));
            }
            if (TxtSoundMixerHeader != null)
            {
                TxtSoundMixerHeader.Foreground = isDark ? new SolidColorBrush(Color.FromRgb(244, 246, 255)) : new SolidColorBrush(Color.FromRgb(15, 23, 42));
            }
            if (BdSoundMixerMasterCard != null)
            {
                BdSoundMixerMasterCard.Background = isDark ? new SolidColorBrush(Color.FromRgb(24, 29, 56)) : new SolidColorBrush(Color.FromRgb(248, 250, 252));
                BdSoundMixerMasterCard.BorderBrush = isDark ? new SolidColorBrush(Color.FromRgb(40, 48, 86)) : new SolidColorBrush(Color.FromRgb(226, 232, 240));
            }
            if (BdSoundMixerFooter != null)
            {
                BdSoundMixerFooter.Background = isDark ? new SolidColorBrush(Color.FromRgb(24, 29, 56)) : new SolidColorBrush(Color.FromRgb(248, 250, 252));
                BdSoundMixerFooter.BorderBrush = isDark ? new SolidColorBrush(Color.FromRgb(40, 48, 86)) : new SolidColorBrush(Color.FromRgb(226, 232, 240));
            }
            if (BdFactoryReset != null && TxtFactoryResetDesc != null)
            {
                BdFactoryReset.Background = isDark ? new SolidColorBrush(Color.FromRgb(43, 20, 32)) : new SolidColorBrush(Color.FromRgb(255, 241, 242));
                BdFactoryReset.BorderBrush = isDark ? new SolidColorBrush(Color.FromRgb(119, 30, 45)) : new SolidColorBrush(Color.FromRgb(254, 205, 211));
                TxtFactoryResetDesc.Foreground = isDark ? new SolidColorBrush(Color.FromRgb(252, 165, 165)) : new SolidColorBrush(Color.FromRgb(159, 18, 57));
            }

            // 5. Apply chosen global background theme + translucent/matte surface brushes across Home & Subject Workspaces
            ApplyGlobalBgTheme(_state.Settings?.BgTheme ?? "aurora", saveToDisk: false);

            // 6. Re-render remaining visual lists with new palette
            RenderActivityHeatmap();
            UpdateDashboardStats();
            RenderArchivedSubjects();
            RenderCustomSoundPresets();
            RenderSoundMixerChannels();
            UpdateMixerUIState();
            RenderControlCenterThemesAndEffects();
            if (_currentSubject != null)
            {
                RenderFoldersAndFiles();
                RenderVideosList();
                RenderRecordingsList();
                RenderTasksList();
                RenderLinksList();
                RenderDeadlinesList();
            }

            // 7. Preserve PDF viewer's independent dark/light mode state
            if (PdfWebView?.CoreWebView2 != null)
            {
                _ = ApplyPdfDarkModeToWebViewAsync();
            }

            // 8. Persist Theme Mode separately from Surface Theme
            if (_state != null)
            {
                _state.Theme = isDark ? "dark" : "light";
                if (saveToDisk) SaveState();
            }
        }

        private void BtnNavFullscreen_Click(object sender, RoutedEventArgs e)
        {
            SetFullscreen(!_isFullscreen);
        }

        private void SetFullscreen(bool fs)
        {
            _isFullscreen = fs;
            if (_isFullscreen)
            {
                WindowStyle = WindowStyle.None;
                WindowState = WindowState.Maximized;
                TxtNavFullscreenIcon.Text = "🗗";
            }
            else
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
                WindowState = WindowState.Normal;
                TxtNavFullscreenIcon.Text = "⛶";
            }
        }

        // ================= HEATMAP & STATS =================

        private void RenderActivityHeatmap()
        {
            if (HeatmapWrapPanel == null) return;
            HeatmapWrapPanel.Children.Clear();
            var now = DateTime.Now;

            for (int i = 29; i >= 0; i--)
            {
                string dateStr = now.AddDays(-i).ToString("yyyy-MM-dd");
                int count = _state.Activity.GetValueOrDefault(dateStr, 0);

                var color = (count > 6) ? Color.FromRgb(124, 92, 255)
                          : (count > 3) ? Color.FromRgb(168, 85, 247)
                          : (count > 0) ? Color.FromRgb(34, 211, 238)
                          : (_isDarkMode ? Color.FromRgb(22, 27, 52) : Color.FromRgb(203, 213, 225));

                var box = new Border
                {
                    Width = 11,
                    Height = 11,
                    CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(color),
                    Margin = new Thickness(2),
                    ToolTip = $"{dateStr}: {count} actions"
                };
                HeatmapWrapPanel.Children.Add(box);
            }
        }

        private void UpdateDashboardStats()
        {
            int mins = (int)((DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _sessionStartTimeUnix) / 60);
            if (TxtHomeSessionUptime != null) TxtHomeSessionUptime.Text = $"{mins}m";

            string top = "None";
            double maxT = 0;
            foreach (var s in _state.Subjects)
            {
                double t = _state.SubjectTimes.GetValueOrDefault(s.Id, 0);
                if (t > maxT) { maxT = t; top = s.Name; }
            }
            if (TxtHomeTopSubject != null) TxtHomeTopSubject.Text = top;

            // Calculate Pomodoro Focus Ratio matching original app
            int pomoCount = _state.Activity.GetValueOrDefault(DateTime.Now.ToString("yyyy-MM-dd"), 0);
            double ratio = Math.Min(100, (pomoCount / 4.0) * 100);
            if (TxtHomeFocusRatio != null)
            {
                TxtHomeFocusRatio.Text = $"{Math.Round(ratio)}%";
                TxtHomeFocusRatio.Foreground = (ratio >= 100)
                    ? new SolidColorBrush(Color.FromRgb(16, 185, 129))
                    : (_isDarkMode ? new SolidColorBrush(Color.FromRgb(248, 250, 252)) : new SolidColorBrush(Color.FromRgb(15, 23, 42)));
            }
            if (BdFocusRatioRing != null)
            {
                BdFocusRatioRing.BorderBrush = (ratio >= 100)
                    ? new SolidColorBrush(Color.FromRgb(16, 185, 129))
                    : (ratio > 0
                        ? new SolidColorBrush(Color.FromRgb(124, 92, 255))
                        : (_isDarkMode ? new SolidColorBrush(Color.FromRgb(40, 48, 86)) : new SolidColorBrush(Color.FromRgb(203, 213, 225))));
            }
        }

        private void SaveState(bool countActivity = true)
        {
            if (countActivity)
            {
                string today = DateTime.Now.ToString("yyyy-MM-dd");
                _state.Activity[today] = _state.Activity.GetValueOrDefault(today, 0) + 1;
            }
            _storage.SaveStateSync(_state);
        }

        private void SaveStateDebounced()
        {
            _sliderSaveTimer.Stop();
            _sliderSaveTimer.Start();
        }

        private void ShowToast(string msg)
        {
            TxtToastMessage.Text = msg;
            ToastBorder.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private static string CleanIconText(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "🎓";
            if (raw.Contains("cap")) return "🎓";
            if (raw.Contains("book")) return "📚";
            if (raw.Contains("code")) return "💻";
            if (raw.Contains("calc")) return "📐";
            if (raw.Contains("flask")) return "🔬";
            if (raw.Contains("plane")) return "✈️";
            return raw;
        }

        private static readonly Dictionary<string, Color> TAILWIND_COLORS = new(StringComparer.OrdinalIgnoreCase)
        {
            ["slate-400"] = Color.FromRgb(148, 163, 184), ["slate-500"] = Color.FromRgb(100, 116, 139), ["slate-600"] = Color.FromRgb(71, 85, 105),
            ["red-400"] = Color.FromRgb(248, 113, 113), ["red-500"] = Color.FromRgb(239, 68, 68), ["red-600"] = Color.FromRgb(220, 38, 38),
            ["rose-400"] = Color.FromRgb(251, 113, 133), ["rose-500"] = Color.FromRgb(244, 63, 94), ["rose-600"] = Color.FromRgb(225, 29, 72),
            ["pink-400"] = Color.FromRgb(244, 114, 182), ["pink-500"] = Color.FromRgb(236, 72, 153), ["pink-600"] = Color.FromRgb(219, 39, 119),
            ["fuchsia-400"] = Color.FromRgb(232, 121, 249), ["fuchsia-500"] = Color.FromRgb(217, 70, 239), ["fuchsia-600"] = Color.FromRgb(192, 38, 211),
            ["purple-400"] = Color.FromRgb(192, 132, 252), ["purple-500"] = Color.FromRgb(168, 85, 247), ["purple-600"] = Color.FromRgb(147, 51, 234),
            ["violet-400"] = Color.FromRgb(167, 139, 250), ["violet-500"] = Color.FromRgb(139, 92, 246), ["violet-600"] = Color.FromRgb(124, 58, 237),
            ["indigo-400"] = Color.FromRgb(129, 140, 248), ["indigo-500"] = Color.FromRgb(99, 102, 241), ["indigo-600"] = Color.FromRgb(79, 70, 229),
            ["blue-400"] = Color.FromRgb(96, 165, 250), ["blue-500"] = Color.FromRgb(59, 130, 246), ["blue-600"] = Color.FromRgb(37, 99, 235),
            ["sky-400"] = Color.FromRgb(56, 189, 248), ["sky-500"] = Color.FromRgb(14, 165, 233), ["sky-600"] = Color.FromRgb(2, 132, 199),
            ["cyan-400"] = Color.FromRgb(34, 211, 238), ["cyan-500"] = Color.FromRgb(6, 182, 212), ["cyan-600"] = Color.FromRgb(8, 145, 178),
            ["teal-400"] = Color.FromRgb(45, 212, 191), ["teal-500"] = Color.FromRgb(20, 184, 166), ["teal-600"] = Color.FromRgb(13, 148, 136),
            ["emerald-400"] = Color.FromRgb(52, 211, 153), ["emerald-500"] = Color.FromRgb(16, 185, 129), ["emerald-600"] = Color.FromRgb(5, 150, 105),
            ["green-400"] = Color.FromRgb(74, 222, 128), ["green-500"] = Color.FromRgb(34, 197, 94), ["green-600"] = Color.FromRgb(22, 163, 74),
            ["lime-400"] = Color.FromRgb(163, 230, 53), ["lime-500"] = Color.FromRgb(132, 204, 22), ["lime-600"] = Color.FromRgb(101, 163, 13),
            ["yellow-400"] = Color.FromRgb(250, 204, 21), ["yellow-500"] = Color.FromRgb(234, 179, 8),
            ["amber-400"] = Color.FromRgb(251, 191, 36), ["amber-500"] = Color.FromRgb(245, 158, 11), ["amber-600"] = Color.FromRgb(217, 119, 6),
            ["orange-400"] = Color.FromRgb(251, 146, 60), ["orange-500"] = Color.FromRgb(249, 115, 22), ["orange-600"] = Color.FromRgb(234, 88, 12)
        };

        private static List<Color> ParseTailwindStops(string? raw)
        {
            var list = new List<Color>();
            if (string.IsNullOrWhiteSpace(raw)) return list;

            if (raw.TrimStart().StartsWith("#"))
            {
                try
                {
                    if (ColorConverter.ConvertFromString(raw.Trim()) is Color hc) list.Add(hc);
                }
                catch { }
                return list;
            }

            foreach (var part in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string token = part;
                if (token.StartsWith("from-", StringComparison.OrdinalIgnoreCase)) token = token[5..];
                else if (token.StartsWith("via-", StringComparison.OrdinalIgnoreCase)) token = token[4..];
                else if (token.StartsWith("to-", StringComparison.OrdinalIgnoreCase)) token = token[3..];
                else continue;

                if (TAILWIND_COLORS.TryGetValue(token, out Color c))
                {
                    list.Add(c);
                }
                else
                {
                    // Fallback match by hue name (e.g., "purple-700" -> "purple-600")
                    string hue = token.Split('-')[0];
                    if (TAILWIND_COLORS.TryGetValue($"{hue}-500", out Color fallback))
                    {
                        list.Add(fallback);
                    }
                }
            }
            return list;
        }

        private static Brush ParseColorBrush(string? raw, bool horizontal = false)
        {
            var stops = ParseTailwindStops(raw);
            if (stops.Count == 1)
            {
                return new SolidColorBrush(stops[0]);
            }
            if (stops.Count >= 2)
            {
                var gsc = new GradientStopCollection();
                for (int i = 0; i < stops.Count; i++)
                {
                    double offset = (double)i / (stops.Count - 1);
                    gsc.Add(new GradientStop(stops[i], offset));
                }
                return new LinearGradientBrush(gsc, new Point(0, 0), horizontal ? new Point(1, 0) : new Point(1, 1));
            }
            return new SolidColorBrush(Color.FromRgb(124, 92, 255));
        }

        private static Color ExtractPrimaryAccentColor(string? raw)
        {
            var stops = ParseTailwindStops(raw);
            return stops.Count > 0 ? stops[0] : Color.FromRgb(124, 92, 255);
        }

        private static Brush GetSoundChannelIconBrush(string? channelId)
        {
            return (channelId ?? "") switch
            {
                "zen_video" => new SolidColorBrush(Color.FromRgb(245, 158, 11)),   // #F59E0B Gold
                "rain" => new SolidColorBrush(Color.FromRgb(56, 189, 248)),        // #38BDF8 Sky
                "campfire" => new SolidColorBrush(Color.FromRgb(249, 115, 22)),    // #F97316 Ember
                "wind" => new SolidColorBrush(Color.FromRgb(16, 185, 129)),        // #10B981 Emerald
                "ocean" => new SolidColorBrush(Color.FromRgb(6, 182, 212)),        // #06B6D4 Cyan
                "airplane" => new SolidColorBrush(Color.FromRgb(129, 140, 248)),   // #818CF8 Indigo
                "crickets" => new SolidColorBrush(Color.FromRgb(167, 139, 250)),   // #A78BFA Violet
                "brown_noise" => new SolidColorBrush(Color.FromRgb(192, 132, 252)),// #C084FC Purple
                "pink_noise" => new SolidColorBrush(Color.FromRgb(244, 114, 182)), // #F472B6 Pink
                _ => new SolidColorBrush(Color.FromRgb(34, 211, 238))              // #22D3EE Custom Cyan
            };
        }

        private static Brush GetFileIconBrush(string? filename)
        {
            string ext = Path.GetExtension(filename ?? "").ToLowerInvariant();
            return ext switch
            {
                ".pdf" => new SolidColorBrush(Color.FromRgb(244, 63, 94)),
                ".doc" or ".docx" => new SolidColorBrush(Color.FromRgb(59, 130, 246)),
                ".ppt" or ".pptx" => new SolidColorBrush(Color.FromRgb(249, 115, 22)),
                ".xls" or ".xlsx" or ".csv" => new SolidColorBrush(Color.FromRgb(16, 185, 129)),
                ".png" or ".jpg" or ".jpeg" or ".gif" => new SolidColorBrush(Color.FromRgb(6, 182, 212)),
                _ => new SolidColorBrush(Color.FromRgb(124, 92, 255))
            };
        }

        private static string GetFileEmoji(string? filename)
        {
            string ext = Path.GetExtension(filename ?? "").ToLowerInvariant();
            return ext switch
            {
                ".pdf" => "📕",
                ".doc" or ".docx" => "📘",
                ".ppt" or ".pptx" => "📙",
                ".xls" or ".xlsx" or ".csv" => "📗",
                ".png" or ".jpg" or ".jpeg" or ".gif" => "🖼️",
                ".zip" or ".rar" => "🗜️",
                ".txt" or ".md" => "📝",
                _ => "📄"
            };
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            _isClosing = true;
            UpdateRenderLoopState();
            _clockTimer?.Stop();
            _pomoTimer?.Stop();
            _toastTimer?.Stop();
            _sliderSaveTimer?.Stop();
            _audio?.StopAndResetAll();
            _audio?.Dispose();
            SaveState(countActivity: false);
        }
    }
}
