using Nivo.Core;
using Nivo.Services;
using System.Windows.Input;
using System.Windows.Threading;
namespace Nivo.UI;

public sealed class ChatComposer : Border, IDisposable
{
    public static readonly string[] Modes = ["General", "Screen", "Agent", "Crop", "Web", "Game"];
    private static readonly Dictionary<string, string> ModeArabic = new() { ["General"] = "عام", ["Screen"] = "الشاشة", ["Agent"] = "الوكيل", ["Crop"] = "قصّ", ["Web"] = "الويب", ["Game"] = "ألعاب" };
    private static readonly Dictionary<string, string> CropArabic = new() { ["Explain"] = "اشرح", ["Translate"] = "ترجم", ["Summarize"] = "لخّص", ["Fact-check"] = "تحقق", ["Abbreviation"] = "اختصار", ["Table"] = "جدول", ["Note"] = "ملاحظة", ["Task"] = "مهمة", ["Copy as image"] = "نسخ كصورة" };
    private readonly Controller c;
    private readonly TextBox input;
    private readonly TextBlock hint = Ui.Text("", 11, "#9CB3C3");
    private readonly TextBlock placeholder = Ui.Text(Loc.T("Ask NIVO anything…", "اسأل نيڤو أي شيء…"), 15, "#9CB3C3");
    private readonly Image thumbnail = new() { MaxWidth = 136, MaxHeight = 76, Stretch = Stretch.Uniform };
    private readonly Border thumbnailFrame;
    private readonly TextBlock thumbnailMeta = Ui.Text("", 11, "#9CB3C3");
    private readonly WrapPanel quick = new() { Margin = new Thickness(0, 6, 0, 4) };
    private readonly Dictionary<string, Button> modes = [];
    private readonly Button send;
    private readonly Button voice;
    private bool voiceShowsStop;
    private (string Mode, bool Busy)? modeAppearance;
    private readonly StackPanel tools;
    public event Action? Submitted;

    public ChatComposer(Controller controller, bool compact = false)
    {
        c = controller; FlowDirection = Loc.Flow;
        CornerRadius = new CornerRadius(20); BorderThickness = new Thickness(1); Padding = new Thickness(16, 12, 16, 12);
        SetResourceReference(BackgroundProperty, "Glass"); SetResourceReference(BorderBrushProperty, "Hairline");
        var content = new StackPanel(); var tabs = new WrapPanel();
        foreach (var mode in Modes)
        {
            var m = mode;
            var b = Ui.Btn(Loc.IsArabic ? ModeArabic[m] : m, () => { c.Mode = m; c.Notify(); input?.Focus(); });
            b.Tag = m; b.FontSize = 12; b.Padding = new Thickness(12, 6, 12, 6); b.Margin = new Thickness(0, 0, 4, 6);
            b.SetResourceReference(StyleProperty, "PillButton"); tabs.Children.Add(b); modes[m] = b;
        }
        content.Children.Add(tabs);
        var thumbRow = Ui.Row(new Border { CornerRadius = new CornerRadius(6), Background = Ui.Brush("#140A1F6E"), ClipToBounds = true, Child = thumbnail, Margin = new Thickness(0, 0, 10, 0) }, thumbnailMeta);
        thumbnailMeta.VerticalAlignment = VerticalAlignment.Center;
        thumbnailFrame = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(6), Margin = new Thickness(0, 4, 0, 6), HorizontalAlignment = HorizontalAlignment.Left, Child = thumbRow, Visibility = Visibility.Collapsed };
        thumbnailFrame.SetResourceReference(Border.BackgroundProperty, "Panel"); thumbnailFrame.SetResourceReference(Border.BorderBrushProperty, "Hairline");
        content.Children.Add(thumbnailFrame);
        var entry = new Grid();
        input = new TextBox { Text = c.Draft, AcceptsReturn = true, MinHeight = compact ? 48 : 64, MaxHeight = 120, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Margin = new Thickness(0), Padding = new Thickness(4, 10, 4, 10), FontSize = 15 };
        placeholder.Margin = new Thickness(6, 10, 6, 0); placeholder.IsHitTestVisible = false; entry.Children.Add(input); entry.Children.Add(placeholder); content.Children.Add(entry);
        input.TextChanged += (_, _) => { c.Draft = input.Text; placeholder.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; input.FlowDirection = input.Text.Length == 0 ? Loc.Flow : (IsArabic(input.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight); };
        input.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Tab) { e.Handled = true; CycleMode(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1); }
            if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { e.Handled = true; await Send(); }
        };
        foreach (string action in CaptureActions.Names)
        {
            var act = action;
            var b = Ui.AsyncBtn(Loc.IsArabic ? CropArabic[act] : act, async () => {
                if (c.Capture == null || c.Busy) return;
                if (act == "Copy as image") { System.Windows.Clipboard.SetImage(c.Capture); c.SetStatus(Loc.T("Image copied locally · no AI request", "تم نسخ الصورة محليًا دون إرسال للذكاء الاصطناعي")); return; }
                if (act is "Note" or "Task") { var title = Ui.Prompt(Loc.T("Save image with a " + act.ToLowerInvariant(), "حفظ الصورة مع " + CropArabic[act]), Loc.T("Title · saved locally without AI/OCR", "العنوان · تُحفظ محليًا بدون ذكاء اصطناعي"), c.Draft); if (title != null) CaptureActions.SaveImage(c, act, title); return; }
                if (act == "Fact-check") { c.Mode = "Web"; c.Notify(); }
                SetDraft(CaptureActions.Prompt(act, c.Store.Data.Settings.Language)); await Send(allowPlanning: false);
            });
            b.Tag = act; b.Padding = new Thickness(10, 5, 10, 5); b.FontSize = 11; quick.Children.Add(b);
        }
        quick.Visibility = c.Capture == null ? Visibility.Collapsed : Visibility.Visible;
        content.Children.Add(quick);
        var footer = new Grid { Margin = new Thickness(0, 4, 0, 0) }; footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tools = Ui.Row();
        tools.Children.Add(Icon("\uE723", Loc.T("Attach files · or drop them here", "إرفاق ملفات · أو اسحبها هنا"), () => { var files = Ui.PickFiles(); if (files.Length > 0) AddFiles(files); }));
        var capture = Ui.AsyncBtn("", async () => await ((App)Application.Current).CaptureAsync(c.Mode == "Crop")); capture.Content = Ui.Glyph("\uE7F4", 17); capture.ToolTip = Loc.T("Capture screen / crop", "التقاط الشاشة / قصّ"); Ui.Flat(capture); capture.Padding = new Thickness(10, 8, 10, 8); tools.Children.Add(capture);
        var hoverCap = Ui.AsyncBtn("", async () => await ((App)Application.Current).CaptureAsync(false, true)); hoverCap.Content = Ui.Glyph("\uE81D", 17); hoverCap.ToolTip = Loc.T("Hover to ask · capture area around cursor", "أشّر واسأل · التقاط المنطقة حول المؤشر"); Ui.Flat(hoverCap); hoverCap.Padding = new Thickness(10, 8, 10, 8); tools.Children.Add(hoverCap);
        voice = Ui.InterruptibleAsyncBtn("", RecordVoice, () => c.Store.Data.Settings.UseAccountVoice); voice.Content = Ui.Glyph("\uE720", 17); voice.ToolTip = Loc.T("Talk · configured account voice or speech API", "Talk · صوت الحساب أو API الصوت المختار"); Ui.Flat(voice); voice.Padding = new Thickness(10, 8, 10, 8); tools.Children.Add(voice);
        tools.Children.Add(Icon("\uE894", Loc.T("Clear attachments", "مسح المرفقات"), () => { c.Capture = null; c.Attachments.Clear(); c.Notify(); })); footer.Children.Add(tools);
        send = Ui.Btn(Loc.T("Ask  ↗", "اسأل  ↗"), () => { }, true); send.Tag = "Submit"; send.Click += async (_, _) => { try { await Send(); } catch (Exception ex) { Ui.Error(ex); } }; send.Margin = new Thickness(0); send.MinWidth = 94; Grid.SetColumn(send, 1); footer.Children.Add(send); content.Children.Add(footer);
        hint.Margin = new Thickness(4, 5, 4, 0); content.Children.Add(hint); Child = content;
        AllowDrop = true; PreviewDragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) => { try { if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files) AddFiles(files); } catch (Exception ex) { Ui.Error(ex); } e.Handled = true; };
        c.Changed += Refresh; Loaded += (_, _) => { Refresh(); input.Focus(); }; Refresh();
    }
    private static Button Icon(string glyph, string tooltip, Action action) { var b = Ui.Btn("", action); b.Content = Ui.Glyph(glyph, 17); b.ToolTip = tooltip; b.Padding = new Thickness(10, 8, 10, 8); b.Margin = new Thickness(0, 0, 4, 0); Ui.Flat(b); return b; }
    public void AddFiles(IEnumerable<string> paths)
    {
        if (c.Busy) throw new InvalidOperationException(Loc.T("Wait for the current request before attaching files.", "انتظر انتهاء الطلب الحالي قبل إرفاق ملفات."));
        var files = ValidateDropFiles(paths);
        var pictures = files.Where(IsPicture).ToArray();
        var documents = files.Except(pictures).ToArray();
        if (c.Attachments.Concat(documents).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 4) throw new InvalidOperationException(Loc.T("Attach up to four files at a time.", "الحد الأقصى 4 ملفات في المرة."));
        var image = pictures.Length == 1 ? FileService.LoadImage(pictures[0]) : null;
        foreach (var f in documents) if (!c.Attachments.Contains(f, StringComparer.OrdinalIgnoreCase)) c.Attachments.Add(f);
        if (image != null) c.Capture = image;
        c.Notify();
    }
    private static bool IsPicture(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg";
    internal static string[] ValidateDropFiles(IEnumerable<string> paths)
    {
        var files = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Any(f => !File.Exists(f))) throw new InvalidOperationException(Loc.T("Drop files, rather than folders.", "أفلت ملفات وليس مجلدات."));
        string[] supported = [".txt", ".md", ".csv", ".json", ".cs", ".js", ".py", ".html", ".log", ".pdf", ".docx", ".pptx", ".xlsx", ".mp4", ".mov", ".mkv", ".avi", ".webm", ".png", ".jpg", ".jpeg"];
        if (files.Any(f => !supported.Contains(Path.GetExtension(f).ToLowerInvariant()) && !ReviewedEditService.IsAllowedExtension(f))) throw new InvalidOperationException(Loc.T("Choose a text/document, video, PNG or JPEG file.", "اختر ملف نص أو مستند أو فيديو أو صورة PNG/JPEG."));
        var pictures = files.Where(IsPicture).ToArray();
        if (pictures.Length > 1) throw new InvalidOperationException(Loc.T("Attach one image at a time, alongside up to four documents or videos.", "أرفق صورة واحدة في كل مرة مع ما يصل إلى 4 مستندات أو فيديوهات."));
        var documents = files.Except(pictures).ToArray();
        if (documents.Length > 4) throw new InvalidOperationException(Loc.T("Attach up to four files at a time.", "الحد الأقصى 4 ملفات في المرة."));
        return files;
    }
    public void CycleMode(int direction = 1) { c.Mode = Modes[(Math.Max(0, Array.IndexOf(Modes, c.Mode)) + direction + Modes.Length) % Modes.Length]; c.Notify(); }
    public void SetDraft(string text) { input.Text = text; input.Focus(); input.CaretIndex = text.Length; }
    public void FocusInput() { if (input.Text != c.Draft) input.Text = c.Draft; input.Focus(); }
    private static string ModeTip(string mode) => mode switch
    {
        "Screen" => Loc.T("Screen · ask about your display  ·  enter ask  ·  tab mode", "الشاشة · اسأل عن أي شيء ظاهر على شاشتك  ·  Enter للإرسال"),
        "Agent" => Loc.T("Agent · plans reviewed tasks (docs, slides, video, reminders)  ·  enter plan", "الوكيل · ينشئ خطة مهام (مستندات، عروض، فيديو، تذكيرات) بموافقتك"),
        "Crop" => Loc.T("Crop · box a region for 9 quick actions  ·  enter ask", "قصّ · حدد منطقة من الشاشة لتسع أدوات سريعة"),
        "Web" => Loc.T("Web · read-only web search with source citations  ·  enter ask", "الويب · بحث مباشر على الإنترنت مع ذكر المصادر"),
        "Game" => Loc.T("Game · wiki answers (Minecraft, Terraria, Elden Ring)  ·  enter ask", "ألعاب · إجابات من ويكي الألعاب أثناء اللعب"),
        _ => Loc.T("enter ask  ·  drop files  ·  tab switch mode  ·  esc close", "Enter للإرسال  ·  اسحب ملفات  ·  Tab لتبديل الوضع  ·  Esc إغلاق")
    };
    private void Refresh()
    {
        if (input.Text != c.Draft) input.Text = c.Draft;
        bool accountVoice = Application.Current is App app && app.AccountTalkActive;
        if (voiceShowsStop != accountVoice)
        {
            voiceShowsStop = accountVoice;
            voice.Content = Ui.Glyph(accountVoice ? "\uE71A" : "\uE720", 17);
            voice.ToolTip = accountVoice ? Loc.T("Stop Talk", "إيقاف الصوت") : Loc.T("Talk · configured account voice or speech API", "Talk · صوت الحساب أو API الصوت المختار");
            System.Windows.Automation.AutomationProperties.SetName(voice, accountVoice ? Loc.T("Stop Talk", "إيقاف الصوت") : "Talk");
        }

        if (modeAppearance != (c.Mode, c.Busy))
        {
            modeAppearance = (c.Mode, c.Busy);
            foreach (var (mode, button) in modes)
            {
                bool selected = mode == c.Mode;
                button.SetResourceReference(Control.BackgroundProperty, selected ? "ActiveGlass" : "Panel");
                button.SetResourceReference(Control.ForegroundProperty, selected ? "OnAccent" : "Ink");
                button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
                button.IsEnabled = !c.Busy;
            }
        }
        tools.IsEnabled = !c.Busy; quick.IsEnabled = !c.Busy;
        send.Content = c.Busy ? Loc.T("Stop  ■", "إيقاف  ■") : Loc.T("Ask  ↗", "اسأل  ↗");
        hint.Text = c.Attachments.Count > 0 ? string.Join("  ·  ", c.Attachments.Select(Path.GetFileName)) : c.Capture != null ? (c.Store.Data.Settings.ConfirmImageSharing ? Loc.T("Capture ready · confirmation before sharing", "الصورة جاهزة · سيتم التأكيد قبل المشاركة") : Loc.T("Capture ready · send with your question", "الصورة جاهزة · أرسلها مع سؤالك")) : c.Store.Data.Settings.ComputerAccessEnabled && c.Mode == "General" ? Loc.T("Windows access on · commands are reviewed before execution", "الوصول لويندوز مفعّل · تُراجع الأوامر قبل تنفيذها") : ModeTip(c.Mode);
        quick.Visibility = c.Capture == null ? Visibility.Collapsed : Visibility.Visible;
        thumbnail.Source = c.Capture;
        thumbnailFrame.Visibility = c.Capture == null ? Visibility.Collapsed : Visibility.Visible;
        if (c.Capture != null) thumbnailMeta.Text = Loc.T($"Image ready · {c.Capture.PixelWidth} × {c.Capture.PixelHeight} px", $"صورة جاهزة · {c.Capture.PixelWidth} × {c.Capture.PixelHeight} بكسل");
        input.FlowDirection = input.Text.Length == 0 ? Loc.Flow : (IsArabic(input.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight);
        placeholder.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task Send(bool allowPlanning = true)
    {
        if (c.Busy) { ((App)Application.Current).StopAll(); return; }
        if (string.IsNullOrWhiteSpace(input.Text)) return;
        var chat = c.Current;
        string request = input.Text;
        if (c.Mode is "Screen" or "Crop" && c.Capture == null) { if (!await ((App)Application.Current).CaptureAsync(c.Mode == "Crop") || c.Capture == null) return; }
        if (!ReferenceEquals(chat, c.Current)) return;
        input.Clear();
        try { int count = c.Current.Messages.Count; var pending = c.SendAsync(request, allowPlanning: allowPlanning); if (c.Busy || c.Current.Messages.Count > count) Submitted?.Invoke(); await pending; if (ReferenceEquals(chat, c.Current) && c.Current.Messages.Count == count && input.Text.Length == 0) SetDraft(request); }
        catch (Exception ex) { if (ReferenceEquals(chat, c.Current) && input.Text.Length == 0) SetDraft(request); Ui.Error(ex); }
    }
    private async Task RecordVoice()
    {
        if (c.Store.Data.Settings.UseAccountVoice) { await ((App)Application.Current).ToggleAccountTalkAsync(); return; }
        if (!c.Store.Data.Settings.VoiceEnabled) { ((App)Application.Current).Hub.Show(); ((App)Application.Current).Hub.Navigate("Settings", "Voice"); return; }
        if (!c.Voice.Recording)
        {
            c.Voice.StartRecording(); c.SetStatus(Loc.T("Listening · press the microphone again to send", "جاري الاستماع · اضغط الميكروفون مجددًا للإرسال"));
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) }; timer.Tick += (_, _) => { timer.Stop(); if (c.Voice.Recording) { c.Voice.CancelRecording(); c.SetStatus(Loc.T("Recording limit reached", "وصل التسجيل للحد الأقصى")); } }; timer.Start();
        }
        else { using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(90)); var chat = c.Current; try { string transcript = await c.Voice.StopAndTranscribe(ct.Token); if (ReferenceEquals(chat, c.Current)) SetDraft(transcript); } finally { c.SetStatus(Loc.T("Ready when you are", "جاهز عندما تكون مستعدًا")); } }
    }
    public static bool IsArabic(string text) => text.Any(ch => ch is >= '\u0600' and <= '\u06ff');
    public void Dispose() => c.Changed -= Refresh;
}
