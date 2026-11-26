using Nivo.Core;
using Nivo.Services;

namespace Nivo.UI;

public sealed partial class MainWindow : Window
{
    private readonly Controller controller;
    private ContentControl page = new();
    private TextBlock status = Ui.Text("●  Ready when you are", 12, "#AFC1D0");
    private Button incognitoBtn = null!;
    private Button modelPill = null!;
    private Button langBtn = null!;
    private readonly Dictionary<string, Button> navigation = [];
    private readonly List<string> clipHistory = [];
    private string currentPage = "Assistant";
    private string workspaceFilter = "All";
    private ChatView? chat;

    public MainWindow(Controller controller)
    {
        this.controller = controller;
        Loc.Apply(controller.Store.Data.Settings.Language);
        Title = Loc.T("NIVO Local · Your desktop, understood", "نيڤو المحلي · جهازك يفهمك");
        Ui.StyleWindow(this);
        Width = 1120; Height = 790; WindowStyle = WindowStyle.None;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), CornerRadius = new CornerRadius(16), GlassFrameThickness = new Thickness(0) });
        MinWidth = 850; MinHeight = 620; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        BuildChrome();
        controller.Changed += OnChanged; controller.Store.Changed += StoreChanged;
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) ((App)Application.Current).StopAll(); };
        Closing += (_, e) => { if (!((App)Application.Current).Exiting) { e.Cancel = true; Hide(); } };
        OnChanged();
        Navigate("Assistant");
    }

    public void RebuildChrome()
    {
        Title = Loc.T("NIVO Local · Your desktop, understood", "نيڤو المحلي · جهازك يفهمك");
        BuildChrome();
        OnChanged();
        Navigate(currentPage);
    }

    private void BuildChrome()
    {
        chat?.Dispose(); chat = null;
        page = new ContentControl();
        navigation.Clear();
        FlowDirection = Loc.Flow;
        status = Ui.Text("", 12, "#AFC1D0"); status.VerticalAlignment = VerticalAlignment.Center;

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(222) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = new DockPanel(); sidebar.SetResourceReference(BackgroundProperty, "Sidebar");
        var identity = Ui.Row(new Border { Child = Ui.Orb(32), Margin = new Thickness(0, 0, 10, 0) }, Ui.Stack(Ui.Text(Loc.T("NIVO", "نيڤو"), 22, weight: FontWeights.SemiBold), Ui.Text(Loc.T("Your desktop companion", "مساعدك على سطح المكتب"), 10, "#89A8BA")));
        identity.Margin = new Thickness(24, 24, 16, 26); DockPanel.SetDock(identity, Dock.Top); sidebar.Children.Add(identity);
        var bottom = Ui.Stack(Ui.Text(Loc.T("Your PC. Your models.", "جهازك. موديلاتك."), 13, "#BDCFDC"), Ui.Text(Loc.T("OpenCode · Codex · Antigravity\nAccount Talk · experimental", "OpenCode · Codex · Antigravity\nTalk بصوت الحساب · تجريبي"), 11, "#8EA7BA"), Ui.Btn(Loc.T("Quit NIVO", "إغلاق نيڤو"), () => ((App)Application.Current).Quit()));
        bottom.Margin = new Thickness(20, 12, 14, 16); DockPanel.SetDock(bottom, Dock.Bottom); sidebar.Children.Add(bottom);

        var links = new StackPanel { Margin = new Thickness(12, 0, 6, 0) };
        var navItems = new[] {
            ("Assistant", Loc.T("Assistant", "المساعد"), "\uE8F2"),
            ("Workspace", Loc.T("Workspace", "مساحة العمل"), "\uE8A5"),
            ("Memory", Loc.T("Memory", "الذاكرة"), "\uE9D9"),
            ("Clipboard", Loc.T("Clipboard", "الحافظة الذكية"), "\uE77F"),
            ("Your Usual", Loc.T("Your Usual", "روتينك المعتاد"), "\uE768"),
            ("Inbox", Loc.T("Inbox", "البريد"), "\uE715"),
            ("Task history", Loc.T("Task history", "السجل"), "\uE81C"),
            ("Settings", Loc.T("Settings", "الإعدادات"), "\uE713")
        };
        foreach (var (key, label, glyph) in navItems)
        {
            var target = key;
            var content = Ui.Row(Ui.Glyph(glyph, 17), Ui.Text("   " + label, 14));
            var b = Ui.Btn("", () => Navigate(target)); b.Tag = target; b.Content = content; b.HorizontalContentAlignment = HorizontalAlignment.Left; b.Background = Brushes.Transparent; b.BorderThickness = new Thickness(0); b.Margin = new Thickness(0, 0, 0, 4); b.Height = 40; b.Padding = new Thickness(12, 8, 12, 8); links.Children.Add(b); navigation[target] = b;
        }
        sidebar.Children.Add(links); root.Children.Add(sidebar);

        var main = new DockPanel { Margin = new Thickness(28, 20, 26, 20) }; Grid.SetColumn(main, 1);
        var top = new Grid { Margin = new Thickness(0, 0, 0, 16) }; top.ColumnDefinitions.Add(new ColumnDefinition()); top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        modelPill = Ui.Btn("", () => Navigate("Settings", "OpenCode AI")); modelPill.SetResourceReference(StyleProperty, "PillButton"); modelPill.Padding = new Thickness(10, 4, 10, 4); modelPill.FontSize = 11; modelPill.Margin = new Thickness(10, 0, 8, 0);
        var statusRow = Ui.Row(status, modelPill); statusRow.VerticalAlignment = VerticalAlignment.Center; top.Children.Add(statusRow);

        langBtn = Ui.Btn(Loc.IsArabic ? "EN" : "عربي", () => ((App)Application.Current).ApplyLanguage(Loc.IsArabic ? "English" : "Arabic"));
        langBtn.Tag = "LanguageToggle"; langBtn.ToolTip = Loc.T("Switch interface language to Arabic", "تبديل واجهة البرنامج إلى الإنجليزية");
        incognitoBtn = Ui.Btn(Loc.T("Incognito", "خفي"), () => controller.ToggleIncognito());
        var dockBtn = Ui.Btn(Loc.T("Dock", "الشريط"), () => { controller.Store.Data.Settings.DesktopEnabled = !controller.Store.Data.Settings.DesktopEnabled; controller.Store.Save(); ((App)Application.Current).RefreshShell(); });
        var controls = Ui.Row(langBtn, incognitoBtn, Ui.Btn(Loc.T("New chat", "محادثة جديدة"), () => { controller.NewChat(); Navigate("Assistant"); }), dockBtn, Ui.Btn(Loc.T("Island", "الجزيرة"), () => ((App)Application.Current).ToggleIsland()));
        Grid.SetColumn(controls, 1); top.Children.Add(controls); DockPanel.SetDock(top, Dock.Top); main.Children.Add(top);
        main.Children.Add(page); var mainFrame = new Border { Child = main }; mainFrame.SetResourceReference(Border.BackgroundProperty, "Panel"); Grid.SetColumn(mainFrame, 1); root.Children.Add(mainFrame);

        var layout = new DockPanel();
        var titlebar = new Grid { Height = 42, FlowDirection = FlowDirection.LeftToRight }; titlebar.SetResourceReference(BackgroundProperty, "Glass");
        var caption = Ui.Row(Ui.Glyph("\uE8F2", 14), Ui.Text("   " + Loc.T("NIVO · Your desktop, understood", "نيڤو · جهازك يفهمك"), 12)); caption.Margin = new Thickness(16, 0, 150, 0); caption.VerticalAlignment = VerticalAlignment.Center; titlebar.Children.Add(caption);
        titlebar.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; else if (!Ui.InButton(e.OriginalSource as DependencyObject) && e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };
        var windowButtons = Ui.Row(); windowButtons.HorizontalAlignment = HorizontalAlignment.Right;
        foreach (var item in new[] { ("−", (Action)(() => WindowState = WindowState.Minimized)), ("□", (Action)(() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized)), ("×", (Action)(() => Hide())) })
        { var b = Ui.Btn(item.Item1, item.Item2); b.SetResourceReference(StyleProperty, "WindowControl"); b.Tag = item.Item1 == "×" ? "Close" : ""; b.Width = 28; b.Height = 28; b.Padding = new Thickness(0); b.Margin = new Thickness(4, 7, 4, 7); windowButtons.Children.Add(b); }
        titlebar.Children.Add(windowButtons); DockPanel.SetDock(titlebar, Dock.Top); layout.Children.Add(titlebar); layout.Children.Add(root);
        var frame = new Border { Child = layout, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), ClipToBounds = true }; frame.SetResourceReference(Border.BorderBrushProperty, "Hairline"); Content = frame;
    }

    private static string ShortModel(string id) { var idx = id.LastIndexOf('/'); return idx >= 0 && idx + 1 < id.Length ? id[(idx + 1)..] : id; }
    private void OnChanged()
    {
        string baseStatus = controller.Status == "Ready when you are" ? Loc.T("Ready when you are", "جاهز عندما تكون مستعدًا") : controller.Status;
        status.Text = (controller.Store.Incognito ? Loc.T("Incognito  ·  ", "خفي  ·  ") : "●  ") + baseStatus;
        if (controller.IsQuiet) status.Text += Loc.T("  ·  Quiet mode", "  ·  وضع هادئ");
        modelPill.Content = "⚡ " + ShortModel(controller.Store.Data.Settings.Model);
        modelPill.ToolTip = Loc.T("Active OpenCode model: ", "الموديل النشط: ") + controller.Store.Data.Settings.Model + (string.IsNullOrWhiteSpace(controller.Store.Data.Settings.FallbackModel) ? "" : "\nFallback: " + controller.Store.Data.Settings.FallbackModel);
        incognitoBtn.Content = controller.Store.Incognito ? Loc.T("Incognito ON", "الخفي مفعّل") : Loc.T("Incognito", "خفي");
        incognitoBtn.SetResourceReference(Control.BackgroundProperty, controller.Store.Incognito ? "Selected" : "Panel");
    }
    private void StoreChanged() { if (currentPage is "Workspace" or "Memory" or "Your Usual" or "Task history" && !controller.Busy) Navigate(currentPage); }
    public void Navigate(string name, string? subTab = null)
    {
        if (currentPage == "Settings") Ui.ApplyTheme(controller.Store.Data.Settings.Theme);
        chat?.Dispose(); chat = null; currentPage = name;
        foreach (var pair in navigation) { if (pair.Key == name) pair.Value.SetResourceReference(Control.BackgroundProperty, "Selected"); else pair.Value.Background = Brushes.Transparent; }
        if (name == "Assistant") { chat = new ChatView(controller); page.Content = chat; Motion.Enter(chat); return; }
        if (name == "Settings") { var view = SettingsPage(subTab ?? "Personalization"); page.Content = view; Motion.Enter(view); return; }
        var contents = name switch { "Workspace" => WorkspacePage(), "Memory" => MemoryPage(), "Clipboard" => ClipboardPage(), "Your Usual" => RoutinesPage(), "Inbox" => InboxPage(), _ => HistoryPage() };
        page.Content = new ScrollViewer { Content = contents, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Motion.Enter((FrameworkElement)page.Content);
    }
    public void ShowAssistant(string? draft = null)
    {
        if (draft != null) controller.Draft = draft;
        Show(); Activate(); Navigate("Assistant");
    }
    private static StackPanel PageHeader(string title, string description)
    {
        var p = Ui.Stack(Ui.Text(title, 30, weight: FontWeights.SemiBold), Ui.Text(description, 14, "#9FB3C3")); Ui.Space(p, 20); return p;
    }

    private StackPanel HistoryPage()
    {
        var p = PageHeader(Loc.T("Everything we worked on.", "كل ما عملنا عليه."), Loc.T("Real task results and your locally saved conversations.", "نتائج المهام الحقيقية ومحادثاتك المحفوظة محليًا."));
        p.Children.Add(Ui.Row(Ui.Btn(Loc.T("Open output folder", "فتح مجلد المخرجات"), () => DesktopService.OpenTarget(controller.Store.Output)), Ui.Btn(Loc.T("Export local data", "تصدير البيانات"), ExportData), Ui.Btn(Loc.T("Clear history", "مسح السجل"), () => { if (Ui.Confirm(Loc.T("Delete saved conversations and task history? Output files remain.", "حذف المحادثات وسجل المهام؟ ملفات المخرجات تبقى محفوظة."))) { controller.Store.Data.Conversations.Clear(); controller.Store.Data.Tasks.Clear(); controller.Store.Save(); } })));
        foreach (var record in controller.Store.Data.Tasks.Take(30))
            p.Children.Add(Ui.Card(Ui.Stack(Ui.Text(record.Status + "  ·  " + record.Name, 16, record.Status == "Done" ? "#8ADBD5" : "#D9B794"), Ui.Text(record.Time.ToString("g"), 11, "#91AABA"), new TextBox { Text = record.Detail, IsReadOnly = true, AcceptsReturn = true, MaxHeight = 130, VerticalScrollBarVisibility = ScrollBarVisibility.Auto })));
        foreach (var conversation in controller.Store.Data.Conversations.Take(40).ToArray())
        {
            var c = conversation;
            var row = Ui.Row(Ui.Btn(c.Title + "  ·  " + c.Messages.Count + Loc.T(" messages", " رسائل"), () => { controller.OpenChat(c); Navigate("Assistant"); }), Ui.Btn(Loc.T("Delete", "حذف"), () => { controller.Store.Data.Conversations.Remove(c); controller.Store.Save(); }));
            p.Children.Add(row);
        }
        if (controller.Store.Data.Tasks.Count == 0 && controller.Store.Data.Conversations.Count == 0) p.Children.Add(Ui.Card(Ui.Text(Loc.T("No history yet. Your first conversation will appear here.", "لا يوجد سجل بعد. ستظهر محادثاتك ومهامك هنا."))));
        return p;
    }

    private void ExportData()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "nivo-local-export.json", Filter = "JSON|*.json" };
        if (dialog.ShowDialog() == true) File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(controller.Store.Data, new JsonSerializerOptions { WriteIndented = true }));
    }

    private StackPanel ClipboardPage()
    {
        var p = PageHeader(Loc.T("Pick up where you copied.", "أكمل من حيث نسخت."), Loc.T("NIVO reads your clipboard only when you press Read clipboard. Nothing is sent until you ask.", "يقرأ نيڤو الحافظة فقط عند الضغط على قراءة الحافظة. لا يُرسَل شيء حتى تطلب."));
        var preview = new TextBox { IsReadOnly = true, AcceptsReturn = true, Height = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var image = new Image { MaxHeight = 200, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 12) };
        var historyPanel = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        string? text = null; System.Windows.Media.Imaging.BitmapSource? bitmap = null;

        void RenderHistory()
        {
            historyPanel.Children.Clear();
            foreach (var entry in clipHistory.Take(6))
            {
                var item = entry;
                var label = item.Replace("\r", " ").Replace("\n", " ").Trim();
                if (label.Length > 36) label = label[..36] + "…";
                var chip = Ui.Btn("📋  " + label, () => { text = item; bitmap = null; preview.Text = item; image.Source = null; });
                chip.SetResourceReference(StyleProperty, "PillButton"); chip.Padding = new Thickness(10, 5, 10, 5); chip.FontSize = 11.5;
                historyPanel.Children.Add(chip);
            }
        }

        p.Children.Add(Ui.Row(Ui.Btn(Loc.T("Read clipboard", "قراءة الحافظة الآن"), () => {
            text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null;
            bitmap = System.Windows.Clipboard.ContainsImage() ? System.Windows.Clipboard.GetImage() : null;
            if (text?.Length > 60000) text = text[..60000];
            if (!string.IsNullOrWhiteSpace(text) && !clipHistory.Contains(text)) { clipHistory.Insert(0, text); if (clipHistory.Count > 12) clipHistory.RemoveAt(12); RenderHistory(); }
            preview.Text = text ?? (bitmap == null ? Loc.T("Clipboard has no text or image.", "الحافظة فارغة حاليًا.") : Loc.T($"Image from clipboard ({bitmap.PixelWidth} × {bitmap.PixelHeight} px)", $"صورة من الحافظة ({bitmap.PixelWidth} × {bitmap.PixelHeight} بكسل)"));
            image.Source = bitmap;
        }, true), Ui.Btn(Loc.T("Clear", "مسح العرض"), () => { text = null; bitmap = null; preview.Text = ""; image.Source = null; })));
        p.Children.Add(preview); p.Children.Add(image);
        var actions = new WrapPanel();
        var clipCmds = new[] {
            ("Summarize", Loc.T("Summarize", "لخّص")),
            (Loc.IsArabic ? "Translate to Arabic" : "Translate to English", Loc.T("Translate to English", "ترجم للعربية")),
            ("Rewrite", Loc.T("Rewrite", "أعد الصياغة")),
            ("Explain", Loc.T("Explain", "اشرح")),
            ("Use in a task", Loc.T("Use in a task", "استخدمه في مهمة"))
        };
        foreach (var (cmd, label) in clipCmds)
        {
            var c = cmd; actions.Children.Add(Ui.Btn(label, () => {
                if (text == null && bitmap == null) throw new InvalidOperationException(Loc.T("Read some clipboard content first.", "اضغط قراءة الحافظة أولًا."));
                controller.Capture = bitmap; controller.Mode = c == "Use in a task" ? "Agent" : "General";
                ShowAssistant(c + " the following user-provided content:\n\n" + text);
            }));
        }
        p.Children.Add(actions);
        if (clipHistory.Count > 0)
        {
            var histHeader = Ui.Text(Loc.T("Recent clipboard snippets (this session)", "آخر ما قرأته من الحافظة في هذه الجلسة"), 12, "#9CB3C3");
            histHeader.Margin = new Thickness(0, 12, 0, 0);
            p.Children.Add(histHeader);
            RenderHistory();
        }
        p.Children.Add(historyPanel);
        return p;
    }

    private StackPanel WorkspacePage()
    {
        var p = PageHeader(Loc.T("A little room for your work.", "مساحة مرتبة لشغلك."), Loc.T("Notes, tasks, and reminders. Stored on this computer.", "ملاحظات، مهام، وتذكيرات. محفوظة محليًا على جهازك."));
        var title = new TextBox { MinHeight = 72, AcceptsReturn = true, ToolTip = Loc.T("Task or note content", "محتوى المهمة أو الملاحظة") };
        var kind = new ComboBox { Width = 125 };
        kind.Items.Add(new ComboBoxItem { Content = Loc.T("Task", "مهمة"), Tag = "Task" });
        kind.Items.Add(new ComboBoxItem { Content = Loc.T("Note", "ملاحظة"), Tag = "Note" });
        kind.SelectedIndex = 0;
        var date = new DatePicker { Width = 165, Margin = new Thickness(10, 5, 10, 12), ToolTip = Loc.T("Optional reminder date", "تاريخ التذكير (اختياري)") };
        var time = new TextBox { Text = "09:00", Width = 85, FlowDirection = FlowDirection.LeftToRight, ToolTip = Loc.T("24-hour reminder time", "الوقت بنظام 24 ساعة") };
        var addWorkBtn = Ui.Btn(Loc.T("Add to workspace", "إضافة لمساحة العمل"), () => {
                if (string.IsNullOrWhiteSpace(title.Text)) return;
                DateTime? due = null; if (date.SelectedDate.HasValue) { if (!TimeSpan.TryParse(time.Text, out var t) || t.TotalHours >= 24 || t.TotalHours < 0) throw new InvalidDataException(Loc.T("Use a valid 24-hour time, for example 16:30.", "أدخل وقتًا صحيحًا بنظام 24 ساعة مثل 16:30.")); due = date.SelectedDate.Value.Date.Add(t); }
                string selectedKind = (kind.SelectedItem as ComboBoxItem)?.Tag as string ?? "Task";
                controller.Store.Data.Workspace.Add(new() { Title = title.Text.Trim(), Kind = selectedKind, Due = due }); controller.Store.Save();
            }, true);
        addWorkBtn.HorizontalAlignment = HorizontalAlignment.Left;
        p.Children.Add(Ui.Card(Ui.Stack(Ui.Text(Loc.T("Add something worth keeping", "أضف مهمة أو ملاحظة أو تذكيرًا"), 17), title,
            Ui.Text(Loc.T("Type · Optional reminder date & time (24h)", "النوع · تاريخ ووقت التذكير (اختياري)"), 11.5, "#9CB3C3"),
            Ui.Row(kind, date, time),
            addWorkBtn)));

        var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var (key, label) in new[] { ("All", Loc.T("All", "الكل")), ("Task", Loc.T("Tasks", "مهام")), ("Note", Loc.T("Notes", "ملاحظات")), ("Reminders", Loc.T("Reminders", "تذكيرات")), ("Done", Loc.T("Done", "مكتملة")) })
        {
            var f = key;
            var fb = Ui.Btn(label, () => { workspaceFilter = f; Navigate("Workspace"); });
            fb.SetResourceReference(StyleProperty, "PillButton"); fb.Padding = new Thickness(12, 5, 12, 5); fb.FontSize = 12;
            if (workspaceFilter == f) { fb.SetResourceReference(Control.BackgroundProperty, "ActiveGlass"); fb.SetResourceReference(Control.ForegroundProperty, "OnAccent"); fb.FontWeight = FontWeights.SemiBold; }
            filters.Children.Add(fb);
        }
        p.Children.Add(filters);

        var items = controller.Store.Data.Workspace.Where(w => workspaceFilter switch { "Task" => w.Kind == "Task" && !w.Done, "Note" => w.Kind == "Note" && !w.Done, "Reminders" => w.Due.HasValue && !w.Done, "Done" => w.Done, _ => true }).OrderBy(t => t.Done).ThenBy(t => t.Due ?? DateTime.MaxValue).ToArray();
        foreach (var item in items)
        {
            var dueNote = item.Due.HasValue ? ("  ·  " + (item.Due < DateTime.Now && !item.Done ? Loc.T("Overdue · ", "متأخر · ") : "") + item.Due.Value.ToString("f")) : "";
            var kindText = item.Kind == "Note" ? Loc.T("Note", "ملاحظة") : Loc.T("Task", "مهمة");
            var line = Ui.Stack(Ui.Text(kindText + dueNote, 11, item.Due < DateTime.Now && !item.Done ? "#D9B794" : "#94AFBF"), Ui.Text(item.Title, 16, item.Done ? "#8DA2AE" : null));
            line.Children.Add(new Border { Height = 6 });
            line.Children.Add(Ui.Row(Ui.Btn(item.Done ? Loc.T("Reopen", "إعادة فتح") : Loc.T("Mark done", "تم الإنجاز"), () => { item.Done = !item.Done; item.Notified = false; controller.Store.Save(); }), Ui.Btn(Loc.T("Edit", "تعديل"), () => { var value = Ui.Prompt(Loc.T("Edit item", "تعديل العنصر"), Loc.T("Content", "المحتوى"), item.Title, true); if (value != null) { item.Title = value; controller.Store.Save(); } }), Ui.Btn(Loc.T("Delete", "حذف"), () => { if (Ui.Confirm(Loc.T("Delete this workspace item?", "حذف هذا العنصر؟"))) { controller.Store.Data.Workspace.Remove(item); controller.Store.Save(); } })));
            if (!string.IsNullOrWhiteSpace(item.ReferenceFile)) line.Children.Add(Ui.Btn(Loc.T("Open saved image", "فتح الصورة المحفوظة"), () => DesktopService.OpenTarget(item.ReferenceFile)));
            p.Children.Add(Ui.Card(line));
        }
        if (items.Length == 0) p.Children.Add(Ui.Card(Ui.Text(Loc.T("No items in this view yet.", "لا توجد عناصر في هذا التصنيف بعد."), 14, "#9CB3C3")));

        Ui.Space(p); p.Children.Add(Ui.Text(Loc.T("Outputs", "الملفات المُنتَجة"), 21, weight: FontWeights.SemiBold));
        p.Children.Add(Ui.Btn(Loc.T("Open output folder", "فتح مجلد المخرجات"), () => DesktopService.OpenTarget(controller.Store.Output)));
        foreach (string file in FileService.CompletedOutputs(controller.Store).OrderByDescending(File.GetLastWriteTime).Take(8))
        {
            var path = file; var kb = Math.Max(1, new FileInfo(path).Length / 1024);
            var outBtn = Ui.Btn($"📄  {Path.GetFileName(file)}  ({kb} KB)", () => DesktopService.OpenTarget(path));
            var menu = new ContextMenu();
            var reveal = new MenuItem { Header = Loc.T("Show in Explorer", "عرض في المجلد") }; reveal.Click += (_, _) => DesktopService.RevealInExplorer(path);
            menu.Items.Add(reveal); outBtn.ContextMenu = menu;
            p.Children.Add(outBtn);
        }
        return p;
    }

    private StackPanel RoutinesPage()
    {
        var p = PageHeader(Loc.T("Your usual, in one click.", "روتينك المعتاد بضغطة واحدة."), Loc.T("Apps, links, music, focus, and saved window positions. Replay runs locally.", "تطبيقاتك، روابطك، الموسيقى، وضع التركيز، وترتيب النوافذ. التشغيل محلي بدون استهلاك موديل."));
        var name = new TextBox { ToolTip = Loc.T("Routine name", "اسم الروتين") }; var targets = new TextBox { Height = 110, AcceptsReturn = true, FlowDirection = FlowDirection.LeftToRight, ToolTip = Loc.T("One full application path or HTTPS link per line", "مسار تطبيق أو رابط HTTPS في كل سطر") };
        var music = new TextBox { FlowDirection = FlowDirection.LeftToRight, ToolTip = Loc.T("Optional HTTPS music link or local media file", "رابط موسيقى أو ملف صوتي (اختياري)") }; var focus = new CheckBox { Content = Loc.T("Enable Focus Mode when this setup starts", "تفعيل وضع التركيز عند بدء هذا الروتين") };
        var focusProfile = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = false, ToolTip = Loc.T("Focus profile applied when this setup enables Focus Mode", "ملف التركيز المُطبّق عند تفعيل وضع التركيز بهذا الروتين") };
        foreach (var (tag, label) in new[] { ("", Loc.T("Keep current profile", "الإبقاء على الملف الحالي")), ("Study", Loc.T("Study", "الدراسة")), ("Coding", Loc.T("Coding", "البرمجة")), ("Deep Work", Loc.T("Deep Work", "العمل العميق")), ("General", Loc.T("General", "عام")) })
            focusProfile.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        focusProfile.SelectedIndex = 0;
        focus.Checked += (_, _) => focusProfile.IsEnabled = true;
        focus.Unchecked += (_, _) => focusProfile.IsEnabled = false;
        void SelectFocusProfile(string value)
        {
            for (int i = 0; i < focusProfile.Items.Count; i++)
                if (focusProfile.Items[i] is ComboBoxItem item && string.Equals(item.Tag as string, value, StringComparison.OrdinalIgnoreCase)) { focusProfile.SelectedIndex = i; return; }
            focusProfile.SelectedIndex = 0;
        }
        var pickApp = Ui.Btn(Loc.T("Browse app (.exe)", "تصفح تطبيق (.exe)"), () => {
            var picked = Ui.PickFile("Applications|*.exe;*.lnk|All files|*.*");
            if (picked != null) targets.Text = string.IsNullOrWhiteSpace(targets.Text) ? picked : targets.Text.TrimEnd() + Environment.NewLine + picked;
        });
        var addOpen = Ui.Btn(Loc.T("Add open apps", "إضافة التطبيقات المفتوحة"), () => {
            var open = RoutineService.OpenExecutables();
            if (open.Length == 0) { controller.SetStatus(Loc.T("No other visible app windows found.", "لم يتم العثور على تطبيقات أخرى مفتوحة.")); return; }
            var merged = targets.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Concat(open).Distinct(StringComparer.OrdinalIgnoreCase).Take(12);
            targets.Text = string.Join(Environment.NewLine, merged);
        });
        var studyPreset = Ui.Btn(Loc.T("✨ Study preset", "✨ قالب دراسة"), () => { name.Text = Loc.T("Study", "دراسة"); targets.Text = "notepad.exe\nhttps://scholar.google.com"; focus.IsChecked = true; SelectFocusProfile("Study"); });
        var codePreset = Ui.Btn(Loc.T("✨ Coding preset", "✨ قالب برمجة"), () => { name.Text = Loc.T("Coding", "برمجة"); targets.Text = "https://github.com\nhttps://www.google.com"; focus.IsChecked = true; SelectFocusProfile("Coding"); });
        p.Children.Add(Ui.Card(Ui.Stack(Ui.Text(Loc.T("Name", "الاسم")), name, Ui.Text(Loc.T("Applications and links · one per line", "التطبيقات والروابط · عنصر في كل سطر")), targets, Ui.Row(pickApp, addOpen, studyPreset, codePreset), Ui.Text(Loc.T("Music · optional URL or media path", "الموسيقى · رابط أو ملف اختياري")), music, focus, Ui.Text(Loc.T("Focus profile · optional", "ملف التركيز · اختياري"), 12, "#9CB3C3"), focusProfile, Ui.Btn(Loc.T("Save setup", "حفظ الروتين"), () => {
            var entries = targets.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (string.IsNullOrWhiteSpace(name.Text) || entries.Count is < 1 or > 12) throw new InvalidDataException(Loc.T("Name the setup and add 1–12 apps or links.", "اكتب اسم الروتين وأضف من 1 إلى 12 تطبيقًا أو رابطًا."));
            string selectedProfile = focus.IsChecked == true ? ((focusProfile.SelectedItem as ComboBoxItem)?.Tag as string ?? "") : "";
            controller.Store.Data.Routines.Add(new() { Name = name.Text.Trim(), Targets = entries, Music = music.Text.Trim(), Focus = focus.IsChecked == true, FocusProfile = selectedProfile }); controller.Store.Save();
        }, true))));
        foreach (var routine in controller.Store.Data.Routines.ToArray())
        {
            string resolvedProfile = RoutineService.ResolveFocusProfile(routine);
            string focusLabel = routine.Focus
                ? (resolvedProfile.Length > 0 ? Loc.T($"Focus on · {resolvedProfile}", $"التركيز مفعّل · {FocusPolicy.DisplayName(resolvedProfile)}") : Loc.T("Focus on", "التركيز مفعّل"))
                : Loc.T("Focus off", "التركيز مغلق");
            var body = Ui.Stack(Ui.Text(routine.Name, 19), Ui.Text(string.Join("\n", routine.Targets), 12, "#9CB3C3"), Ui.Text(Loc.T($"{routine.Windows.Count} saved positions · {focusLabel}", $"{routine.Windows.Count} مواضع نوافذ محفوظة · {focusLabel}") + (routine.Music.Length > 0 ? Loc.T(" · Music included", " · مع موسيقى") : ""), 12, "#9CB3C3"));
            body.Children.Add(Ui.Row(Ui.AsyncBtn(Loc.T("Start setup", "تشغيل الروتين"), async () => { await controller.RunRoutineAsync(routine); ((App)Application.Current).RefreshShell(); }, true), Ui.Btn(Loc.T("Save current positions", "حفظ مواقع النوافذ الحالية"), () => {
                routine.Windows = RoutineService.Capture(routine.Targets); controller.Store.Save(); controller.SetStatus(Loc.T($"Saved {routine.Windows.Count} matching app windows.", $"تم حفظ مواقع {routine.Windows.Count} نافذة مطابقة."));
            }), Ui.Btn(Loc.T("Delete", "حذف"), () => { if (Ui.Confirm(Loc.T("Delete this saved setup?", "حذف هذا الروتين؟"))) { controller.Store.Data.Routines.Remove(routine); controller.Store.Save(); } })));
            p.Children.Add(Ui.Card(body));
        }
        p.Children.Add(Ui.Text(Loc.T("In chat, use /usual followed by the setup name to replay it locally.", "في المحادثة، اكتب /usual متبوعًا باسم الروتين لتشغيله محليًا."), 12, "#9CB3C3"));
        return p;
    }
}
