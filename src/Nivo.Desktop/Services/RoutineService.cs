using Nivo.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;
namespace Nivo.Services;

public static class RoutineService
{
    private delegate bool EnumProc(IntPtr handle, IntPtr value);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr value);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder title, int length);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr handle, int command);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    private static List<(IntPtr Handle, string Executable, string Title, NativeRect Rect)> Visible()
    {
        var windows = new List<(IntPtr, string, string, NativeRect)>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || IsIconic(handle)) return true;
            GetWindowThreadProcessId(handle, out uint pid); if (pid == Environment.ProcessId) return true;
            try
            {
                using var process = Process.GetProcessById((int)pid); string? path = process.MainModule?.FileName;
                var title = new StringBuilder(512); GetWindowText(handle, title, title.Capacity);
                if (path != null && title.Length > 0 && GetWindowRect(handle, out var rect) && rect.Right - rect.Left > 100 && rect.Bottom - rect.Top > 100) windows.Add((handle, path, title.ToString(), rect));
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return windows;
    }
    public static string[] OpenExecutables() => Visible().Select(w => w.Executable).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
    public static List<SavedWindow> Capture(IReadOnlyCollection<string> selectedTargets)
    {
        var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
        return Visible().Where(w => selectedTargets.Any(t => string.Equals(t, w.Executable, StringComparison.OrdinalIgnoreCase) || (!Path.IsPathRooted(t) && string.Equals(t, Path.GetFileName(w.Executable), StringComparison.OrdinalIgnoreCase)))).Take(12).Select(w => new SavedWindow
        {
            Executable = w.Executable, Title = w.Title, X = (w.Rect.Left - screen.Left) / (double)screen.Width, Y = (w.Rect.Top - screen.Top) / (double)screen.Height, Width = (w.Rect.Right - w.Rect.Left) / (double)screen.Width, Height = (w.Rect.Bottom - w.Rect.Top) / (double)screen.Height
        }).ToList();
    }
    public static string ResolveFocusProfile(Routine routine)
    {
        if (!string.IsNullOrWhiteSpace(routine.FocusProfile))
            return FocusPolicy.Normalize(routine.FocusProfile);
        if (!routine.Focus || string.IsNullOrWhiteSpace(routine.Name))
            return "";
        return routine.Name.Trim() switch
        {
            "الدراسة" or "دراسة" => "Study",
            "البرمجة" or "برمجة" => "Coding",
            "العمل العميق" => "Deep Work",
            var n when string.Equals(n, "Study", StringComparison.OrdinalIgnoreCase) => "Study",
            var n when string.Equals(n, "Coding", StringComparison.OrdinalIgnoreCase) => "Coding",
            var n when string.Equals(n, "Deep Work", StringComparison.OrdinalIgnoreCase) => "Deep Work",
            _ => ""
        };
    }
    public static void ApplyFocus(Routine routine, Store store)
    {
        store.Data.Settings.Focus = routine.Focus;
        if (routine.Focus)
        {
            string profile = ResolveFocusProfile(routine);
            if (!string.IsNullOrWhiteSpace(profile))
                store.Data.Settings.FocusProfile = profile;
        }
        store.Save();
    }
    public static async Task<string> Run(Routine routine, Store store, Action<string> progress, CancellationToken ct)
    {
        foreach (string target in routine.Targets) { ct.ThrowIfCancellationRequested(); progress("Opening " + Path.GetFileName(target)); DesktopService.OpenTarget(target); await Task.Delay(150, ct); }
        if (!string.IsNullOrWhiteSpace(routine.Music)) { ct.ThrowIfCancellationRequested(); DesktopService.OpenTarget(routine.Music); }
        ApplyFocus(routine, store);
        var pending = routine.Windows.ToList(); var placed = new HashSet<IntPtr>(); int restored = 0;
        for (int attempt = 0; attempt < 20 && pending.Count > 0; attempt++)
        {
            ct.ThrowIfCancellationRequested(); var visible = Visible(); var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
            foreach (var saved in pending.ToArray())
            {
                var matches = visible.Where(w => !placed.Contains(w.Handle) && string.Equals(w.Executable, saved.Executable, StringComparison.OrdinalIgnoreCase)).ToArray();
                var window = matches.FirstOrDefault(w => w.Title == saved.Title); if (window.Handle == IntPtr.Zero) window = matches.FirstOrDefault(); if (window.Handle == IntPtr.Zero) continue;
                int width = (int)Math.Clamp(saved.Width * screen.Width, 320, screen.Width), height = (int)Math.Clamp(saved.Height * screen.Height, 240, screen.Height);
                int x = (int)Math.Clamp(screen.Left + saved.X * screen.Width, screen.Left, screen.Right - width), y = (int)Math.Clamp(screen.Top + saved.Y * screen.Height, screen.Top, screen.Bottom - height);
                ShowWindow(window.Handle, 9); if (SetWindowPos(window.Handle, IntPtr.Zero, x, y, width, height, 0x0014)) restored++;
                placed.Add(window.Handle); pending.Remove(saved);
            }
            if (pending.Count > 0) { progress("Restoring saved window positions…"); await Task.Delay(250, ct); }
        }
        string focusSummary = routine.Focus ? $"on ({FocusPolicy.Normalize(store.Data.Settings.FocusProfile)})" : "off";
        return $"Opened {routine.Targets.Count} apps/links. Restored {restored}/{routine.Windows.Count} window positions. Focus {focusSummary}.";
    }
}
