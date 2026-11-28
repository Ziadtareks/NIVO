using System.Windows.Automation;
using Nivo.Core;
using Nivo.Services;

namespace Nivo.UI;

public sealed partial class MainWindow
{
    private FrameworkElement SettingsPage(string initialTab = "Personalization")
    {
        var s = controller.Store.Data.Settings;
        var shell = new DockPanel { FlowDirection = Loc.Flow, MaxWidth = 960 };
        var header = PageHeader(
            Loc.T("Make yourself at home.", "اضبط نيڤو على طريقتك."),
            Loc.T("Local companion preferences, OpenCode CLI models, and desktop behavior. Your choices stay on this PC.", "إعدادات المساعد المحلي وموديلات OpenCode CLI وسلوك سطح المكتب. كل اختياراتك تبقى على جهازك."));

        var tabBar = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
        var tabs = new Dictionary<string, FrameworkElement>();
        var tabLabels = new Dictionary<string, string>
        {
            ["Personalization"] = Loc.T("Personalization", "المظهر واللغة"),
            ["OpenCode AI"] = Loc.T("AI & accounts", "الذكاء والحسابات"),
            ["Shortcuts"] = Loc.T("Shortcuts", "الاختصارات"),
            ["Desktop"] = Loc.T("Desktop", "سطح المكتب"),
            ["Voice"] = Loc.T("Voice", "الصوت"),
            ["About"] = Loc.T("About", "حول والبيانات")
        };
        var tabButtons = new Dictionary<string, Button>();
        var activeTabContent = new ContentControl();
        var contentHost = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 10, 8) };
        contentHost.Children.Add(activeTabContent);

        var saveStatus = Ui.Text(Loc.T("Changes across all tabs are saved together.", "يتم حفظ تعديلات جميع التبويبات معًا."), 12, "#9CB3C3");
        saveStatus.Tag = "SaveStatus";
        saveStatus.VerticalAlignment = VerticalAlignment.Center;
        bool initializing = true;
        void MarkDirty()
        {
            if (initializing) return;
            saveStatus.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
            saveStatus.Text = Loc.T("Unsaved changes · click Save preferences to apply.", "تغييرات غير محفوظة · اضغط حفظ الإعدادات لتطبيقها.");
        }

        static Border Divider()
        {
            var line = new Border { Height = 1, Margin = new Thickness(0, 10, 0, 14), Opacity = 0.55 };
            line.SetResourceReference(Border.BackgroundProperty, "Hairline");
            return line;
        }

        static Border Section(string title, string? description, params UIElement[] body)
        {
            var stack = new StackPanel();
            stack.Children.Add(Ui.Text(title, 18, weight: FontWeights.SemiBold));
            if (!string.IsNullOrWhiteSpace(description))
            {
                var desc = Ui.Text(description, 12.5, "#9CB3C3");
                desc.Margin = new Thickness(0, 4, 0, 0);
                stack.Children.Add(desc);
            }
            stack.Children.Add(Divider());
            foreach (var element in body) stack.Children.Add(element);
            return Ui.Card(stack, new Thickness(20, 18, 20, 18));
        }

        static StackPanel Field(string label, UIElement control, string? hint = null)
        {
            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
            var title = Ui.Text(label, 13, weight: FontWeights.SemiBold);
            block.Children.Add(title);
            if (!string.IsNullOrWhiteSpace(hint))
            {
                var sub = Ui.Text(hint, 11.5, "#9CB3C3");
                sub.Margin = new Thickness(0, 2, 0, 2);
                block.Children.Add(sub);
            }
            block.Children.Add(control);
            return block;
        }

        static Grid TwoCol(UIElement first, UIElement second)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(first, 0);
            Grid.SetColumn(second, 2);
            grid.Children.Add(first);
            grid.Children.Add(second);
            return grid;
        }

        static Border Notice(TextBlock text, bool warning = false)
        {
            var box = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 4, 0, 12),
                Child = text
            };
            box.SetResourceReference(Border.BackgroundProperty, "Input");
            box.SetResourceReference(Border.BorderBrushProperty, warning ? "Warning" : "Hairline");
            return box;
        }

        static WrapPanel ActionRow(params UIElement[] actions)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 6) };
            foreach (var action in actions) row.Children.Add(action);
            return row;
        }

        CheckBox OptionToggle(string title, string subtitle, bool isChecked, string? tag = null)
        {
            var label = Ui.Text(title, 13.5, weight: FontWeights.Medium);
            var desc = Ui.Text(subtitle, 12, "#9CB3C3");
            desc.Margin = new Thickness(0, 2, 0, 0);
            var content = new StackPanel();
            content.Children.Add(label);
            content.Children.Add(desc);
            var check = new CheckBox
            {
                Content = content,
                IsChecked = isChecked,
                Tag = tag,
                VerticalContentAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 0, 10)
            };
            AutomationProperties.SetName(check, title);
            check.Checked += (_, _) => MarkDirty();
            check.Unchecked += (_, _) => MarkDirty();
            return check;
        }

        // Tab 1: Personalization
        var theme = new ComboBox { ItemsSource = Theme.Names, SelectedItem = s.Theme, Visibility = Visibility.Collapsed };
        var reduced = OptionToggle(
            Loc.T("Reduce motion", "تقليل الحركة"),
            Loc.T("Turns off interface animation inside NIVO.", "يوقف حركة الواجهة داخل نيڤو."),
            s.ReduceMotion, "ReduceMotion");
        var followWindowsAnimations = OptionToggle(
            Loc.T("Follow Windows animation settings", "اتباع إعدادات الحركة في ويندوز"),
            Loc.T("Switch off to enable NIVO animations independently. Windows preferences stay unchanged.", "أوقفه لتفعيل حركة نيڤو بشكل مستقل. إعدادات ويندوز نفسها لن تتغير."),
            s.FollowWindowsAnimations, "FollowWindowsAnimations");
        theme.SelectionChanged += (_, _) => { if (theme.SelectedItem is string name) { Ui.ApplyTheme(name); MarkDirty(); } };
        var gallery = new UniformGridEx(3) { Margin = new Thickness(0, 2, 0, 10) };
        var themeButtons = new Dictionary<string, Button>();
        void UpdateThemeSelection(string selectedName)
        {
            foreach (var item in themeButtons)
            {
                bool active = item.Key == selectedName;
                item.Value.BorderThickness = new Thickness(active ? 2 : 1);
                item.Value.SetResourceReference(Control.BorderBrushProperty, active ? "Accent" : "Hairline");
            }
        }
        foreach (string name in Theme.Names)
        {
            var palette = Theme.Palette(name);
            var swatch = new Border
            {
                Height = 34,
                CornerRadius = new CornerRadius(7),
                Background = new LinearGradientBrush(Ui.Brush(palette.GlassTop).Color, Ui.Brush(palette.Sidebar).Color, 30),
                Margin = new Thickness(0, 0, 0, 8)
            };
            var dot = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                Background = Ui.Brush(palette.Accent),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8)
            };
            swatch.Child = dot;
            var themeName = name;
            var button = Ui.Btn("", () => { theme.SelectedItem = themeName; UpdateThemeSelection(themeName); });
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.Content = Ui.Stack(swatch, Ui.Text(themeName, 12.5, weight: FontWeights.Medium));
            button.Padding = new Thickness(12);
            gallery.Children.Add(button);
            themeButtons[themeName] = button;
        }
        UpdateThemeSelection(s.Theme);
        var persona = new ComboBox { ItemsSource = new[] { "Chill Smart", "Excited Inventor", "Lazy Sleepy" }, SelectedItem = s.Persona };
        persona.SelectionChanged += (_, _) => MarkDirty();
        var language = new ComboBox { ItemsSource = new[] { "Arabic", "English" }, SelectedItem = s.Language };
        language.SelectionChanged += (_, _) => MarkDirty();
        tabs["Personalization"] = Ui.Stack(
            Section(
                Loc.T("Theme & motion", "الثيم والحركة"),
                Loc.T("Theme previews apply immediately across the window. Save preferences to keep your choice.", "معاينة الثيم فورية على النافذة. اضغط حفظ الإعدادات لتثبيت اختيارك."),
                gallery, theme, reduced, followWindowsAnimations),
            Section(
                Loc.T("Assistant voice & language", "شخصية المساعد واللغة"),
                Loc.T("Choose how NIVO speaks to you and which interface direction (RTL Arabic / LTR English) to use.", "اختر أسلوب ردود نيڤو ولغة الواجهة واتجاهها (عربي من اليمين / إنجليزي من اليسار)."),
                TwoCol(
                    Field(Loc.T("Personality", "شخصية المساعد"), persona, Loc.T("Controls tone without reducing accuracy.", "يحدد أسلوب الرد دون التأثير على الدقة.")),
                    Field(Loc.T("App & response language", "لغة التطبيق والردود"), language, Loc.T("Switches layout direction on save.", "يبدّل اتجاه الواجهة عند الحفظ.")))));

        // Tab 2: OpenCode AI
        var accountPaths = new Dictionary<string, TextBox>();
        var knownModels = new[] { s.Model, s.FallbackModel, s.VisionModel, s.VisionFallbackModel }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var model = new ComboBox { IsEditable = true, ItemsSource = knownModels, Text = s.Model, FlowDirection = FlowDirection.LeftToRight };
        var fallback = new ComboBox { IsEditable = true, ItemsSource = new[] { "" }.Concat(knownModels).ToArray(), Text = s.FallbackModel, FlowDirection = FlowDirection.LeftToRight, ToolTip = Loc.T("Optional backup model used automatically if the primary model hits quota / 429", "موديل بديل اختياري يُستخدم تلقائيًا عند وصول الموديل الأساسي للحد 429") };
        var vision = new ComboBox { IsEditable = true, ItemsSource = knownModels, Text = s.VisionModel, FlowDirection = FlowDirection.LeftToRight };
        var visionFallback = new ComboBox { IsEditable = true, ItemsSource = new[] { "" }.Concat(knownModels).ToArray(), Text = s.VisionFallbackModel, FlowDirection = FlowDirection.LeftToRight };
        void WatchRouting(ComboBox combo, Func<string> savedValue)
        {
            // Creating the editable template raises TextChanged for its initial value.
            // Read the saved value live so a subsequent Save refreshes this comparison too.
            void RoutingChanged() { if (!string.Equals(combo.Text ?? "", savedValue() ?? "", StringComparison.Ordinal)) MarkDirty(); }
            combo.SelectionChanged += (_, _) => RoutingChanged();
            combo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => RoutingChanged()));
        }
        WatchRouting(model, () => s.Model);
        WatchRouting(fallback, () => s.FallbackModel);
        WatchRouting(vision, () => s.VisionModel);
        WatchRouting(visionFallback, () => s.VisionFallbackModel);

        var cli = new TextBox { Text = s.CliPath, FlowDirection = FlowDirection.LeftToRight, ToolTip = Loc.T("Leave empty to auto-detect OpenCode.exe", "اتركه فارغًا للاكتشاف التلقائي لـ opencode.exe") };
        cli.TextChanged += (_, _) => MarkDirty();

        bool initialStrict = s.FreeModelsOnly || !s.UseUserOpenCodeConfig;
        var useUserConfig = OptionToggle(
            Loc.T("Use my OpenCode CLI providers, logins & saved Google key", "استخدام مزوّدي وحسابات OpenCode CLI ومفتاح Google المحفوظ"),
            Loc.T("Reads ~/.config/opencode and passes your saved Google key when using Google models.", "يقرأ ~/.config/opencode ويمرّر مفتاح Google المحفوظ عند استخدام موديلات Google."),
            !initialStrict,
            "UseUserOpenCodeConfig");
        var freeOnly = OptionToggle(
            Loc.T("Restrict to built-in opencode/*-free models only (strict isolated mode)", "حصر الاستخدام على موديلات opencode/*-free المدمجة فقط (عزل صارم)"),
            Loc.T("Isolates OpenCode config and excludes logins and API keys; only built-in free models can run.", "يعزل إعدادات OpenCode ويستبعد الحسابات ومفاتيح API؛ يسمح فقط بالموديلات المجانية المدمجة."),
            initialStrict,
            "FreeModelsOnly");
        var isolationNote = Ui.Text("", 12, "#9CB3C3");
        isolationNote.Tag = "IsolationNote";
        var isolationNotice = Notice(isolationNote);
        bool syncingIsolation = false;
        void UpdateIsolationNote()
        {
            bool strict = freeOnly.IsChecked == true || useUserConfig.IsChecked != true;
            isolationNote.SetResourceReference(TextBlock.ForegroundProperty, strict ? "Warning" : "Muted");
            isolationNotice.SetResourceReference(Border.BorderBrushProperty, strict ? "Warning" : "Hairline");
            isolationNote.Text = strict
                ? Loc.T("Active mode: Strict isolated free mode — user OpenCode providers, logins, and saved API keys are excluded; only built-in opencode/*-free models are permitted.", "الوضع النشط: عزل صارم للموديلات المجانية — يتم استبعاد مزوّدي وحسابات OpenCode ومفاتيح API المحفوظة، ويُسمح فقط بموديلات opencode/*-free المدمجة.")
                : Loc.T("Active mode: User OpenCode CLI config enabled — your OpenCode providers, logins, and saved Google key are available for the models you select.", "الوضع النشط: إعدادات OpenCode CLI الخاصة بك مفعّلة — تتاح مزوّداتك وحساباتك ومفتاح Google المحفوظ للموديلات التي تختارها.");
        }
        void SyncIsolation(bool strict)
        {
            if (syncingIsolation) return;
            syncingIsolation = true;
            freeOnly.IsChecked = strict;
            useUserConfig.IsChecked = !strict;
            syncingIsolation = false;
            UpdateIsolationNote();
            MarkDirty();
        }
        freeOnly.Checked += (_, _) => SyncIsolation(true);
        freeOnly.Unchecked += (_, _) => SyncIsolation(false);
        useUserConfig.Checked += (_, _) => SyncIsolation(false);
        useUserConfig.Unchecked += (_, _) => SyncIsolation(true);
        UpdateIsolationNote();

        var googleKey = new PasswordBox { FlowDirection = FlowDirection.LeftToRight, Tag = "GoogleApiKey" };
        googleKey.PasswordChanged += (_, _) => { if (!string.IsNullOrEmpty(googleKey.Password)) MarkDirty(); };
        var googleKeyStatus = Ui.Text("", 12, "#CCBCA3");
        googleKeyStatus.Tag = "GoogleKeyStatus";
        var googleKeyWarningBox = Notice(googleKeyStatus, warning: true);
        void RefreshGoogleKeyStatus()
        {
            bool corrupt = controller.Ai.HasCorruptGoogleSecret();
            googleKeyWarningBox.Visibility = corrupt ? Visibility.Visible : Visibility.Collapsed;
            googleKeyStatus.Visibility = corrupt ? Visibility.Visible : Visibility.Collapsed;
            googleKeyStatus.Text = corrupt
                ? Loc.T("The saved Google AI Studio API key could not be decrypted for this Windows user. Non-Google models still work; re-enter a new key or remove the saved key below to use Google models.", "تعذّر فك تشفير مفتاح Google AI Studio المحفوظ لهذا المستخدم. تستمر الموديلات غير التابعة لـ Google في العمل؛ أعد إدخال مفتاح جديد أو احذف المفتاح المحفوظ بالأسفل لاستخدام موديلات Google.")
                : "";
        }
        RefreshGoogleKeyStatus();

        var diagnostic = Ui.Text(
            Loc.T("NIVO only uses the models you select. Click Refresh models to load every model configured in your OpenCode CLI.", "يستخدم نيڤو فقط الموديلات التي تختارها. اضغط تحديث الموديلات لتحميل كل الموديلات المتاحة في OpenCode CLI."),
            12, "#A3BACC");
        diagnostic.Tag = "CatalogDiagnostic";
        var diagnosticBox = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 4, 0, 12),
            Child = new ScrollViewer
            {
                MaxHeight = 148,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = diagnostic
            }
        };
        diagnosticBox.SetResourceReference(Border.BackgroundProperty, "Input");
        diagnosticBox.SetResourceReference(Border.BorderBrushProperty, "Hairline");

        CancellationTokenSource? catalogCts = null;
        bool pageUnloaded = false;
        void CancelCatalogLoad()
        {
            var pending = catalogCts;
            catalogCts = null;
            if (pending == null) return;
            try { pending.Cancel(); }
            catch (ObjectDisposedException) { }
            finally { pending.Dispose(); }
        }

        async Task RefreshCatalogAsync(bool showErrorDialog = true)
        {
            if (pageUnloaded) return;
            CancelCatalogLoad();
            var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            catalogCts = timeout;
            diagnostic.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            diagnosticBox.SetResourceReference(Border.BorderBrushProperty, "Hairline");
            diagnostic.Text = Loc.T("Checking OpenCode CLI and loading models…", "جاري فحص OpenCode CLI وتحميل الموديلات…");
            try
            {
                var models = await controller.Ai.ModelsAsync(timeout.Token, cli.Text.Trim(), useUserConfig.IsChecked == true, freeOnly.IsChecked == true);
                if (pageUnloaded || !ReferenceEquals(catalogCts, timeout)) return;
                var currentModel = model.Text; var currentFallback = fallback.Text; var currentVision = vision.Text; var currentVisionFallback = visionFallback.Text;
                var list = new[] { currentModel, currentFallback, currentVision, currentVisionFallback }
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Concat(models)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                initializing = true;
                try
                {
                    model.ItemsSource = list; model.Text = currentModel;
                    vision.ItemsSource = list; vision.Text = currentVision;
                    fallback.ItemsSource = new[] { "" }.Concat(list).ToArray(); fallback.Text = currentFallback;
                    visionFallback.ItemsSource = new[] { "" }.Concat(list).ToArray(); visionFallback.Text = currentVisionFallback;
                }
                finally { initializing = false; }
                bool restrict = freeOnly.IsChecked == true || useUserConfig.IsChecked != true;
                RefreshGoogleKeyStatus();
                string secretWarning = !restrict && controller.Ai.HasCorruptGoogleSecret()
                    ? "\n\n" + Loc.T("Warning: The saved Google AI Studio API key could not be decrypted for this Windows user. Re-enter or remove it below before using Google models.", "تنبيه: تعذّر فك تشفير مفتاح Google AI Studio المحفوظ لهذا المستخدم. أعد إدخاله أو احذفه بالأسفل قبل استخدام موديلات Google.")
                    : "";
                diagnostic.SetResourceReference(TextBlock.ForegroundProperty, secretWarning.Length > 0 ? "Warning" : "Muted");
                diagnosticBox.SetResourceReference(Border.BorderBrushProperty, secretWarning.Length > 0 ? "Warning" : "Hairline");
                diagnostic.Text = Loc.T($"Found {models.Length} models in OpenCode CLI:\n", $"تم العثور على {models.Length} موديل في OpenCode CLI:\n") + string.Join("\n", models) + secretWarning;
            }
            catch (OperationCanceledException)
            {
                if (pageUnloaded || !ReferenceEquals(catalogCts, timeout)) return;
                diagnostic.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
                diagnosticBox.SetResourceReference(Border.BorderBrushProperty, "Warning");
                diagnostic.Text = Loc.T("Model catalog check timed out after 30 seconds. Check your OpenCode CLI path or click Refresh models to try again.", "انتهت مهلة فحص قائمة الموديلات بعد 30 ثانية. تحقق من مسار OpenCode CLI أو اضغط تحديث الموديلات للمحاولة مجددًا.");
                if (showErrorDialog) throw;
            }
            catch (Exception ex)
            {
                if (pageUnloaded || !ReferenceEquals(catalogCts, timeout)) return;
                System.Diagnostics.Trace.TraceWarning("NIVO OpenCode catalog load failed ({0}): {1}", ex.GetType().Name, ex.Message);
                diagnostic.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
                diagnosticBox.SetResourceReference(Border.BorderBrushProperty, "Warning");
                diagnostic.Text = Loc.T("Could not load OpenCode models: ", "تعذّر تحميل موديلات OpenCode: ") + ex.Message;
                if (showErrorDialog) throw;
            }
            finally
            {
                if (ReferenceEquals(catalogCts, timeout))
                {
                    catalogCts = null;
                    timeout.Dispose();
                }
            }
        }

        tabs["OpenCode AI"] = Ui.Stack(
            Section(
                Loc.T("Chat and image models", "موديلات المحادثة والصور"),
                Loc.T("NIVO selects a free OpenCode chat model on first use. Choose another model here or connect an account below.", "نيڤو بيختار موديل OpenCode مجاني للمحادثة في أول تشغيل. تقدر تغيّره هنا أو تربط حساب من القسم اللي تحت."),
                TwoCol(
                    Field(Loc.T("Chat & task planning", "المحادثة وتخطيط المهام"), model, Loc.T("Primary text model.", "الموديل الأساسي للنصوص.")),
                    Field(Loc.T("Text fallback (optional)", "البديل للنصوص (اختياري)"), fallback, Loc.T("Used only if primary hits quota / 429.", "يُستخدم فقط عند نفاد حصة الأساسي (429)."))),
                TwoCol(
                    Field(Loc.T("Image & screen vision", "الصور والشاشة"), vision, Loc.T("Select a model that accepts images.", "اختر موديلًا يدعم الصور.")),
                    Field(Loc.T("Vision fallback (optional)", "بديل الصور (اختياري)"), visionFallback, Loc.T("Dedicated image-capable backup only.", "بديل مخصص للصور فقط (لا يُستخدم بديل النص)."))),
                Notice(Ui.Text(Loc.T("Images go to the selected provider when you send a question or choose an image action. An optional confirmation is available in Desktop settings.", "تُرسل الصور للمزوّد المختار عند إرسال سؤالك أو اختيار إجراء للصورة. يمكنك تفعيل تأكيد اختياري من إعدادات سطح المكتب."), 12, "#9CB3C3"))),
            Section(
                Loc.T("OpenCode connection", "اتصال OpenCode"),
                Loc.T("Choose whether OpenCode can use your configured providers and logins, or stay strictly isolated to built-in free models.", "اختر ما إذا كان بإمكان OpenCode استخدام مزوّداتك وحساباتك، أو البقاء معزولًا على الموديلات المجانية المدمجة فقط."),
                useUserConfig,
                freeOnly,
                isolationNotice,
                Field(Loc.T("opencode.exe path", "مسار opencode.exe"), cli, Loc.T("Leave blank to auto-detect opencode.exe.", "اتركه فارغًا للاكتشاف التلقائي لـ opencode.exe.")),
                ActionRow(
                    Ui.Btn(Loc.T("Choose executable", "اختيار الملف"), () => { var path = Ui.PickFile("OpenCode executable|opencode.exe"); if (path != null) cli.Text = path; }),
                    Ui.AsyncBtn(Loc.T("Refresh models & check CLI", "تحديث الموديلات وفحص CLI"), () => RefreshCatalogAsync(showErrorDialog: true))),
                diagnosticBox),
            AccountSettingsPanel(model, vision, () => SyncIsolation(false), MarkDirty, accountPaths),
            Section(
                Loc.T("Gemini · Google AI Studio", "Gemini · Google AI Studio"),
                Loc.T("Optional key passed only to OpenCode when user providers are enabled. Google account quotas and billing determine Free Tier eligibility.", "مفتاح اختياري يُمرّر إلى OpenCode فقط عند تفعيل مزوّدات المستخدم. الحصة المجانية والتكلفة حسب حساب Google."),
                Field(Loc.T("Google AI Studio API key", "مفتاح Google AI Studio API"), googleKey, Loc.T("Leave blank to keep the saved key. Save preferences before refreshing models.", "اتركه فارغًا للاحتفاظ بالمفتاح المحفوظ. احفظ الإعدادات قبل تحديث الموديلات.")),
                googleKeyWarningBox,
                Notice(Ui.Text(Loc.T("Stored with Windows DPAPI user encryption on this PC and passed only to OpenCode CLI.", "يُحفظ بتشفير ويندوز DPAPI لهذا المستخدم على هذا الجهاز ويُمرّر إلى OpenCode CLI فقط."), 12, "#9CB3C3")),
                ActionRow(
                    Ui.Btn(Loc.T("Use Gemini for text & images", "استخدام Gemini للنص والصور"), () => {
                        model.Text = vision.Text = "google/gemini-3.5-flash-lite";
                        fallback.Text = visionFallback.Text = "";
                        SyncIsolation(false);
                    }),
                    Ui.Btn(Loc.T("Remove saved Google key", "حذف مفتاح Google المحفوظ"), () => {
                        controller.Store.SetSecret("google", "");
                        googleKey.Clear();
                        controller.Ai.ClearCooldowns();
                        RefreshGoogleKeyStatus();
                        saveStatus.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                        saveStatus.Text = Loc.T("Saved Google key removed.", "تم حذف مفتاح Google المحفوظ.");
                    }))));

        // Tab 3: Shortcuts
        var shortcuts = new Dictionary<string, TextBox>();
        var shortcutDefs = new[]
        {
            ("ask", Loc.T("Silent Ask", "السؤال الصامت"), Loc.T("Opens the compact Ask bar from anywhere.", "يفتح شريط السؤال الصامت من أي مكان.")),
            ("island", Loc.T("Toggle island", "إظهار / إخفاء الجزيرة"), Loc.T("Shows or hides the top Living Notch.", "يُظهر أو يخفي الجزيرة الحيّة بالأعلى.")),
            ("incognito", Loc.T("Incognito", "الوضع الخفي"), Loc.T("Starts a private session without saving chats or memories.", "يبدأ جلسة خاصة دون حفظ المحادثات أو الذكريات.")),
            ("crop", Loc.T("Screen crop", "قصّ جزء من الشاشة"), Loc.T("Selects a region of your screen to ask about.", "يحدد جزءًا من الشاشة للسؤال عنه."))
        };
        var shortcutFields = new List<UIElement>();
        foreach (var (key, title, hint) in shortcutDefs)
        {
            var field = new TextBox
            {
                IsReadOnly = true,
                FlowDirection = FlowDirection.LeftToRight,
                Text = s.Shortcuts.GetValueOrDefault(key, new Settings().Shortcuts[key]),
                ToolTip = Loc.T("Click, then press your shortcut", "اضغط هنا ثم اكتب الاختصار")
            };
            field.GotKeyboardFocus += (_, _) => ((App)Application.Current).SuspendHotkeys();
            field.LostKeyboardFocus += (_, _) => ((App)Application.Current).ConfigureHotkeys();
            field.PreviewKeyDown += (_, e) => {
                var pressed = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
                var modifiers = System.Windows.Input.Keyboard.Modifiers;
                if (pressed == System.Windows.Input.Key.Tab && modifiers == System.Windows.Input.ModifierKeys.None) return;
                e.Handled = true;
                if (pressed is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift or System.Windows.Input.Key.Escape) return;
                if ((modifiers & (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt)) == 0) return;
                try
                {
                    var gesture = new System.Windows.Input.KeyGesture(pressed, modifiers);
                    field.Text = new System.Windows.Input.KeyGestureConverter().ConvertToInvariantString(gesture) ?? field.Text;
                    MarkDirty();
                }
                catch (ArgumentException) { }
            };
            shortcuts[key] = field;
            shortcutFields.Add(Field(title, field, hint));
        }
        tabs["Shortcuts"] = Section(
            Loc.T("Global keyboard shortcuts", "اختصارات لوحة المفاتيح العامة"),
            Loc.T("Click a field and press a combination with Ctrl or Alt. Press Tab to move between fields. Each action requires a unique shortcut.", "اضغط على أي خانة ثم اضغط الاختصار المطلوب مع Ctrl أو Alt. استخدم Tab للتنقل بين الخانات، ولكل إجراء اختصار مختلف."),
            TwoCol(shortcutFields[0], shortcutFields[1]),
            TwoCol(shortcutFields[2], shortcutFields[3]));

        // Tab 4: Desktop
        bool startupInitiallyEnabled = StartupService.IsEnabled;
        var startWithWindows = OptionToggle(
            Loc.T("Start NIVO with Windows", "تشغيل نيڤو مع Windows"),
            Loc.T("Open NIVO automatically when you sign in. Keep the executable in a permanent folder; turn this off to stop automatic startup.", "يفتح نيڤو تلقائيًا عند تسجيل الدخول. احتفظ بالبرنامج في مجلد ثابت؛ أوقف الخيار لإلغاء التشغيل التلقائي."),
            startupInitiallyEnabled);
        startWithWindows.Tag = "StartWithWindows";

        var computerAccess = OptionToggle(
            Loc.T("Computer access · Windows commands", "الوصول للجهاز · أوامر Windows"),
            Loc.T("Let normal chat plan PowerShell actions on files, apps and settings. Every command is reviewed before it runs; results go to your AI model. Uses this process's Windows rights. Run NIVO as Administrator yourself when required. Incognito does not undo command changes.", "يسمح للمحادثة بتجهيز أوامر PowerShell للملفات والبرامج والإعدادات. تُراجع الأوامر قبل التنفيذ وتُرسل نتائجها للموديل. يستخدم صلاحيات البرنامج الحالية؛ شغّل نيڤو كمسؤول عند الحاجة. الوضع الخفي لا يتراجع عن تغييرات الأوامر."),
            s.ComputerAccessEnabled);
        computerAccess.Tag = "ComputerAccessEnabled";
        var dock = OptionToggle(
            Loc.T("Show NIVO Floating Glass Dock", "إظهار شريط الكبسولات الزجاجي العائم (Dock)"),
            Loc.T("Displays pinned apps, running windows, focus controls, and quick actions.", "يعرض التطبيقات المثبّتة والنوافذ المفتوحة وأدوات التركيز والوصول السريع."),
            s.DesktopEnabled);
        var replace = OptionToggle(
            Loc.T("Hide Windows taskbar while the dock is active", "إخفاء شريط مهام ويندوز أثناء عمل الشريط"),
            Loc.T("Automatically restores the Windows taskbar whenever NIVO exits or the dock is turned off.", "يُعيد شريط مهام ويندوز تلقائيًا عند إغلاق نيڤو أو إيقاف الشريط."),
            s.ReplaceTaskbar);
        var focus = OptionToggle(
            Loc.T("Focus Mode", "وضع التركيز"),
            Loc.T("Keeps pinned apps and only the running apps allowed by your selected Focus profile.", "يبقي التطبيقات المثبّتة والتطبيقات المفتوحة المسموح بها في ملف التركيز فقط."),
            s.Focus);
        string normalizedFocusProfile = FocusPolicy.Normalize(s.FocusProfile ?? "");
        var focusProfile = new ComboBox { Tag = "FocusProfile" };
        foreach (var (profileKey, profileLabel) in new[] {
            ("General", Loc.T("General", "عام")),
            ("Study", Loc.T("Study", "الدراسة")),
            ("Coding", Loc.T("Coding", "البرمجة")),
            ("Deep Work", Loc.T("Deep Work", "العمل العميق"))
        })
        {
            var item = new ComboBoxItem { Content = profileLabel, Tag = profileKey };
            focusProfile.Items.Add(item);
            if (string.Equals(profileKey, normalizedFocusProfile, StringComparison.Ordinal)) focusProfile.SelectedItem = item;
        }
        if (focusProfile.SelectedIndex < 0) focusProfile.SelectedIndex = 0;
        focusProfile.SelectionChanged += (_, _) => MarkDirty();
        var gaming = OptionToggle(
            Loc.T("Gaming Mode", "وضع الألعاب"),
            Loc.T("Hides Dock and Island and pauses suggestions. Fullscreen apps are detected automatically.", "يخفي الدوك والجزيرة ويوقف الاقتراحات. يتم اكتشاف برامج الفول سكرين تلقائيًا."),
            s.Gaming);
        var edgeGlow = OptionToggle(
            Loc.T("Screen-edge glow while agent tasks work", "توهج حواف الشاشة أثناء عمل الوكيل"),
            Loc.T("Click-through visual indicator while tasks run; automatically off with reduced motion.", "مؤشر بصري لا يعيق النقر أثناء تنفيذ المهام؛ يتوقف تلقائيًا مع تقليل الحركة."),
            s.EdgeGlow);

        var confirmImageSharing = OptionToggle(
            Loc.T("Confirm before sending images", "تأكيد قبل إرسال الصور"),
            Loc.T("Optional extra prompt. Leave off to send captures directly with your question or image action.", "تأكيد إضافي اختياري. اتركه مغلقًا لإرسال اللقطة مباشرة مع سؤالك أو الإجراء المختار."),
            s.ConfirmImageSharing, "ConfirmImageSharing");
        var hover = OptionToggle(
            Loc.T("Hover capture", "أشّر واسأل"),
            Loc.T("Captures a bounded area around the pointer instead of the full screen when triggered.", "يلتقط مساحة محدودة حول مؤشر الفأرة بدل كامل الشاشة عند تشغيل الالتقاط."),
            s.Hover);
        var proactive = OptionToggle(
            Loc.T("Proactive help", "المساعدة الاستباقية"),
            Loc.T("Checks the active window title on the timer below to offer quiet local check-ins or help.", "يفحص عنوان النافذة النشطة حسب المؤقت أدناه لتقديم متابعة هادئة أو مساعدة."),
            s.Proactive);
        var errorAwareness = OptionToggle(
            Loc.T("Repeated-error awareness (opt-in)", "اكتشاف الأخطاء المتكررة (اختياري)"),
            Loc.T("Sends bounded visible error text via Windows UI Automation through your selected OpenCode model (no screenshots, no mic).", "يرسل نص الخطأ الظاهر والمحدود عبر Windows UI Automation إلى موديل OpenCode المختار (بدون لقطات شاشة أو ميكروفون)."),
            s.ErrorAwareness,
            "ErrorAwareness");

        var intervals = new[]
        {
            (Seconds: 120, Label: Loc.T("Low · 2 min", "منخفض · دقيقتان")),
            (Seconds: 60, Label: Loc.T("Medium · 60 s", "متوسط · 60 ثانية")),
            (Seconds: 30, Label: Loc.T("High · 30 s", "عالٍ · 30 ثانية"))
        };
        int seconds = s.ScanSeconds;
        var interval = new WrapPanel { Margin = new Thickness(0, 6, 0, 8) };
        var frequencyButtons = new Dictionary<int, Button>();
        void UpdateFrequencyButtons()
        {
            foreach (var pair in frequencyButtons)
            {
                bool active = pair.Key == seconds;
                pair.Value.SetResourceReference(Control.BackgroundProperty, active ? "Selected" : "Panel");
                pair.Value.SetResourceReference(Control.BorderBrushProperty, active ? "Accent" : "Hairline");
                pair.Value.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
        foreach (var item in intervals)
        {
            var chosen = item.Seconds;
            var b = Ui.Btn(item.Label, () => { seconds = chosen; UpdateFrequencyButtons(); MarkDirty(); });
            b.Padding = new Thickness(13, 8, 13, 8);
            interval.Children.Add(b);
            frequencyButtons[chosen] = b;
        }
        UpdateFrequencyButtons();

        var distractions = new TextBox { Text = string.Join(", ", s.DistractionKeywords) };
        distractions.TextChanged += (_, _) => MarkDirty();
        var excluded = new TextBox { Text = string.Join(", ", s.ExcludedApps) };
        excluded.TextChanged += (_, _) => MarkDirty();

        tabs["Desktop"] = Ui.Stack(
            Section(Loc.T("Startup", "بدء التشغيل"), Loc.T("Choose when NIVO opens.", "اختار متى يفتح نيڤو."), startWithWindows),
            Section(Loc.T("Computer access", "الوصول للجهاز"), Loc.T("Execute reviewed Windows actions directly from your conversation.", "نفّذ إجراءات Windows بعد مراجعتها من المحادثة مباشرة."), computerAccess,
                Notice(Ui.Text(ComputerCommandService.IsAdministrator() ? Loc.T("NIVO is running as Administrator.", "نيڤو يعمل بصلاحيات Administrator.") : Loc.T("NIVO is running with standard user permissions. Windows access checks still apply.", "نيڤو يعمل بصلاحيات المستخدم العادي. قيود Windows ما زالت مطبّقة."), 12, "#9CB3C3"))),
            Section(
                Loc.T("Floating dock & Focus Mode", "الشريط العائم ووضع التركيز"),
                Loc.T("Control the dock, taskbar visibility, and which unpinned apps stay visible during Focus Mode.", "تحكم في الشريط العائم وشريط المهام والتطبيقات المفتوحة الظاهرة أثناء وضع التركيز."),
                dock,
                replace,
                focus,
                Field(
                    Loc.T("Focus profile", "ملف التركيز"),
                    focusProfile,
                    Loc.T("Controls which unpinned running apps stay visible on the dock in Focus Mode.", "يحدد التطبيقات المفتوحة غير المثبّتة التي تبقى ظاهرة في الشريط أثناء وضع التركيز.")),
                gaming,
                edgeGlow),
            Section(
                Loc.T("Capture, awareness & privacy boundaries", "الالتقاط والوعي وحدود الخصوصية"),
                Loc.T("Everything below is opt-in and bounded. No automatic screenshots or microphone access.", "كل الخيارات أدناه اختيارية ومحدودة. لا توجد لقطات شاشة تلقائية ولا وصول للميكروفون."),
                hover,
                confirmImageSharing,
                proactive,
                Field(Loc.T("Proactive check frequency", "تكرار الفحص الاستباقي"), interval),
                errorAwareness,
                TwoCol(
                    Field(Loc.T("Local check-in distraction keywords", "كلمات المشتتات للمتابعة السريعة"), distractions, Loc.T("Comma separated window title keywords.", "كلمات من عناوين النوافذ مفصولة بفاصلة.")),
                    Field(Loc.T("Excluded apps & titles", "التطبيقات والعناوين المستثناة"), excluded, Loc.T("Never inspected or captured (comma separated).", "لا تُفحص ولا تُلتقط أبدًا (مفصولة بفاصلة)."))),
                Notice(Ui.Text(Loc.T("No automatic screenshots or microphone access. Error awareness is off by default and skips passwords, NIVO, games, Incognito, and excluded apps. Reminders still run.", "لا تُؤخذ أي لقطات شاشة تلقائيًا ولا يُستخدم الميكروفون. اكتشاف الأخطاء مغلق افتراضيًا ويستثني كلمات المرور ونيڤو والألعاب والوضع الخفي والتطبيقات المستثناة. التذكيرات تستمر بالعمل."), 12, "#9BB2C3"))));

        // Tab 5: Voice
        var voiceAccountProvider = new ComboBox { Tag = "VoiceAccountProvider", FlowDirection = Loc.Flow };
        foreach (var item in new[] { ("Auto", Loc.T("Auto · most recently connected account", "تلقائي · آخر حساب تم ربطه")), ("codex", "Codex"), ("antigravity", "Antigravity") })
            voiceAccountProvider.Items.Add(new ComboBoxItem { Tag = item.Item1, Content = item.Item2 });
        voiceAccountProvider.SelectedItem = voiceAccountProvider.Items.Cast<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, s.VoiceAccountProvider, StringComparison.OrdinalIgnoreCase)) ?? voiceAccountProvider.Items[0];
        voiceAccountProvider.SelectionChanged += (_, _) => MarkDirty();
        var matchLanguage = OptionToggle(Loc.T("Match my spoken language", "الرد بنفس اللغة اللي بتكلم بيها"),
            Loc.T("Switch reply language as you speak. Turn off to prefer the interface language. Applies to the next Talk session.", "لو غيّرت لغتك وسط الكلام، الرد يتغيّر معاك. اقفل الاختيار لو تفضّل لغة الواجهة. التغيير يبدأ مع مكالمة Talk الجاية."),
            s.MatchSpokenLanguage, "MatchSpokenLanguage");
        var accountVoice = OptionToggle(
            Loc.T("Talk with my connected account (experimental)", "Talk بصوت حسابي المتصل (تجريبي)"),
            Loc.T("Uses the connected account’s native voice inside NIVO. Press Talk to connect, then press it again to stop. Account voice availability applies. No separate API fallback.", "يستخدم صوت الحساب المتصل الأصلي داخل نيڤو. اضغط Talk للاتصال، ثم اضغطه مجددًا للإيقاف. يتطلب إتاحة الصوت للحساب. لا يتحوّل إلى API منفصل تلقائيًا."),
            s.UseAccountVoice, "UseAccountVoice");
        var enabled = OptionToggle(
            Loc.T("Enable voice using my configured APIs", "تفعيل الصوت باستخدام روابط API الخاصة بي"),
            Loc.T("Off until you configure at least one speech endpoint. No microphone access or voice charges while disabled.", "مغلق افتراضيًا حتى تضيف رابط خدمة صوت واحدًا على الأقل. لا يتم الوصول للميكروفون أثناء التعطيل."),
            s.VoiceEnabled);
        var tts = new TextBox { Text = s.TtsEndpoint, FlowDirection = FlowDirection.LeftToRight };
        var stt = new TextBox { Text = s.SttEndpoint, FlowDirection = FlowDirection.LeftToRight };
        var ttsModel = new TextBox { Text = s.TtsModel, FlowDirection = FlowDirection.LeftToRight };
        var sttModel = new TextBox { Text = s.SttModel, FlowDirection = FlowDirection.LeftToRight };
        var voice = new TextBox { Text = s.Voice, FlowDirection = FlowDirection.LeftToRight };
        var ttsKey = new PasswordBox { FlowDirection = FlowDirection.LeftToRight };
        var sttKey = new PasswordBox { FlowDirection = FlowDirection.LeftToRight };
        foreach (var box in new[] { tts, stt, ttsModel, sttModel, voice }) box.TextChanged += (_, _) => MarkDirty();
        ttsKey.PasswordChanged += (_, _) => { if (!string.IsNullOrEmpty(ttsKey.Password)) MarkDirty(); };
        sttKey.PasswordChanged += (_, _) => { if (!string.IsNullOrEmpty(sttKey.Password)) MarkDirty(); };

        tabs["Voice"] = Ui.Stack(
            Section(
                Loc.T("Native account Talk", "Talk بصوت الحساب الأصلي"),
                Loc.T("Voice starts from this app after the provider confirms the session. Antigravity runs privately in the background; its transcript is not mirrored into NIVO chat and it is unavailable in Incognito. Account quotas apply. Stop with Talk again or Escape. Computer actions use the normal reviewed text task flow.", "يبدأ الصوت من داخل البرنامج بعد تأكيد المزوّد للجلسة. يعمل Antigravity في الخلفية؛ نص محادثته لا يُنسخ إلى شات نيڤو وهو غير متاح في الوضع الخفي. تُطبّق حصة الحساب. للإيقاف اضغط Talk مجددًا أو Escape. أوامر الجهاز تُنفّذ من مسار المهام النصية المعتاد بمراجعة الخطة."),
                accountVoice, matchLanguage,
                Field(Loc.T("Voice account", "حساب الصوت"), voiceAccountProvider,
                    Loc.T("Auto checks the last account you connected, then the other connected account. This is separate from the saved text-model preference.", "التلقائي يفحص آخر حساب ربطته ثم الحساب الآخر المتاح. الاختيار منفصل عن تفضيل موديل المحادثة المحفوظ.")),
                Ui.InterruptibleAsyncBtn(Loc.T("Start / stop Talk", "بدء / إيقاف Talk"), async () => await ((App)Application.Current).ToggleAccountTalkAsync())),
            Section(
                Loc.T("Voice · bring your own API", "الصوت · أضف الـ API الخاص بك"),
                Loc.T("Push-to-talk speech using your own HTTPS or local loopback speech endpoints.", "صوت يعمل بالضغط للتحدث باستخدام روابط HTTPS أو خادم صوت محلي خاص بك."),
                enabled,
                Notice(Ui.Text(Loc.T("Voice is push-to-talk. API keys use Windows DPAPI user encryption.", "الصوت يعمل بالضغط للتحدث، وتُحفظ المفاتيح مشفرة بتشفير ويندوز للمستخدم."), 12, "#CCBCA3"))),
            Section(
                Loc.T("Text-to-Speech (TTS)", "التحويل إلى صوت (TTS)"),
                Loc.T("Optional spoken replies for assistant answers.", "قراءة ردود المساعد بصوت اختياري."),
                Field(Loc.T("TTS endpoint", "رابط TTS"), tts, Loc.T("Complete HTTPS URL (or localhost adapter).", "رابط HTTPS كامل أو خادم محلي.")),
                TwoCol(
                    Field(Loc.T("TTS model", "موديل الصوت TTS"), ttsModel),
                    Field(Loc.T("Voice ID", "معرّف الصوت"), voice)),
                Field(Loc.T("TTS API key", "مفتاح TTS"), ttsKey, Loc.T("Leave blank to keep the saved key.", "اتركه فارغًا للاحتفاظ بالمفتاح المحفوظ."))),
            Section(
                Loc.T("Speech-to-Text (STT) & saved keys", "تحويل الكلام إلى نص (STT) والمفاتيح"),
                Loc.T("Optional dictation input when you hold push-to-talk.", "إدخال صوتي اختياري عند الضغط للتحدث."),
                Field(Loc.T("STT endpoint", "رابط STT"), stt, Loc.T("Complete HTTPS URL (or localhost adapter).", "رابط HTTPS كامل أو خادم محلي.")),
                TwoCol(
                    Field(Loc.T("STT model", "موديل STT"), sttModel),
                    Field(Loc.T("STT API key", "مفتاح STT"), sttKey, Loc.T("Leave blank to keep the saved key.", "اتركه فارغًا للاحتفاظ بالمفتاح المحفوظ."))),
                ActionRow(Ui.Btn(Loc.T("Remove saved voice keys", "حذف مفاتيح الصوت المحفوظة"), () => {
                    if (Ui.Confirm(Loc.T("Remove both saved voice API keys?", "حذف مفاتيح الصوت المحفوظة؟")))
                    {
                        controller.Store.SetSecret("tts", "");
                        controller.Store.SetSecret("stt", "");
                        ttsKey.Clear();
                        sttKey.Clear();
                        saveStatus.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                        saveStatus.Text = Loc.T("Saved voice keys removed.", "تم حذف مفاتيح الصوت المحفوظة.");
                    }
                }))));

        // Tab 6: About & Data
        var version = typeof(App).Assembly.GetName().Version;
        var dataPathText = Ui.Text(controller.Store.Root, 12, "#9CB3C3");
        dataPathText.FlowDirection = FlowDirection.LeftToRight;
        var shortcutSummary = Ui.Text(string.Join("\n", s.Shortcuts.Select(k => k.Key + "  ·  " + k.Value)), 12, "#9CB3C3");
        shortcutSummary.FlowDirection = FlowDirection.LeftToRight;
        tabs["About"] = Ui.Stack(
            Section(
                Loc.T("Local data & privacy", "البيانات المحلية والخصوصية"),
                Loc.T("Conversations, memories, tasks, and routines are stored locally in your Windows user folder.", "تُحفظ المحادثات والذكريات والمهام والروتين محليًا في مجلد المستخدم على جهازك."),
                Notice(dataPathText),
                Notice(Ui.Text(Loc.T("App conversations, memories, and tasks live locally on your PC. Incognito disables NIVO's saved conversation and memory writes.", "محادثاتك وذكرياتك ومهامك محفوظة محليًا على جهازك فقط. الوضع الخفي يوقف حفظ المحادثات والذكريات."), 12, "#CCBCA3")),
                ActionRow(
                    Ui.Btn(Loc.T("Open data folder", "فتح مجلد البيانات"), () => DesktopService.OpenTarget(controller.Store.Root)),
                    Ui.Btn(Loc.T("Export my data", "تصدير بياناتي"), ExportData))),
            Section(
                Loc.T("About NIVO", "عن نيڤو"),
                Loc.T($"Version {version?.Major}.{version?.Minor}.{version?.Build} · Native Windows companion", $"الإصدار {version?.Major}.{version?.Minor}.{version?.Build} · مساعد ويندوز محلي"),
                Ui.Text(Loc.T("OpenCode CLI models · Floating Glass Dock · Living Notch · Bilingual AR/EN", "موديلات OpenCode CLI · الشريط الزجاجي العائم · الجزيرة الحيّة · عربي / إنجليزي"), 13, "#9CB3C3"),
                new Border { Height = 8 },
                Ui.Text(Loc.T("Saved shortcuts", "الاختصارات المحفوظة"), 13, weight: FontWeights.SemiBold),
                Notice(shortcutSummary)));

        bool catalogLoaded = false;
        string activeTabName = initialTab;
        void MaybeAutoLoadCatalog()
        {
            if (AccountConnections.IsAccountModel(s.Model)) return;
            if (activeTabName != "OpenCode AI" || catalogLoaded || pageUnloaded || !shell.IsLoaded || Left <= -10000) return;
            catalogLoaded = true;
            _ = RefreshCatalogAsync(showErrorDialog: false);
        }
        void SelectTab(string name, bool allowAutoLoad = true)
        {
            if (!tabs.ContainsKey(name)) name = "Personalization";
            bool changed = activeTabName != name;
            activeTabName = name;
            activeTabContent.Content = tabs[name];
            if (changed) Motion.Enter(tabs[name], 6);
            foreach (var pair in tabButtons)
            {
                bool active = pair.Key == name;
                pair.Value.SetResourceReference(Control.BackgroundProperty, active ? "ActiveGlass" : "Panel");
                pair.Value.SetResourceReference(Control.ForegroundProperty, active ? "OnAccent" : "Ink");
                pair.Value.SetResourceReference(Control.BorderBrushProperty, active ? "Accent" : "Hairline");
                pair.Value.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            }
            if (allowAutoLoad) MaybeAutoLoadCatalog();
        }
        shell.Loaded += (_, _) => { pageUnloaded = false; MaybeAutoLoadCatalog(); };
        shell.Unloaded += (_, _) => { pageUnloaded = true; CancelCatalogLoad(); };

        foreach (var tabName in tabs.Keys)
        {
            var key = tabName;
            var btn = Ui.Btn(tabLabels[key], () => SelectTab(key));
            btn.Tag = key;
            btn.SetResourceReference(StyleProperty, "PillButton");
            btn.Padding = new Thickness(14, 7, 14, 7);
            btn.FontSize = 13;
            btn.Margin = new Thickness(0, 0, 6, 6);
            tabBar.Children.Add(btn);
            tabButtons[key] = btn;
        }
        SelectTab(initialTab, allowAutoLoad: false);
        initializing = false;

        var top = Ui.Stack(header, tabBar);
        DockPanel.SetDock(top, Dock.Top);
        shell.Children.Add(top);

        var revertBtn = Ui.Btn(Loc.T("Revert", "تراجع"), () => {
            Ui.ApplyTheme(s.Theme);
            Navigate("Settings", activeTabName);
        });
        revertBtn.Tag = "RevertPreferences";
        revertBtn.Margin = new Thickness(0, 0, 8, 0);
        revertBtn.Padding = new Thickness(14, 9, 14, 9);

        var saveBtn = Ui.Btn(Loc.T("Save preferences", "حفظ الإعدادات"), () => {
            if (controller.Busy && s.ComputerAccessEnabled && computerAccess.IsChecked != true)
            {
                s.ComputerAccessEnabled = false; controller.Stop(); controller.Store.Save();
                saveStatus.Text = Loc.T("Computer access disabled; current work is stopping. Save other changes after it stops.", "تم تعطيل الوصول للجهاز وطلب إيقاف العمل الحالي. احفظ باقي التغييرات بعد توقفه.");
                return;
            }
            if (controller.Busy || ((App)Application.Current).AccountTalkActive) throw new InvalidOperationException(Loc.T("Finish the current request or stop Talk before changing models or settings.", "أكمل الطلب الحالي أو أوقف Talk قبل تغيير الإعدادات."));
            if (!string.IsNullOrWhiteSpace(tts.Text)) VoiceService.ValidateEndpoint(tts.Text);
            if (!string.IsNullOrWhiteSpace(stt.Text)) VoiceService.ValidateEndpoint(stt.Text);
            if (enabled.IsChecked == true && string.IsNullOrWhiteSpace(tts.Text) && string.IsNullOrWhiteSpace(stt.Text)) throw new InvalidDataException(Loc.T("Configure at least one speech endpoint before enabling voice.", "أضف رابط خدمة صوت واحدًا على الأقل قبل التفعيل."));
            var parsedShortcuts = shortcuts.Values.Select(f => DesktopService.Hotkeys.Parse(f.Text)).ToArray();
            if (parsedShortcuts.Distinct().Count() != parsedShortcuts.Length) throw new InvalidDataException(Loc.T("Use a different shortcut for each action.", "استخدم اختصارًا مختلفًا لكل إجراء."));
            var nextModel = model.Text.Trim(); var nextFallback = fallback.Text.Trim(); var nextVision = vision.Text.Trim(); var nextVisionFallback = visionFallback.Text.Trim();
            var validation = new Settings { UseUserOpenCodeConfig = useUserConfig.IsChecked == true, FreeModelsOnly = freeOnly.IsChecked == true };
            OpenCodeService.ValidateModel(nextModel, validation);
            OpenCodeService.ValidateModel(nextVision, validation);
            if (!string.IsNullOrWhiteSpace(nextVisionFallback)) OpenCodeService.ValidateModel(nextVisionFallback, validation);
            if (!string.IsNullOrWhiteSpace(nextFallback)) OpenCodeService.ValidateModel(nextFallback, validation);
            if (!string.IsNullOrWhiteSpace(accountPaths["codex"].Text)) CodexAccountService.FindExecutable(accountPaths["codex"].Text);
            if (!string.IsNullOrWhiteSpace(accountPaths["antigravity"].Text)) AntigravityAccountService.FindExecutable(accountPaths["antigravity"].Text);
            bool startupRequested = startWithWindows.IsChecked == true;
            if (startupRequested != startupInitiallyEnabled)
            {
                StartupService.SetEnabled(startupRequested);
                startupInitiallyEnabled = startupRequested;
            }
            foreach (var shortcut in shortcuts) s.Shortcuts[shortcut.Key] = shortcut.Value.Text;
            s.FollowWindowsAnimations = followWindowsAnimations.IsChecked == true; Motion.FollowWindows = s.FollowWindowsAnimations;
            s.ReduceMotion = reduced.IsChecked == true; Motion.Reduced = s.ReduceMotion; s.Theme = (string)theme.SelectedItem; s.Persona = (string)persona.SelectedItem;
            string nextLang = (string)language.SelectedItem;
            bool langChanged = !string.Equals(s.Language, nextLang, StringComparison.OrdinalIgnoreCase);
            s.Language = nextLang;
            s.UseUserOpenCodeConfig = validation.UseUserOpenCodeConfig; s.FreeModelsOnly = validation.FreeModelsOnly;
            if (s.Model != nextModel) s.PendingModelDiscovery = false;
            s.Model = nextModel; s.FallbackModel = nextFallback; s.VisionModel = nextVision; s.VisionFallbackModel = nextVisionFallback; s.CliPath = cli.Text.Trim();
            s.CodexCliPath = accountPaths["codex"].Text.Trim(); s.AntigravityCliPath = accountPaths["antigravity"].Text.Trim();
            bool accessWasEnabled = s.ComputerAccessEnabled; s.ComputerAccessEnabled = computerAccess.IsChecked == true;
            if (accessWasEnabled && !s.ComputerAccessEnabled) controller.Stop();
            s.DesktopEnabled = dock.IsChecked == true; s.ReplaceTaskbar = replace.IsChecked == true; s.Focus = focus.IsChecked == true; s.FocusProfile = (focusProfile.SelectedItem as ComboBoxItem)?.Tag as string ?? "General"; s.Gaming = gaming.IsChecked == true; s.Proactive = proactive.IsChecked == true; s.ErrorAwareness = errorAwareness.IsChecked == true; s.ScanSeconds = seconds; s.EdgeGlow = edgeGlow.IsChecked == true; s.DistractionKeywords = distractions.Text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(k => k.Trim()).Where(k => k.Length > 0).ToList();
            s.Hover = hover.IsChecked == true; s.ConfirmImageSharing = confirmImageSharing.IsChecked == true;
            s.ExcludedApps = excluded.Text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToLowerInvariant()).ToList();
            s.MatchSpokenLanguage = matchLanguage.IsChecked == true;
            s.UseAccountVoice = accountVoice.IsChecked == true;
            s.VoiceAccountProvider = (voiceAccountProvider.SelectedItem as ComboBoxItem)?.Tag as string ?? "Auto";
            s.VoiceEnabled = enabled.IsChecked == true; s.TtsEndpoint = tts.Text.Trim(); s.SttEndpoint = stt.Text.Trim(); s.TtsModel = ttsModel.Text.Trim(); s.SttModel = sttModel.Text.Trim(); s.Voice = voice.Text.Trim();
            if (!string.IsNullOrWhiteSpace(ttsKey.Password)) { controller.Store.SetSecret("tts", ttsKey.Password); ttsKey.Clear(); }
            if (!string.IsNullOrWhiteSpace(sttKey.Password)) { controller.Store.SetSecret("stt", sttKey.Password); sttKey.Clear(); }
            if (!string.IsNullOrWhiteSpace(googleKey.Password)) { controller.Store.SetSecret("google", googleKey.Password.Trim()); googleKey.Clear(); }
            controller.Store.Save(); controller.Ai.ClearCooldowns(); RefreshGoogleKeyStatus(); Ui.ApplyTheme(s.Theme); ((App)Application.Current).RefreshShell(); ((App)Application.Current).ConfigureHotkeys();
            if (langChanged) ((App)Application.Current).ApplyLanguage(nextLang, false);
            else
            {
                controller.Notify();
                saveStatus.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
                saveStatus.Text = Loc.T("Preferences saved ✓", "تم حفظ الإعدادات ✓");
                controller.SetStatus(Loc.T("Preferences saved", "تم حفظ الإعدادات"));
            }
        }, true);
        saveBtn.Tag = "SavePreferences"; saveBtn.Margin = new Thickness(0);

        var saveActions = Ui.Row(revertBtn, saveBtn);
        saveActions.VerticalAlignment = VerticalAlignment.Center;
        var saveBarGrid = new Grid();
        saveBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        saveBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        saveBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        saveBarGrid.Children.Add(saveStatus);
        Grid.SetColumn(saveActions, 2);
        saveBarGrid.Children.Add(saveActions);
        var saveBar = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 0), Margin = new Thickness(0, 8, 0, 0), Child = saveBarGrid };
        saveBar.SetResourceReference(Border.BorderBrushProperty, "Hairline");
        DockPanel.SetDock(saveBar, Dock.Bottom);
        shell.Children.Add(saveBar);

        shell.Children.Add(new ScrollViewer { Content = contentHost, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        return shell;
    }
}
