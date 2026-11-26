using Nivo.Core;

namespace Nivo.Services;

public static class FocusPolicy
{
    public static string Normalize(string? profile) => (profile?.Trim() ?? "") switch
    {
        "الدراسة" or "دراسة" => "Study",
        "البرمجة" or "برمجة" => "Coding",
        "العمل العميق" => "Deep Work",
        "عام" => "General",
        var p when string.Equals(p, "Study", StringComparison.OrdinalIgnoreCase) => "Study",
        var p when string.Equals(p, "Coding", StringComparison.OrdinalIgnoreCase) => "Coding",
        var p when string.Equals(p, "Deep Work", StringComparison.OrdinalIgnoreCase) => "Deep Work",
        var p when string.Equals(p, "General", StringComparison.OrdinalIgnoreCase) => "General",
        _ => "General"
    };

    public static string DisplayName(string? profile)
    {
        string normalized = Normalize(profile);
        return Loc.IsArabic
            ? normalized switch { "Study" => "الدراسة", "Coding" => "البرمجة", "Deep Work" => "العمل العميق", _ => "عام" }
            : normalized;
    }

    public static bool KeepRunning(string? profile, string? process)
    {
        if (string.IsNullOrWhiteSpace(process)) return false;
        string name = Path.GetFileNameWithoutExtension(process.Trim());
        string normalized = Normalize(profile);
        if (normalized == "Study" && name.StartsWith("foxit", StringComparison.OrdinalIgnoreCase))
            return true;
        string[] allowed = normalized switch
        {
            "Study" => ["msedge", "chrome", "firefox", "acrobat", "foxit", "foxitreader", "foxitpdfreader", "foxitpdfeditor", "sumatrapdf", "notepad", "onenote", "obsidian", "winword", "powerpnt"],
            "Coding" => ["code", "rider", "devenv", "idea64", "cursor", "windsurf", "notepad", "windowsterminal", "wezterm", "pwsh", "cmd", "slack"],
            _ => []
        };
        return allowed.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    public static string ContextProfile(string? process)
    {
        if (string.IsNullOrWhiteSpace(process)) return "";
        string active = Path.GetFileNameWithoutExtension(process.Trim()).ToLowerInvariant();
        if (active.Contains("code") || active.Contains("devenv") || active.Contains("studio") || active.Contains("rider") || active.Contains("idea") || active.Contains("cursor") || active.Contains("windsurf"))
            return "Coding";
        if (active.Contains("winword") || active.Contains("acrobat") || active.Contains("foxit") || active.Contains("sumatrapdf") || active.Contains("powerpnt") || active.Contains("reader") || active.Contains("onenote") || active.Contains("obsidian"))
            return "Study";
        return "";
    }
}
