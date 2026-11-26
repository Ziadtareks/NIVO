using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Nivo.Core;
using Nivo.Services;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Nivo.UI;

public sealed class CropWindow : Window
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpShowWindow = 0x0040;

    public static readonly string[] ToolbarActions =
    [
        "Explain", "Translate", "Summarize", "Fact-check",
        "Abbreviation", "Table", "Note", "Task", "Copy"
    ];

    private readonly Canvas canvas = new();
    private readonly Rectangle selection = new() { Stroke = Ui.Brush("#38D6F5"), StrokeThickness = 2, Fill = Ui.Brush("#1A38D6F5"), RadiusX = 6, RadiusY = 6 };
    private readonly System.Windows.Shapes.Path[] corners = new System.Windows.Shapes.Path[4];
    private readonly Border sizeBadge;
    private readonly TextBlock sizeLabel = new() { Foreground = Ui.Brush("#F4FBFF"), FontSize = 12, FontWeight = FontWeights.SemiBold };
    private readonly Border actionBar;
    private readonly WrapPanel barRow;
    private readonly PixelRect? monitorBounds;
    private Point origin;
    private bool selecting;
    private bool repositioning;
    private readonly BitmapSource image;
    public BitmapSource? Result { get; private set; }
    public string SelectedAction { get; private set; } = "Ask";

    public CropWindow(BitmapSource screenshot, bool immediate = false, PixelRect? monitorBounds = null)
    {
        image = screenshot;
        this.monitorBounds = monitorBounds;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; Topmost = true; ShowInTaskbar = false;
        Left = 0; Top = 0; Width = SystemParameters.PrimaryScreenWidth; Height = SystemParameters.PrimaryScreenHeight;
        FlowDirection = FlowDirection.LeftToRight;

        var grid = new Grid { Cursor = Cursors.Cross };
        grid.Children.Add(new Image { Source = screenshot, Stretch = Stretch.Fill });
        grid.Children.Add(new Border { Background = Ui.Brush("#66050D1E") });

        sizeBadge = new Border { Background = Ui.Brush("#E60A1830"), BorderBrush = Ui.Brush("#8838D6F5"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 4, 10, 4), Child = sizeLabel, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        canvas.Children.Add(selection);
        for (int i = 0; i < 4; i++)
        {
            corners[i] = new System.Windows.Shapes.Path { Stroke = Ui.Brush("#67F0FF"), StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            canvas.Children.Add(corners[i]);
        }
        canvas.Children.Add(sizeBadge);

        Button BarBtn(string text, string action, bool primary = false, bool local = false)
        {
            var b = Ui.Btn(text, () => Finish(action));
            b.Tag = action;
            b.SetResourceReference(StyleProperty, "PillButton");
            b.Background = primary ? Ui.Brush("#38D6F5") : local ? Ui.Brush("#173652") : Ui.Brush("#1A2D58");
            b.Foreground = primary ? Ui.Brush("#071836") : Ui.Brush("#E8F4FF");
            b.BorderBrush = primary ? Ui.Brush("#78ECFF") : local ? Ui.Brush("#479CC9") : Ui.Brush("#3C629E");
            b.FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal;
            b.Padding = new Thickness(10, 5, 10, 5); b.FontSize = 11.5; b.Margin = new Thickness(3, 2, 3, 2);
            return b;
        }

        barRow = new WrapPanel { FlowDirection = Loc.Flow, HorizontalAlignment = HorizontalAlignment.Left };
        barRow.Children.Add(BarBtn(Loc.T("Ask  ↗", "اسأل  ↗"), "Ask", primary: true));
        barRow.Children.Add(BarBtn(Loc.T("Explain", "اشرح"), "Explain"));
        barRow.Children.Add(BarBtn(Loc.T("Translate", "ترجم"), "Translate"));
        barRow.Children.Add(BarBtn(Loc.T("Summarize", "لخّص"), "Summarize"));
        barRow.Children.Add(BarBtn(Loc.T("Fact-check", "تحقق"), "Fact-check"));
        barRow.Children.Add(BarBtn(Loc.T("Abbreviation", "اختصار"), "Abbreviation"));
        barRow.Children.Add(BarBtn(Loc.T("Table", "جدول"), "Table"));
        barRow.Children.Add(BarBtn(Loc.T("Note", "ملاحظة"), "Note", local: true));
        barRow.Children.Add(BarBtn(Loc.T("Task", "مهمة"), "Task", local: true));
        barRow.Children.Add(BarBtn(Loc.T("Copy", "نسخ"), "Copy", local: true));
        barRow.Children.Add(BarBtn("×", "Cancel"));

        actionBar = new Border
        {
            Background = Ui.Brush("#F00A193A"), BorderBrush = Ui.Brush("#6638D6F5"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18), Padding = new Thickness(8, 6, 8, 6), Child = barRow,
            Visibility = Visibility.Collapsed, Cursor = Cursors.Arrow
        };
        Ui.Elevate(actionBar);
        canvas.Children.Add(actionBar);
        grid.Children.Add(canvas);

        var hint = new Border { Background = Ui.Brush("#E60A1830"), BorderBrush = Ui.Brush("#6638D6F5"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = new Thickness(20, 9, 20, 9), Margin = new Thickness(0, 26, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, FlowDirection = Loc.Flow, Child = new TextBlock { Text = Loc.T("Drag to box any region  ·  Enter to ask  ·  Esc to cancel", "اسحب لتحديد أي جزء من الشاشة  ·  Enter للسؤال  ·  Esc للإلغاء"), Foreground = Ui.Brush("#F4FBFF"), FontSize = 14.5, FontWeight = FontWeights.Medium } };
        grid.Children.Add(hint);

        SourceInitialized += (_, _) => PositionOnMonitorIfConfigured();
        DpiChanged += (_, _) => { if (!repositioning) PositionOnMonitorIfConfigured(); };

        void UpdateCorners(double left, double top, double w, double h)
        {
            double len = Math.Min(16, Math.Min(w, h) * 0.35);
            if (len < 4) { foreach (var c in corners) c.Visibility = Visibility.Collapsed; return; }
            corners[0].Data = Geometry.Parse(FormattableString.Invariant($"M {left:F1},{top + len:F1} L {left:F1},{top:F1} L {left + len:F1},{top:F1}"));
            corners[1].Data = Geometry.Parse(FormattableString.Invariant($"M {left + w - len:F1},{top:F1} L {left + w:F1},{top:F1} L {left + w:F1},{top + len:F1}"));
            corners[2].Data = Geometry.Parse(FormattableString.Invariant($"M {left:F1},{top + h - len:F1} L {left:F1},{top + h:F1} L {left + len:F1},{top + h:F1}"));
            corners[3].Data = Geometry.Parse(FormattableString.Invariant($"M {left + w - len:F1},{top + h:F1} L {left + w:F1},{top + h:F1} L {left + w:F1},{top + h - len:F1}"));
            foreach (var c in corners) c.Visibility = Visibility.Visible;
        }

        grid.MouseLeftButtonDown += (_, e) => {
            if (Ui.InButton(e.OriginalSource as DependencyObject)) return;
            selecting = true;
            actionBar.Visibility = Visibility.Collapsed;
            origin = e.GetPosition(canvas);
            selection.Width = selection.Height = 0;
            Canvas.SetLeft(selection, origin.X); Canvas.SetTop(selection, origin.Y);
            sizeBadge.Visibility = Visibility.Visible;
            grid.CaptureMouse();
        };
        grid.MouseMove += (_, e) => {
            if (!selecting || e.LeftButton != MouseButtonState.Pressed || !grid.IsMouseCaptured) return;
            var point = e.GetPosition(canvas);
            double left = Math.Min(origin.X, point.X), top = Math.Min(origin.Y, point.Y), w = Math.Abs(point.X - origin.X), h = Math.Abs(point.Y - origin.Y);
            Canvas.SetLeft(selection, left); Canvas.SetTop(selection, top); selection.Width = w; selection.Height = h;
            UpdateCorners(left, top, w, h);
            var px = CaptureGeometry.MapSelectionToPixels(left, top, w, h, ActualWidth, ActualHeight, image.PixelWidth, image.PixelHeight, FlowDirection == FlowDirection.RightToLeft);
            sizeLabel.Text = $"{px.Width} × {px.Height} px";
            double badgeX = Math.Clamp(left, 8, Math.Max(8, ActualWidth - 130));
            double badgeY = top > 38 ? top - 34 : Math.Clamp(top + h + 8, 8, Math.Max(8, ActualHeight - 36));
            Canvas.SetLeft(sizeBadge, badgeX); Canvas.SetTop(sizeBadge, badgeY);
        };
        grid.MouseLeftButtonUp += (_, _) => {
            if (!selecting) return;
            selecting = false; grid.ReleaseMouseCapture();
            if (selection.Width < 8 || selection.Height < 8) { sizeBadge.Visibility = Visibility.Collapsed; foreach (var c in corners) c.Visibility = Visibility.Collapsed; return; }
            CaptureRect();
            if (immediate) { Finish("Ask"); return; }
            double left = Canvas.GetLeft(selection), top = Canvas.GetTop(selection), h = selection.Height;
            double maxBarWidth = Math.Min(820, Math.Max(320, ActualWidth - 32));
            actionBar.MaxWidth = maxBarWidth;
            actionBar.Visibility = Visibility.Visible;
            actionBar.Measure(new Size(maxBarWidth, ActualHeight));
            actionBar.UpdateLayout();
            double barW = Math.Max(320, Math.Min(maxBarWidth, actionBar.ActualWidth > 0 ? actionBar.ActualWidth : actionBar.DesiredSize.Width));
            double barH = Math.Max(46, actionBar.ActualHeight > 0 ? actionBar.ActualHeight : actionBar.DesiredSize.Height);
            var pos = CaptureGeometry.ClampFloatingBarPosition(left, top, h, barW, barH, ActualWidth, ActualHeight);
            Canvas.SetLeft(actionBar, pos.X); Canvas.SetTop(actionBar, pos.Y);
        };
        PreviewKeyDown += (_, e) => {
            if (e.Key == Key.Escape) { Finish("Cancel"); e.Handled = true; }
            if (e.Key == Key.Enter && Result != null) { Finish("Ask"); e.Handled = true; }
        };
        Content = grid;
    }

    private void PositionOnMonitorIfConfigured()
    {
        if (monitorBounds is not { IsEmpty: false } b || Left <= -10000) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        repositioning = true;
        try { SetWindowPos(hwnd, HwndTopmost, b.X, b.Y, b.Width, b.Height, SwpShowWindow); }
        finally { repositioning = false; }
    }

    private void CaptureRect()
    {
        var rect = CaptureGeometry.MapSelectionToPixels(
            Canvas.GetLeft(selection),
            Canvas.GetTop(selection),
            selection.Width,
            selection.Height,
            ActualWidth,
            ActualHeight,
            image.PixelWidth,
            image.PixelHeight,
            FlowDirection == FlowDirection.RightToLeft);
        if (rect.IsEmpty) { Result = null; return; }
        Result = new CroppedBitmap(image, rect);
        Result.Freeze();
    }

    private void Finish(string action)
    {
        if (action == "Cancel")
        {
            Result = null;
            SelectedAction = "Cancel";
            if (ComponentDispatcher.IsThreadModal) DialogResult = false;
            else Close();
            return;
        }
        SelectedAction = action;
        if (ComponentDispatcher.IsThreadModal) DialogResult = Result != null;
        else Close();
    }
}
