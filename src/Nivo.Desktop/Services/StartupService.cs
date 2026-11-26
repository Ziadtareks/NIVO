using System.Runtime.InteropServices;
using Nivo.Core;

namespace Nivo.Services;

public static class StartupService
{
    private const string ShortcutName = "NIVO.lnk";
    public static bool IsEnabled => File.Exists(Path.Combine(StartupDirectory, ShortcutName));
    private static string StartupDirectory => Environment.GetFolderPath(Environment.SpecialFolder.Startup);

    public static void SetEnabled(bool enabled) => SetEnabled(enabled,
        Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate NIVO's executable."), StartupDirectory);

    internal static void SetEnabled(bool enabled, string executable, string startupDirectory)
    {
        if (!Path.IsPathFullyQualified(startupDirectory)) throw new InvalidDataException("An absolute startup folder is required.");
        string shortcutPath = Path.Combine(startupDirectory, ShortcutName);
        if (!enabled) { File.Delete(shortcutPath); return; }
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose an existing NIVO executable before enabling startup.");
        Directory.CreateDirectory(startupDirectory);
        object? shell = null, shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows shortcuts are unavailable."));
            dynamic windowsShell = shell!;
            shortcut = windowsShell.CreateShortcut(shortcutPath);
            dynamic link = shortcut;
            link.TargetPath = Path.GetFullPath(executable);
            link.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable));
            link.Arguments = "";
            link.Description = "Start NIVO when you sign in to Windows";
            link.IconLocation = Path.GetFullPath(executable) + ",0";
            link.Save();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException)
        { throw new InvalidOperationException(Loc.T("Windows startup could not be updated. Check access to your Startup folder.", "تعذّر تحديث التشغيل مع Windows. تحقق من صلاحية الوصول لمجلد بدء التشغيل."), ex); }
        finally
        {
            if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}
