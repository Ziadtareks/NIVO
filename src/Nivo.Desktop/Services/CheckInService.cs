using Nivo.Core;
namespace Nivo.Services;

public sealed class CheckInService
{
    private string observed = "";
    private DateTime since, lastNudge = DateTime.MinValue;
    public string? Check(Store store, string title, bool quiet, DateTime now)
    {
        if (quiet || store.Incognito || !store.Data.Settings.Proactive) { observed = ""; return null; }
        if (observed != title) { observed = title; since = now; return null; }
        if (now - since < TimeSpan.FromSeconds(120) || now - lastNudge < TimeSpan.FromMinutes(15)) return null;
        if (!store.Data.Settings.DistractionKeywords.Any(k => !string.IsNullOrWhiteSpace(k) && title.Contains(k, StringComparison.OrdinalIgnoreCase))) return null;
        var due = store.Data.Workspace.Where(w => !w.Done && w.Due.HasValue && w.Due.Value >= now && w.Due.Value <= now.AddDays(1)).OrderBy(w => w.Due).FirstOrDefault();
        if (due == null) return null;
        lastNudge = now;
        return store.Data.Settings.Language == "English" ? $"Quick check-in: {due.Title} is due {due.Due:g}. Want to get back to it?" : $"متابعة سريعة: {due.Title} موعدها {due.Due:g}. نرجع نكمّلها؟";
    }
    private static readonly string[] KnownGames = ["minecraft", "terraria", "eldenring", "valorant", "cs2", "dota2", "league of legends", "cyberpunk2077", "gta5", "fortnite", "overwatch", "r5apex", "bg3", "witcher3", "rocketleague", "helldivers2", "palworld", "stardew", "hollow_knight", "genshinimpact"];
    public static bool IsGame(string process, string title) =>
        KnownGames.Any(g => process.Contains(g, StringComparison.OrdinalIgnoreCase)) ||
        (process.Equals("javaw", StringComparison.OrdinalIgnoreCase) && title.Contains("Minecraft", StringComparison.OrdinalIgnoreCase));
}
