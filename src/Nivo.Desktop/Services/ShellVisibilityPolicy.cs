namespace Nivo.Services;

public readonly record struct ForegroundWindowInfo(
    IntPtr Handle,
    int Pid,
    string Title,
    string Process,
    string ClassName,
    bool IsVisible,
    bool IsMinimized,
    bool IsMaximized,
    bool IsCloaked,
    uint Style,
    uint ExStyle,
    PixelRect WindowBounds,
    PixelRect MonitorBounds,
    PixelRect MonitorWorkArea,
    bool IsDesktopOrShellHandle = false,
    bool IsD3dExclusiveFullscreen = false);

public readonly record struct ExternalSuppressionState(
    bool AutoGaming,
    bool AutoFullscreen,
    IntPtr SourceHandle)
{
    public static readonly ExternalSuppressionState None = new(false, false, IntPtr.Zero);
    public bool Any => AutoGaming || AutoFullscreen;
}

public readonly record struct ShellVisibilityInput(
    bool DesktopEnabled,
    bool ReplaceTaskbar,
    bool ManualGaming,
    bool IslandHiddenByUser,
    bool Capturing,
    int OwnProcessId,
    ForegroundWindowInfo Foreground,
    ExternalSuppressionState PreviousExternal,
    bool PreviousSourceStillSuppressed);

public readonly record struct ShellVisibilityDecision(
    ExternalSuppressionState ExternalState,
    bool Suppressed,
    bool ShowDock,
    bool ShowIsland,
    bool HideTaskbar,
    string Reason);

public static class ShellVisibilityPolicy
{
    public const uint WsCaption = 0x00C00000;
    public const uint WsBorder = 0x00800000;
    public const uint WsDlgFrame = 0x00400000;
    public const uint WsThickFrame = 0x00040000;
    public const uint WsMaximize = 0x01000000;
    public const uint WsPopup = 0x80000000;
    public const uint WsExToolWindow = 0x00000080;
    public const uint WsExAppWindow = 0x00040000;
    public const int FullscreenTolerancePx = 2;
    public const int WorkAreaInsetThresholdPx = 8;

    private static readonly HashSet<string> ShellBackgroundClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland",
        "TaskSwitcherWnd",
        "MultitaskingViewFrame",
        "TaskListThumbnailWnd",
        "ForegroundStaging",
        "XamlExplorerHostIslandWindow",
        "ApplicationManager_DesktopShellWindow",
        "Shell_InputSwitchTopLevelWindow",
        "EdgeUiInputTopWndClass",
        "NativeHWNDHost",
        "ImmersiveLauncher"
    };

    private static readonly HashSet<string> ShellHostProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "StartMenuExperienceHost",
        "ShellExperienceHost",
        "SearchHost",
        "SearchApp",
        "SearchUI",
        "TextInputHost",
        "InputApp",
        "LockApp",
        "LogonUI",
        "ScreenClippingHost",
        "dwm",
        "csrss",
        "winlogon"
    };

    public static bool IsOwnWindow(ForegroundWindowInfo window, int ownProcessId) =>
        ownProcessId > 0 && window.Pid == ownProcessId;

    public static bool IsShellOrDesktopWindow(ForegroundWindowInfo window)
    {
        if (window.Handle == IntPtr.Zero || window.IsDesktopOrShellHandle)
            return true;

        string className = (window.ClassName ?? "").Trim();
        if (className.Length > 0 && ShellBackgroundClasses.Contains(className))
            return true;

        string processName = NormalizeProcessName(window.Process);
        if (processName.Length > 0 && ShellHostProcesses.Contains(processName))
            return true;

        if (processName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals((window.Title ?? "").Trim(), "Program Manager", StringComparison.OrdinalIgnoreCase))
                return true;

            // Only real File Explorer folder windows (CabinetWClass / ExploreWClass) are user apps;
            // all other explorer.exe surfaces are desktop/taskbar/shell chrome.
            bool isFileExplorerWindow =
                className.Equals("CabinetWClass", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("ExploreWClass", StringComparison.OrdinalIgnoreCase);
            if (!isFileExplorerWindow)
                return true;
        }

        return false;
    }

    public static bool CoversRect(PixelRect outer, PixelRect inner, int tolerancePx = FullscreenTolerancePx)
    {
        if (outer.IsEmpty || inner.IsEmpty)
            return false;

        int tol = Math.Max(0, tolerancePx);
        return outer.Left <= inner.Left + tol
            && outer.Top <= inner.Top + tol
            && outer.Right >= inner.Right - tol
            && outer.Bottom >= inner.Bottom - tol;
    }

    public static bool HasWorkAreaInset(PixelRect monitorBounds, PixelRect monitorWorkArea, int thresholdPx = WorkAreaInsetThresholdPx)
    {
        if (monitorBounds.IsEmpty || monitorWorkArea.IsEmpty)
            return false;

        int threshold = Math.Max(0, thresholdPx);
        int leftInset = Math.Max(0, monitorWorkArea.Left - monitorBounds.Left);
        int topInset = Math.Max(0, monitorWorkArea.Top - monitorBounds.Top);
        int rightInset = Math.Max(0, monitorBounds.Right - monitorWorkArea.Right);
        int bottomInset = Math.Max(0, monitorBounds.Bottom - monitorWorkArea.Bottom);
        return leftInset > threshold
            || topInset > threshold
            || rightInset > threshold
            || bottomInset > threshold;
    }

    public static bool HasStandardWindowChrome(uint style) =>
        (style & WsCaption) == WsCaption || (style & WsThickFrame) != 0;

    public static bool IsGameWindow(ForegroundWindowInfo window, int ownProcessId)
    {
        if (window.Handle == IntPtr.Zero || window.Pid <= 0) return false;
        if (IsOwnWindow(window, ownProcessId)) return false;
        if (IsShellOrDesktopWindow(window)) return false;
        if (!window.IsVisible || window.IsMinimized || window.IsCloaked) return false;
        return CheckInService.IsGame(window.Process ?? "", window.Title ?? "");
    }

    public static bool IsFullscreenWindow(ForegroundWindowInfo window, int ownProcessId)
    {
        if (window.Handle == IntPtr.Zero || window.Pid <= 0) return false;
        if (IsOwnWindow(window, ownProcessId)) return false;
        if (IsShellOrDesktopWindow(window)) return false;
        if (!window.IsVisible || window.IsMinimized || window.IsCloaked) return false;

        bool isNonAppToolWindow = (window.ExStyle & WsExToolWindow) != 0 && (window.ExStyle & WsExAppWindow) == 0;
        if (isNonAppToolWindow && !window.IsD3dExclusiveFullscreen)
            return false;

        if (window.MonitorBounds.IsEmpty || window.WindowBounds.IsEmpty)
            return window.IsD3dExclusiveFullscreen;

        if (!CoversRect(window.WindowBounds, window.MonitorBounds, FullscreenTolerancePx))
            return false;

        if (window.IsD3dExclusiveFullscreen)
            return true;

        // When a docked taskbar reserves part of the monitor, a normal maximized app stops at MonitorWorkArea
        // and cannot cover MonitorBounds; covering the full monitor over the taskbar indicates true fullscreen.
        if (HasWorkAreaInset(window.MonitorBounds, window.MonitorWorkArea, WorkAreaInsetThresholdPx))
            return true;

        // On monitors without a docked taskbar (e.g., secondary monitors or auto-hide taskbar),
        // distinguish standard maximized/titled programs from borderless/F11 fullscreen apps.
        return !HasStandardWindowChrome(window.Style);
    }

    public static bool IsSourceWindowStillSuppressed(ForegroundWindowInfo source, ExternalSuppressionState previous, int ownProcessId)
    {
        if (!previous.Any || source.Handle == IntPtr.Zero) return false;
        if (previous.AutoGaming && IsGameWindow(source, ownProcessId)) return true;
        if (previous.AutoFullscreen && IsFullscreenWindow(source, ownProcessId)) return true;
        return false;
    }

    public static ShellVisibilityDecision Evaluate(ShellVisibilityInput input)
    {
        ExternalSuppressionState nextExternal;

        if (input.Foreground.Handle == IntPtr.Zero || IsOwnWindow(input.Foreground, input.OwnProcessId))
        {
            bool preserve = input.PreviousExternal.Any
                && (input.PreviousExternal.SourceHandle == IntPtr.Zero || input.PreviousSourceStillSuppressed);
            nextExternal = preserve ? input.PreviousExternal : ExternalSuppressionState.None;
        }
        else if (IsShellOrDesktopWindow(input.Foreground))
        {
            nextExternal = ExternalSuppressionState.None;
        }
        else
        {
            bool isGame = IsGameWindow(input.Foreground, input.OwnProcessId);
            bool isFullscreen = IsFullscreenWindow(input.Foreground, input.OwnProcessId);
            nextExternal = isGame || isFullscreen
                ? new ExternalSuppressionState(isGame, isFullscreen, input.Foreground.Handle)
                : ExternalSuppressionState.None;
        }

        return Decide(
            nextExternal,
            input.DesktopEnabled,
            input.ReplaceTaskbar,
            input.ManualGaming,
            input.IslandHiddenByUser,
            input.Capturing);
    }

    public static ShellVisibilityDecision Decide(
        ExternalSuppressionState externalState,
        bool desktopEnabled,
        bool replaceTaskbar,
        bool manualGaming,
        bool islandHiddenByUser,
        bool capturing)
    {
        bool suppressed = manualGaming || externalState.Any;
        bool showDock = !capturing && !suppressed && desktopEnabled;
        bool showIsland = !capturing && !suppressed && !islandHiddenByUser;
        bool hideTaskbar = showDock && replaceTaskbar;

        string reason = capturing
            ? "capturing"
            : manualGaming
                ? "manual-gaming"
                : externalState.AutoGaming
                    ? "game"
                    : externalState.AutoFullscreen
                        ? "fullscreen"
                        : "normal";

        return new ShellVisibilityDecision(externalState, suppressed, showDock, showIsland, hideTaskbar, reason);
    }

    private static string NormalizeProcessName(string? process)
    {
        if (string.IsNullOrWhiteSpace(process)) return "";
        string trimmed = process.Trim();
        return Path.GetFileNameWithoutExtension(trimmed);
    }
}
