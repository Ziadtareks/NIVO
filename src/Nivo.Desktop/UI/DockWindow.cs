using Nivo.Core;
using Nivo.Services;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
namespace Nivo.UI;

public sealed class DockWindow : Window, Motion.IRenderParticipant
{
    private static double GlassOverlayOpacity => Theme.IsOled ? 1 : 0.78;
    private const double NormalMagnifyRadius = 104.0;
    private const double NormalScaleGain = 0.30;
    private const double NormalLiftY = -10.0;
    private const double NormalPartFactor = 0.60 * NormalScaleGain;
    private const double FocusMagnifyRadius = 96.0;
    private const double FocusScaleGain = 0.14;
    private const double FocusLiftY = -5.0;
    private const double FocusPartFactor = 0.55 * FocusScaleGain;
    private const double HoverTauSeconds = 0.026;
    private const double HoverPillTauSeconds = 0.020;

    private sealed class DockIcon(Button button, FrameworkElement visual, Border? activeHalo, ScaleTransform scale, TranslateTransform lift, string displayName, bool isSuggested)
    {
        public Button Button { get; } = button;
        public FrameworkElement Visual { get; } = visual;
        public Border? ActiveHalo { get; } = activeHalo;
        public ScaleTransform Scale { get; } = scale;
        public TranslateTransform Lift { get; } = lift;
        public string DisplayName { get; } = displayName;
        public bool IsSuggested { get; } = isSuggested;
        public double RestCenterX { get; set; }
        public double CurrentScale { get; set; } = 1.0;
        public double TargetScale { get; set; } = 1.0;
        public double CurrentLiftX { get; set; }
        public double TargetLiftX { get; set; }
        public double CurrentLiftY { get; set; }
        public double TargetLiftY { get; set; }
    }

    private sealed class DockDivider(Border element, TranslateTransform lift)
    {
        public Border Element { get; } = element;
        public TranslateTransform Lift { get; } = lift;
        public double RestCenterX { get; set; }
        public double CurrentLiftX { get; set; }
        public double TargetLiftX { get; set; }
    }

    private sealed record DockCapsule(string Tag, Border Capsule, ImageBrush WallpaperBrush, Border GlassOverlay, Border SpecularRim);

    private readonly Controller c;
    private readonly StackPanel apps = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly List<DockIcon> icons = [];
    private readonly List<DockDivider> dividers = [];
    private readonly List<DockCapsule> capsules = [];
    private readonly Border contextPill;
    private readonly TextBlock contextPillText = new() { FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = ShellTheme.Ink("#EAF6FF") };
    private readonly TranslateTransform contextPillOffset = new();
    private readonly Border hoverPill;
    private readonly TextBlock hoverPillText = new() { FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = ShellTheme.Ink("#071836") };
    private readonly TranslateTransform hoverPillOffset = new();
    private readonly TextBlock statusDate = new() { FontSize = 10, Foreground = ShellTheme.Ink("#A8C6E8"), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock clock = new() { FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = ShellTheme.Ink("#F2F9FF"), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock wifiGlyph = Ui.Glyph("\uE701", 13);
    private readonly Button muteBtn;
    private readonly Button langPill;
    private readonly Button focusBtn;
    private readonly Grid rootGrid = new();
    private readonly StackPanel centerCluster;
    private readonly Border appsCapsule;
    private readonly Border essentialsCapsule;
    private readonly Border statusCapsule;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string previousSnapshot = "";
    private string lastContextText = "";
    private HashSet<string> previousSuggestedTargets = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, BitmapSource> iconCache = new(StringComparer.OrdinalIgnoreCase);
    private bool contextVisible, snapshotInFlight, closed, muteStateKnown, hasInitialLayout;
    private bool? lastMute;
    private double lastAppsScale = 1.0;
    private double committedAppsWidth = double.NaN;
    private int lastDividerCount, contextEntranceCount, suggestedEntranceCount;
    private readonly TextBlock muteGlyph = Ui.Glyph("\uE767", 13);
    private bool? lastNetworkOnline; private bool? lastFocus;
    private DateTime lastNetworkCheck, lastWallpaperCheck;
    private readonly WallpaperSource wallpaperSource = new();
    private double? activePointerX;
    private bool hoverTargetsDirty, hoverTrackingActive, hoverPillTracking;
    private double hoverPillCurrentX, hoverPillTargetX;
    private string lastHoverPillText = "", measuredContextPillText = "";
    private double cachedHoverPillWidth, cachedContextPillWidth;

    public DockWindow(Controller controller)
    {
        c = controller; Title = "NIVO Desktop"; Height = 128; Width = SystemParameters.PrimaryScreenWidth; Left = 0;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; Topmost = true; ShowInTaskbar = false; ShowActivated = false;
        FlowDirection = FlowDirection.LeftToRight;

        wallpaperSource.RefreshDesktop();

        Border MakeCapsule(string tag, UIElement child, double radius, Thickness padding)
        {
            var inner = new Grid();
            var wallpaperBrush = new ImageBrush { ImageSource = wallpaperSource.Image, Viewbox = new Rect(.18, .88, .64, .12), ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Stretch = Stretch.Fill };
            var wp = new Border { Tag = "CapsuleWallpaper", CornerRadius = new CornerRadius(radius), Background = wallpaperBrush, Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 26 }, Opacity = .58 };
            // A Border corner radius does not clip a child's blurred output.
            var clipGeometry = new RectangleGeometry(new Rect(0, 0, 1, 1), radius, radius);
            var wallpaperClip = new Border { Child = wp, Clip = clipGeometry, CacheMode = new BitmapCache() };
            wallpaperClip.SizeChanged += (_, e) => clipGeometry.Rect = new Rect(e.NewSize);
            inner.Children.Add(wallpaperClip);
            var glass = new Border { Tag = "CapsuleGlassOverlay", CornerRadius = new CornerRadius(radius), Background = ShellTheme.Glass, Opacity = GlassOverlayOpacity };
            inner.Children.Add(glass);
            // Specular liquid glass top highlight rim
            var specular = new Border { Height = 15, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 1, 12, 0), CornerRadius = new CornerRadius(radius, radius, 8, 8), Background = BuildSpecularRimBrush(), Opacity = Theme.IsOled ? 0 : 1, IsHitTestVisible = false };
            inner.Children.Add(specular);
            inner.Children.Add(new Border { Padding = padding, Child = child });
            var capsule = new Border { Tag = tag, CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(1), BorderBrush = ShellTheme.Line("#8868A0E8"), ClipToBounds = true, Child = inner };
            Ui.Elevate(capsule);
            capsules.Add(new DockCapsule(tag, capsule, wallpaperBrush, glass, specular));
            return capsule;
        }

        // Floating context badge above dock ("وضع التركيز · الدراسة" / "✨ مقترحة للبرمجة")
        contextPillText.FlowDirection = Loc.Flow;
        contextPill = new Border
        {
            Tag = "DockContextPill",
            Background = ShellTheme.Surface("#E60B1D42"), BorderBrush = ShellTheme.Line("#6638D6F5"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 5, 14, 5),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 78), RenderTransform = contextPillOffset, CacheMode = new BitmapCache(),
            Child = contextPillText, Visibility = Visibility.Collapsed
        };
        Ui.Elevate(contextPill);

        // Floating cyan hover pill right above the hovered icon
        hoverPillText.FlowDirection = Loc.Flow;
        hoverPill = new Border
        {
            Tag = "DockHoverPill",
            Background = ShellTheme.Surface("#38D6F5"), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 82),
            RenderTransform = hoverPillOffset, CacheMode = new BitmapCache(),
            Child = hoverPillText, Visibility = Visibility.Collapsed, IsHitTestVisible = false
        };
        Ui.Elevate(hoverPill);

        // === Capsule 2 (Center): Your Apps ===
        appsCapsule = MakeCapsule("AppsCapsule", new Border { Height = 44 }, 27, new Thickness(12, 6, 12, 6));
        appsCapsule.Height = 56; appsCapsule.VerticalAlignment = VerticalAlignment.Bottom;
        appsCapsule.CacheMode = new BitmapCache();
        var appsHost = new Grid { VerticalAlignment = VerticalAlignment.Bottom };
        appsHost.Children.Add(appsCapsule);
        apps.Background = Brushes.Transparent;
        apps.Margin = new Thickness(12, 0, 12, 2);
        apps.VerticalAlignment = VerticalAlignment.Bottom;
        appsHost.Children.Add(apps);

        // === Capsule 1 (Essentials: Start, Search, Focus) ===
        Button GlassIconBtn(string glyph, string tooltip, Action onClick)
        {
            var g = Ui.Glyph(glyph, 16); g.Foreground = ShellTheme.Ink("#E6F4FF");
            var b = Ui.Btn("", onClick); b.Content = g; b.ToolTip = tooltip;
            Ui.Flat(b); b.Width = 38; b.Height = 38; b.Padding = new Thickness(0); b.Margin = new Thickness(2, 0, 2, 0);
            return b;
        }
        var startIcon = new Grid { Width = 16, Height = 16 };
        var sq = new System.Windows.Controls.Primitives.UniformGrid { Rows = 2, Columns = 2 };
        for (int i = 0; i < 4; i++) sq.Children.Add(new Border { Background = ShellTheme.Surface("#67E8F9"), CornerRadius = new CornerRadius(1.5), Margin = new Thickness(1) });
        startIcon.Children.Add(sq);
        var startBtn = Ui.Btn("", DesktopService.OpenStartMenu); startBtn.Content = startIcon; startBtn.ToolTip = Loc.T("Start menu", "قائمة ابدأ");
        Ui.Flat(startBtn); startBtn.Width = 38; startBtn.Height = 38; startBtn.Padding = new Thickness(0); startBtn.Margin = new Thickness(2, 0, 2, 0);

        var searchBtn = GlassIconBtn("\uE721", Loc.T("Silent Ask · ", "السؤال الصامت · ") + c.Store.Data.Settings.Shortcuts.GetValueOrDefault("ask", "Alt+Shift+S"), () => ((App)Application.Current).ShowAsk());
        focusBtn = GlassIconBtn("\uE708", Loc.T("Focus mode (right-click for profiles)", "وضع التركيز (كليك يمين لاختيار المجال)"), () => { c.Store.Data.Settings.Focus = !c.Store.Data.Settings.Focus; c.Store.Save(); Refresh(); });
        focusBtn.SetResourceReference(StyleProperty, "PillButton"); focusBtn.BorderThickness = new Thickness(0); focusBtn.BorderBrush = Brushes.Transparent;
        focusBtn.ContextMenu = BuildFocusMenu();

        var essentialsRow = Loc.IsArabic ? Ui.Row(focusBtn, searchBtn, startBtn) : Ui.Row(startBtn, searchBtn, focusBtn);
        essentialsCapsule = MakeCapsule("EssentialsCapsule", essentialsRow, 27, new Thickness(8, 6, 8, 6));
        essentialsCapsule.Height = 56; essentialsCapsule.VerticalAlignment = VerticalAlignment.Bottom;
        essentialsCapsule.Margin = new Thickness(12, 0, 12, 0);

        centerCluster = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 14) };
        if (Loc.IsArabic) { centerCluster.Children.Add(appsHost); centerCluster.Children.Add(essentialsCapsule); }
        else { centerCluster.Children.Add(essentialsCapsule); centerCluster.Children.Add(appsHost); }

        // === Capsule 3 (Status: Time/Date, Volume, Wi-Fi, Language, Quick Tray Chevron) ===
        var timeStack = Ui.Stack(clock, statusDate); timeStack.VerticalAlignment = VerticalAlignment.Center; timeStack.Margin = new Thickness(6, 0, 6, 0);

        muteGlyph.Foreground = ShellTheme.Ink("#DCEEFF");
        muteBtn = Ui.Btn("", () =>
        {
            if (SystemAudio.TryToggleMute(out bool? muted, out string? error))
            {
                ApplyMuteState(muted);
                return;
            }
            ApplyMuteState(null);
            if (!string.IsNullOrWhiteSpace(error))
            {
                c.SetStatus(error);
                throw new InvalidOperationException(error);
            }
        });
        muteBtn.Content = muteGlyph; muteBtn.ToolTip = Loc.T("Mute / Unmute audio", "كتم / تشغيل الصوت");
        Ui.Flat(muteBtn); muteBtn.Width = 28; muteBtn.Height = 28; muteBtn.Padding = new Thickness(0); muteBtn.Margin = new Thickness(1, 0, 1, 0);

        wifiGlyph.Foreground = ShellTheme.Ink("#93C5FD"); wifiGlyph.Margin = new Thickness(4, 0, 4, 0);
        wifiGlyph.ToolTip = Loc.T("Network status", "حالة الاتصال");

        langPill = Ui.Btn(Loc.IsArabic ? "EN" : "عربي", () => ((App)Application.Current).ApplyLanguage(Loc.IsArabic ? "English" : "Arabic"));
        langPill.Foreground = ShellTheme.Ink("#E6F4FF"); langPill.FontSize = 11; langPill.FontWeight = FontWeights.SemiBold;
        Ui.Flat(langPill); langPill.Padding = new Thickness(6, 3, 6, 3); langPill.Margin = new Thickness(2, 0, 2, 0);
        langPill.ToolTip = Loc.T("Switch language (Arabic / English)", "تبديل اللغة (عربي / إنجليزي)");

        var trayChevron = Ui.Btn("⌃", () => { });
        trayChevron.Foreground = ShellTheme.Ink("#B8D4F5"); trayChevron.FontSize = 12;
        Ui.Flat(trayChevron); trayChevron.Width = 26; trayChevron.Height = 28; trayChevron.Padding = new Thickness(0); trayChevron.Margin = new Thickness(1, 0, 1, 0);
        trayChevron.ToolTip = Loc.T("Quick actions", "أدوات سريعة");
        trayChevron.Click += (_, _) => { trayChevron.ContextMenu = BuildQuickMenu(); trayChevron.ContextMenu.PlacementTarget = trayChevron; trayChevron.ContextMenu.IsOpen = true; };

        var statusRow = Ui.Row(timeStack, muteBtn, wifiGlyph, langPill, trayChevron);
        statusCapsule = MakeCapsule("StatusCapsule", statusRow, 23, new Thickness(10, 5, 8, 5));
        statusCapsule.Height = 48; statusCapsule.HorizontalAlignment = Loc.IsArabic ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        statusCapsule.VerticalAlignment = VerticalAlignment.Bottom; statusCapsule.Margin = new Thickness(18, 0, 18, 18);

        rootGrid.Children.Add(statusCapsule);
        rootGrid.Children.Add(centerCluster);
        rootGrid.Children.Add(contextPill);
        rootGrid.Children.Add(hoverPill);
        Content = rootGrid;
        Ui.StyleWindow(this);

        apps.MouseMove += (_, e) => Magnify(e.GetPosition(apps).X);
        apps.MouseLeave += (_, _) => Magnify(null);
        c.Changed += OnStateChanged;
        timer.Tick += (_, _) => PollStatus();
        Loaded += (_, _) =>
        {
            FitDockLayout(animateWidth: false);
            hasInitialLayout = true;
            UpdateWallpaperViewboxes();
            PositionContextPill(animate: false);
        };
        SizeChanged += (_, _) =>
        {
            FitDockLayout(animateWidth: false);
            UpdateWallpaperViewboxes();
            PositionContextPill(animate: false);
        };
        LocationChanged += (_, _) => UpdateWallpaperViewboxes();
        statusCapsule.SizeChanged += (_, _) =>
        {
            FitDockLayout(animateWidth: false);
            UpdateWallpaperViewboxes();
        };
        essentialsCapsule.SizeChanged += (_, _) => UpdateWallpaperViewboxes();
        appsCapsule.SizeChanged += (_, _) =>
        {
            UpdateWallpaperViewboxes();
            PositionContextPill(animate: false);
        };
        centerCluster.SizeChanged += (_, _) =>
        {
            UpdateWallpaperViewboxes();
            PositionContextPill(animate: false);
        };
        apps.SizeChanged += (_, _) =>
        {
            UpdateRestCenters();
            PositionContextPill(animate: false);
        };
        contextPill.SizeChanged += (_, _) =>
        {
            if (contextPill.Visibility == Visibility.Visible && !Motion.IsAnimating(contextPillOffset, TranslateTransform.XProperty))
                PositionContextPill(animate: false);
        };
        Motion.PolicyChanged += OnMotionPolicyChanged;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                PollStatus();
                timer.Start();
                Motion.Enter(centerCluster, 14);
            }
            else
            {
                timer.Stop();
                StopHoverTracking(snapToRest: true, hideHoverPill: true);
                CancelDockClocks();
            }
        };
        Closed += (_, _) =>
        {
            closed = true;
            timer.Stop();
            c.Changed -= OnStateChanged;
            Motion.PolicyChanged -= OnMotionPolicyChanged;
            StopHoverTracking(snapToRest: true, hideHoverPill: true);
            CancelDockClocks();
        };
        UpdateClockText(); Refresh();
    }

    private double HostWidth => rootGrid.ActualWidth > 0 ? rootGrid.ActualWidth : (ActualWidth > 0 ? ActualWidth : (!double.IsNaN(Width) && Width > 0 ? Width : SystemParameters.PrimaryScreenWidth));

    private Color CurrentThemeAccentColor()
    {
        if (Application.Current?.Resources["Accent"] is SolidColorBrush brush) return brush.Color;
        return Ui.Brush(Theme.Palette(c.Store.Data.Settings.Theme).Accent).Color;
    }

    private bool IsCurrentThemeDark()
    {
        Color bg = Application.Current?.Resources["WindowBackground"] is SolidColorBrush brush
            ? brush.Color
            : Ui.Brush(Theme.Palette(c.Store.Data.Settings.Theme).Background).Color;
        return (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) < 128;
    }

    private Brush BuildSpecularRimBrush()
    {
        byte topAlpha = IsCurrentThemeDark() ? (byte)0x28 : (byte)0x42;
        return new LinearGradientBrush(Color.FromArgb(topAlpha, 255, 255, 255), Colors.Transparent, 90);
    }

    private void ApplyActiveHaloStyle(Border halo)
    {
        Color accent = CurrentThemeAccentColor();
        bool dark = IsCurrentThemeDark();
        var fill = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.58,
            RadiusY = 0.58
        };
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(dark ? (byte)52 : (byte)46, accent.R, accent.G, accent.B), 0.0));
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(dark ? (byte)20 : (byte)18, accent.R, accent.G, accent.B), 0.72));
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1.0));
        halo.Background = fill;
        halo.BorderBrush = new SolidColorBrush(Color.FromArgb(dark ? (byte)108 : (byte)92, accent.R, accent.G, accent.B));
    }

    public void ApplyTheme()
    {
        if (closed) return;
        Ui.StyleWindow(this);
        foreach (var cap in capsules)
        {
            cap.GlassOverlay.Background = ShellTheme.Glass;
            cap.GlassOverlay.Opacity = GlassOverlayOpacity;
            cap.SpecularRim.Background = BuildSpecularRimBrush();
            cap.SpecularRim.Opacity = Theme.IsOled ? 0 : 1;
        }
        foreach (var icon in icons)
        {
            if (icon.ActiveHalo != null) ApplyActiveHaloStyle(icon.ActiveHalo);
        }
        bool focus = c.Store.Data.Settings.Focus;
        lastFocus = focus;
        focusBtn.Background = focus ? Ui.Brush("#3838D6F5") : Brushes.Transparent;
        UpdateWallpaperViewboxes();
        InvalidateVisual();
    }

    private static void SnapTo(DependencyObject target, DependencyProperty property, double value)
    {
        Motion.AnimateTo(target, property, value, 0);
    }

    private ContextMenu BuildFocusMenu()
    {
        var menu = new ContextMenu();
        foreach (var (en, ar) in new[] { ("Study", "الدراسة"), ("Coding", "البرمجة"), ("Deep Work", "العمل العميق"), ("General", "عام") })
        {
            var label = Loc.T(en, ar);
            var mi = new MenuItem { Header = "🌙  " + label };
            mi.Click += (_, _) => { c.Store.Data.Settings.Focus = true; c.Store.Data.Settings.FocusProfile = en; c.Store.Save(); Refresh(); };
            menu.Items.Add(mi);
        }
        var off = new MenuItem { Header = Loc.T("Turn off Focus mode", "إيقاف وضع التركيز") };
        off.Click += (_, _) => { c.Store.Data.Settings.Focus = false; c.Store.Save(); Refresh(); };
        menu.Items.Add(new Separator()); menu.Items.Add(off);
        return menu;
    }

    private ContextMenu BuildQuickMenu()
    {
        var menu = new ContextMenu();
        void Add(string label, Action act) { var m = new MenuItem { Header = label }; m.Click += (_, _) => { try { act(); } catch (Exception ex) { Ui.Error(ex); } }; menu.Items.Add(m); }
        Add(Loc.T("Open NIVO Hub", "فتح المركز الرئيسي"), () => ((App)Application.Current).Hub.ShowAssistant());
        Add(Loc.T("Screen crop", "قص جزء من الشاشة"), async () => await ((App)Application.Current).CaptureAsync(true));
        Add(Loc.T("Hover to ask (cursor area)", "أشّر واسأل (حول المؤشر)"), async () => await ((App)Application.Current).CaptureAsync(false, true));
        Add(c.Store.Incognito ? Loc.T("Turn off Incognito", "إيقاف الوضع الخفي") : Loc.T("Turn on Incognito", "تفعيل الوضع الخفي"), () => { c.ToggleIncognito(); Refresh(); });
        Add(Loc.T("Pin an app or link…", "تثبيت تطبيق أو رابط…"), PinApp);
        menu.Items.Add(new Separator());
        Add(Loc.T("Settings", "الإعدادات"), () => { ((App)Application.Current).Hub.Show(); ((App)Application.Current).Hub.Navigate("Settings"); });
        return menu;
    }

    private void UpdateClockText()
    {
        string nextClock = Loc.IsArabic ? DateTime.Now.ToString("h:mm tt").Replace("AM", "ص").Replace("PM", "م") : DateTime.Now.ToString("h:mm tt");
        string nextDate = c.Store.Incognito ? Loc.T("Incognito", "خفي") : DateTime.Now.ToString("yyyy/MM/dd");
        if (!string.Equals(clock.Text, nextClock, StringComparison.Ordinal)) clock.Text = nextClock;
        if (!string.Equals(statusDate.Text, nextDate, StringComparison.Ordinal)) statusDate.Text = nextDate;
    }

    private void ApplyMuteState(bool? currentMute)
    {
        if (muteStateKnown && currentMute == lastMute) return;
        muteStateKnown = true;
        lastMute = currentMute;
        if (currentMute == null)
        {
            muteGlyph.Text = "\uE74F";
            muteGlyph.Foreground = ShellTheme.Ink("#8EA7C4");
            muteGlyph.Opacity = 0.55;
            muteBtn.ToolTip = Loc.T("Audio output unavailable", "إخراج الصوت غير متاح");
        }
        else
        {
            muteGlyph.Text = currentMute == true ? "\uE74F" : "\uE767";
            muteGlyph.Foreground = currentMute == true ? Ui.Brush("#B84444") : ShellTheme.Ink("#DCEEFF");
            muteGlyph.Opacity = 1.0;
            muteBtn.ToolTip = currentMute == true ? Loc.T("Unmute audio", "إلغاء كتم الصوت") : Loc.T("Mute audio", "كتم الصوت");
        }
    }

    private void PollStatus()
    {
        UpdateClockText();
        ApplyMuteState(SystemAudio.Muted());
        if (DateTime.UtcNow - lastNetworkCheck > TimeSpan.FromSeconds(10))
        {
            lastNetworkCheck = DateTime.UtcNow; bool online = DesktopService.IsNetworkOnline();
            if (online != lastNetworkOnline) { lastNetworkOnline = online; wifiGlyph.Text = online ? "\uE701" : "\uE871"; wifiGlyph.Foreground = online ? ShellTheme.Ink("#93C5FD") : Ui.Brush("#B84444"); }
        }
        if (DateTime.UtcNow - lastWallpaperCheck > TimeSpan.FromSeconds(8))
        {
            lastWallpaperCheck = DateTime.UtcNow;
            if (wallpaperSource.RefreshDesktop())
            {
                foreach (var cap in capsules) cap.WallpaperBrush.ImageSource = wallpaperSource.Image;
                UpdateWallpaperViewboxes();
            }
        }
    }

    private void OnStateChanged()
    {
        bool focus = c.Store.Data.Settings.Focus;
        if (focus != lastFocus) { lastFocus = focus; focusBtn.Background = focus ? Ui.Brush("#3838D6F5") : Brushes.Transparent; }
        UpdateClockText();
    }

    private static string NormalizeTarget(string? target) => (target ?? "").Trim().TrimEnd('/');

    private static bool SameTarget(string? a, string? b) =>
        string.Equals(NormalizeTarget(a), NormalizeTarget(b), StringComparison.OrdinalIgnoreCase);

    private static bool MatchesTarget(string target, DesktopService.RunningWindow win)
    {
        if (string.IsNullOrWhiteSpace(win.Executable)) return false;
        if (string.Equals(target, win.Executable, StringComparison.OrdinalIgnoreCase)) return true;
        return !Path.IsPathRooted(target) && string.Equals(target, Path.GetFileName(win.Executable), StringComparison.OrdinalIgnoreCase);
    }

    private string BuildSnapshot(IReadOnlyList<DesktopService.RunningWindow> running)
    {
        string launchers = string.Join(";", c.Store.Data.Launchers.Take(8).Select(l => l.Name + "=" + l.Target));
        string windows = string.Join(",", running.Select(r => r.Handle + ":" + r.Active));
        return c.LastExternalProcess + "|" + c.Store.Data.Settings.Focus + "|" + FocusPolicy.Normalize(c.Store.Data.Settings.FocusProfile) + "|" + launchers + "|" + windows;
    }

    internal bool ApplySnapshot(IReadOnlyList<DesktopService.RunningWindow> running)
    {
        string snap = BuildSnapshot(running);
        if (previousSnapshot == snap) return false;
        Refresh(running);
        return true;
    }

    public static (double AppsWidth, double Scale) ComputeAppsLayout(double dockWidth, double essentialsWidth, double reservedStatusWidth, double appsContentWidth, double appsHorizontalMargin)
    {
        double maxNoOverlap = Math.Max(80, dockWidth - 2 * reservedStatusWidth - essentialsWidth);
        double minCapsule = Math.Min(120, maxNoOverlap);
        double contentBudget = Math.Max(1, maxNoOverlap - appsHorizontalMargin);
        double scale = appsContentWidth > 0 ? Math.Min(1.0, contentBudget / appsContentWidth) : 1.0;
        double width = Math.Clamp(appsContentWidth * scale + appsHorizontalMargin, minCapsule, maxNoOverlap);
        return (width, scale);
    }

    public static double ComputeHoverPillLeft(double iconCenterX, double pillWidth, double hostWidth, double edgePadding = 16)
    {
        double maxLeft = Math.Max(edgePadding, hostWidth - pillWidth - edgePadding);
        return Math.Clamp(iconCenterX - pillWidth / 2, edgePadding, maxLeft);
    }

    public static Rect ComputeWallpaperViewbox(Rect capsuleBounds, double referenceWidth, double referenceHeight)
    {
        double rw = Math.Max(1, referenceWidth);
        double rh = Math.Max(1, referenceHeight);
        double left = Math.Clamp(capsuleBounds.Left / rw, 0.0, 0.99);
        double top = Math.Clamp(capsuleBounds.Top / rh, 0.0, 0.99);
        double right = Math.Clamp(capsuleBounds.Right / rw, left + 0.01, 1.0);
        double bottom = Math.Clamp(capsuleBounds.Bottom / rh, top + 0.01, 1.0);
        return new Rect(left, top, right - left, bottom - top);
    }

    public static (double Scale, double LiftY, double PartingX) ComputeMagnifyTransform(double restCenterX, double pointerX, bool focusMode)
    {
        double radius = focusMode ? FocusMagnifyRadius : NormalMagnifyRadius;
        double maxScaleGain = focusMode ? FocusScaleGain : NormalScaleGain;
        double maxLiftY = focusMode ? FocusLiftY : NormalLiftY;
        double partFactor = focusMode ? FocusPartFactor : NormalPartFactor;

        double dist = Math.Abs(restCenterX - pointerX);
        double proximity = dist < radius ? 0.5 * (1.0 + Math.Cos(Math.PI * dist / radius)) : 0.0;
        double scale = 1.0 + maxScaleGain * proximity;
        double liftY = maxLiftY * proximity;

        double clamped = Math.Min(dist, radius);
        double integral = 0.5 * (clamped + (radius / Math.PI) * Math.Sin(Math.PI * clamped / radius));
        double sign = restCenterX > pointerX ? 1.0 : restCenterX < pointerX ? -1.0 : 0.0;
        double partingX = sign * partFactor * integral;
        return (scale, liftY, partingX);
    }

    private void UpdateRestCenters()
    {
        double cursor = 0;
        int iconIdx = 0;
        int divIdx = 0;
        foreach (UIElement child in apps.Children)
        {
            double slotWidth;
            if (child is FrameworkElement fe)
            {
                double w = fe.ActualWidth > 0 ? fe.ActualWidth : (!double.IsNaN(fe.Width) ? fe.Width : fe.DesiredSize.Width);
                slotWidth = w + fe.Margin.Left + fe.Margin.Right;
                double restCenter = fe.ActualWidth > 0
                    ? VisualTreeHelper.GetOffset(fe).X + fe.ActualWidth / 2
                    : cursor + fe.Margin.Left + w / 2;
                if (iconIdx < icons.Count && ReferenceEquals(icons[iconIdx].Button, fe))
                    icons[iconIdx++].RestCenterX = restCenter;
                else if (divIdx < dividers.Count && ReferenceEquals(dividers[divIdx].Element, fe))
                    dividers[divIdx++].RestCenterX = restCenter;
            }
            else
            {
                slotWidth = child.DesiredSize.Width;
            }
            cursor += slotWidth;
        }
    }

    private static double GetIconRestCenterX(DockIcon item) =>
        item.Button.ActualWidth > 0
            ? VisualTreeHelper.GetOffset(item.Button).X + item.Button.ActualWidth / 2
            : item.RestCenterX;

    private static double GetDividerRestCenterX(DockDivider divider) =>
        divider.Element.ActualWidth > 0
            ? VisualTreeHelper.GetOffset(divider.Element).X + divider.Element.ActualWidth / 2
            : divider.RestCenterX;

    private void UpdateWallpaperViewboxes()
    {
        double screenW = Math.Max(1, SystemParameters.PrimaryScreenWidth);
        double screenH = Math.Max(1, SystemParameters.PrimaryScreenHeight);
        double dockW = ActualWidth > 0 ? ActualWidth : (!double.IsNaN(Width) && Width > 0 ? Width : screenW);
        double dockH = ActualHeight > 0 ? ActualHeight : (!double.IsNaN(Height) && Height > 0 ? Height : 128);
        bool offscreen = Left <= -10000 || Top <= -10000;
        double refW = offscreen ? dockW : screenW;
        double refH = screenH;
        double originX = offscreen ? 0 : Left;
        double originY = offscreen ? Math.Max(0, screenH - dockH) : Top;

        foreach (var cap in capsules)
        {
            if (!cap.Capsule.IsLoaded || cap.Capsule.ActualWidth <= 0 || cap.Capsule.ActualHeight <= 0) continue;
            var topLeft = cap.Capsule.TranslatePoint(new Point(0, 0), rootGrid);
            var bounds = new Rect(originX + topLeft.X, originY + topLeft.Y, cap.Capsule.ActualWidth, cap.Capsule.ActualHeight);
            var viewbox = ComputeWallpaperViewbox(bounds, refW, refH);
            var current = cap.WallpaperBrush.Viewbox;
            if (Math.Abs(current.X - viewbox.X) > 0.0005
                || Math.Abs(current.Y - viewbox.Y) > 0.0005
                || Math.Abs(current.Width - viewbox.Width) > 0.0005
                || Math.Abs(current.Height - viewbox.Height) > 0.0005)
            {
                cap.WallpaperBrush.Viewbox = viewbox;
            }
        }
    }

    private void PositionContextPill(bool animate)
    {
        if (!contextVisible) return;
        double targetCenter = c.Store.Data.Settings.Focus
            ? AppsCapsuleRootCenterX
            : (SuggestedClusterRootCenterX ?? AppsCapsuleRootCenterX);
        double hostWidth = HostWidth;
        if (!string.Equals(measuredContextPillText, contextPillText.Text, StringComparison.Ordinal) || cachedContextPillWidth <= 0)
        {
            measuredContextPillText = contextPillText.Text;
            contextPill.Measure(new Size(double.PositiveInfinity, Height));
            cachedContextPillWidth = contextPill.DesiredSize.Width;
        }
        double pillWidth = cachedContextPillWidth;
        double targetLeft = ComputeHoverPillLeft(targetCenter, pillWidth, hostWidth);
        if (animate && hasInitialLayout && IsLoaded && IsVisible && Motion.Enabled && !IsAppsCapsuleWidthAnimating)
            Motion.AnimateTo(contextPillOffset, TranslateTransform.XProperty, targetLeft, 150);
        else
            SnapTo(contextPillOffset, TranslateTransform.XProperty, targetLeft);
    }

    private void FitDockLayout(bool animateWidth = false)
    {
        double dockWidth = !double.IsNaN(Width) && Width > 0 ? Width : (ActualWidth > 0 ? ActualWidth : SystemParameters.PrimaryScreenWidth);
        double statusMarginH = statusCapsule.Margin.Left + statusCapsule.Margin.Right;
        double statusBoxWidth;
        if (statusCapsule.IsLoaded && statusCapsule.ActualWidth > 0)
        {
            statusBoxWidth = statusCapsule.ActualWidth;
        }
        else
        {
            statusCapsule.Measure(new Size(double.PositiveInfinity, statusCapsule.Height));
            double measuredStatus = Math.Max(0, statusCapsule.DesiredSize.Width - statusMarginH);
            statusBoxWidth = Math.Max(220, measuredStatus);
        }
        double statusOuterMargin = Loc.IsArabic ? statusCapsule.Margin.Left : statusCapsule.Margin.Right;
        const double capsuleGap = 16;
        double reservedStatusWidth = statusBoxWidth + statusOuterMargin + capsuleGap;

        double essentialsWidth;
        if (essentialsCapsule.IsLoaded && essentialsCapsule.ActualWidth > 0)
        {
            essentialsWidth = essentialsCapsule.ActualWidth + essentialsCapsule.Margin.Left + essentialsCapsule.Margin.Right;
        }
        else
        {
            essentialsCapsule.Measure(new Size(double.PositiveInfinity, essentialsCapsule.Height));
            essentialsWidth = Math.Max(164, essentialsCapsule.DesiredSize.Width);
        }

        double appsMarginH = apps.Margin.Left + apps.Margin.Right;
        double measuredAppsContent = 0;
        foreach (UIElement child in apps.Children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            measuredAppsContent += child.DesiredSize.Width;
        }
        UpdateRestCenters();
        double fallbackAppsContent = icons.Count * 52 + lastDividerCount * 17;
        double appsContentWidth = Math.Max(measuredAppsContent, fallbackAppsContent);

        var (appsWidth, scale) = ComputeAppsLayout(dockWidth, essentialsWidth, reservedStatusWidth, appsContentWidth, appsMarginH);
        if (Math.Abs(scale - lastAppsScale) > 0.001)
        {
            lastAppsScale = scale;
            apps.LayoutTransform = scale < 0.999 ? new ScaleTransform(scale, scale) : Transform.Identity;
            double bottomMargin = Math.Max(2, (56 - 52 * scale) / 2);
            apps.Margin = new Thickness(12, 0, 12, bottomMargin);
        }

        if (double.IsNaN(committedAppsWidth) || Math.Abs(appsWidth - committedAppsWidth) > 0.5)
        {
            bool shouldAnimate = animateWidth && hasInitialLayout && IsLoaded && IsVisible && Motion.Enabled && double.IsFinite(committedAppsWidth);
            committedAppsWidth = appsWidth;
            if (shouldAnimate)
                Motion.AnimateTo(appsCapsule, FrameworkElement.WidthProperty, appsWidth, 180);
            else
                SnapTo(appsCapsule, FrameworkElement.WidthProperty, appsWidth);
        }
    }

    public void Refresh(IReadOnlyList<DesktopService.RunningWindow>? snapshot = null)
    {
        StopHoverTracking(snapToRest: false, hideHoverPill: true);
        apps.Children.Clear(); icons.Clear(); dividers.Clear();
        hoverPill.Visibility = Visibility.Collapsed;
        hoverPillOffset.X = hoverPillCurrentX = hoverPillTargetX;

        IReadOnlyList<DesktopService.RunningWindow> running = snapshot ?? (Left > -10000 ? DesktopService.RunningWindows() : []);
        previousSnapshot = BuildSnapshot(running);
        var matchedHandles = new HashSet<IntPtr>();

        // 1. NIVO Glowing Orb right inside the Apps capsule (like frame_08 / frame_10 / frame_25)
        AddNivoOrbButton();

        // 2. Pinned apps (stay fixed in place)
        foreach (var item in c.Store.Data.Launchers.Take(8))
        {
            var openWin = running.FirstOrDefault(w => MatchesTarget(item.Target, w));
            if (openWin != null) matchedHandles.Add(openWin.Handle);
            AddPinnedApp(item, openWin);
        }

        // 3. Live running Windows apps not already pinned (hidden in Focus mode)
        var unpinnedRunning = running.Where(w => !matchedHandles.Contains(w.Handle) && !string.IsNullOrEmpty(w.Executable) && (!c.Store.Data.Settings.Focus || FocusPolicy.KeepRunning(c.Store.Data.Settings.FocusProfile, w.Process))).Take(6).ToArray();
        if (unpinnedRunning.Length > 0)
        {
            AddDivider();
            foreach (var win in unpinnedRunning) AddRunningApp(win);
        }

        // 4. Context-aware suggestions (hidden in Focus mode)
        string context = FocusPolicy.ContextProfile(c.LastExternalProcess);
        bool coding = context == "Coding";
        bool study = context == "Study";
        var suggestions = c.Store.Data.Settings.Focus ? Array.Empty<Launcher>() : coding
            ? new[] { new Launcher { Name = "GitHub", Target = "https://github.com", Glyph = "\uE943" }, new Launcher { Name = "Slack", Target = "https://app.slack.com", Glyph = "\uE8F2" }, new Launcher { Name = "Google", Target = "https://www.google.com", Glyph = "\uE721" } }
            : study
            ? new[] { new Launcher { Name = Loc.T("Scholar", "الباحث العلمي"), Target = "https://scholar.google.com", Glyph = "\uE7BE" }, new Launcher { Name = Loc.T("Notes", "الملاحظات"), Target = "notepad.exe", Glyph = "\uE70B" }, new Launcher { Name = Loc.T("PDF Reader", "قارئ PDF"), Target = "nivo:choose-pdf", Glyph = "\uEA90" } }
            : Array.Empty<Launcher>();
        var extra = suggestions.Where(s => !c.Store.Data.Launchers.Any(a => SameTarget(a.Target, s.Target))).ToArray();
        var nextSuggestedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (extra.Length > 0)
        {
            AddDivider();
            foreach (var item in extra)
            {
                string key = NormalizeTarget(item.Target);
                nextSuggestedTargets.Add(key);
                bool enterSuggestion = hasInitialLayout && IsVisible && !previousSuggestedTargets.Contains(key);
                AddSuggestedApp(item, enterSuggestion);
            }
        }
        previousSuggestedTargets = nextSuggestedTargets;

        bool nextContextVisible;
        string nextContextText;
        if (c.Store.Data.Settings.Focus)
        {
            string profile = FocusPolicy.DisplayName(c.Store.Data.Settings.FocusProfile);
            nextContextText = Loc.T($"🌙  Focus mode · {profile}", $"🌙  وضع التركيز · {profile}");
            nextContextVisible = true;
        }
        else if (extra.Length > 0)
        {
            nextContextText = coding ? Loc.T("✨  Suggested for coding", "✨  مقترحة للبرمجة") : Loc.T("✨  Suggested for study", "✨  مقترحة للدراسة");
            nextContextVisible = true;
        }
        else
        {
            nextContextText = "";
            nextContextVisible = false;
        }

        bool wasContextVisible = contextVisible;
        bool contextChanged = nextContextVisible && (!wasContextVisible || !string.Equals(lastContextText, nextContextText, StringComparison.Ordinal));
        contextVisible = nextContextVisible;
        lastContextText = nextContextText;
        if (nextContextVisible)
        {
            contextPillText.Text = nextContextText;
            contextPill.Visibility = Visibility.Visible;
        }
        else
        {
            contextPill.Visibility = Visibility.Collapsed;
            SnapTo(contextPillOffset, TranslateTransform.XProperty, contextPillOffset.X);
        }

        lastDividerCount = (unpinnedRunning.Length > 0 ? 1 : 0) + (extra.Length > 0 ? 1 : 0);
        OnStateChanged();
        FitDockLayout(animateWidth: true);
        if (nextContextVisible)
        {
            PositionContextPill(animate: wasContextVisible && !contextChanged);
            if (contextChanged && hasInitialLayout && IsVisible)
            {
                Motion.Enter(contextPill, 6);
                contextEntranceCount++;
            }
        }
        Top = (c.Store.Data.Settings.ReplaceTaskbar ? SystemParameters.PrimaryScreenHeight : SystemParameters.WorkArea.Bottom) - Height;
        UpdateWallpaperViewboxes();
    }

    private void AddNivoOrbButton()
    {
        var b = Ui.Btn("", () => ((App)Application.Current).Hub.ShowAssistant());
        b.Width = 48; b.Height = 52; b.Padding = new Thickness(4); b.Margin = new Thickness(2, 0, 2, 0);
        b.Background = Brushes.Transparent; b.BorderThickness = new Thickness(0);
        b.ToolTip = Loc.T("NIVO Hub", "نيڤو");
        ToolTipService.SetIsEnabled(b, false);
        System.Windows.Automation.AutomationProperties.SetName(b, (string)b.ToolTip);
        var orb = Ui.Orb(34);
        var dot = new Border { Width = 5, Height = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 3, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, Background = ShellTheme.Surface("#38D6F5") };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(orb); stack.Children.Add(dot);
        b.Content = stack;
        b.ContextMenu = BuildQuickMenu();
        var scale = new ScaleTransform(); var lift = new TranslateTransform();
        var transforms = new TransformGroup(); transforms.Children.Add(b.RenderTransform); transforms.Children.Add(scale); transforms.Children.Add(lift);
        b.RenderTransform = transforms; b.RenderTransformOrigin = new Point(.5, 1);
        icons.Add(new DockIcon(b, orb, null, scale, lift, Loc.T("NIVO", "نيڤو"), false));
        apps.Children.Add(b);
    }

    private void AddDivider()
    {
        var lift = new TranslateTransform();
        var divider = new Border { Width = 1, Height = 26, Margin = new Thickness(8, 0, 8, 12), Background = ShellTheme.Line("#8880B8F0"), RenderTransform = lift };
        dividers.Add(new DockDivider(divider, lift));
        apps.Children.Add(divider);
    }

    internal int IconCount => icons.Count;
    internal double IconAppsCenterX(int index) => icons[index].Button.TranslatePoint(new Point((icons[index].Button.ActualWidth > 0 ? icons[index].Button.ActualWidth : icons[index].Button.Width) / 2, 0), apps).X;
    internal double IconRootCenterX(int index) => icons[index].Button.TranslatePoint(new Point((icons[index].Button.ActualWidth > 0 ? icons[index].Button.ActualWidth : icons[index].Button.Width) / 2, 0), rootGrid).X;
    internal double IconRestCenterX(int index) => GetIconRestCenterX(icons[index]);
    internal double IconScale(int index) => icons[index].Scale.ScaleX;
    internal double IconLiftX(int index) => icons[index].Lift.X;
    internal double IconLiftY(int index) => icons[index].Lift.Y;
    internal bool IconHasActiveHalo(int index) => icons[index].ActiveHalo != null;
    internal Rect IconRootBounds(int index)
    {
        var button = icons[index].Button;
        if (!rootGrid.IsAncestorOf(button)) return Rect.Empty;
        double w = button.ActualWidth > 0 ? button.ActualWidth : button.Width;
        double h = button.ActualHeight > 0 ? button.ActualHeight : button.Height;
        return button.TransformToAncestor(rootGrid).TransformBounds(new Rect(0, 0, w, h));
    }
    internal Rect IconVisualRootBounds(int index)
    {
        var visual = icons[index].Visual;
        if (!rootGrid.IsAncestorOf(visual)) return Rect.Empty;
        double w = visual.ActualWidth > 0 ? visual.ActualWidth : visual.Width;
        double h = visual.ActualHeight > 0 ? visual.ActualHeight : visual.Height;
        return visual.TransformToAncestor(rootGrid).TransformBounds(new Rect(0, 0, w, h));
    }
    internal double HoverPillRootCenterX => hoverPill.TranslatePoint(new Point((hoverPill.ActualWidth > 0 ? hoverPill.ActualWidth : hoverPill.DesiredSize.Width) / 2, 0), rootGrid).X;
    internal string HoverPillLabel => hoverPillText.Text;
    internal bool HoverPillVisible => hoverPill.Visibility == Visibility.Visible;
    internal bool IsHoverPillAnimating => hoverPillTracking || Motion.IsAnimating(hoverPillOffset, TranslateTransform.XProperty) || hoverPillOffset.HasAnimatedProperties;
    internal bool HasActiveMagnifyClocks =>
        hoverTrackingActive
        || icons.Any(i => Motion.IsAnimating(i.Scale, ScaleTransform.ScaleXProperty)
            || Motion.IsAnimating(i.Scale, ScaleTransform.ScaleYProperty)
            || Motion.IsAnimating(i.Lift, TranslateTransform.XProperty)
            || Motion.IsAnimating(i.Lift, TranslateTransform.YProperty)
            || i.Scale.HasAnimatedProperties
            || i.Lift.HasAnimatedProperties)
        || dividers.Any(d => Motion.IsAnimating(d.Lift, TranslateTransform.XProperty) || d.Lift.HasAnimatedProperties)
        || IsHoverPillAnimating;

    internal double AppsCapsuleWidth => appsCapsule.ActualWidth > 0 ? appsCapsule.ActualWidth : appsCapsule.Width;
    internal double CommittedAppsCapsuleWidth => committedAppsWidth;
    internal bool IsAppsCapsuleWidthAnimating => Motion.IsAnimating(appsCapsule, FrameworkElement.WidthProperty) || appsCapsule.HasAnimatedProperties;
    internal bool ContextPillVisible => contextPill.Visibility == Visibility.Visible;
    internal string ContextPillLabel => contextPillText.Text;
    internal double ContextPillRootCenterX => contextPill.TranslatePoint(new Point((contextPill.ActualWidth > 0 ? contextPill.ActualWidth : contextPill.DesiredSize.Width) / 2, 0), rootGrid).X;
    internal double AppsCapsuleRootCenterX
    {
        get
        {
            if (appsCapsule.IsLoaded && appsCapsule.ActualWidth > 0)
                return appsCapsule.TranslatePoint(new Point(appsCapsule.ActualWidth / 2, 0), rootGrid).X;
            double appsW = double.IsFinite(committedAppsWidth) ? committedAppsWidth : (!double.IsNaN(appsCapsule.Width) ? appsCapsule.Width : 200);
            double essW = essentialsCapsule.DesiredSize.Width > 0 ? essentialsCapsule.DesiredSize.Width : 164;
            double clusterLeft = (HostWidth - (appsW + essW)) / 2;
            return Loc.IsArabic ? clusterLeft + appsW / 2 : clusterLeft + essW + appsW / 2;
        }
    }
    internal double? SuggestedClusterRootCenterX
    {
        get
        {
            int first = -1, last = -1;
            for (int i = 0; i < icons.Count; i++)
            {
                if (!icons[i].IsSuggested) continue;
                if (first < 0) first = i;
                last = i;
            }
            if (first < 0) return null;
            double clusterCenterApps = (GetIconRestCenterX(icons[first]) + GetIconRestCenterX(icons[last])) / 2.0;
            if (apps.IsLoaded && apps.ActualWidth > 0)
                return apps.TranslatePoint(new Point(clusterCenterApps, 0), rootGrid).X;
            double appsW = double.IsFinite(committedAppsWidth) ? committedAppsWidth : (!double.IsNaN(appsCapsule.Width) ? appsCapsule.Width : 200);
            double appsLeftInRoot = AppsCapsuleRootCenterX - appsW / 2 + apps.Margin.Left;
            return appsLeftInRoot + clusterCenterApps * lastAppsScale;
        }
    }
    internal int ContextPillEntranceCount => contextEntranceCount;
    internal int SuggestedEntranceCount => suggestedEntranceCount;
    internal Rect CapsuleWallpaperViewbox(string capsuleTag) => capsules.Single(cap => cap.Tag == capsuleTag).WallpaperBrush.Viewbox;
    internal IReadOnlyDictionary<string, Rect> WallpaperViewboxes => capsules.ToDictionary(cap => cap.Tag, cap => cap.WallpaperBrush.Viewbox, StringComparer.Ordinal);
    internal double CapsuleGlassOpacity(string capsuleTag) => capsules.Single(cap => cap.Tag == capsuleTag).GlassOverlay.Opacity;

    private void OnMotionPolicyChanged()
    {
        if (!Motion.Enabled)
            StopHoverTracking(snapToRest: true, hideHoverPill: false);
    }

    private void CancelDockClocks()
    {
        Motion.Cancel(appsCapsule);
        Motion.Cancel(contextPillOffset);
        Motion.Cancel(centerCluster);
        Motion.Cancel(contextPill);
        Motion.Cancel(hoverPillOffset);
    }

    private bool IsHoverAtRest()
    {
        for (int i = 0; i < icons.Count; i++)
        {
            var item = icons[i];
            if (Math.Abs(item.CurrentScale - 1.0) >= 0.0005
                || Math.Abs(item.CurrentLiftX) >= 0.01
                || Math.Abs(item.CurrentLiftY) >= 0.01)
                return false;
        }
        for (int i = 0; i < dividers.Count; i++)
        {
            if (Math.Abs(dividers[i].CurrentLiftX) >= 0.01) return false;
        }
        return !hoverPillTracking;
    }

    private void SnapIconsAndDividersToRest()
    {
        for (int i = 0; i < icons.Count; i++)
        {
            var item = icons[i];
            item.TargetScale = 1.0;
            item.TargetLiftX = 0.0;
            item.TargetLiftY = 0.0;
            if (Math.Abs(item.CurrentScale - 1.0) >= 1e-6)
            {
                item.CurrentScale = 1.0;
                item.Scale.ScaleX = 1.0;
                item.Scale.ScaleY = 1.0;
            }
            if (Math.Abs(item.CurrentLiftX) >= 1e-6)
            {
                item.CurrentLiftX = 0.0;
                item.Lift.X = 0.0;
            }
            if (Math.Abs(item.CurrentLiftY) >= 1e-6)
            {
                item.CurrentLiftY = 0.0;
                item.Lift.Y = 0.0;
            }
        }
        for (int i = 0; i < dividers.Count; i++)
        {
            var divider = dividers[i];
            divider.TargetLiftX = 0.0;
            if (Math.Abs(divider.CurrentLiftX) >= 1e-6)
            {
                divider.CurrentLiftX = 0.0;
                divider.Lift.X = 0.0;
            }
        }
    }

    private void HideHoverPill()
    {
        hoverPillTracking = false;
        if (hoverPill.Visibility != Visibility.Collapsed)
            hoverPill.Visibility = Visibility.Collapsed;
        var nextContextVis = contextVisible ? Visibility.Visible : Visibility.Collapsed;
        if (contextPill.Visibility != nextContextVis)
            contextPill.Visibility = nextContextVis;
    }

    private void StopHoverTracking(bool snapToRest, bool hideHoverPill)
    {
        if (hoverTrackingActive)
        {
            hoverTrackingActive = false;
            Motion.UnregisterRenderParticipant(this);
        }
        hoverTargetsDirty = false;
        hoverPillTracking = false;
        if (snapToRest) SnapIconsAndDividersToRest();
        if (hideHoverPill)
        {
            activePointerX = null;
            HideHoverPill();
        }
        else if (activePointerX.HasValue && hoverPill.Visibility == Visibility.Visible)
        {
            UpdateHoverTargets(activePointerX, animatePill: false);
        }
    }

    private void UpdateHoverTargets(double? x, bool animatePill)
    {
        bool focusMode = c.Store.Data.Settings.Focus;
        bool magnifyEnabled = x.HasValue && Motion.Enabled;
        DockIcon? closest = null;
        double closestRestCenter = 0;
        double closestTargetLiftX = 0;
        double bestDist = 42;

        for (int i = 0; i < icons.Count; i++)
        {
            var item = icons[i];
            double restCenter = GetIconRestCenterX(item);
            double targetScale = 1.0, targetLiftY = 0.0, targetLiftX = 0.0;
            if (x.HasValue)
            {
                double dist = Math.Abs(x.Value - restCenter);
                if (magnifyEnabled)
                    (targetScale, targetLiftY, targetLiftX) = ComputeMagnifyTransform(restCenter, x.Value, focusMode);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    closest = item;
                    closestRestCenter = restCenter;
                    closestTargetLiftX = targetLiftX;
                }
            }
            item.TargetScale = targetScale;
            item.TargetLiftX = targetLiftX;
            item.TargetLiftY = targetLiftY;
        }

        for (int i = 0; i < dividers.Count; i++)
        {
            var divider = dividers[i];
            double targetLiftX = 0.0;
            if (magnifyEnabled)
            {
                double restCenter = GetDividerRestCenterX(divider);
                (_, _, targetLiftX) = ComputeMagnifyTransform(restCenter, x!.Value, focusMode);
            }
            divider.TargetLiftX = targetLiftX;
        }

        if (closest is { } hit && x.HasValue)
        {
            bool wasHoverVisible = hoverPill.Visibility == Visibility.Visible;
            if (!wasHoverVisible) hoverPill.Visibility = Visibility.Visible;
            if (contextPill.Visibility != Visibility.Collapsed) contextPill.Visibility = Visibility.Collapsed;
            if (!string.Equals(lastHoverPillText, hit.DisplayName, StringComparison.Ordinal) || cachedHoverPillWidth <= 0)
            {
                lastHoverPillText = hit.DisplayName;
                hoverPillText.Text = lastHoverPillText;
                hoverPill.Measure(new Size(double.PositiveInfinity, Height));
                cachedHoverPillWidth = hoverPill.DesiredSize.Width;
            }

            double settledAppsX = closestRestCenter + closestTargetLiftX;
            double hitWidth = hit.Button.ActualWidth > 0 ? hit.Button.ActualWidth : hit.Button.Width;
            var winPos = apps.IsLoaded && apps.ActualWidth > 0
                ? apps.TranslatePoint(new Point(settledAppsX, 0), rootGrid)
                : hit.Button.TranslatePoint(new Point(hitWidth / 2, 0), rootGrid);
            double targetLeft = ComputeHoverPillLeft(winPos.X, cachedHoverPillWidth, HostWidth);
            hoverPillTargetX = targetLeft;
            if (animatePill && wasHoverVisible && Motion.Enabled)
            {
                hoverPillTracking = Math.Abs(hoverPillCurrentX - targetLeft) >= 0.05;
                if (!hoverPillTracking && Math.Abs(hoverPillCurrentX - targetLeft) >= 1e-6)
                {
                    hoverPillCurrentX = targetLeft;
                    hoverPillOffset.X = targetLeft;
                }
            }
            else
            {
                hoverPillTracking = false;
                if (Math.Abs(hoverPillCurrentX - targetLeft) >= 1e-6 || Math.Abs(hoverPillOffset.X - targetLeft) >= 1e-6)
                {
                    hoverPillCurrentX = targetLeft;
                    hoverPillOffset.X = targetLeft;
                }
            }
        }
        else
        {
            HideHoverPill();
        }
    }

    bool Motion.IRenderParticipant.Step(double dtSeconds)
    {
        if (closed || !IsVisible || !Motion.Enabled)
        {
            hoverTrackingActive = false;
            hoverTargetsDirty = false;
            hoverPillTracking = false;
            SnapIconsAndDividersToRest();
            if (closed || !IsVisible) HideHoverPill();
            else if (activePointerX.HasValue) UpdateHoverTargets(activePointerX, animatePill: false);
            return false;
        }

        if (hoverTargetsDirty)
        {
            hoverTargetsDirty = false;
            UpdateHoverTargets(activePointerX, animatePill: true);
        }

        double alpha = 1.0 - Math.Exp(-dtSeconds / HoverTauSeconds);
        double pillAlpha = 1.0 - Math.Exp(-dtSeconds / HoverPillTauSeconds);
        bool anyMoving = false;

        for (int i = 0; i < icons.Count; i++)
        {
            var item = icons[i];

            double dScale = item.TargetScale - item.CurrentScale;
            if (Math.Abs(dScale) >= 0.0005)
            {
                item.CurrentScale += dScale * alpha;
                item.Scale.ScaleX = item.CurrentScale;
                item.Scale.ScaleY = item.CurrentScale;
                anyMoving = true;
            }
            else if (Math.Abs(dScale) >= 1e-6)
            {
                item.CurrentScale = item.TargetScale;
                item.Scale.ScaleX = item.CurrentScale;
                item.Scale.ScaleY = item.CurrentScale;
            }

            double dLiftX = item.TargetLiftX - item.CurrentLiftX;
            if (Math.Abs(dLiftX) >= 0.03)
            {
                item.CurrentLiftX += dLiftX * alpha;
                item.Lift.X = item.CurrentLiftX;
                anyMoving = true;
            }
            else if (Math.Abs(dLiftX) >= 1e-6)
            {
                item.CurrentLiftX = item.TargetLiftX;
                item.Lift.X = item.CurrentLiftX;
            }

            double dLiftY = item.TargetLiftY - item.CurrentLiftY;
            if (Math.Abs(dLiftY) >= 0.03)
            {
                item.CurrentLiftY += dLiftY * alpha;
                item.Lift.Y = item.CurrentLiftY;
                anyMoving = true;
            }
            else if (Math.Abs(dLiftY) >= 1e-6)
            {
                item.CurrentLiftY = item.TargetLiftY;
                item.Lift.Y = item.CurrentLiftY;
            }
        }

        for (int i = 0; i < dividers.Count; i++)
        {
            var divider = dividers[i];
            double dLiftX = divider.TargetLiftX - divider.CurrentLiftX;
            if (Math.Abs(dLiftX) >= 0.03)
            {
                divider.CurrentLiftX += dLiftX * alpha;
                divider.Lift.X = divider.CurrentLiftX;
                anyMoving = true;
            }
            else if (Math.Abs(dLiftX) >= 1e-6)
            {
                divider.CurrentLiftX = divider.TargetLiftX;
                divider.Lift.X = divider.CurrentLiftX;
            }
        }

        if (hoverPillTracking)
        {
            double dPill = hoverPillTargetX - hoverPillCurrentX;
            if (Math.Abs(dPill) >= 0.05)
            {
                hoverPillCurrentX += dPill * pillAlpha;
                hoverPillOffset.X = hoverPillCurrentX;
                anyMoving = true;
            }
            else
            {
                hoverPillCurrentX = hoverPillTargetX;
                hoverPillOffset.X = hoverPillCurrentX;
                hoverPillTracking = false;
            }
        }

        if (!anyMoving)
        {
            hoverTrackingActive = false;
            return false;
        }
        return true;
    }

    void Motion.IRenderParticipant.OnMotionDisabled()
    {
        hoverTrackingActive = false;
        hoverTargetsDirty = false;
        hoverPillTracking = false;
        SnapIconsAndDividersToRest();
        if (activePointerX.HasValue && IsVisible && !closed)
            UpdateHoverTargets(activePointerX, animatePill: false);
        else
            HideHoverPill();
    }

    internal void Magnify(double? x)
    {
        if (closed || !IsVisible)
        {
            StopHoverTracking(snapToRest: true, hideHoverPill: true);
            return;
        }

        activePointerX = x;
        if (!Motion.Enabled)
        {
            StopHoverTracking(snapToRest: true, hideHoverPill: !x.HasValue);
            if (x.HasValue) UpdateHoverTargets(x, animatePill: false);
            return;
        }

        if (!x.HasValue)
        {
            HideHoverPill();
            if (!hoverTrackingActive && IsHoverAtRest())
            {
                hoverTargetsDirty = false;
                return;
            }
        }

        hoverTargetsDirty = true;
        if (!hoverTrackingActive)
        {
            hoverTrackingActive = true;
            Motion.RegisterRenderParticipant(this);
        }
    }

    public async void UpdateContext()
    {
        if (Left <= -10000 || snapshotInFlight || closed) return;
        snapshotInFlight = true;
        try
        {
            var running = await Task.Run(DesktopService.RunningWindows); if (closed) return;
            ApplySnapshot(running);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { System.Diagnostics.Trace.TraceInformation("NIVO window snapshot skipped ({0}).", ex.GetType().Name); }
        finally { snapshotInFlight = false; }
    }

    private void AddPinnedApp(Launcher item, DesktopService.RunningWindow? running)
    {
        var b = Ui.Btn("", () => { if (running != null) DesktopService.FocusWindow(running.Handle); else DesktopService.OpenTarget(item.Target); });
        ConfigureDockButton(b, item.Target, item.Glyph, item.Name + " · pinned", item.Name, running != null, running?.Active == true, isSuggested: false);
        var menu = new ContextMenu();
        if (running != null)
        {
            var focus = new MenuItem { Header = Loc.T("Switch to ", "انتقل إلى ") + item.Name }; focus.Click += (_, _) => DesktopService.FocusWindow(running.Handle); menu.Items.Add(focus);
            var close = new MenuItem { Header = Loc.T("Close window", "إغلاق النافذة") }; close.Click += (_, _) => DesktopService.CloseWindow(running.Handle); menu.Items.Add(close);
        }
        var remove = new MenuItem { Header = Loc.T("Unpin ", "إلغاء تثبيت ") + item.Name };
        remove.Click += (_, _) => { c.Store.Data.Launchers.Remove(item); c.Store.Save(); Refresh(); };
        menu.Items.Add(remove); b.ContextMenu = menu;
        apps.Children.Add(b);
    }

    private void AddRunningApp(DesktopService.RunningWindow win)
    {
        var b = Ui.Btn("", () => DesktopService.FocusWindow(win.Handle));
        string shortName = win.Title.Length > 24 ? win.Process : win.Title;
        ConfigureDockButton(b, win.Executable, "\uE737", win.Title + Loc.T(" · running", " · مفتوح"), shortName, true, win.Active, isSuggested: false);
        var menu = new ContextMenu();
        var pin = new MenuItem { Header = Loc.T("Pin to NIVO Dock", "تثبيت في شريط نيڤو") };
        pin.Click += (_, _) => {
            if (c.Store.Data.Launchers.Any(a => SameTarget(a.Target, win.Executable))) return;
            if (c.Store.Data.Launchers.Count >= 8) { Ui.Error(new InvalidOperationException(Loc.T("Unpin an app before adding another (8 max).", "الحد الأقصى 8 تطبيقات مثبّتة."))); return; }
            c.Store.Data.Launchers.Add(new() { Name = win.Process, Target = win.Executable }); c.Store.Save(); Refresh();
        };
        var close = new MenuItem { Header = Loc.T("Close window", "إغلاق النافذة") };
        close.Click += (_, _) => DesktopService.CloseWindow(win.Handle);
        menu.Items.Add(pin); menu.Items.Add(close); b.ContextMenu = menu;
        apps.Children.Add(b);
    }

    private void AddSuggestedApp(Launcher item, bool enter)
    {
        var b = Ui.Btn("", () => { if (item.Target == "nivo:choose-pdf") { string? pdf = Ui.PickFile("PDF documents|*.pdf"); if (pdf != null) DesktopService.OpenTarget(pdf); } else DesktopService.OpenTarget(item.Target); });
        ConfigureDockButton(b, item.Target, item.Glyph, item.Name + " · suggested", item.Name, false, false, isSuggested: true);
        apps.Children.Add(b);
        if (enter && b.Content is FrameworkElement visual)
        {
            Motion.Enter(visual, 10);
            suggestedEntranceCount++;
        }
    }

    private void ConfigureDockButton(Button b, string target, string fallbackGlyph, string tooltip, string displayName, bool isRunning, bool isActive, bool isSuggested)
    {
        b.Width = 48; b.Height = 52; b.Padding = new Thickness(4); b.Margin = new Thickness(2, 0, 2, 0);
        b.Background = Brushes.Transparent; b.BorderThickness = new Thickness(0); b.ToolTip = tooltip;
        ToolTipService.SetIsEnabled(b, false); // The positioned hover pill is the app's visible label.
        System.Windows.Automation.AutomationProperties.SetName(b, tooltip);
        var actualIcon = AppIcon(target);
        FrameworkElement iconVisual;
        if (actualIcon != null)
            iconVisual = new Image { Source = actualIcon, Width = 34, Height = 34 };
        else
        {
            var g = Ui.Glyph(fallbackGlyph, 19); g.Foreground = Ui.Brush("#E6F4FF");
            iconVisual = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(10), Background = new LinearGradientBrush(Ui.Brush("#384B78").Color, Ui.Brush("#1F2F55").Color, 90), BorderBrush = ShellTheme.Line("#4480B8F0"), BorderThickness = new Thickness(1), Child = g };
        }
        Border? activeHalo = null;
        UIElement iconSlot;
        if (isActive)
        {
            activeHalo = new Border
            {
                Tag = "ActiveIconHalo",
                Width = 38,
                Height = 38,
                Margin = new Thickness(-2),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false
            };
            ApplyActiveHaloStyle(activeHalo);
            var iconHost = new Grid { Width = 34, Height = 34, ClipToBounds = false };
            iconHost.Children.Add(activeHalo);
            iconHost.Children.Add(iconVisual);
            iconSlot = iconHost;
        }
        else
        {
            iconSlot = iconVisual;
        }
        var dot = new Border { Width = isActive ? 18 : isRunning ? 5 : 4, Height = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 3, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, Opacity = isRunning ? 1 : .4 };
        dot.Background = isActive ? Ui.Brush("#38D6F5") : ShellTheme.Ink(isRunning ? "#E2F2FF" : "#80A4C8");
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(iconSlot); stack.Children.Add(dot);
        b.Content = stack;
        var scale = new ScaleTransform(); var lift = new TranslateTransform();
        var transforms = new TransformGroup(); transforms.Children.Add(b.RenderTransform); transforms.Children.Add(scale); transforms.Children.Add(lift);
        b.RenderTransform = transforms; b.RenderTransformOrigin = new Point(.5, 1);
        icons.Add(new DockIcon(b, iconVisual, activeHalo, scale, lift, displayName, isSuggested));
    }

    private static BitmapSource? AppIcon(string target)
    {
        try
        {
            string path = target;
            if (!Path.IsPathRooted(path) && !target.Contains("://")) { path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), target); if (!File.Exists(path)) path = Path.Combine(Environment.SystemDirectory, target); }
            if (!File.Exists(path)) return null;
            if (iconCache.TryGetValue(path, out var cached)) return cached;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path); if (icon == null) return null;
            var bitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(34, 34)); bitmap.Freeze(); if (iconCache.Count >= 128) iconCache.Clear(); iconCache[path] = bitmap; return bitmap;
        }
        catch { return null; }
    }

    private void PinApp()
    {
        if (c.Store.Data.Launchers.Count >= 8) throw new InvalidOperationException(Loc.T("Unpin an app before adding another (eight pinned apps maximum).", "أزل تطبيقًا مثبّتًا أولًا (الحد الأقصى 8 تطبيقات)."));
        var target = Ui.Prompt(Loc.T("Pin an app", "تثبيت تطبيق"), Loc.T("Full .exe / .lnk path, or HTTPS link", "مسار التطبيق الكامل أو رابط HTTPS")); if (target == null) return;
        target = target.Trim();
        if (c.Store.Data.Launchers.Any(a => SameTarget(a.Target, target))) return;
        var name = Ui.Prompt(Loc.T("Pin an app", "تثبيت تطبيق"), Loc.T("Display name", "اسم التطبيق"), Path.GetFileNameWithoutExtension(target)); if (name == null) return;
        c.Store.Data.Launchers.Add(new() { Name = name, Target = target }); c.Store.Save(); Refresh();
    }
}
