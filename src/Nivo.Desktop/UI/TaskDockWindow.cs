using Nivo.Core;
using System.Windows.Threading;
namespace Nivo.UI;

public sealed class TaskDockWindow : Window
{
    private readonly Controller c;
    private readonly System.Windows.Shapes.Ellipse dot = new() { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock state = Ui.Text("Working", 13, weight: FontWeights.SemiBold);
    private readonly TextBlock detail = Ui.Text("", 13, "#9CB3C3");
    private readonly ActivityGlyph activity = new();
    private readonly DispatcherTimer hide = new() { Interval = TimeSpan.FromSeconds(20) };
    private string previous = ""; private int sequence;
    private readonly StackPanel approval;
    public TaskDockWindow(Controller controller)
    {
        c = controller; Title = Loc.T("NIVO · Task dock", "نيڤو · بطاقة المهمة"); Width = 350; SizeToContent = SizeToContent.Height; Topmost = true; ShowInTaskbar = false; ShowActivated = false; WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        FlowDirection = Loc.Flow;
        Left = SystemParameters.WorkArea.Left + 20;
        var header = new Grid(); header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Ui.Row(dot, state)); Grid.SetColumn(activity, 1); header.Children.Add(activity);
        detail.Margin = new Thickness(0, 10, 0, 14); detail.MaxHeight = 88;
        approval = Ui.Row(Ui.Btn(Loc.T("Review & allow", "مراجعة والسماح"), () => { var review = Application.Current.Windows.OfType<ReviewPlanWindow>().FirstOrDefault(); if (review != null) { review.Show(); review.Activate(); } }, true), Ui.Btn(Loc.T("Deny", "رفض"), () => ((App)Application.Current).StopAll()));
        approval.Visibility = Visibility.Collapsed;
        var stop = Ui.Btn(Loc.T("Stop", "إيقاف"), () => ((App)Application.Current).StopAll()); var history = Ui.Btn(Loc.T("Task history", "سجل المهام"), () => { ((App)Application.Current).Hub.Show(); ((App)Application.Current).Hub.Navigate("Task history"); }); var dismiss = Ui.Btn("×", Hide);
        var card = Ui.Card(Ui.Stack(header, detail, approval, Ui.Row(stop, history, dismiss)), new Thickness(20)); card.Margin = new Thickness(6); card.SetResourceReference(Border.BackgroundProperty, "Glass"); Ui.Elevate(card); Content = card; Ui.StyleWindow(this);
        SizeChanged += (_, _) => Top = SystemParameters.WorkArea.Bottom - ActualHeight - 100; hide.Tick += (_, _) => { hide.Stop(); Hide(); }; Closed += (_, _) => { hide.Stop(); activity.SetActive(false); };
    }
    public void Update()
    {
        state.Text = "NIVO · " + c.TaskState; detail.Text = c.TaskDetail; activity.SetActive(c.TaskState == "Working" && c.Busy);
        dot.Fill = Ui.Brush(c.TaskState switch { "Working" => "#22D3EE", "Needs you" or "Needs details" => "#F59E0B", "Done" => "#10B981", "Failed" => "#EF4444", _ => "#94A3B8" });
        approval.Visibility = c.TaskActive && c.TaskState == "Needs you" ? Visibility.Visible : Visibility.Collapsed;
        if (previous == c.TaskState && sequence == c.TaskSequence) return; sequence = c.TaskSequence;
        previous = c.TaskState; hide.Stop(); Show(); Motion.Enter((FrameworkElement)Content, 10);
        if (c.TaskState is "Done" or "Failed" or "Stopped" or "Needs details") hide.Start();
    }
}
