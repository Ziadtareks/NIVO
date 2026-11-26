namespace Nivo.Core;

public static class Loc
{
    public static bool IsArabic { get; private set; } = true;
    public static FlowDirection Flow => IsArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    public static void Apply(string? language) => IsArabic = !string.Equals(language, "English", StringComparison.OrdinalIgnoreCase);
    public static string T(string en, string ar) => IsArabic ? ar : en;
}
