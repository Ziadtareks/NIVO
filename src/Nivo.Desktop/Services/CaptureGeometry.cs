namespace Nivo.Services;

public readonly record struct PixelPoint(int X, int Y);

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Left => X;
    public int Top => Y;
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public static class CaptureGeometry
{
    public const int DefaultPointWidth = 760;
    public const int DefaultPointHeight = 460;

    /// <summary>
    /// Clamps a capture region of the desired physical size centered around <paramref name="point"/>
    /// strictly inside <paramref name="monitorBounds"/>. Supports negative coordinates on secondary monitors.
    /// </summary>
    public static PixelRect ClampRegionAroundPoint(
        PixelPoint point,
        PixelRect monitorBounds,
        int desiredWidth = DefaultPointWidth,
        int desiredHeight = DefaultPointHeight)
    {
        if (monitorBounds.IsEmpty)
            return new PixelRect(monitorBounds.X, monitorBounds.Y, 0, 0);

        int width = Math.Clamp(desiredWidth, 1, monitorBounds.Width);
        int height = Math.Clamp(desiredHeight, 1, monitorBounds.Height);
        int x = Math.Clamp(point.X - width / 2, monitorBounds.Left, monitorBounds.Right - width);
        int y = Math.Clamp(point.Y - height / 2, monitorBounds.Top, monitorBounds.Bottom - height);
        return new PixelRect(x, y, width, height);
    }

    /// <summary>
    /// Scales a logical (96-DPI) capture size into physical pixels for a monitor's DPI scale factors.
    /// </summary>
    public static (int Width, int Height) ScaleSizeForDpi(
        int dipWidth,
        int dipHeight,
        double dpiScaleX,
        double dpiScaleY)
    {
        double sx = dpiScaleX > 0 && double.IsFinite(dpiScaleX) ? dpiScaleX : 1.0;
        double sy = dpiScaleY > 0 && double.IsFinite(dpiScaleY) ? dpiScaleY : 1.0;
        return (
            Math.Max(1, (int)Math.Round(dipWidth * sx)),
            Math.Max(1, (int)Math.Round(dipHeight * sy)));
    }

    /// <summary>
    /// Converts a physical screen rectangle relative to its containing monitor's physical origin
    /// into WPF device-independent units (DIPs) on that monitor.
    /// </summary>
    public static Rect PhysicalToMonitorDip(
        PixelRect physicalRect,
        PixelRect monitorBounds,
        double dpiScaleX,
        double dpiScaleY)
    {
        double sx = dpiScaleX > 0 && double.IsFinite(dpiScaleX) ? dpiScaleX : 1.0;
        double sy = dpiScaleY > 0 && double.IsFinite(dpiScaleY) ? dpiScaleY : 1.0;
        return new Rect(
            (physicalRect.X - monitorBounds.X) / sx,
            (physicalRect.Y - monitorBounds.Y) / sy,
            Math.Max(0, physicalRect.Width / sx),
            Math.Max(0, physicalRect.Height / sy));
    }

    /// <summary>
    /// Converts a physical screen point relative to its containing monitor's physical origin
    /// into WPF device-independent units (DIPs) on that monitor.
    /// </summary>
    public static Point PhysicalPointToMonitorDip(
        PixelPoint point,
        PixelRect monitorBounds,
        double dpiScaleX,
        double dpiScaleY)
    {
        double sx = dpiScaleX > 0 && double.IsFinite(dpiScaleX) ? dpiScaleX : 1.0;
        double sy = dpiScaleY > 0 && double.IsFinite(dpiScaleY) ? dpiScaleY : 1.0;
        return new Point(
            (point.X - monitorBounds.X) / sx,
            (point.Y - monitorBounds.Y) / sy);
    }

    /// <summary>
    /// Maps a visual selection rectangle on a WPF crop surface to clamped integer pixel coordinates
    /// within the captured bitmap, accounting for DPI scaling and optional RTL horizontal mirroring.
    /// </summary>
    public static Int32Rect MapSelectionToPixels(
        double visualLeft,
        double visualTop,
        double selectionWidth,
        double selectionHeight,
        double surfaceWidth,
        double surfaceHeight,
        int imagePixelWidth,
        int imagePixelHeight,
        bool isRtl = false)
    {
        if (imagePixelWidth <= 0 || imagePixelHeight <= 0)
            return Int32Rect.Empty;

        double safeSurfaceW = Math.Max(1.0, surfaceWidth);
        double safeSurfaceH = Math.Max(1.0, surfaceHeight);
        double scaleX = imagePixelWidth / safeSurfaceW;
        double scaleY = imagePixelHeight / safeSurfaceH;
        double bmpLeft = isRtl ? (safeSurfaceW - visualLeft - selectionWidth) : visualLeft;

        int x = Math.Clamp((int)Math.Round(bmpLeft * scaleX), 0, imagePixelWidth - 1);
        int y = Math.Clamp((int)Math.Round(visualTop * scaleY), 0, imagePixelHeight - 1);
        int w = Math.Clamp((int)Math.Round(selectionWidth * scaleX), 1, imagePixelWidth - x);
        int h = Math.Clamp((int)Math.Round(selectionHeight * scaleY), 1, imagePixelHeight - y);
        return new Int32Rect(x, y, w, h);
    }

    /// <summary>
    /// Clamps a floating toolbar's top-left position inside a surface with margin padding,
    /// preferring placement below the selection and falling back above or inside when near screen edges.
    /// </summary>
    public static Point ClampFloatingBarPosition(
        double selectionLeft,
        double selectionTop,
        double selectionHeight,
        double barWidth,
        double barHeight,
        double surfaceWidth,
        double surfaceHeight,
        double margin = 16,
        double gap = 12)
    {
        double maxX = Math.Max(margin, surfaceWidth - barWidth - margin);
        double x = Math.Clamp(selectionLeft, margin, maxX);

        double belowY = selectionTop + selectionHeight + gap;
        double aboveY = selectionTop - barHeight - gap;
        double maxY = Math.Max(margin, surfaceHeight - barHeight - margin);

        double y;
        if (belowY + barHeight + margin <= surfaceHeight)
            y = belowY;
        else if (aboveY >= margin)
            y = aboveY;
        else
            y = Math.Clamp(belowY, margin, maxY);

        return new Point(x, y);
    }
}
