using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Nivo.Core;
using Nivo.Services;
using Ellipse = System.Windows.Shapes.Ellipse;
using PathShape = System.Windows.Shapes.Path;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Nivo.UI;

/// <summary>
/// Armed full-monitor live overlay for Point &amp; Ask.
/// Tracks the physical pointer across primary and secondary monitors, displays a cyan spotlight
/// matching the exact clamped capture region, confirms on left-click, and cancels on Esc / right-click.
/// </summary>
public sealed class PointPickerWindow : Window
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpShowWindow = 0x0040;

    private readonly bool pinToActiveMonitor;
    private readonly Canvas canvas = new();
    private readonly PathShape scrimPath;
    private readonly CombinedGeometry scrimGeometry;
    private readonly RectangleGeometry outerScrimRect = new();
    private readonly RectangleGeometry spotlightCutout = new() { RadiusX = 14, RadiusY = 14 };
    private readonly Rectangle spotlightBorder;
    private readonly PathShape[] corners = new PathShape[4];
    private readonly Ellipse reticleRing;
    private readonly Ellipse reticleDot;
    private readonly Border sizeBadge;
    private readonly TextBlock sizeLabel = new() { Foreground = Ui.Brush("#F4FBFF"), FontSize = 12, FontWeight = FontWeights.SemiBold };
    private readonly DispatcherTimer trackTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    private bool armed;
    private bool waitingForButtonRelease;
    private bool repositioning;
    private bool finished;

    public PixelRect ActiveMonitorBounds { get; private set; }
    public PixelRect? SelectedRegion { get; private set; }
    public PixelPoint? SelectedPoint { get; private set; }
    public bool Confirmed { get; private set; }
    internal bool IsArmed => armed && !finished;

    public PointPickerWindow(bool pinToActiveMonitor = true)
    {
        this.pinToActiveMonitor = pinToActiveMonitor;
        Title = Loc.T("NIVO · Point & Ask", "نيڤو · أشّر واسأل");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        FlowDirection = FlowDirection.LeftToRight;
        Cursor = Cursors.Cross;

        var initialCursor = DesktopService.CursorPosition();
        ActiveMonitorBounds = DesktopService.MonitorBoundsAt(initialCursor);
        Left = 0;
        Top = 0;
        Width = Math.Max(320, SystemParameters.PrimaryScreenWidth);
        Height = Math.Max(240, SystemParameters.PrimaryScreenHeight);

        scrimGeometry = new CombinedGeometry(GeometryCombineMode.Exclude, outerScrimRect, spotlightCutout);
        scrimPath = new PathShape
        {
            Fill = Ui.Brush("#4D050D1E"),
            Data = scrimGeometry,
            IsHitTestVisible = false
        };

        spotlightBorder = new Rectangle
        {
            Stroke = Ui.Brush("#38D6F5"),
            StrokeThickness = 2,
            Fill = Ui.Brush("#1038D6F5"),
            RadiusX = 14,
            RadiusY = 14,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true,
            Effect = new DropShadowEffect
            {
                Color = Ui.Brush("#38D6F5").Color,
                BlurRadius = 18,
                ShadowDepth = 0,
                Opacity = 0.7
            }
        };

        reticleRing = new Ellipse
        {
            Width = 18,
            Height = 18,
            Stroke = Ui.Brush("#7DF2FF"),
            StrokeThickness = 1.6,
            Fill = Ui.Brush("#2638D6F5"),
            IsHitTestVisible = false
        };
        reticleDot = new Ellipse
        {
            Width = 5,
            Height = 5,
            Fill = Ui.Brush("#E8FCFF"),
            IsHitTestVisible = false
        };

        sizeBadge = new Border
        {
            Background = Ui.Brush("#E60A1830"),
            BorderBrush = Ui.Brush("#8838D6F5"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 4, 10, 4),
            Child = sizeLabel,
            IsHitTestVisible = false
        };

        canvas.Children.Add(scrimPath);
        canvas.Children.Add(spotlightBorder);
        for (int i = 0; i < 4; i++)
        {
            corners[i] = new PathShape
            {
                Stroke = Ui.Brush("#67F0FF"),
                StrokeThickness = 3,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
            canvas.Children.Add(corners[i]);
        }
        canvas.Children.Add(reticleRing);
        canvas.Children.Add(reticleDot);
        canvas.Children.Add(sizeBadge);

        var hint = new Border
        {
            Background = Ui.Brush("#E60A1830"),
            BorderBrush = Ui.Brush("#6638D6F5"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(20, 9, 20, 9),
            Margin = new Thickness(0, 26, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            FlowDirection = Loc.Flow,
            Child = new TextBlock
            {
                Text = Loc.T(
                    "Move to target and click to capture  ·  Esc or right-click to cancel",
                    "حرّك المؤشر إلى الهدف واضغط للالتقاط  ·  Esc أو كليك يمين للإلغاء"),
                Foreground = Ui.Brush("#F4FBFF"),
                FontSize = 14.5,
                FontWeight = FontWeights.Medium
            }
        };

        // 1/255 alpha ensures hit-testing across the entire transparent overlay without tinting.
        var root = new Grid { Background = Ui.Brush("#01000000"), Cursor = Cursors.Cross };
        root.Children.Add(canvas);
        root.Children.Add(hint);
        Content = root;

        SourceInitialized += (_, _) =>
        {
            if (ShouldPinToMonitor())
                PositionOnMonitor(ActiveMonitorBounds);
        };

        Loaded += (_, _) =>
        {
            if (ShouldPinToMonitor())
            {
                Activate();
                Focus();
                Keyboard.Focus(this);
            }
            waitingForButtonRelease = Mouse.LeftButton == MouseButtonState.Pressed;
            armed = !waitingForButtonRelease;
            UpdateSpotlight(DesktopService.CursorPosition());
            if (ShouldPinToMonitor())
                trackTimer.Start();
        };

        DpiChanged += (_, _) =>
        {
            if (ShouldPinToMonitor() && !repositioning)
                PositionOnMonitor(ActiveMonitorBounds);
            UpdateSpotlight(DesktopService.CursorPosition());
        };

        SizeChanged += (_, _) => UpdateSpotlight(DesktopService.CursorPosition());

        Deactivated += (_, _) =>
        {
            if (ShouldPinToMonitor() && armed && !repositioning && !finished)
                CancelSelection();
        };

        trackTimer.Tick += (_, _) =>
        {
            if (finished) return;
            if (waitingForButtonRelease && Mouse.LeftButton == MouseButtonState.Released)
            {
                waitingForButtonRelease = false;
                armed = true;
            }
            UpdateSpotlight(DesktopService.CursorPosition());
        };

        root.MouseMove += (_, _) =>
        {
            if (!finished) UpdateSpotlight(DesktopService.CursorPosition());
        };

        root.MouseLeftButtonUp += (_, _) =>
        {
            if (waitingForButtonRelease)
            {
                waitingForButtonRelease = false;
                armed = true;
            }
        };

        root.MouseLeftButtonDown += (_, e) =>
        {
            if (!armed || finished) return;
            e.Handled = true;
            ConfirmAt(DesktopService.CursorPosition());
        };

        root.MouseRightButtonDown += (_, e) =>
        {
            e.Handled = true;
            CancelSelection();
        };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelSelection();
            }
            else if (e.Key == Key.Enter && armed && !finished)
            {
                e.Handled = true;
                ConfirmAt(DesktopService.CursorPosition());
            }
        };

        Closed += (_, _) => trackTimer.Stop();
    }

    private bool ShouldPinToMonitor() => pinToActiveMonitor && Left > -10000;

    private void PositionOnMonitor(PixelRect bounds)
    {
        if (bounds.IsEmpty) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        repositioning = true;
        try
        {
            SetWindowPos(hwnd, HwndTopmost, bounds.X, bounds.Y, bounds.Width, bounds.Height, SwpShowWindow);
        }
        finally
        {
            repositioning = false;
        }
    }

    private (double ScaleX, double ScaleY) MonitorDpiScale(PixelPoint cursor)
    {
        var (sx, sy) = DesktopService.MonitorDpiScaleAt(cursor);
        if (sx > 0 && sy > 0) return (sx, sy);
        var dpi = VisualTreeHelper.GetDpi(this);
        return (dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0, dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0);
    }

    private PixelRect ComputeTargetRegion(PixelPoint cursor, PixelRect monitorBounds)
    {
        var (sx, sy) = MonitorDpiScale(cursor);
        var (physW, physH) = CaptureGeometry.ScaleSizeForDpi(
            CaptureGeometry.DefaultPointWidth,
            CaptureGeometry.DefaultPointHeight,
            sx,
            sy);
        return CaptureGeometry.ClampRegionAroundPoint(cursor, monitorBounds, physW, physH);
    }

    private void UpdateSpotlight(PixelPoint cursor)
    {
        var monitor = DesktopService.MonitorBoundsAt(cursor);
        if (ShouldPinToMonitor() && monitor != ActiveMonitorBounds && !monitor.IsEmpty)
        {
            ActiveMonitorBounds = monitor;
            PositionOnMonitor(monitor);
        }

        double surfaceW = ActualWidth > 0 ? ActualWidth : Width;
        double surfaceH = ActualHeight > 0 ? ActualHeight : Height;
        if (surfaceW <= 0 || surfaceH <= 0 || ActiveMonitorBounds.IsEmpty) return;

        var (sx, sy) = MonitorDpiScale(cursor);
        var physRect = ComputeTargetRegion(cursor, ActiveMonitorBounds);
        var dipRect = CaptureGeometry.PhysicalToMonitorDip(physRect, ActiveMonitorBounds, sx, sy);
        var dipCursor = CaptureGeometry.PhysicalPointToMonitorDip(cursor, ActiveMonitorBounds, sx, sy);

        outerScrimRect.Rect = new Rect(0, 0, surfaceW, surfaceH);
        spotlightCutout.Rect = dipRect;

        Canvas.SetLeft(spotlightBorder, dipRect.X);
        Canvas.SetTop(spotlightBorder, dipRect.Y);
        spotlightBorder.Width = dipRect.Width;
        spotlightBorder.Height = dipRect.Height;

        UpdateCorners(dipRect.X, dipRect.Y, dipRect.Width, dipRect.Height);

        double rx = Math.Clamp(dipCursor.X, 0, surfaceW);
        double ry = Math.Clamp(dipCursor.Y, 0, surfaceH);
        Canvas.SetLeft(reticleRing, rx - reticleRing.Width / 2);
        Canvas.SetTop(reticleRing, ry - reticleRing.Height / 2);
        Canvas.SetLeft(reticleDot, rx - reticleDot.Width / 2);
        Canvas.SetTop(reticleDot, ry - reticleDot.Height / 2);

        sizeLabel.Text = $"{physRect.Width} × {physRect.Height} px";
        sizeBadge.Measure(new Size(surfaceW, surfaceH));
        double badgeW = Math.Max(96, sizeBadge.DesiredSize.Width);
        double badgeH = Math.Max(26, sizeBadge.DesiredSize.Height);
        var badgePos = CaptureGeometry.ClampFloatingBarPosition(
            dipRect.X,
            dipRect.Y,
            dipRect.Height,
            badgeW,
            badgeH,
            surfaceW,
            surfaceH,
            margin: 12,
            gap: 8);
        Canvas.SetLeft(sizeBadge, badgePos.X);
        Canvas.SetTop(sizeBadge, dipRect.Y > badgeH + 14 ? dipRect.Y - badgeH - 8 : badgePos.Y);
    }

    private void UpdateCorners(double left, double top, double w, double h)
    {
        double len = Math.Min(20, Math.Min(w, h) * 0.25);
        if (len < 4)
        {
            foreach (var c in corners) c.Visibility = Visibility.Collapsed;
            return;
        }
        corners[0].Data = Geometry.Parse(FormattableString.Invariant($"M {left:F1},{top + len:F1} L {left:F1},{top:F1} L {left + len:F1},{top:F1}"));
        corners[1].Data = Geometry.Parse(FormattableString.Invariant($"M {left + w - len:F1},{top:F1} L {left + w:F1},{top:F1} L {left + w:F1},{top + len:F1}"));
        corners[2].Data = Geometry.Parse(FormattableString.Invariant($"M {left:F1},{top + h - len:F1} L {left:F1},{top + h:F1} L {left + len:F1},{top + h:F1}"));
        corners[3].Data = Geometry.Parse(FormattableString.Invariant($"M {left + w - len:F1},{top + h:F1} L {left + w:F1},{top + h:F1} L {left + w:F1},{top + h - len:F1}"));
        foreach (var c in corners) c.Visibility = Visibility.Visible;
    }

    private void ConfirmAt(PixelPoint cursor)
    {
        if (finished) return;
        finished = true;
        trackTimer.Stop();

        var monitor = DesktopService.MonitorBoundsAt(cursor);
        ActiveMonitorBounds = monitor;
        SelectedPoint = cursor;
        SelectedRegion = ComputeTargetRegion(cursor, monitor);
        Confirmed = SelectedRegion is { IsEmpty: false };

        if (ComponentDispatcher.IsThreadModal)
            DialogResult = Confirmed;
        else
            Close();
    }

    private void CancelSelection()
    {
        if (finished) return;
        finished = true;
        trackTimer.Stop();
        Confirmed = false;
        SelectedPoint = null;
        SelectedRegion = null;

        if (ComponentDispatcher.IsThreadModal)
            DialogResult = false;
        else
            Close();
    }
}
