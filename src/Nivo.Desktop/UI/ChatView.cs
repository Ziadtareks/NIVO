using Nivo.Core;
using System.Windows.Threading;
namespace Nivo.UI;

public sealed class ChatView : DockPanel, IDisposable
{
    private readonly Controller c;
    private readonly StackPanel messages = new();
    private readonly ScrollViewer scroll;
    private readonly ChatComposer composer;
    private readonly Dictionary<ChatMessage, (TextBox Text, StackPanel Actions)> rows = [];
    private readonly DispatcherTimer throttle;
    private Guid conversation;
    private bool dirty;
    private FrameworkElement? welcome;
    public int MessageVisualCount => rows.Count;
    public ChatView(Controller controller, bool compact = false)
    {
        c = controller; FlowDirection = Loc.Flow;
        composer = new(c, compact); DockPanel.SetDock(composer, Dock.Bottom); Children.Add(composer);
        scroll = new ScrollViewer { Content = messages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 16) }; Children.Add(scroll);
        c.Changed += MarkDirty; throttle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) }; throttle.Tick += (_, _) => { throttle.Stop(); if (dirty) { dirty = false; Render(); } }; Render();
    }
    private void MarkDirty() { dirty = true; throttle.Start(); }
    private void Render()
    {
        bool atBottom = scroll.ScrollableHeight - scroll.VerticalOffset < 45;
        if (conversation != c.Current.Id) { atBottom = true; conversation = c.Current.Id; rows.Clear(); messages.Children.Clear(); welcome = null; }
        if (c.Current.Messages.Count == 0 && welcome == null)
        {
            var mark = Ui.Orb(64); mark.HorizontalAlignment = HorizontalAlignment.Left;
            var intro = Ui.Stack(mark, new Border { Height = 20 }, Ui.Text(Loc.T("Right here, with you.", "هنا معك، خطوة بخطوة."), 34, weight: FontWeights.SemiBold), Ui.Text(Loc.T("A thought, a file, or something on your screen.\nWhat are we working on?", "فكرة، ملف، أو شيء على شاشتك.\nبماذا نبدأ الآن؟"), 16, "#9CB3C3")); intro.Margin = new Thickness(8, 18, 8, 22);
            var actions = new UniformGridEx(2) { Margin = new Thickness(0, 20, 0, 0), MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
            Button QuickCard(string label, string glyph, Func<Task> onClick)
            {
                var b = Ui.AsyncBtn("", onClick);
                b.Content = Ui.Row(Ui.Glyph(glyph, 18), Ui.Text("   " + label, 14));
                b.HorizontalContentAlignment = HorizontalAlignment.Left; b.HorizontalAlignment = HorizontalAlignment.Stretch; b.Padding = new Thickness(16, 12, 16, 12);
                return b;
            }
            actions.Children.Add(QuickCard(Loc.T("Understand my screen", "افهم شاشتي الآن"), "\uE7F4", async () => {
                if (await ((App)Application.Current).CaptureAsync(false, false, false)) { c.Mode = "Screen"; c.Draft = Loc.T("Explain what is shown in the image.", "اشرح لي ما يظهر في الصورة."); composer.SetDraft(c.Draft); c.Notify(); }
            }));
            actions.Children.Add(QuickCard(Loc.T("Crop & translate screen", "قصّ وترجم جزءًا من الشاشة"), "\uE7A8", async () => {
                if (await ((App)Application.Current).CaptureAsync(true, false, false, true) && c.Capture != null) { c.Mode = "Crop"; c.Draft = Loc.T("Translate the text in the selected image into English.", "ترجم النص الموجود في الصورة إلى العربية."); composer.SetDraft(c.Draft); c.Notify(); }
            }));
            actions.Children.Add(QuickCard(Loc.T("Make a document or presentation", "إنشاء مستند أو عرض تقديمي"), "\uE8A5", () => {
                c.Mode = "Agent"; composer.SetDraft(Loc.T("Create a six-slide presentation about ", "أنشئ عرضًا من 6 شرائح عن ")); c.Notify(); return Task.CompletedTask;
            }));
            actions.Children.Add(QuickCard(Loc.T("Think through an idea", "رتّب فكرة أو خطة عمل"), "\uE8F2", () => {
                c.Mode = "General"; composer.SetDraft(Loc.T("Help me turn this idea into a plan: ", "ساعدني أحوّل الفكرة دي لخطة عملية: ")); c.Notify(); return Task.CompletedTask;
            }));
            intro.Children.Add(actions);

            var recentChats = c.Store.Data.Conversations.Take(3).ToArray();
            if (recentChats.Length > 0)
            {
                var recentRow = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
                var recentHeader = Ui.Text(Loc.T("Recent conversations", "المحادثات الأخيرة"), 12, "#9CB3C3");
                recentHeader.Margin = new Thickness(0, 18, 0, 0);
                intro.Children.Add(recentHeader);
                foreach (var chatItem in recentChats)
                {
                    var target = chatItem;
                    var pill = Ui.Btn("💬  " + target.Title, () => c.OpenChat(target));
                    pill.SetResourceReference(StyleProperty, "PillButton"); pill.Padding = new Thickness(12, 6, 12, 6); pill.FontSize = 12;
                    recentRow.Children.Add(pill);
                }
                intro.Children.Add(recentRow);
            }

            welcome = intro; messages.Children.Add(welcome);
        }
        if (c.Current.Messages.Count > 0 && welcome != null) { messages.Children.Remove(welcome); welcome = null; }
        foreach (var message in c.Current.Messages)
        {
            if (!rows.TryGetValue(message, out var row))
            {
                var text = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Margin = new Thickness(0), Padding = new Thickness(0, 10, 0, 12), FontSize = 15 };
                Button? copyBtn = null;
                copyBtn = Ui.Btn(Loc.T("Copy", "نسخ"), () => {
                    System.Windows.Clipboard.SetText(message.Text);
                    copyBtn!.Content = Loc.T("Copied ✓", "تم النسخ ✓");
                    var reset = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
                    reset.Tick += (_, _) => { reset.Stop(); copyBtn!.Content = Loc.T("Copy", "نسخ"); };
                    reset.Start();
                });
                var actions = Ui.Row(copyBtn, Ui.Btn(Loc.T("Remember", "احفظ في الذاكرة"), () => { c.Store.AddMemory(message.Text); c.SetStatus(Loc.T("Saved in Memory", "تم الحفظ في الذاكرة")); }), Ui.AsyncBtn(Loc.T("Read aloud", "قراءة صوتية"), async () => await ((App)Application.Current).ReadAloud(message.Text)));
                foreach (Button b in actions.Children) { b.Padding = new Thickness(10, 5, 10, 5); b.FontSize = 11; }
                var body = Ui.Stack(Ui.Text(message.Role == "user" ? Loc.T("You", "أنت") : Loc.T("NIVO", "نيڤو"), 12, message.Role == "assistant" ? "#8ADBD5" : null, FontWeights.SemiBold), text, actions);
                var card = Ui.Card(body, new Thickness(20, 16, 20, 12)); if (message.Role == "user") card.SetResourceReference(Border.BackgroundProperty, "Selected");
                messages.Children.Add(card); rows[message] = row = (text, actions); Motion.Enter(card, 8);
            }
            var value = message.Text.Length == 0 ? Loc.T("Thinking…", "جاري التفكير…") : message.Text;
            if (row.Text.Text != value) { row.Text.Text = value; row.Text.FlowDirection = ChatComposer.IsArabic(value) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight; }
            row.Actions.Visibility = message.Role == "assistant" && message.Text.Length > 0 && !c.Busy ? Visibility.Visible : Visibility.Collapsed;
        }
        if (atBottom && rows.Count > 0) scroll.ScrollToEnd();
    }
    public void Dispose() { c.Changed -= MarkDirty; throttle.Stop(); composer.Dispose(); }
}
public sealed class UniformGridEx : System.Windows.Controls.Primitives.UniformGrid { public UniformGridEx(int columns) { Columns = columns; } }
