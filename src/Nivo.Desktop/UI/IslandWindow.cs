using Nivo.Core;
using Nivo.Services;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
namespace Nivo.UI;

public sealed class IslandWindow : Window, Motion.IRenderParticipant
{
    private static readonly System.Text.RegularExpressions.Regex OutputStampRegex = new(@"-\d{8}-\d{6}-\d{3}-[0-9a-f]{6}(?=\.[^.]+$)", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly SolidColorBrush DotIncognito = FrozenBrush("#A78BFA");
    private static readonly SolidColorBrush DotQuiet = FrozenBrush("#7E91AB");
    private static readonly SolidColorBrush DotRecording = FrozenBrush("#22D3EE");
    private static readonly SolidColorBrush DotBusy = FrozenBrush("#38BDF8");
    private static readonly SolidColorBrush DotSpeaking = FrozenBrush("#60A5FA");
    private static readonly SolidColorBrush DotReady = FrozenBrush("#F59E0B");
    private static SolidColorBrush FrozenBrush(string hex) { var b = Ui.Brush(hex); b.Freeze(); return b; }

    private readonly Controller c;
    private readonly Func<(string? Text, System.Windows.Media.Imaging.BitmapSource? Image)> readClipboard;
    private readonly Action<string?, System.Windows.Media.Imaging.BitmapSource?> writeClipboard;
    private ChatMessage? clipboardReply;
    private ActivityGlyph? bannerActivity;
    private TextBlock? bannerLabel;
    private bool clipTransformed;
    private string inlineMode = "General";
    private readonly TextBlock titleText = new() { FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = ShellTheme.Ink("#F0F8FF"), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock status = new() { FontSize = 11, Foreground = ShellTheme.Ink("#A8C6E8"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border frame;
    private readonly Border contentPadding;
    private readonly Border shellSurface;
    private readonly ScaleTransform shellSize = new(1, 1, 118, 0);
    private readonly TranslateTransform glowOffset = new();
    private readonly ScaleTransform glowWidth = new(1, 1, 92, 0);
    private readonly TranslateTransform headerStartOffset = new();
    private readonly TranslateTransform headerEndOffset = new();
    private readonly RectangleGeometry contentClip = new(new Rect(0, 0, 236, 50));
    private bool morphActive;
    private double morphFromW, morphFromH, morphToW, morphToH, morphElapsed, headerBaseWidth = 208;
    private double lastGlowTarget = double.NaN;
    private bool recentDirty = true, recentInitialized, recentPopulated, lastRecentDirExists;
    private DateTime lastRecentDirWriteUtc;
    private (string Path, long Ticks)[] lastRecentEntries = [];
    internal Size DisplayedSize => new(frame.Width * shellSize.ScaleX, frame.Height * shellSize.ScaleY);
    internal bool IsSizeAnimating => morphActive || Motion.IsAnimating(shellSize, ScaleTransform.ScaleXProperty) || Motion.IsAnimating(shellSize, ScaleTransform.ScaleYProperty) || Motion.IsAnimating(header, FrameworkElement.WidthProperty);
    private readonly Border bottomGlow;
    private readonly Grid header;
    private readonly Grid expandedHost = new() { Tag = "IslandExpandedHost", Visibility = Visibility.Collapsed };
    private readonly StackPanel commandView;
    private readonly StackPanel recentList = new();
    private readonly StackPanel inlineAskView;
    private readonly TextBox inlineInput;
    private readonly TextBlock inlinePlaceholder;
    private readonly Border inlineReplyBox;
    private readonly TextBlock inlineQuestionLabel = new() { FontSize = 11.5, Foreground = ShellTheme.Ink("#8EAED4"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 48, Margin = new Thickness(0, 0, 0, 4) };
    private readonly TextBox inlineReply;
    private readonly Image inlineThumb = new() { Width = 42, Height = 42, Stretch = Stretch.UniformToFill };
    private readonly Border inlineThumbWrap;
    private readonly Button inlineSendBtn;
    private readonly StackPanel clipboardView;
    private readonly Image clipThumb = new() { Width = 58, Height = 46, Stretch = Stretch.UniformToFill };
    private readonly Border clipThumbWrap;
    private readonly TextBox clipPreview;
    private readonly TextBlock clipBadge = new() { FontSize = 11, Foreground = ShellTheme.Ink("#9ADCF5") };
    private readonly Dictionary<string, Button> clipActionButtons = [];
    private readonly StackPanel dropView;
    private readonly TextBlock dropListText = new() { FontSize = 12.5, Foreground = ShellTheme.Ink("#E8F4FF"), TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel dropConvertRow = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly Grid compactBanner = new() { Visibility = Visibility.Collapsed };
    private readonly Button openButton;
    private readonly Button statusActionBtn;
    private readonly TextBlock statusBannerText = new() { FontSize = 12, Foreground = ShellTheme.Ink("#D6E8FF"), VerticalAlignment = VerticalAlignment.Center };
    private readonly System.Windows.Shapes.Ellipse bannerDot = new() { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ActivityGlyph activity = new();
    private readonly System.Windows.Shapes.Ellipse dot = new() { Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer replyPoll = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private readonly DispatcherTimer hoverIntent = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly DispatcherTimer autoHideBanner = new() { Interval = TimeSpan.FromSeconds(12) };
    private readonly DispatcherTimer collapseContent = new() { Interval = TimeSpan.FromMilliseconds(110) };
    private string activeView = "command";
    private string compactMode = ""; // "", "working", "done", "suggest"
    private string[] pendingDroppedFiles = [];
    private string? clipText;
    private System.Windows.Media.Imaging.BitmapSource? clipBitmap;
    private bool pinned, wasBusy, localConverting, closed;
    private bool IsAr => c.Store.Data.Settings.Language != "English";
    public bool IsExpanded { get; private set; }
    public string ActiveView => activeView;

    public IslandWindow(Controller controller, Func<(string? Text, System.Windows.Media.Imaging.BitmapSource? Image)>? clipboardReader = null, Action<string?, System.Windows.Media.Imaging.BitmapSource?>? clipboardWriter = null)
    {
        c = controller;
        readClipboard = clipboardReader ?? (() => (System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null, System.Windows.Clipboard.ContainsImage() ? System.Windows.Clipboard.GetImage() : null));
        writeClipboard = clipboardWriter ?? ((text, image) => { if (image != null) System.Windows.Clipboard.SetImage(image); else if (text != null) System.Windows.Clipboard.SetText(text); });
        Width = 560; Height = 500;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; Topmost = true; ShowInTaskbar = false; ShowActivated = false;
        Title = "NIVO Island"; AllowDrop = true;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && ((App)Application.Current).AccountTalkActive)
            { ((App)Application.Current).RequestAccountTalkStop(); e.Handled = true; }
        };
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2; Top = 0;

        // === Top notch header ===
        header = new Grid { Tag = "IslandHeader", Width = 208, HorizontalAlignment = HorizontalAlignment.Center, Height = 36, FlowDirection = IsAr ? FlowDirection.RightToLeft : FlowDirection.LeftToRight };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        var headerOrb = Ui.Orb(26);
        AppendTransform(headerOrb, headerStartOffset);
        header.Children.Add(headerOrb);
        var labels = Ui.Stack(Ui.Row(dot, titleText), status); labels.VerticalAlignment = VerticalAlignment.Center;
        AppendTransform(labels, headerStartOffset);
        Grid.SetColumn(labels, 1); header.Children.Add(labels);
        activity.IsHitTestVisible = false; AppendTransform(activity, headerEndOffset); Grid.SetColumn(activity, 2); header.Children.Add(activity);
        openButton = Ui.Btn("⌄", ToggleExpanded);
        openButton.Foreground = ShellTheme.Ink("#DCEEFF"); openButton.ToolTip = IsAr ? "توسيع الجزيرة الحيّة" : "Expand Living Island";
        openButton.Margin = new Thickness(0); openButton.Padding = new Thickness(4); Ui.Flat(openButton);
        AppendTransform(openButton, headerEndOffset);
        Grid.SetColumn(openButton, 3); header.Children.Add(openButton);

        // === View 1: Command Center (4x2 Tile Grid + Recent Outputs, matching frame_37 & frame_54) ===
        statusActionBtn = DarkPillBtn("", () => { if (c.Busy) ((App)Application.Current).StopAll(); else c.ToggleIncognito(); });
        statusActionBtn.Margin = new Thickness(0);
        var statusBarGrid = new Grid();
        statusBarGrid.ColumnDefinitions.Add(new ColumnDefinition());
        statusBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var bannerLeft = Ui.Row(bannerDot, statusBannerText); bannerLeft.VerticalAlignment = VerticalAlignment.Center;
        statusBarGrid.Children.Add(bannerLeft); Grid.SetColumn(statusActionBtn, 1); statusBarGrid.Children.Add(statusActionBtn);
        var statusBanner = new Border { Background = ShellTheme.Surface("#1F3566"), BorderBrush = ShellTheme.Line("#3C629E"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 7, 8, 7), Margin = new Thickness(0, 6, 0, 10), Child = statusBarGrid };

        var gridTiles = new UniformGridEx(4) { Margin = new Thickness(0, 0, 0, 10) };
        gridTiles.Children.Add(Tile("\uE720", "Talk", "تحدّث", async () => await TriggerVoiceOrSettings(), canInterrupt: () => c.Store.Data.Settings.UseAccountVoice));
        gridTiles.Children.Add(Tile("\uE8F2", "Silent Ask", "السؤال الصامت", () => { pinned = true; SwitchView("ask"); }));
        gridTiles.Children.Add(Tile("\uE7F4", "Ask screen", "اسأل عن شاشتي", async () => { if (await ((App)Application.Current).CaptureAsync(false, false, false) && c.Capture != null) { pinned = true; Expand(true); SwitchView("ask"); } }));
        gridTiles.Children.Add(Tile("\uE7A8", "Crop & ask", "اسأل عن جزء من الشاشة", async () => { if (await ((App)Application.Current).CaptureAsync(true, false, false) && c.Capture != null) { pinned = true; Expand(true); SwitchView("ask"); } }));
        gridTiles.Children.Add(Tile("\uE8C1", "Translate crop", "ترجم جزءًا من الشاشة", async () => await QuickCropAction("Translate")));
        gridTiles.Children.Add(Tile("\uEA80", "Explain crop", "اشرح جزءًا من الشاشة", async () => await QuickCropAction("Explain")));
        gridTiles.Children.Add(Tile("\uE8C8", "Copy region", "انسخ جزءًا من الشاشة", async () => await QuickCropCopy(), IsAr ? "مجاني" : "Free"));
        gridTiles.Children.Add(Tile("\uE77F", "Smart Clipboard", "الحافظة الذكية", () => { pinned = true; SwitchView("clipboard"); }));

        var recentBar = new Grid { Margin = new Thickness(4, 0, 4, 6) };
        recentBar.ColumnDefinitions.Add(new ColumnDefinition()); recentBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var recentHeader = new TextBlock { Text = IsAr ? "أحدث ما صنعته نيڤو" : "Recent creations", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = ShellTheme.Ink("#8CB3DF"), VerticalAlignment = VerticalAlignment.Center };
        var hubLink = DarkPillBtn(IsAr ? "المركز الرئيسي ↗" : "Open Hub ↗", () => ((App)Application.Current).Hub.ShowAssistant());
        hubLink.Padding = new Thickness(10, 3, 10, 3); hubLink.FontSize = 10.5; hubLink.Margin = new Thickness(0);
        recentBar.Children.Add(recentHeader); Grid.SetColumn(hubLink, 1); recentBar.Children.Add(hubLink);
        commandView = Ui.Stack(statusBanner, gridTiles, recentBar, recentList);

        // === View 2: Inline Silent Ask inside the Notch (matching frame_39 & frame_40) ===
        var askTitle = new TextBlock { Text = IsAr ? "السؤال الصامت" : "Silent Ask", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = ShellTheme.Ink("#F0F8FF"), VerticalAlignment = VerticalAlignment.Center };
        var askHead = new Grid { Margin = new Thickness(2, 0, 2, 8) };
        askHead.ColumnDefinitions.Add(new ColumnDefinition()); askHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        askHead.Children.Add(askTitle);
        var hoverQuickBtn = DarkPillBtn(IsAr ? "🎯 أشّر واسأل" : "🎯 Point & Ask", async () => { if (await ((App)Application.Current).CaptureAsync(false, true, false) && c.Capture != null) { pinned = true; Expand(true); SwitchView("ask"); } });
        hoverQuickBtn.Padding = new Thickness(10, 3, 10, 3); hoverQuickBtn.FontSize = 11; hoverQuickBtn.Margin = new Thickness(0);
        Grid.SetColumn(hoverQuickBtn, 1); askHead.Children.Add(hoverQuickBtn);

        inlineInput = new TextBox { Background = Brushes.Transparent, Foreground = ShellTheme.Ink("#F2F9FF"), BorderThickness = new Thickness(0), CaretBrush = Ui.Brush("#4AE3F5"), FontSize = 13.5, Padding = new Thickness(14, 9, 14, 9), Margin = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center };
        inlinePlaceholder = new TextBlock { Text = IsAr ? "اكتب سؤالك…" : "Ask anything…", Foreground = ShellTheme.Ink("#7B9CC4"), FontSize = 13.5, Margin = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        inlineInput.TextChanged += (_, _) => { inlinePlaceholder.Visibility = inlineInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; inlineInput.FlowDirection = inlineInput.Text.Length == 0 ? (IsAr ? FlowDirection.RightToLeft : FlowDirection.LeftToRight) : (ChatComposer.IsArabic(inlineInput.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight); };
        inlineInput.PreviewKeyDown += async (_, e) => { if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { e.Handled = true; await SendInlineAsk(); } if (e.Key == Key.Escape) { SwitchView("command"); e.Handled = true; } };
        var inputPill = new Border { Background = ShellTheme.Surface("#182C56"), BorderBrush = ShellTheme.Line("#3F6CB2"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(22), MinHeight = 42, Child = new Grid { Children = { inlineInput, inlinePlaceholder } } };

        inlineThumbWrap = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(10), BorderBrush = ShellTheme.Line("#6638D6F5"), BorderThickness = new Thickness(1.5), ClipToBounds = true, Margin = new Thickness(8, 0, 8, 0), Child = inlineThumb, Visibility = Visibility.Collapsed };
        inlineSendBtn = CyanPillBtn("↑", async () => await SendInlineAsk());
        inlineSendBtn.Width = 40; inlineSendBtn.Height = 40; inlineSendBtn.Padding = new Thickness(0); inlineSendBtn.Margin = new Thickness(6, 0, 0, 0); inlineSendBtn.FontSize = 16;

        var askInputGrid = new Grid { Margin = new Thickness(0, 2, 0, 10) };
        askInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        askInputGrid.ColumnDefinitions.Add(new ColumnDefinition());
        askInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        askInputGrid.Children.Add(inlineThumbWrap); Grid.SetColumn(inputPill, 1); askInputGrid.Children.Add(inputPill); Grid.SetColumn(inlineSendBtn, 2); askInputGrid.Children.Add(inlineSendBtn);

        inlineReply = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent, Foreground = ShellTheme.Ink("#EDF6FF"), BorderThickness = new Thickness(0), MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 13.5, Margin = new Thickness(0), Padding = new Thickness(0) };
        var copyInlineBtn = DarkPillBtn("📋", () => { if (inlineReply.Text.Length > 0) System.Windows.Clipboard.SetText(inlineReply.Text); });
        copyInlineBtn.Width = 32; copyInlineBtn.Height = 32; copyInlineBtn.Padding = new Thickness(0); copyInlineBtn.Margin = new Thickness(8, 0, 0, 0); copyInlineBtn.ToolTip = IsAr ? "نسخ الإجابة" : "Copy answer";
        var replyRow = new Grid(); replyRow.ColumnDefinitions.Add(new ColumnDefinition()); replyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        replyRow.Children.Add(Ui.Stack(inlineQuestionLabel, inlineReply)); Grid.SetColumn(copyInlineBtn, 1); copyInlineBtn.VerticalAlignment = VerticalAlignment.Top; replyRow.Children.Add(copyInlineBtn);
        inlineReplyBox = new Border { Background = ShellTheme.Surface("#132347"), BorderBrush = ShellTheme.Line("#2F528C"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 10, 10, 10), Margin = new Thickness(0, 0, 0, 8), Child = replyRow, Visibility = Visibility.Collapsed };

        var askFooter = Ui.Row(DarkPillBtn(IsAr ? "← رجوع" : "← Back", ReturnToCommands), DarkPillBtn(IsAr ? "فتح في المحادثة" : "Open chat", () => ((App)Application.Current).Hub.ShowAssistant()), DarkPillBtn(IsAr ? "احفظ في الذاكرة" : "Remember", () => { if (inlineReply.Text.Length > 0) { c.Store.AddMemory(inlineReply.Text); c.SetStatus(IsAr ? "تم الحفظ في الذاكرة" : "Saved in Memory"); } }));
        inlineAskView = Ui.Stack(askHead, askInputGrid, inlineReplyBox, askFooter);

        // === View 3: Smart Clipboard inside the Notch (matching frame_45) ===
        var clipHead = new Grid { Margin = new Thickness(2, 0, 2, 8) };
        clipHead.ColumnDefinitions.Add(new ColumnDefinition()); clipHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var clipTitle = new TextBlock { Text = IsAr ? "الحافظة الذكية" : "Smart Clipboard", FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = ShellTheme.Ink("#F0F8FF"), VerticalAlignment = VerticalAlignment.Center };
        var clipBadgeWrap = new Border { Background = ShellTheme.Surface("#1B3161"), BorderBrush = ShellTheme.Line("#385E9B"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 3, 10, 3), Child = clipBadge };
        clipHead.Children.Add(clipTitle); Grid.SetColumn(clipBadgeWrap, 1); clipHead.Children.Add(clipBadgeWrap);

        clipThumbWrap = new Border { Width = 60, Height = 48, CornerRadius = new CornerRadius(8), BorderBrush = ShellTheme.Line("#5578AEEB"), BorderThickness = new Thickness(1), ClipToBounds = true, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top, Child = clipThumb, Visibility = Visibility.Collapsed };
        clipPreview = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent, Foreground = ShellTheme.Ink("#E8F4FF"), BorderThickness = new Thickness(0), Height = 78, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 12.5, Margin = new Thickness(0), Padding = new Thickness(0) };
        var clipContentGrid = new Grid(); clipContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); clipContentGrid.ColumnDefinitions.Add(new ColumnDefinition());
        clipContentGrid.Children.Add(clipThumbWrap); Grid.SetColumn(clipPreview, 1); clipContentGrid.Children.Add(clipPreview);
        var clipPreviewCard = new Border { Background = ShellTheme.Surface("#162852"), BorderBrush = ShellTheme.Line("#305089"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 10), Child = clipContentGrid };

        var clipActions = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var (key, en, ar) in new[] { ("Summarize", "Summarize", "لخّص"), ("Translate", "Translate", "ترجم"), ("Rewrite", "Rewrite", "أعد الصياغة"), ("Explain", "Explain", "اشرح") })
        {
            var k = key;
            var btn = DarkPillBtn(IsAr ? ar : en, async () => await RunClipboardAction(k));
            clipActionButtons[k] = btn;
            clipActions.Children.Add(btn);
        }
        var taskClipBtn = DarkPillBtn(IsAr ? "✨ استخدمه في مهمة" : "✨ Use in a task", UseClipboardInTask);
        taskClipBtn.BorderBrush = ShellTheme.Line("#4AE3F5"); taskClipBtn.Foreground = ShellTheme.Ink("#7DF2FF");
        clipActions.Children.Add(taskClipBtn);

        var clipFoot = Ui.Row(DarkPillBtn(IsAr ? "← رجوع" : "← Back", ReturnToCommands), DarkPillBtn(IsAr ? "تحديث الحافظة" : "Refresh", ReadClipboard), DarkPillBtn(IsAr ? "نسخ" : "Copy", CopyClipboardPreview));
        clipboardView = Ui.Stack(clipHead, clipPreviewCard, clipActions, clipFoot);

        // === View 4: Direct 1-Click File Converter & Drop Zone (matching frame_50 & frame_51) ===
        var dropHead = new TextBlock { Text = IsAr ? "⬇  ارمِ ملفك هنا للتحويل أو المعالجة فورًا" : "⬇  Drop files for instant conversion or AI help", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = ShellTheme.Ink("#6FE8FA"), Margin = new Thickness(0, 4, 0, 6) };
        var dropBox = new Border { Background = ShellTheme.Surface("#172C59"), BorderBrush = ShellTheme.Line("#4ADDF2"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 0, 0, 10), Child = new ScrollViewer { Content = dropListText, MaxHeight = 84, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        var dropSecondary = Ui.Row(
            DarkPillBtn(IsAr ? "سلّمها للوكيل" : "Hand to Agent", () => HandleDroppedFiles(true)),
            DarkPillBtn(IsAr ? "اسأل عنها" : "Ask about it", () => HandleDroppedFiles(false)),
            DarkPillBtn(IsAr ? "إلغاء" : "Cancel", () => { pendingDroppedFiles = []; ReturnToCommands(); })
        );
        dropView = Ui.Stack(dropHead, dropBox, dropConvertRow, dropSecondary);

        expandedHost.Children.Add(commandView);
        expandedHost.Children.Add(inlineAskView);
        expandedHost.Children.Add(clipboardView);
        expandedHost.Children.Add(dropView);
        expandedHost.Margin = new Thickness(4, 4, 4, 6);
        expandedHost.FlowDirection = IsAr ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        // Outer notch shell (deep cosmic glass gradient + glowing cyan bottom light bar)
        var notchBg = new LinearGradientBrush(Ui.Brush("#F40C1C44").Color, Ui.Brush("#FA07112B").Color, 90);
        bottomGlow = new Border { Tag = "IslandBottomGlow", Height = 2.5, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(26, 0, 26, 0), CornerRadius = new CornerRadius(2), IsHitTestVisible = false, Background = new LinearGradientBrush { StartPoint = new Point(0, .5), EndPoint = new Point(1, .5), GradientStops = [new GradientStop(Colors.Transparent, 0), new GradientStop(Ui.Brush("#38D6F5").Color, .5), new GradientStop(Colors.Transparent, 1)] } };
        var inner = new Grid();
        contentPadding = new Border { Padding = new Thickness(14, 5, 14, 8), Child = Ui.Stack(header, compactBanner, expandedHost) };
        inner.Children.Add(contentPadding);
        inner.Children.Add(bottomGlow);

        // Keep layout at the destination size. Only the glass surface and content clip
        // morph between sizes, so animation does not repeatedly remeasure the whole panel.
        shellSurface = new Border { Tag = "IslandShellSurface", CornerRadius = new CornerRadius(0, 0, 24, 24), BorderThickness = new Thickness(1, 0, 1, 1), Background = ShellTheme.Glass, BorderBrush = ShellTheme.Line("#5868A0E8"), RenderTransform = shellSize, CacheMode = new BitmapCache(), IsHitTestVisible = false };
        Ui.Elevate(shellSurface);
        contentClip.Transform = shellSize; inner.Clip = contentClip;
        bottomGlow.RenderTransform = new TransformGroup { Children = { glowWidth, glowOffset } };
        var layers = new Grid(); layers.Children.Add(shellSurface); layers.Children.Add(inner);
        frame = new Border { Width = 236, Height = 50, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(0), Margin = new Thickness(8, 0, 8, 8), Background = Brushes.Transparent, Child = layers };
        Content = frame;
        void RefitContent(object? sender, SizeChangedEventArgs args) {
            if (!IsExpanded || activeView is not ("ask" or "drop") || compactMode != "" || closed) return;
            double height = TargetHeight();
            if (Math.Abs(frame.Height - height) < .5) return;
            SetFrameSize(frame.Width, height);
        }
        inlineAskView.SizeChanged += RefitContent;
        dropView.SizeChanged += RefitContent;

        header.MouseLeftButtonDown += (_, e) => { if (Ui.InButton(e.OriginalSource as DependencyObject)) return; if (e.ClickCount == 2) ((App)Application.Current).ShowAsk(); else ToggleExpanded(); };
        hoverIntent.Tick += (_, _) => { hoverIntent.Stop(); if (frame.IsMouseOver && compactMode == "") Expand(true); };
        frame.MouseEnter += (_, _) => { autoHideBanner.Stop(); if (compactMode == "") hoverIntent.Start(); };
        frame.MouseLeave += (_, _) => { hoverIntent.Stop(); if (!pinned && !c.Busy && !localConverting && activeView == "command" && compactMode == "") Expand(false); else if (compactMode is "done" or "suggest") autoHideBanner.Start(); };

        PreviewDragEnter += (_, e) => { if (TryPreviewFiles(e, out var files)) { e.Effects = DragDropEffects.Copy; pinned = true; ShowDropPreview(files); } else e.Effects = DragDropEffects.None; e.Handled = true; };
        PreviewDragLeave += (_, e) => { if (!c.Busy && !localConverting && activeView == "drop" && !new Rect(frame.RenderSize).Contains(e.GetPosition(frame))) { pendingDroppedFiles = []; pinned = false; SwitchView("command"); Expand(false); } };
        PreviewDragOver += (sender, e) => { e.Effects = TryPreviewFiles(e, out _) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) => { e.Handled = true; if (TryPreviewFiles(e, out var files)) DropFiles(files); else e.Effects = DragDropEffects.None; };

        replyPoll.Tick += (_, _) => SyncInlineReply();
        collapseContent.Tick += (_, _) => { collapseContent.Stop(); if (!IsExpanded) { expandedHost.Visibility = Visibility.Collapsed; Motion.AnimateTo(expandedHost, UIElement.OpacityProperty, 1, 0); } };
        autoHideBanner.Tick += (_, _) => { autoHideBanner.Stop(); DismissCompactBanner(); };
        c.Changed += Update;
        c.Store.Changed += OnStoreChanged;
        Motion.PolicyChanged += OnMotionPolicyChanged;
        Closed += (_, _) =>
        {
            closed = true;
            StopMorph(snapToFinal: false);
            collapseContent.Stop(); replyPoll.Stop(); autoHideBanner.Stop(); hoverIntent.Stop();
            c.Changed -= Update; c.Store.Changed -= OnStoreChanged; Motion.PolicyChanged -= OnMotionPolicyChanged;
            CancelIslandClocks();
            activity.SetActive(false); ClearBanner();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Motion.Enter(frame, -8);
                Update();
                if (IsExpanded && c.Busy && (activeView == "ask" || (activeView == "clipboard" && clipboardReply != null))) replyPoll.Start();
                if (compactMode is "done" or "suggest" && Left > -10000) autoHideBanner.Start();
            }
            else
            {
                StopMorph(snapToFinal: true);
                hoverIntent.Stop(); autoHideBanner.Stop(); replyPoll.Stop();
                if (collapseContent.IsEnabled)
                {
                    collapseContent.Stop();
                    if (!IsExpanded) { expandedHost.Visibility = Visibility.Collapsed; Motion.AnimateTo(expandedHost, UIElement.OpacityProperty, 1, 0); }
                }
                CancelIslandClocks();
                activity.SetActive(false); bannerActivity?.SetActive(false);
            }
        };
        SwitchView("command"); RefreshRecent(forceCheck: true); Update();
    }

    // === Public Compact Notch Banner States (frame_34 Done + frame_42 Suggestion + frame_29 Working) ===

    private void ClearBanner() { autoHideBanner.Stop(); bannerActivity?.SetActive(false); bannerActivity = null; bannerLabel = null; compactBanner.Children.Clear(); }

    private void ToggleExpanded()
    {
        pinned = !IsExpanded;
        if (!pinned && c.Busy && !c.BackgroundBusy) ShowWorkingBanner(c.TaskActive && c.TaskDetail.Length > 0 ? c.TaskDetail : c.Status);
        else Expand(pinned);
    }

    public void ShowCompleted(string title, string? filePath = null)
    {
        if (((App)Application.Current).Exiting) return;
        if (closed) { var current = ((App)Application.Current).Island; if (current != null && current != this) current.ShowCompleted(title, filePath); return; }
        collapseContent.Stop(); compactMode = "done"; pinned = false; IsExpanded = false;
        expandedHost.Visibility = Visibility.Collapsed; header.Visibility = Visibility.Collapsed;
        openButton.Content = "⌄";
        ClearBanner(); compactBanner.FlowDirection = IsAr ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        var row = new Grid { Height = 40 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var checkIcon = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = ShellTheme.Surface("#2834D399"), BorderBrush = ShellTheme.Line("#34D399"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 10, 0), Child = new TextBlock { Text = "✓", Foreground = ShellTheme.Ink("#5EEAD4"), FontSize = 13, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        var info = Ui.Stack(new TextBlock { Text = title, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = ShellTheme.Ink("#F0F8FF"), TextTrimming = TextTrimming.CharacterEllipsis }, new TextBlock { Text = IsAr ? "تم" : "Done", FontSize = 10.5, Foreground = ShellTheme.Ink("#7DF2FF") });
        info.VerticalAlignment = VerticalAlignment.Center;
        var btns = Ui.Row(); btns.VerticalAlignment = VerticalAlignment.Center;
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            var openBtn = CyanPillBtn(IsAr ? "فتح" : "Open", () => { DesktopService.OpenTarget(filePath); DismissCompactBanner(); });
            openBtn.Padding = new Thickness(14, 4, 14, 4); openBtn.Margin = new Thickness(6, 0, 4, 0);
            var folderBtn = DarkPillBtn("📁", () => DesktopService.RevealInExplorer(filePath));
            folderBtn.Padding = new Thickness(8, 4, 8, 4); folderBtn.Margin = new Thickness(0, 0, 4, 0); folderBtn.ToolTip = IsAr ? "عرض في المجلد" : "Show in folder";
            btns.Children.Add(openBtn); btns.Children.Add(folderBtn);
        }
        var closeBtn = DarkPillBtn("×", DismissCompactBanner); closeBtn.Padding = new Thickness(8, 3, 8, 3); closeBtn.Margin = new Thickness(0);
        btns.Children.Add(closeBtn);
        row.Children.Add(checkIcon); Grid.SetColumn(info, 1); row.Children.Add(info); Grid.SetColumn(btns, 2); row.Children.Add(btns);
        compactBanner.Children.Add(row); compactBanner.Visibility = Visibility.Visible;
        Motion.Enter(compactBanner, -4);
        SetFrameSize(436, 56);
        SetGlow(1);
        recentDirty = true;
        RefreshRecent(forceCheck: true);
        if (Left > -10000) { Show(); autoHideBanner.Interval = TimeSpan.FromSeconds(12); autoHideBanner.Start(); }
    }

    public void ShowSuggestion(string text, Action onAccept)
    {
        collapseContent.Stop();
        compactMode = "suggest"; pinned = false; IsExpanded = false;
        expandedHost.Visibility = Visibility.Collapsed; header.Visibility = Visibility.Collapsed;
        openButton.Content = "⌄";
        ClearBanner(); compactBanner.FlowDirection = IsAr ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        var row = new Grid { MinHeight = 40 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var infoBadge = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), BorderBrush = ShellTheme.Line("#67E8F9"), BorderThickness = new Thickness(1.3), Margin = new Thickness(0, 0, 10, 0), Child = new TextBlock { Text = "i", Foreground = ShellTheme.Ink("#67E8F9"), FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        var msg = new TextBlock { Text = text, FontSize = 12.5, Foreground = ShellTheme.Ink("#EDF6FF"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 270 };
        var messageScroll = new ScrollViewer { Content = msg, MaxHeight = 84, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var acceptBtn = CyanPillBtn(IsAr ? "ساعدني" : "Do it", () => { DismissCompactBanner(); onAccept(); });
        acceptBtn.Padding = new Thickness(14, 4, 14, 4); acceptBtn.Margin = new Thickness(8, 0, 4, 0);
        var dismissBtn = DarkPillBtn(IsAr ? "ليس الآن" : "Not now", DismissCompactBanner);
        dismissBtn.Padding = new Thickness(12, 4, 12, 4); dismissBtn.Margin = new Thickness(0);
        var btns = Ui.Row(acceptBtn, dismissBtn); btns.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(infoBadge); Grid.SetColumn(messageScroll, 1); row.Children.Add(messageScroll); Grid.SetColumn(btns, 2); row.Children.Add(btns);
        compactBanner.Children.Add(row); compactBanner.Visibility = Visibility.Visible;
        Motion.Enter(compactBanner, -4);
        compactBanner.Measure(new Size(466, double.PositiveInfinity));
        SetFrameSize(496, Math.Clamp(compactBanner.DesiredSize.Height + 14, 58, 112));
        SetGlow(1);
        if (Left > -10000) { Show(); autoHideBanner.Interval = TimeSpan.FromSeconds(25); autoHideBanner.Start(); }
    }

    private void ShowWorkingBanner(string label)
    {
        collapseContent.Stop();
        compactMode = "working"; IsExpanded = false;
        openButton.Content = "⌄";
        expandedHost.Visibility = Visibility.Collapsed; header.Visibility = Visibility.Collapsed;
        ClearBanner(); compactBanner.FlowDirection = IsAr ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        var row = new Grid { Height = 36 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var orbWrap = new Border { Margin = new Thickness(0, 0, 10, 0), Child = Ui.Orb(24) };
        var lbl = new TextBlock { Text = label, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = ShellTheme.Ink("#EFF8FF"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        bannerLabel = lbl;
        var spin = new ActivityGlyph(); bannerActivity = spin; spin.SetActive(true); spin.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(orbWrap); Grid.SetColumn(lbl, 1); row.Children.Add(lbl); var actions = Ui.Row(spin, DarkPillBtn(IsAr ? "إيقاف" : "Stop", () => ((App)Application.Current).StopAll()), DarkPillBtn("⌄", () => { pinned = true; Expand(true); })); foreach (var button in actions.Children.OfType<Button>()) { button.Margin = new Thickness(6, 0, 0, 0); button.VerticalAlignment = VerticalAlignment.Center; }
        Grid.SetColumn(actions, 2); row.Children.Add(actions);
        compactBanner.Children.Add(row); compactBanner.Visibility = Visibility.Visible;
        Motion.Enter(compactBanner, -4);
        SetFrameSize(464, 54);
        SetGlow(1);
    }

    private void DismissCompactBanner()
    {
        ClearBanner();
        compactMode = "";
        compactBanner.Visibility = Visibility.Collapsed;
        header.Visibility = Visibility.Visible;
        openButton.Content = IsExpanded ? "⌃" : "⌄";
        AnimateSize(IsExpanded);
        Update();
    }

    private Button Tile(string glyph, string en, string ar, Action action, string? badge = null)
    {
        var btn = Ui.Btn("", action);
        StyleTile(btn, glyph, en, ar, badge);
        return btn;
    }

    private Button Tile(string glyph, string en, string ar, Func<Task> action, string? badge = null, Func<bool>? canInterrupt = null)
    {
        var btn = canInterrupt != null ? Ui.InterruptibleAsyncBtn("", action, canInterrupt) : Ui.AsyncBtn("", action);
        StyleTile(btn, glyph, en, ar, badge);
        return btn;
    }

    private void StyleTile(Button btn, string glyph, string en, string ar, string? badge)
    {
        var icon = Ui.Glyph(glyph, 16); icon.Foreground = ShellTheme.Ink("#67E8F9");
        var lbl = new TextBlock { Text = IsAr ? ar : en, FontSize = 11, Foreground = ShellTheme.Ink("#E6F2FF"), TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        var host = new Grid();
        host.Children.Add(Ui.Stack(icon, lbl));
        if (!string.IsNullOrEmpty(badge))
        {
            var pill = new Border { Background = ShellTheme.Surface("#2A4A7F"), BorderBrush = ShellTheme.Line("#4E7EC9"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(5, 1, 5, 1), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -4, -2, 0), Child = new TextBlock { Text = badge, FontSize = 8.5, Foreground = ShellTheme.Ink("#9AE6FF"), FontWeight = FontWeights.SemiBold } };
            host.Children.Add(pill);
        }
        btn.Content = host;
        btn.Tag = en;
        btn.Background = ShellTheme.Surface("#1A2D58"); btn.BorderBrush = ShellTheme.Line("#34548E");
        btn.Padding = new Thickness(6, 10, 6, 8); btn.Margin = new Thickness(3); btn.Height = 72;
    }

    private static Button DarkPillBtn(string text, Action action)
    {
        var b = Ui.Btn(text, action);
        b.SetResourceReference(StyleProperty, "PillButton");
        b.Background = ShellTheme.Surface("#1E3363"); b.Foreground = ShellTheme.Ink("#E8F4FF"); b.BorderBrush = ShellTheme.Line("#3E629C");
        b.Padding = new Thickness(12, 5, 12, 5); b.FontSize = 11.5; b.Margin = new Thickness(0, 0, 6, 4);
        return b;
    }

    private static Button DarkPillBtn(string text, Func<Task> action)
    {
        var b = Ui.AsyncBtn(text, action);
        b.SetResourceReference(StyleProperty, "PillButton");
        b.Background = ShellTheme.Surface("#1E3363"); b.Foreground = ShellTheme.Ink("#E8F4FF"); b.BorderBrush = ShellTheme.Line("#3E629C");
        b.Padding = new Thickness(12, 5, 12, 5); b.FontSize = 11.5; b.Margin = new Thickness(0, 0, 6, 4);
        return b;
    }

    private static Button CyanPillBtn(string text, Action action)
    {
        var b = Ui.Btn(text, action);
        b.SetResourceReference(StyleProperty, "PillButton");
        b.Background = ShellTheme.Surface("#38D6F5"); b.Foreground = ShellTheme.Ink("#071836"); b.FontWeight = FontWeights.SemiBold;
        b.Padding = new Thickness(12, 5, 12, 5); b.FontSize = 11.5; b.Margin = new Thickness(0, 0, 6, 4);
        return b;
    }

    private static Button CyanPillBtn(string text, Func<Task> action)
    {
        var b = Ui.AsyncBtn(text, action);
        b.SetResourceReference(StyleProperty, "PillButton");
        b.Background = ShellTheme.Surface("#38D6F5"); b.Foreground = ShellTheme.Ink("#071836"); b.FontWeight = FontWeights.SemiBold;
        b.Padding = new Thickness(12, 5, 12, 5); b.FontSize = 11.5; b.Margin = new Thickness(0, 0, 6, 4);
        return b;
    }

    public void SwitchView(string view)
    {
        bool changed = activeView != view;
        ClearBanner(); compactMode = ""; compactBanner.Visibility = Visibility.Collapsed; header.Visibility = Visibility.Visible;
        activeView = view;
        commandView.Visibility = view == "command" ? Visibility.Visible : Visibility.Collapsed;
        inlineAskView.Visibility = view == "ask" ? Visibility.Visible : Visibility.Collapsed;
        clipboardView.Visibility = view == "clipboard" ? Visibility.Visible : Visibility.Collapsed;
        dropView.Visibility = view == "drop" ? Visibility.Visible : Visibility.Collapsed;
        if (view != "command" && !IsExpanded)
        {
            collapseContent.Stop(); expandedHost.IsHitTestVisible = true;
            Motion.AnimateTo(expandedHost, UIElement.OpacityProperty, 1, 140);
            IsExpanded = true;
            openButton.Content = "⌃";
            expandedHost.Visibility = Visibility.Visible;
        }
        if (view == "clipboard") ReadClipboard();
        if (view == "ask")
        {
            inlineMode = c.Capture == null ? "General" : "Screen";
            inlineThumb.Source = c.Capture;
            inlineThumbWrap.Visibility = c.Capture == null ? Visibility.Collapsed : Visibility.Visible;
            if (!c.Busy) { inlineReplyBox.Visibility = Visibility.Collapsed; inlineReply.Clear(); inlineQuestionLabel.Text = ""; }
            if (Left > -10000) Activate();
            inlineInput.Focus();
            if (c.Busy) replyPoll.Start(); else replyPoll.Stop();
        }
        else if (view != "clipboard") replyPoll.Stop();
        if (IsExpanded)
        {
            AnimateSize(true);
            if (changed && !Motion.IsAnimating(expandedHost, UIElement.OpacityProperty)) Motion.Enter(view switch { "ask" => inlineAskView, "clipboard" => clipboardView, "drop" => dropView, _ => commandView }, 5);
        }
    }

    public async Task OpenAskWithPrompt(string prompt, bool autoSend = false, bool allowPlanning = true)
    {
        string requestedMode = c.Mode is "Web" or "Game" or "Screen" or "Crop" ? c.Mode : c.Capture == null ? "General" : "Screen";
        pinned = true; Expand(true); SwitchView("ask"); inlineMode = requestedMode;
        inlineInput.Text = prompt;
        if (autoSend) await SendInlineAsk(allowPlanning);
    }

    public void DropFiles(string[] files)
    {
        if (c.Busy || localConverting) throw new InvalidOperationException(Loc.T("Wait for the current request before attaching files.", "انتظر انتهاء الطلب الحالي قبل إرفاق ملفات."));
        pendingDroppedFiles = ChatComposer.ValidateDropFiles(files);
        if (pendingDroppedFiles.Length == 0) return;
        ShowDropPreview(pendingDroppedFiles);
        pinned = true; Expand(true);
    }

    private void ShowDropPreview(string[] files)
    {
        dropListText.Text = files.Length == 0 ? (IsAr ? "أفلت ملفات PDF أو Word أو فيديو أو صور" : "Drop PDF, Word, video, or image files") : string.Join("  ·  ", files.Select(Path.GetFileName));
        dropConvertRow.Children.Clear();
        if (files.Length == 1)
        {
            var first = files[0];
            var ext = Path.GetExtension(first).ToLowerInvariant();
            if (ext == ".pdf")
                dropConvertRow.Children.Add(CyanPillBtn("PDF → Word", async () => await DirectConvertAsync(first, "pdf_to_word")));
            else if (ext is ".mp4" or ".mov" or ".mkv" or ".avi" or ".webm")
                dropConvertRow.Children.Add(CyanPillBtn(IsAr ? "حجم أصغر · MP4" : "Compress · MP4", async () => await DirectConvertAsync(first, "compress_video")));
            else if (ext is ".docx" or ".pptx" or ".xlsx")
                dropConvertRow.Children.Add(CyanPillBtn(IsAr ? "تحويل إلى PDF" : "Convert → PDF", async () => await DirectConvertAsync(first, "to_pdf")));
            else if (ext is ".png" or ".jpg" or ".jpeg")
            {
                dropConvertRow.Children.Add(CyanPillBtn(IsAr ? "اشرح الصورة" : "Explain image", async () => await AskDroppedImage(first, "Explain")));
                dropConvertRow.Children.Add(DarkPillBtn(IsAr ? "ترجم النص" : "Translate text", async () => await AskDroppedImage(first, "Translate")));
            }
        }
        SwitchView("drop");
    }

    private void ReturnToCommands() { pinned = false; SwitchView("command"); }
    private bool TryPreviewFiles(DragEventArgs e, out string[] files)
    {
        files = [];
        if (c.Busy || localConverting || !e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return false;
        try { files = ChatComposer.ValidateDropFiles(paths); return files.Length > 0; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }
    private async Task AskDroppedImage(string file, string action)
    {
        if (c.Busy || localConverting) return;
        ChatComposer.ValidateDropFiles([file]);
        c.Capture = FileService.LoadImage(file); c.Mode = "Screen"; pendingDroppedFiles = [];
        await OpenAskWithPrompt(CaptureActions.Prompt(action, c.Store.Data.Settings.Language), true, allowPlanning: false);
    }

    private async Task DirectConvertAsync(string file, string kind)
    {
        if (c.Busy || localConverting) throw new InvalidOperationException(Loc.T("Stop the current task before converting another file.", "أوقف المهمة الحالية قبل تحويل ملف آخر."));
        pendingDroppedFiles = [];
        SwitchView("command");
        localConverting = true;
        string title = kind switch
        {
            "pdf_to_word" => IsAr ? "نيڤو تحوّل الملف إلى Word…" : "Converting PDF to Word…",
            "compress_video" => IsAr ? "نيڤو تضغط الفيديو…" : "Compressing video…",
            _ => IsAr ? "نيڤو تحوّل الملف إلى PDF…" : "Converting file to PDF…"
        };
        ShowWorkingBanner(title);
        try
        {
            string? outPath = await c.ConvertFileAsync(file, kind);
            if (outPath == null) { DismissCompactBanner(); return; }
            ShowCompleted(Path.GetFileName(outPath), outPath);
        }
        catch (Exception ex)
        {
            DismissCompactBanner();
            Ui.Error(ex);
        }
        finally { localConverting = false; }
    }

    private void HandleDroppedFiles(bool asAgent)
    {
        if (c.Busy || localConverting) return;
        if (pendingDroppedFiles.Length == 0) { SwitchView("command"); return; }
        var files = ChatComposer.ValidateDropFiles(pendingDroppedFiles);
        var imageFile = files.SingleOrDefault(f => Path.GetExtension(f).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg");
        var image = imageFile == null ? null : FileService.LoadImage(imageFile);
        pendingDroppedFiles = [];
        c.Attachments.Clear();
        c.Capture = image;
        string draft;
        foreach (var f in files)
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg")) c.Attachments.Add(f);
        }
        if (asAgent)
        {
            c.Mode = "Agent";
            var ext = Path.GetExtension(files[0]).ToLowerInvariant();
            draft = ext switch
            {
                ".pdf" => IsAr ? "حوّل هذا الملف إلى مستند Word" : "Convert this PDF to a Word document",
                ".mp4" or ".mov" or ".mkv" or ".avi" or ".webm" => IsAr ? "اضغط هذا الفيديو لحجم أصغر" : "Compress this video to a smaller size",
                ".docx" or ".pptx" or ".xlsx" => IsAr ? "حوّل هذا الملف إلى PDF" : "Convert this file to PDF",
                _ => IsAr ? "ساعدني في العمل على هذا الملف" : "Help me process this file"
            };
        }
        else { c.Mode = c.Capture != null ? "Screen" : "General"; draft = IsAr ? "اشرح لي هذا الملف ولخّص أهم ما فيه" : "Explain and summarize this file"; }
        c.Notify(); SwitchView("command");
        ((App)Application.Current).Hub.ShowAssistant(draft);
    }

    private async Task TriggerVoiceOrSettings()
    {
        if (c.Store.Data.Settings.UseAccountVoice) { await ((App)Application.Current).ToggleAccountTalkAsync(); return; }
        if (!c.Store.Data.Settings.VoiceEnabled)
        {
            ((App)Application.Current).Hub.Show();
            ((App)Application.Current).Hub.Navigate("Settings", "Voice");
            return;
        }
        if (!c.Voice.Recording)
        {
            c.Voice.StartRecording();
            c.SetStatus(IsAr ? "نستمع… اضغط «تحدّث» مجددًا للإرسال" : "Listening… tap Talk again to send");
        }
        else
        {
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            string transcript = await c.Voice.StopAndTranscribe(ct.Token);
            c.SetStatus("Ready when you are");
            if (!string.IsNullOrWhiteSpace(transcript)) await OpenAskWithPrompt(transcript, false);
        }
    }

    private async Task QuickCropAction(string action)
    {
        if (!await ((App)Application.Current).CaptureAsync(true, false, false, true) || c.Capture == null) return;
        await OpenAskWithPrompt(CaptureActions.Prompt(action, c.Store.Data.Settings.Language), true, allowPlanning: false);
    }

    private async Task QuickCropCopy()
    {
        if (!await ((App)Application.Current).CaptureAsync(true, false, false, true) || c.Capture == null) return;
        System.Windows.Clipboard.SetImage(c.Capture);
        c.Capture = null;
        c.SetStatus(IsAr ? "تم نسخ الجزء المحدد للحافظة" : "Region copied to clipboard");
        ShowCompleted(IsAr ? "تم نسخ الجزء المحدد للحافظة" : "Region copied to clipboard");
    }

    private async Task SendInlineAsk(bool allowPlanning = true)
    {
        if (c.Busy) { ((App)Application.Current).StopAll(); return; }
        if (string.IsNullOrWhiteSpace(inlineInput.Text)) return;
        string q = inlineInput.Text.Trim(); int before = c.Current.Messages.Count;
        c.Mode = inlineMode; inlineInput.Clear(); inlineQuestionLabel.Text = q;
        try
        {
            var request = c.SendAsync(q, allowPlanning: allowPlanning);
            if (c.Busy) { inlineReplyBox.Visibility = Visibility.Visible; inlineReply.Text = IsAr ? "جاري التفكير…" : "Thinking…"; replyPoll.Start(); AnimateSize(true); }
            await request;
            if (c.Current.Messages.Count == before) { inlineInput.Text = q; inlineReply.Text = ""; inlineReplyBox.Visibility = Visibility.Collapsed; replyPoll.Stop(); AnimateSize(true); }
            else SyncInlineReply();
        }
        catch (Exception ex) { inlineInput.Text = q; inlineReply.Text = ex.Message; inlineReplyBox.Visibility = Visibility.Visible; replyPoll.Stop(); AnimateSize(true); }
    }

    private void SyncInlineReply()
    {
        var last = c.Current.Messages.LastOrDefault(m => m.Role == "assistant")?.Text;
        if (!string.IsNullOrEmpty(last))
        {
            if (activeView == "ask")
            {
                bool revealed = inlineReplyBox.Visibility != Visibility.Visible;
                bool changed = !string.Equals(inlineReply.Text, last, StringComparison.Ordinal);
                if (revealed) inlineReplyBox.Visibility = Visibility.Visible;
                if (changed)
                {
                    inlineReply.Text = last;
                    var dir = ChatComposer.IsArabic(last) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
                    if (inlineReply.FlowDirection != dir) inlineReply.FlowDirection = dir;
                }
                if (revealed && IsExpanded) AnimateSize(true);
            }
        }
        if (activeView == "clipboard" && clipboardReply is { Text.Length: > 0 } reply)
        {
            clipTransformed = true;
            if (!string.Equals(clipPreview.Text, reply.Text, StringComparison.Ordinal))
            {
                clipPreview.Text = reply.Text;
                var dir = ChatComposer.IsArabic(reply.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
                if (clipPreview.FlowDirection != dir) clipPreview.FlowDirection = dir;
            }
        }
        if (!c.Busy) replyPoll.Stop();
    }

    private void ReadClipboard()
    {
        clipTransformed = false;
        clipboardReply = null;
        if (activeView == "clipboard") replyPoll.Stop();
        (clipText, clipBitmap) = readClipboard();
        if (clipText?.Length > 60000) clipText = clipText[..60000];
        clipBadge.Text = clipText != null && clipBitmap != null ? (IsAr ? "📋 نص + صورة" : "📋 Text + image") : clipBitmap != null ? (IsAr ? "🖼 صورة" : "🖼 Image") : clipText != null ? (IsAr ? "📝 نص" : "📝 Text") : (IsAr ? "فارغة" : "Empty");
        clipThumb.Source = clipBitmap;
        clipThumbWrap.Visibility = clipBitmap != null ? Visibility.Visible : Visibility.Collapsed;
        clipPreview.Text = clipText ?? (clipBitmap != null ? (IsAr ? $"صورة من الحافظة ({clipBitmap.PixelWidth} × {clipBitmap.PixelHeight})" : $"Clipboard image ({clipBitmap.PixelWidth} × {clipBitmap.PixelHeight})") : (IsAr ? "الحافظة فارغة. انسخ نصًا أو صورة ثم اضغط تحديث." : "Clipboard is empty. Copy text or an image, then refresh."));
        clipPreview.FlowDirection = ChatComposer.IsArabic(clipPreview.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        HighlightClipAction("");
    }

    private void HighlightClipAction(string activeKey)
    {
        foreach (var pair in clipActionButtons)
        {
            bool active = pair.Key == activeKey;
            pair.Value.Background = ShellTheme.Surface(active ? "#38D6F5" : "#1E3363");
            pair.Value.Foreground = ShellTheme.Ink(active ? "#071836" : "#E8F4FF");
            pair.Value.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private async Task RunClipboardAction(string command)
    {
        if (c.Busy) return;
        if (clipText == null && clipBitmap == null) ReadClipboard();
        if (clipText == null && clipBitmap == null) return;
        HighlightClipAction(command);
        string promptCmd = command == "Translate" ? (IsAr ? "Translate to Arabic" : "Translate to English") : command;
        var previousCapture = c.Capture; string previousMode = c.Mode;
        var previousAttachments = c.Attachments.ToArray(); var previousConversation = c.Current; bool wasIncognito = c.Store.Incognito;
        c.Attachments.Clear();
        c.Capture = clipBitmap; c.Mode = clipBitmap != null ? "Screen" : "General"; clipTransformed = false;
        clipPreview.Text = IsAr ? "جاري المعالجة…" : "Working…";
        int before = c.Current.Messages.Count;
        try
        {
            var request = c.SendAsync(promptCmd + " the supplied user-provided content.", allowPlanning: false, referenceData: clipText);
            var response = c.Current.Messages.Skip(before).LastOrDefault(m => m.Role == "assistant");
            clipboardReply = response;
            if (c.Busy) replyPoll.Start();
            await request;
            if (!ReferenceEquals(c.Current, previousConversation) || c.Store.Incognito != wasIncognito) { clipboardReply = null; replyPoll.Stop(); return; }
            if (c.Current.Messages.Count == before) { c.Capture = previousCapture; c.Mode = previousMode; ReadClipboard(); replyPoll.Stop(); }
            else if (ReferenceEquals(clipboardReply, response)) SyncInlineReply();
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(c.Current, previousConversation) && c.Store.Incognito == wasIncognito && c.Current.Messages.Count == before) { c.Capture = previousCapture; c.Mode = previousMode; }
            clipboardReply = null; clipPreview.Text = ex.Message; replyPoll.Stop();
        }
        finally
        {
            if (ReferenceEquals(c.Current, previousConversation) && c.Store.Incognito == wasIncognito)
            {
                if (c.Attachments.Count == 0) c.Attachments.AddRange(previousAttachments);
                if (c.Capture == null) c.Capture = previousCapture;
                if (c.Mode == (clipBitmap != null ? "Screen" : "General")) c.Mode = previousMode;
            }
            c.Notify();
        }
    }

    private void UseClipboardInTask()
    {
        if (c.Busy) return;
        if (clipText == null && clipBitmap == null) ReadClipboard();
        if (clipText == null && clipBitmap == null) return;
        c.Capture = clipBitmap; c.Mode = "Agent"; SwitchView("command");
        ((App)Application.Current).Hub.ShowAssistant((IsAr ? "استخدم هذا المحتوى في مهمة:\n\n" : "Use this clipboard content in a task:\n\n") + clipText);
    }

    private void CopyClipboardPreview()
    {
        if (c.Busy) return;
        if (clipTransformed && !string.IsNullOrWhiteSpace(clipPreview.Text)) writeClipboard(clipPreview.Text, null);
        else if (clipBitmap != null) writeClipboard(null, clipBitmap);
        else if (!string.IsNullOrEmpty(clipText)) writeClipboard(clipText, null);
    }

    private static void AppendTransform(UIElement element, Transform transform)
    {
        if (element.RenderTransform == Transform.Identity) element.RenderTransform = transform;
        else if (element.RenderTransform is TransformGroup group && !group.IsFrozen) group.Children.Add(transform);
        else element.RenderTransform = new TransformGroup { Children = { element.RenderTransform, transform } };
    }

    private void OnStoreChanged()
    {
        recentDirty = true;
        if (IsVisible && IsExpanded && activeView == "command" && !closed)
            RefreshRecent(forceCheck: false);
    }

    private void OnMotionPolicyChanged()
    {
        if (!Motion.Enabled) StopMorph(snapToFinal: true);
        Update();
    }

    private void CancelIslandClocks()
    {
        Motion.Cancel(frame);
        Motion.Cancel(expandedHost);
        Motion.Cancel(bottomGlow);
        Motion.Cancel(compactBanner);
        Motion.Cancel(shellSize);
        Motion.Cancel(glowWidth);
        Motion.Cancel(glowOffset);
        Motion.Cancel(header);
        lastGlowTarget = double.NaN;
    }

    private void RefreshRecent(bool forceCheck = false)
    {
        if (closed) return;
        string outputDir = c.Store.Output;
        bool exists = Directory.Exists(outputDir);
        DateTime dirWriteUtc = exists ? Directory.GetLastWriteTimeUtc(outputDir) : DateTime.MinValue;
        if (!forceCheck && !recentDirty && recentInitialized && exists == lastRecentDirExists && dirWriteUtc == lastRecentDirWriteUtc)
            return;

        recentDirty = false;
        recentInitialized = true;
        lastRecentDirExists = exists;
        lastRecentDirWriteUtc = dirWriteUtc;

        (string Path, long Ticks)[] entries = exists
            ? FileService.CompletedOutputs(c.Store)
                .Select(path => (Path: path, Ticks: File.GetLastWriteTimeUtc(path).Ticks))
                .OrderByDescending(e => e.Ticks)
                .Take(3)
                .ToArray()
            : [];

        if (recentPopulated && entries.Length == lastRecentEntries.Length)
        {
            bool identical = true;
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Ticks != lastRecentEntries[i].Ticks
                    || !string.Equals(entries[i].Path, lastRecentEntries[i].Path, StringComparison.OrdinalIgnoreCase))
                {
                    identical = false;
                    break;
                }
            }
            if (identical) return;
        }

        lastRecentEntries = entries;
        recentPopulated = true;
        recentList.Children.Clear();
        if (!exists) return;
        if (entries.Length == 0)
        {
            recentList.Children.Add(new TextBlock { Text = IsAr ? "الملفات والعروض التي تنشئها نيڤو ستظهر هنا للفتح بكبسة." : "Files and decks NIVO creates appear here for one-click opening.", FontSize = 11, Foreground = ShellTheme.Ink("#7B9CC4"), Margin = new Thickness(4, 0, 4, 4) });
            return;
        }
        foreach (var entry in entries)
        {
            var path = entry.Path;
            var rowGrid = new Grid(); rowGrid.ColumnDefinitions.Add(new ColumnDefinition()); rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock { Text = "📄  " + OutputStampRegex.Replace(Path.GetFileName(path), ""), FontSize = 12, Foreground = ShellTheme.Ink("#E8F4FF"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var open = CyanPillBtn(IsAr ? "فتح" : "Open", () => DesktopService.OpenTarget(path)); open.Margin = new Thickness(8, 0, 0, 0); open.Padding = new Thickness(14, 3, 14, 3);
            rowGrid.Children.Add(name); Grid.SetColumn(open, 1); rowGrid.Children.Add(open);
            var rowCard = new Border { Background = ShellTheme.Surface("#172A54"), BorderBrush = ShellTheme.Line("#2F4E85"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(11), Padding = new Thickness(10, 6, 8, 6), Margin = new Thickness(0, 0, 0, 5), Child = rowGrid };
            var menu = new ContextMenu();
            var reveal = new MenuItem { Header = IsAr ? "عرض في المجلد" : "Show in Explorer" }; reveal.Click += (_, _) => DesktopService.RevealInExplorer(path);
            menu.Items.Add(reveal); rowCard.ContextMenu = menu;
            recentList.Children.Add(rowCard);
        }
    }

    private double TargetHeight() => activeView switch
    {
        "ask" => AskHeight(),
        "clipboard" => 294,
        "drop" => ContentHeight(dropView, 196),
        _ => Math.Min(476, 318 + Math.Max(1, recentList.Children.Count) * 44)
    };

    private double AskHeight() => ContentHeight(inlineAskView, 184);
    private double ContentHeight(FrameworkElement content, double minimum)
    {
        // Child text invalidates its own measure before the parent's cached desired size changes.
        // SizeChanged above refits after WPF has completed that natural layout pass.
        double height = content.DesiredSize.Height + header.Height + contentPadding.Padding.Top + contentPadding.Padding.Bottom + shellSurface.BorderThickness.Top + shellSurface.BorderThickness.Bottom + expandedHost.Margin.Top + expandedHost.Margin.Bottom;
        return Math.Clamp(Math.Ceiling(height), minimum, 476);
    }

    private void ApplyMorphAt(double visibleWidth, double visibleHeight)
    {
        double targetWidth = Math.Max(1, frame.Width);
        double targetHeight = Math.Max(1, frame.Height);
        shellSize.ScaleX = visibleWidth / targetWidth;
        shellSize.ScaleY = visibleHeight / targetHeight;
        double targetGlowWidth = Math.Max(1, targetWidth - 52);
        glowWidth.ScaleX = Math.Max(1, visibleWidth - 52) / targetGlowWidth;
        glowOffset.Y = visibleHeight - targetHeight;
        double headerDelta = Math.Max(0, (visibleWidth - (headerBaseWidth + 28)) * 0.5);
        headerStartOffset.X = -headerDelta;
        headerEndOffset.X = headerDelta;
    }

    private void SettleMorph()
    {
        morphActive = false;
        shellSize.ScaleX = 1;
        shellSize.ScaleY = 1;
        glowWidth.ScaleX = 1;
        glowOffset.Y = 0;
        headerStartOffset.X = 0;
        headerEndOffset.X = 0;
        double finalHeaderW = Math.Max(0, frame.Width - 28);
        headerBaseWidth = finalHeaderW;
        if (Math.Abs(header.Width - finalHeaderW) > 0.5) header.Width = finalHeaderW;
    }

    private void StopMorph(bool snapToFinal)
    {
        if (morphActive)
        {
            morphActive = false;
            Motion.UnregisterRenderParticipant(this);
        }
        if (snapToFinal) SettleMorph();
    }

    bool Motion.IRenderParticipant.Step(double dtSeconds)
    {
        if (!morphActive || closed || !IsVisible || !Motion.Enabled)
        {
            SettleMorph();
            return false;
        }
        const double morphDuration = 0.220;
        morphElapsed += dtSeconds;
        if (morphElapsed >= morphDuration)
        {
            SettleMorph();
            return false;
        }
        double u = morphElapsed / morphDuration;
        double inv = 1.0 - u;
        double eased = 1.0 - inv * inv * inv;
        double curW = morphFromW + (morphToW - morphFromW) * eased;
        double curH = morphFromH + (morphToH - morphFromH) * eased;
        ApplyMorphAt(curW, curH);
        return true;
    }

    void Motion.IRenderParticipant.OnMotionDisabled()
    {
        SettleMorph();
    }

    private void SetFrameSize(double width, double height)
    {
        if (Math.Abs(frame.Width - width) < .5 && Math.Abs(frame.Height - height) < .5) return;
        double visibleWidth = frame.Width * shellSize.ScaleX;
        double visibleHeight = frame.Height * shellSize.ScaleY;
        frame.Width = width; frame.Height = height;
        contentClip.Rect = new Rect(0, 0, width, height);
        shellSize.CenterX = width / 2;
        glowWidth.CenterX = Math.Max(1, width - 52) / 2;

        if (!Motion.Enabled || !IsVisible || closed)
        {
            StopMorph(snapToFinal: true);
            return;
        }

        // Hold header.Width constant during the morph and slide its edge controls with
        // RenderTransforms so the entire size morph causes zero per-frame layout passes.
        bool widthChanging = Math.Abs(visibleWidth - width) >= 0.5;
        double desiredBaseHeader = widthChanging
            ? Math.Max(0, Math.Min(visibleWidth, width) - 28)
            : Math.Max(0, width - 28);
        if (morphActive && widthChanging && headerBaseWidth <= desiredBaseHeader + 0.5)
            desiredBaseHeader = headerBaseWidth;
        headerBaseWidth = desiredBaseHeader;
        if (Math.Abs(header.Width - headerBaseWidth) > 0.5) header.Width = headerBaseWidth;

        morphFromW = visibleWidth;
        morphFromH = visibleHeight;
        morphToW = width;
        morphToH = height;
        morphElapsed = 0;
        ApplyMorphAt(visibleWidth, visibleHeight);

        if (!morphActive)
        {
            morphActive = true;
            Motion.RegisterRenderParticipant(this);
        }
    }

    private void AnimateSize(bool show)
    {
        if (compactMode != "") return;
        SetFrameSize(show ? 528 : 236, show ? TargetHeight() : 50);
    }

    private void Expand(bool show)
    {
        if (IsExpanded == show && compactMode == "") return;
        ClearBanner(); compactMode = ""; compactBanner.Visibility = Visibility.Collapsed; header.Visibility = Visibility.Visible;
        IsExpanded = show;
        openButton.Content = show ? "⌃" : "⌄";
        collapseContent.Stop();
        bool resuming = Motion.IsAnimating(expandedHost, UIElement.OpacityProperty) || expandedHost.Opacity < .99;
        expandedHost.IsHitTestVisible = show;
        if (show) { expandedHost.Visibility = Visibility.Visible; if (resuming) Motion.AnimateTo(expandedHost, UIElement.OpacityProperty, 1, 140); }
        else if (Motion.Enabled && expandedHost.IsVisible) { Motion.AnimateTo(expandedHost, UIElement.OpacityProperty, 0, 100); collapseContent.Start(); }
        else { expandedHost.Visibility = Visibility.Collapsed; Motion.AnimateTo(expandedHost, UIElement.OpacityProperty, 1, 0); }
        if (show) RefreshRecent(forceCheck: false);
        AnimateSize(show);
        if (show && !resuming) Motion.Enter(expandedHost, 6);
    }

    private void SetGlow(double opacity)
    {
        if (double.IsFinite(lastGlowTarget) && Math.Abs(lastGlowTarget - opacity) < .001 && (!IsVisible || Math.Abs(bottomGlow.Opacity - opacity) < .001 || Motion.IsAnimating(bottomGlow, UIElement.OpacityProperty)))
            return;
        lastGlowTarget = opacity;
        Motion.AnimateTo(bottomGlow, UIElement.OpacityProperty, opacity, IsVisible ? 160 : 0);
    }

    private void Update()
    {
        if (closed) return;
        if (!Motion.Enabled && !IsExpanded && collapseContent.IsEnabled)
        {
            collapseContent.Stop(); expandedHost.Visibility = Visibility.Collapsed;
            Motion.AnimateTo(expandedHost, UIElement.OpacityProperty, 1, 0);
        }
        if (!ReferenceEquals(inlineThumb.Source, c.Capture)) inlineThumb.Source = c.Capture;
        var nextThumbVis = c.Capture == null ? Visibility.Collapsed : Visibility.Visible;
        if (inlineThumbWrap.Visibility != nextThumbVis) inlineThumbWrap.Visibility = nextThumbVis;
        if (c.Capture == null && inlineMode is "Screen" or "Crop") inlineMode = "General";

        string nextTitle = IsAr ? "نيڤو" : "NIVO";
        if (!string.Equals(titleText.Text, nextTitle, StringComparison.Ordinal)) titleText.Text = nextTitle;

        bool speaking = c.Status.StartsWith("Speaking", StringComparison.Ordinal);
        bool accountTalk = ((App)Application.Current).AccountTalkActive;
        string nextStatus = c.Store.Incognito ? (IsAr ? "وضع خفي · بدون حفظ" : "Incognito · screen paused") : accountTalk ? c.Status : c.Voice.Recording ? (IsAr ? "نستمع…" : "Listening…") : speaking ? (IsAr ? "نتحدث…" : "Speaking…") : c.Busy ? (IsAr ? "نعمل معك…" : "Working with you…") : c.IsQuiet ? (IsAr ? "وضع هادئ" : "Quiet mode") : (IsAr ? "جاهزة على جهازك" : "Ready when you are");
        if (!string.Equals(status.Text, nextStatus, StringComparison.Ordinal)) status.Text = nextStatus;

        string nextBannerText = c.Busy ? c.Status : c.Store.Incognito ? (IsAr ? "الوضع الخفي مفعّل · لا يتم حفظ المحادثات" : "Incognito active · history paused") : (IsAr ? "نيڤو جاهزة على هذا الجهاز · اسحب أي ملف لهنا" : "NIVO ready on this PC · drop any file here");
        if (!string.Equals(statusBannerText.Text, nextBannerText, StringComparison.Ordinal)) statusBannerText.Text = nextBannerText;

        string nextActionContent = c.Busy ? (IsAr ? "إيقاف" : "Stop") : c.Store.Incognito ? (IsAr ? "إيقاف الخفي" : "Incognito ON") : (IsAr ? "وضع خفي" : "Incognito");
        if (!Equals(statusActionBtn.Content, nextActionContent)) statusActionBtn.Content = nextActionContent;

        string nextSendContent = c.Busy ? "■" : "↑";
        if (!Equals(inlineSendBtn.Content, nextSendContent)) inlineSendBtn.Content = nextSendContent;

        var stateColor = c.Store.Incognito ? DotIncognito : c.IsQuiet ? DotQuiet : c.Voice.Recording || accountTalk ? DotRecording : c.Busy ? DotBusy : speaking ? DotSpeaking : DotReady;
        if (!ReferenceEquals(dot.Fill, stateColor)) dot.Fill = stateColor;
        if (!ReferenceEquals(bannerDot.Fill, stateColor)) bannerDot.Fill = stateColor;

        SetGlow(compactMode is "working" or "done" or "suggest" || c.Busy || c.Voice.Recording || speaking || accountTalk ? 1 : .55);
        activity.SetActive(c.Busy || c.Voice.Recording || speaking || accountTalk);
        bannerActivity?.SetActive(compactMode == "working" && (c.Busy || localConverting));
        if (wasBusy != c.Busy)
        {
            wasBusy = c.Busy;
            if (c.Busy && !c.BackgroundBusy && !IsExpanded && !localConverting && Left > -10000)
                ShowWorkingBanner(c.TaskActive && c.TaskDetail.Length > 0 ? c.TaskDetail : c.Status);
            else if (!c.Busy && compactMode == "working" && !localConverting)
                DismissCompactBanner();
        }
        if (compactMode == "working" && bannerLabel != null && !localConverting)
        {
            string nextWorkingLabel = c.TaskActive && c.TaskDetail.Length > 0 ? c.TaskDetail : c.Status;
            if (!string.Equals(bannerLabel.Text, nextWorkingLabel, StringComparison.Ordinal)) bannerLabel.Text = nextWorkingLabel;
        }
    }
}

public sealed class ActivityGlyph : StackPanel
{
    private bool active, animated;
    internal bool IsAnimating => animated;
    public ActivityGlyph()
    {
        Orientation = Orientation.Horizontal; Width = 24; Height = 24; VerticalAlignment = VerticalAlignment.Center;
        for (int i = 0; i < 4; i++) { var bar = new Border { Width = 3, Height = 5 + i % 2 * 5, CornerRadius = new CornerRadius(2), Margin = new Thickness(1), VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(.5, .5), RenderTransform = new ScaleTransform(), Background = ShellTheme.Surface("#38D6F5") }; Children.Add(bar); }
        Visibility = Visibility.Collapsed;
        IsVisibleChanged += (_, _) => SyncAnimation();
        Loaded += (_, _) => { Motion.PolicyChanged += SyncAnimation; SyncAnimation(); };
        Unloaded += (_, _) => { Motion.PolicyChanged -= SyncAnimation; StopAnimation(); };
    }
    public void SetActive(bool value)
    {
        active = value; Visibility = value ? Visibility.Visible : Visibility.Collapsed; SyncAnimation();
    }
    private void StopAnimation()
    {
        animated = false;
        foreach (Border bar in Children) { var transform = (ScaleTransform)bar.RenderTransform; transform.BeginAnimation(ScaleTransform.ScaleYProperty, null); transform.ScaleY = 1; }
    }
    private void SyncAnimation()
    {
        bool shouldAnimate = active && IsVisible && Motion.Enabled;
        if (animated == shouldAnimate) return;
        StopAnimation();
        if (!shouldAnimate) return;
        animated = true;
        for (int i = 0; i < Children.Count; i++)
        {
            var transform = (ScaleTransform)((Border)Children[i]).RenderTransform;
            var animation = new DoubleAnimation(.5, 1.8, TimeSpan.FromMilliseconds(380 + i * 70)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromMilliseconds(i * 90), EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            Timeline.SetDesiredFrameRate(animation, 30);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }
    }
}
