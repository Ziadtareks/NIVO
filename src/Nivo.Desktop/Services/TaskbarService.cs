using System.Runtime.InteropServices;
using System.Diagnostics;

namespace Nivo.Services;

public static class TaskbarService
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? windowName);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    private static bool hidden;
    private static Process? guard;
    public static void SetHidden(bool value)
    {
        if (value)
        {
            // Separate watchdog restores Explorer's taskbar if this process crashes.
            guard = EnsureGuard(guard, () => {
                var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                info.ArgumentList.Add("--taskbar-guard"); info.ArgumentList.Add(Environment.ProcessId.ToString());
                return Process.Start(info) ?? throw new InvalidOperationException("Cannot start the taskbar recovery process.");
            });
        }
        if (hidden == value) return;
        ShowWindow(FindWindow("Shell_TrayWnd", null), value ? 0 : 5); hidden = value;
    }
    internal static Process EnsureGuard(Process? current, Func<Process> start)
    {
        if (current is { HasExited: false }) return current;
        current?.Dispose();
        return start();
    }
    public static void Guard(int pid)
    {
        try { using var parent = Process.GetProcessById(pid); parent.WaitForExit(); }
        catch (ArgumentException) { /* Parent already exited; restore Explorer below. */ }
        catch (InvalidOperationException ex) { Trace.TraceWarning("NIVO taskbar guard lost its parent: {0}", ex.GetType().Name); }
        catch (System.ComponentModel.Win32Exception ex) { Trace.TraceWarning("NIVO taskbar guard cannot wait on its parent: {0}", ex.NativeErrorCode); }
        ShowWindow(FindWindow("Shell_TrayWnd", null), 5);
    }
    public static void Restore() { ShowWindow(FindWindow("Shell_TrayWnd", null), 5); hidden = false; }
}
