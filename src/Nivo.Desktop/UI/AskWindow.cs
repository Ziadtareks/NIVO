using Nivo.Core;
using System.Windows.Input;
using System.Windows.Threading;
namespace Nivo.UI;

public sealed class AskWindow : Window
{
    private readonly ChatComposer composer;
    private AnswerWindow? answer;
    private bool forceClose;
    public AskWindow(Controller controller)
    {
        Title = Loc.T("NIVO · Silent Ask", "نيڤو · السؤال الصامت"); Width = Math.Min(680, SystemParameters.WorkArea.Width - 32); SizeToContent = SizeToContent.Height; Topmost = true; ShowInTaskbar = false; WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        FlowDirection = Loc.Flow;
        composer = new(controller, true) { Margin = new Thickness(8) }; Ui.Elevate(composer); Content = composer; Ui.StyleWindow(this);
        SizeChanged += (_, _) => Place(); Loaded += (_, _) => Place();
        composer.Submitted += () => { answer ??= new AnswerWindow(controller); answer.Show(); answer.Reveal(); Hide(); };
        Closing += (_, e) => { if (!forceClose && !((App)Application.Current).Exiting) { e.Cancel = true; Hide(); } else { composer.Dispose(); answer?.Close(); } };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { ((App)Application.Current).RequestAccountTalkStop(); Hide(); e.Handled = true; } };
        IsVisibleChanged += (_, _) => { if (IsVisible) { Motion.Enter(composer, 14); composer.FocusInput(); } };
    }
    private void Place() { Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2; Top = SystemParameters.WorkArea.Bottom - ActualHeight - 100; }
    public void Refresh(Controller c) => composer.FocusInput();
    internal void ForceClose() { forceClose = true; Close(); }
}
public sealed class AnswerWindow : Window
{
    private readonly Controller c;
    private readonly TextBox text = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0), Padding = new Thickness(0, 8, 0, 8), FontSize = 14 };
    private readonly TextBlock label = Ui.Text(Loc.T("NIVO", "نيڤو"), 13, weight: FontWeights.SemiBold);
    private readonly Button stopBtn;
    private Button? copyBtn;
    private bool dirty;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    public AnswerWindow(Controller controller)
    {
        c = controller; Title = Loc.T("NIVO · Answer", "نيڤو · الإجابة"); Width = 420; Height = 350; WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize; Topmost = true; ShowInTaskbar = false; ShowActivated = false;
        FlowDirection = Loc.Flow;
        Left = SystemParameters.WorkArea.Right - Width - 20; Top = SystemParameters.WorkArea.Bottom - Height - 100;
        var body = new DockPanel();
        var head = new Grid(); label.VerticalAlignment = VerticalAlignment.Center; head.Children.Add(label);
        var close = Ui.Btn("×", Hide); close.HorizontalAlignment = HorizontalAlignment.Right; close.Padding = new Thickness(7, 2, 7, 2); close.Margin = new Thickness(0); Ui.Flat(close); head.Children.Add(close);
        DockPanel.SetDock(head, Dock.Top); body.Children.Add(head);
        stopBtn = Ui.Btn(Loc.T("Stop  ■", "إيقاف  ■"), () => ((App)Application.Current).StopAll());
        copyBtn = Ui.Btn(Loc.T("Copy", "نسخ"), () => {
            System.Windows.Clipboard.SetText(text.Text);
            if (copyBtn != null) copyBtn.Content = Loc.T("Copied ✓", "تم النسخ ✓");
            var reset = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
            reset.Tick += (_, _) => { reset.Stop(); if (copyBtn != null) copyBtn.Content = Loc.T("Copy", "نسخ"); };
            reset.Start();
        });
        var buttons = Ui.Row(Ui.Btn(Loc.T("Continue", "متابعة"), () => ((App)Application.Current).ShowAsk(), true), Ui.Btn(Loc.T("Open chat", "فتح المحادثة"), () => { ((App)Application.Current).Hub.ShowAssistant(); Hide(); }), copyBtn, stopBtn);
        DockPanel.SetDock(buttons, Dock.Bottom); body.Children.Add(buttons); body.Children.Add(text);
        var card = Ui.Card(body, new Thickness(20)); card.Margin = new Thickness(6); card.SetResourceReference(Border.BackgroundProperty, "Glass"); Ui.Elevate(card); Content = card; Ui.StyleWindow(this);
        c.Changed += MarkDirty; timer.Tick += (_, _) => { if (dirty) { dirty = false; Refresh(); } else timer.Stop(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) { Refresh(); MarkDirty(); } else timer.Stop(); };
        Closed += (_, _) => { timer.Stop(); c.Changed -= MarkDirty; };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
        Refresh();
    }
    private void MarkDirty() { dirty = true; if (IsVisible) timer.Start(); }
    private void Refresh()
    {
        var value = c.Current.Messages.LastOrDefault(m => m.Role == "assistant")?.Text ?? "";
        if (value.Length == 0) value = c.Busy ? Loc.T("Thinking…", "جاري التفكير…") : Loc.T("Your answer will appear here.", "ستظهر إجابتك هنا.");
        if (text.Text != value) { text.Text = value; text.FlowDirection = ChatComposer.IsArabic(value) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight; }
        string tag = c.Mode == "Game" ? Loc.T("Game HUD", "مساعد الألعاب") : Loc.T("Answer", "الإجابة");
        label.Text = c.Busy ? $"{Loc.T("NIVO", "نيڤو")} · {c.Status}" : $"{Loc.T("NIVO", "نيڤو")} · {tag}";
        stopBtn.Visibility = c.Busy ? Visibility.Visible : Visibility.Collapsed;
    }
    public void Reveal() { Refresh(); Motion.Enter((FrameworkElement)Content); }
}
