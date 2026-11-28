using Nivo.Core;
using Nivo.Services;

namespace Nivo.UI;

public sealed partial class MainWindow
{
    private FrameworkElement AccountSettingsPanel(ComboBox chat, ComboBox vision, Action enableAccounts, Action markDirty, Dictionary<string, TextBox> accountPaths)
    {
        var panel = Ui.Stack();
        panel.Children.Add(Ui.Text(Loc.T("Connected accounts", "الحسابات المتصلة"), 20, weight: FontWeights.SemiBold));
        panel.Children.Add(Ui.Text(Loc.T(
            "Sign in in your browser. NIVO keeps a separate provider session; existing T3 Code logins are not copied. Account changes apply immediately; save preferences after choosing a model.",
            "سجّل الدخول من المتصفح. نيڤو يحتفظ بجلسة مستقلة ولا ينسخ حسابات T3 Code. ربط وفصل الحساب فوري؛ احفظ الإعدادات بعد اختيار الموديل."), 12));
        panel.Children.Add(Ui.Text(Loc.T(
            "Account models handle conversation and reported image input. Talk uses native voice from a connected account inside NIVO, with automatic start and stop. Catalog entries do not guarantee quota or subscription access. Text/image access does not establish native Live voice availability.",
            "موديلات الحساب للمحادثة وفهم الصور حسب القدرات المعلنة. Talk يستخدم صوت الحساب المتصل الأصلي داخل نيڤو، مع بدء وإيقاف من البرنامج. ظهور موديل في القائمة لا يضمن توفر حصته أو صلاحية الاشتراك له. دعم النص والصور لا يثبت إتاحة صوت Live الأصلي."), 12));
        foreach (string provider in new[] { "codex", "antigravity" })
        {
            string title = provider == "codex" ? "Codex · ChatGPT" : "Antigravity · Google";
            var path = new TextBox { Text = provider == "codex" ? controller.Store.Data.Settings.CodexCliPath : controller.Store.Data.Settings.AntigravityCliPath, FlowDirection = FlowDirection.LeftToRight, Tag = provider + "CliPath" };
            accountPaths[provider] = path;
            path.TextChanged += (_, _) => markDirty();
            var status = Ui.Text(Loc.T("Not checked. Connect or refresh to read this account's catalog.", "لم يتم الفحص. اربط الحساب أو حدّث لقراءة قائمة موديلاته."), 12);
            status.Tag = provider + "AccountStatus";
            var models = Ui.Stack();
            CancellationTokenSource? operation = null;
            bool unloaded = false;
            void SavePath()
            {
                if (controller.Busy || ((App)Application.Current).AccountTalkActive) throw new InvalidOperationException(Loc.T("Finish the current request or stop Talk before changing accounts.", "أكمل الطلب الحالي أو أوقف Talk قبل تغيير الحسابات."));
                if (provider == "codex") controller.Store.Data.Settings.CodexCliPath = path.Text.Trim();
                else controller.Store.Data.Settings.AntigravityCliPath = path.Text.Trim();
                controller.Store.Save();
            }
            async Task LoadModels(CancellationToken ct)
            {
                var entries = await AccountConnections.ModelsAsync(controller.Store, provider, ct);
                ct.ThrowIfCancellationRequested();
                if (unloaded) return;
                models.Children.Clear();
                foreach (var entry in entries)
                {
                    void Select(ComboBox target)
                    {
                        enableAccounts(); target.Text = entry.Id; markDirty();
                        status.Text = Loc.T("Model selected · Save preferences to apply.", "تم اختيار الموديل · احفظ الإعدادات لتطبيقه.");
                    }
                    var useImage = Ui.Btn(Loc.T("Use for images", "للصور"), () => Select(vision));
                    useImage.IsEnabled = entry.ImageInput;
                    models.Children.Add(Ui.Card(Ui.Stack(Ui.Text(entry.DisplayName, 14, weight: FontWeights.SemiBold),
                        Ui.Text(entry.Id, 12), Ui.Text(entry.Capabilities, 12),
                        Ui.Row(Ui.Btn(Loc.T("Use for chat", "للمحادثة"), () => Select(chat)), useImage)), new Thickness(12)));
                }
                status.Text = Loc.T($"{entries.Count} models reported by {title}. Inference verifies access.", $"{entries.Count} موديل من {title}. توفر الوصول يتأكد عند الاستخدام.");
            }
            async Task Run(string action)
            {
                if (operation != null) return;
                SavePath(); unloaded = false;
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(action == "login" ? 5 : 1));
                operation = timeout;
                status.Text = action == "login" ? Loc.T("Complete sign-in in your browser…", "أكمل تسجيل الدخول في المتصفح…") : Loc.T("Checking account…", "جاري فحص الحساب…");
                try
                {
                    if (action == "logout")
                    {
                        await AccountConnections.LogoutAsync(controller.Store, provider, timeout.Token);
                        models.Children.Clear(); status.Text = Loc.T("Disconnected from NIVO. Other apps are unaffected.", "تم فصل الحساب من نيڤو. حسابات التطبيقات الأخرى لم تتغيّر.");
                    }
                    else
                    {
                        if (action == "login") await AccountConnections.LoginAsync(controller.Store, provider, timeout.Token);
                        await LoadModels(timeout.Token);
                    }
                }
                catch (OperationCanceledException)
                { if (!unloaded) status.Text = Loc.T("Connection check cancelled or timed out. You can retry.", "تم إلغاء فحص الاتصال أو انتهت مهلته. يمكنك المحاولة مجددًا."); }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or TimeoutException or JsonException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                { if (!unloaded) { models.Children.Clear(); status.Text = ex.Message; } }
                finally { if (ReferenceEquals(operation, timeout)) operation = null; }
            }
            var card = Ui.Card(Ui.Stack(Ui.Text(title, 17, weight: FontWeights.SemiBold),
                Ui.Text(Loc.T("CLI executable (blank = auto-detect)", "ملف CLI التنفيذي (فارغ = اكتشاف تلقائي)"), 12), path,
                Ui.Row(Ui.AsyncBtn(Loc.T("Connect account", "ربط الحساب"), () => Run("login"), true),
                    Ui.AsyncBtn(Loc.T("Refresh models", "تحديث الموديلات"), () => Run("models")),
                    Ui.AsyncBtn(Loc.T("Disconnect", "فصل الحساب"), () => Run("logout")),
                    Ui.Btn(Loc.T("Cancel", "إلغاء"), () => operation?.Cancel())), status,
                new ScrollViewer { Content = models, MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }));
            card.Tag = provider + "AccountCard";
            card.Loaded += (_, _) => unloaded = false;
            card.Unloaded += (_, _) =>
            {
                unloaded = true;
                if (operation != null)
                {
                    status.Text = Loc.T("Account operation cancelled when leaving this tab. You can retry.", "تم إلغاء عملية الحساب عند مغادرة التبويب. يمكنك المحاولة مجددًا.");
                    operation.Cancel();
                }
            };
            panel.Children.Add(card);
        }
        return panel;
    }
}
