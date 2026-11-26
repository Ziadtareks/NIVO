using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Nivo.Core;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Nivo.Services;

public static class DesktopService
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")] private static extern int GetClassName(IntPtr handle, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref NativeMonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect rect, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr lparam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lparam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr handle, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr handle, uint msg, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public PixelRect ToPixelRect() => new(Left, Top, Math.Max(0, Right - Left), Math.Max(0, Bottom - Top));
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }
    private readonly record struct CachedWindowIdentity(string Process, string ClassName, long TickMs);
    private static readonly object WindowIdentityCacheLock = new();
    private static readonly Dictionary<(IntPtr Handle, uint Pid), CachedWindowIdentity> WindowIdentityCache = new();
    private const long WindowIdentityCacheTtlMs = 30_000;
    private const int WindowIdentityCacheMaxEntries = 32;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private const int QunsRunningD3dFullScreen = 3;
    public static void OpenStartMenu() { keybd_event(0x5B, 0, 0, UIntPtr.Zero); keybd_event(0x5B, 0, 2, UIntPtr.Zero); }
    public static void ToggleMute() { keybd_event(0xAD, 0, 0, UIntPtr.Zero); keybd_event(0xAD, 0, 2, UIntPtr.Zero); }
    public static bool IsNetworkOnline() { try { return System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable(); } catch { return true; } }
    public static void RevealInExplorer(string path) { if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Path.GetFullPath(path)}\"") { UseShellExecute = true }); }
    public sealed record RunningWindow(IntPtr Handle, string Title, string Process, string Executable, bool Active, bool Minimized);
    public static List<RunningWindow> RunningWindows()
    {
        var foreground = GetForegroundWindow();
        var list = new List<RunningWindow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        EnumWindows((h, _) => {
            if (!IsWindowVisible(h) || GetWindow(h, 4) != IntPtr.Zero) return true;
            int ex = GetWindowLong(h, -20);
            if ((ex & 0x00000080) != 0 && (ex & 0x00040000) == 0) return true; // skip tool windows unless WS_EX_APPWINDOW
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == 0 || pid == Environment.ProcessId) return true;
            var sb = new StringBuilder(256); GetWindowText(h, sb, sb.Capacity);
            string title = sb.ToString().Trim();
            if (title.Length == 0 || title is "Program Manager" or "Windows Input Experience") return true;
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                string pname = proc.ProcessName;
                if (pname.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) || pname.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase) || pname.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase)) return true;
                string exe = ""; try { exe = proc.MainModule?.FileName ?? ""; } catch { }
                string key = exe.Length > 0 ? exe : pname;
                if (seen.Add(key)) list.Add(new(h, title, pname, exe, h == foreground, IsIconic(h)));
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return list.Take(10).ToList();
    }
    public static void FocusWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        if (IsIconic(handle)) ShowWindow(handle, 9);
        SetForegroundWindow(handle);
    }
    public static void CloseWindow(IntPtr handle)
    {
        if (handle != IntPtr.Zero) PostMessage(handle, 0x0010, IntPtr.Zero, IntPtr.Zero);
    }
    public static (string Title, string Process, int Pid) ActiveWindow()
    {
        return WindowInfo(GetForegroundWindow());
    }
    public static ForegroundWindowInfo ForegroundWindow()
    {
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero) return default;
        var root = GetAncestor(handle, 2); // GA_ROOT
        return InspectWindow(root != IntPtr.Zero ? root : handle, checkD3dState: true);
    }
    public static ForegroundWindowInfo InspectWindow(IntPtr handle, bool checkD3dState = false)
    {
        if (handle == IntPtr.Zero || !IsWindow(handle)) return default;
        bool isDesktopOrShell = handle == GetDesktopWindow() || handle == GetShellWindow();
        GetWindowThreadProcessId(handle, out uint pid);
        var (processName, className) = GetCachedWindowIdentity(handle, pid);

        var titleBuf = new StringBuilder(512);
        GetWindowText(handle, titleBuf, titleBuf.Capacity);
        string title = titleBuf.ToString();

        bool visible = IsWindowVisible(handle);
        bool minimized = IsIconic(handle);
        bool maximized = IsZoomed(handle);
        bool cloaked = IsWindowCloaked(handle);
        uint style = unchecked((uint)GetWindowLong(handle, -16));
        uint exStyle = unchecked((uint)GetWindowLong(handle, -20));

        PixelRect windowBounds = TryGetVisibleWindowBounds(handle);
        var (monitorBounds, monitorWorkArea) = GetMonitorRectsForWindow(handle);

        bool d3dExclusive = false;
        if (checkD3dState && visible && !minimized && !cloaked && !isDesktopOrShell && pid != 0 && pid != Environment.ProcessId)
        {
            d3dExclusive = IsD3dExclusiveFullscreenActive();
        }

        return new ForegroundWindowInfo(
            handle,
            (int)pid,
            title,
            processName,
            className,
            visible,
            minimized,
            maximized,
            cloaked,
            style,
            exStyle,
            windowBounds,
            monitorBounds,
            monitorWorkArea,
            isDesktopOrShell,
            d3dExclusive);
    }
    public static (string Title, string Process, int Pid) WindowAtPoint(PixelPoint point)
    {
        var hit = WindowFromPoint(new NativePoint { X = point.X, Y = point.Y });
        if (hit == IntPtr.Zero) return ("", "", 0);
        var root = GetAncestor(hit, 2); // GA_ROOT
        return WindowInfo(root != IntPtr.Zero ? root : hit);
    }
    private static (string Title, string Process, int Pid) WindowInfo(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return ("", "", 0);
        var text = new StringBuilder(1024); GetWindowText(handle, text, text.Capacity); GetWindowThreadProcessId(handle, out uint pid);
        var (name, _) = GetCachedWindowIdentity(handle, pid);
        return (text.ToString(), name, (int)pid);
    }
    private static (string Process, string ClassName) GetCachedWindowIdentity(IntPtr handle, uint pid)
    {
        long now = Environment.TickCount64;
        var key = (handle, pid);
        lock (WindowIdentityCacheLock)
        {
            if (WindowIdentityCache.TryGetValue(key, out var cached) && now - cached.TickMs <= WindowIdentityCacheTtlMs)
                return (cached.Process, cached.ClassName);
        }

        string processName = ResolveProcessName(pid);
        var classBuf = new StringBuilder(256);
        string className = GetClassName(handle, classBuf, classBuf.Capacity) > 0 ? classBuf.ToString() : "";

        lock (WindowIdentityCacheLock)
        {
            if (WindowIdentityCache.Count >= WindowIdentityCacheMaxEntries)
                WindowIdentityCache.Clear();
            WindowIdentityCache[key] = new CachedWindowIdentity(processName, className, now);
        }
        return (processName, className);
    }
    private static string ResolveProcessName(uint pid)
    {
        if (pid == 0) return "";
        IntPtr procHandle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (procHandle != IntPtr.Zero)
        {
            try
            {
                uint capacity = 512;
                var buffer = new StringBuilder((int)capacity);
                if (QueryFullProcessImageName(procHandle, 0, buffer, ref capacity) && capacity > 0)
                    return Path.GetFileNameWithoutExtension(buffer.ToString());
            }
            finally { CloseHandle(procHandle); }
        }
        try { using var proc = Process.GetProcessById((int)pid); return proc.ProcessName; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        { Trace.TraceInformation("NIVO foreground process identity unavailable ({0}).", ex.GetType().Name); return ""; }
    }
    private static PixelRect TryGetVisibleWindowBounds(IntPtr handle)
    {
        try
        {
            if (DwmGetWindowAttribute(handle, DwmwaExtendedFrameBounds, out NativeRect dwmRect, Marshal.SizeOf<NativeRect>()) == 0)
            {
                var rect = dwmRect.ToPixelRect();
                if (!rect.IsEmpty) return rect;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        { Trace.TraceWarning("NIVO DWM bounds unavailable ({0}); using window bounds.", ex.GetType().Name); }
        return GetWindowRect(handle, out var winRect) ? winRect.ToPixelRect() : default;
    }
    private static bool IsWindowCloaked(IntPtr handle)
    {
        try { return DwmGetWindowAttribute(handle, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        { Trace.TraceWarning("NIVO DWM cloaking query unavailable ({0}).", ex.GetType().Name); return false; }
    }
    private static (PixelRect MonitorBounds, PixelRect WorkArea) GetMonitorRectsForWindow(IntPtr handle)
    {
        var hmon = MonitorFromWindow(handle, 2); // MONITOR_DEFAULTTONEAREST
        if (hmon != IntPtr.Zero)
        {
            var info = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
            if (GetMonitorInfo(hmon, ref info))
                return (info.Monitor.ToPixelRect(), info.Work.ToPixelRect());
        }
        var screen = Forms.Screen.PrimaryScreen;
        if (screen == null) return (default, default);
        var b = screen.Bounds;
        var w = screen.WorkingArea;
        return (new PixelRect(b.X, b.Y, b.Width, b.Height), new PixelRect(w.X, w.Y, w.Width, w.Height));
    }
    private static bool IsD3dExclusiveFullscreenActive()
    {
        try { return SHQueryUserNotificationState(out int state) == 0 && state == QunsRunningD3dFullScreen; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        { Trace.TraceWarning("NIVO D3D fullscreen query unavailable ({0}).", ex.GetType().Name); return false; }
    }
    public static PixelPoint CursorPosition()
    {
        if (GetCursorPos(out var p)) return new PixelPoint(p.X, p.Y);
        var b = Forms.Screen.PrimaryScreen!.Bounds;
        return new PixelPoint(b.Left + b.Width / 2, b.Top + b.Height / 2);
    }
    public static PixelRect MonitorBoundsAt(PixelPoint point)
    {
        var screen = Forms.Screen.FromPoint(new Drawing.Point(point.X, point.Y)) ?? Forms.Screen.PrimaryScreen!;
        var b = screen.Bounds;
        return new PixelRect(b.X, b.Y, b.Width, b.Height);
    }
    public static (double ScaleX, double ScaleY) MonitorDpiScaleAt(PixelPoint point)
    {
        try
        {
            var hmon = MonitorFromPoint(new NativePoint { X = point.X, Y = point.Y }, 2); // MONITOR_DEFAULTTONEAREST
            if (hmon != IntPtr.Zero && GetDpiForMonitor(hmon, 0, out uint dpiX, out uint dpiY) == 0 && dpiX > 0 && dpiY > 0)
                return (dpiX / 96.0, dpiY / 96.0);
        }
        catch { }
        return (1.0, 1.0);
    }
    public static void FlushCompositor()
    {
        try { _ = DwmFlush(); } catch { }
    }
    public static BitmapSource CaptureRegion(PixelRect region)
    {
        if (region.IsEmpty) throw new ArgumentOutOfRangeException(nameof(region), "Capture region must have positive width and height.");
        using var bmp = new Drawing.Bitmap(region.Width, region.Height, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(region.X, region.Y, 0, 0, new Drawing.Size(region.Width, region.Height), Drawing.CopyPixelOperation.SourceCopy);
        using var stream = new MemoryStream(); bmp.Save(stream, Drawing.Imaging.ImageFormat.Png); stream.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }
    public static BitmapSource CaptureMonitorAtCursor(out PixelRect monitorBounds)
    {
        var cursor = CursorPosition();
        monitorBounds = MonitorBoundsAt(cursor);
        return CaptureRegion(monitorBounds);
    }
    public static BitmapSource CapturePrimary(bool hover = false)
    {
        if (hover)
        {
            var cursor = CursorPosition();
            var monitor = MonitorBoundsAt(cursor);
            return CaptureRegion(CaptureGeometry.ClampRegionAroundPoint(cursor, monitor));
        }
        var b = Forms.Screen.PrimaryScreen!.Bounds;
        return CaptureRegion(new PixelRect(b.X, b.Y, b.Width, b.Height));
    }
    public static void SavePng(BitmapSource bitmap, string file)
    {
        using var stream = File.Create(file); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
    }
    public static void OpenTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) throw new InvalidDataException("Empty app or link.");
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
        { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); return; }
        if (target is "explorer.exe" or "notepad.exe" or "calc.exe")
        { var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows); var executable = target == "explorer.exe" ? Path.Combine(windows, target) : Path.Combine(windows, "System32", target); Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true }); return; }
        var path = Path.GetFullPath(target);
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("App or file not found.", path);
        if (File.Exists(path) && !new[] { ".exe", ".lnk", ".pdf", ".docx", ".pptx", ".xlsx", ".txt", ".md", ".png", ".jpg", ".mp4", ".wav", ".json" }.Contains(Path.GetExtension(path).ToLowerInvariant())) throw new InvalidOperationException("This file type cannot be opened from NIVO.");
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
    public sealed class Hotkeys : IDisposable
    {
        private readonly IntPtr handle; private readonly HwndSource source;
        private bool emergency;
        public event Action<int>? Pressed;
        public List<int> Failed { get; } = [];
        public static (uint Modifiers, uint Key) Parse(string text)
        {
            var gesture = (System.Windows.Input.KeyGesture?)new System.Windows.Input.KeyGestureConverter().ConvertFromInvariantString(text) ?? throw new InvalidDataException("Invalid keyboard shortcut.");
            if ((gesture.Modifiers & (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt)) == 0) throw new InvalidDataException("A global shortcut needs Ctrl or Alt.");
            uint modifiers = 0x4000;
            if (gesture.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt)) modifiers |= 1;
            if (gesture.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control)) modifiers |= 2;
            if (gesture.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift)) modifiers |= 4;
            if (gesture.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Windows)) modifiers |= 8;
            return (modifiers, (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(gesture.Key));
        }
        public Hotkeys(Window window, Settings settings)
        {
            handle = new WindowInteropHelper(window).EnsureHandle(); source = HwndSource.FromHwnd(handle); source.AddHook(Hook);
            var names = new[] { "ask", "island", "incognito", "crop" };
            var defaults = new Settings().Shortcuts;
            for (int i = 0; i < names.Length; i++)
            {
                var key = Parse(settings.Shortcuts.TryGetValue(names[i], out string? saved) ? saved : defaults[names[i]]);
                if (!RegisterHotKey(handle, i + 1, key.Modifiers, key.Key)) Failed.Add(i + 1);
            }
        }
        private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wparam, IntPtr lparam, ref bool handled)
        { if (msg == 0x0312) { Pressed?.Invoke(wparam.ToInt32()); handled = true; } return IntPtr.Zero; }
        public void SetEmergencyStop(bool enabled)
        {
            if (enabled == emergency) return;
            emergency = enabled;
            if (enabled) { if (!RegisterHotKey(handle, 5, 0x4000, 0x1B)) Failed.Add(5); }
            else UnregisterHotKey(handle, 5);
        }
        public void Dispose() { foreach (int id in new[] { 1, 2, 3, 4, 5 }) UnregisterHotKey(handle, id); source.RemoveHook(Hook); }
    }
}
