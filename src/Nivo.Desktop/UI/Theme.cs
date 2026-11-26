namespace Nivo.UI;

public sealed record ThemePalette(string Ink, string Muted, string Accent, string OnAccent, string Panel, string Background, string Sidebar, string Input, string Line, string Selected, string GlassTop, string GlassBottom);

public static class Theme
{
    public static readonly string[] Names = ["Frost", "Horizon", "Frutiger Aero", "Nebula", "Grey", "Pearl", "OLED"];
    public static string Current { get; private set; } = "Frost";
    public static bool IsOled => Current == "OLED";
    public static ThemePalette Palette(string name) => name switch
    {
        "OLED" => new("#F5F5F5", "#AAAAAA", "#7EDFFF", "#00141C", "#000000", "#000000", "#000000", "#000000", "#292929", "#161616", "#FF000000", "#FF000000"),
        "Horizon" => new("#F3EDFF", "#B8ADC8", "#D8C5FF", "#291B43", "#2D263B", "#1B1729", "#211B30", "#211B30", "#504363", "#49385E", "#ED493B65", "#F5251E38"),
        "Frutiger Aero" => new("#123B4D", "#466A75", "#076B66", "#FFFFFF", "#F0FFF8", "#DAF1F3", "#CDE7EA", "#F5FFFC", "#9DCACC", "#B7DFCD", "#EEFAFFFC", "#E6BBE5E5"),
        "Nebula" => new("#EEF3FF", "#A7B6D4", "#A9C8FF", "#132743", "#222E47", "#141F34", "#19263D", "#18253B", "#405575", "#344E75", "#EE354668", "#F51B2943"),
        "Grey" => new("#ECEFF2", "#ADB6C0", "#D7E1EB", "#242C35", "#30373F", "#22282F", "#282F37", "#252C34", "#525D68", "#475462", "#EE48535F", "#F52C343D"),
        "Pearl" => new("#303E4B", "#667681", "#435D78", "#FFFFFF", "#FFFFFF", "#EDEFF1", "#E2E6E9", "#F9FAFB", "#C6CED5", "#D4DFEA", "#F5FFFFFF", "#EEE4E9ED"),
        _ => new("#0A1F6E", "#4E688D", "#1564D4", "#FFFFFF", "#F6FBFF", "#E4F2FD", "#D6E9FA", "#FFFFFF", "#A8C9EA", "#C1DDF7", "#F5FFFFFF", "#E2D9EDFC")
    };
    public static void Apply(string name)
    {
        Current = name;
        var p = Palette(name); var r = Application.Current.Resources;
        r["Ink"] = Ui.Brush(p.Ink); r["Muted"] = Ui.Brush(p.Muted); r["Accent"] = Ui.Brush(p.Accent); r["OnAccent"] = Ui.Brush(p.OnAccent);
        r["Panel"] = Ui.Brush(p.Panel); r["WindowBackground"] = Ui.Brush(p.Background);
        var side = Ui.Brush(p.Sidebar).Color; var bg = Ui.Brush(p.Background).Color;
        r["Sidebar"] = new LinearGradientBrush(bg, side, 160);
        r["Input"] = Ui.Brush(p.Input); r["Hairline"] = Ui.Brush(p.Line); r["Selected"] = Ui.Brush(p.Selected);
        r["Warning"] = Ui.Brush(name is "Horizon" or "Nebula" or "Grey" or "OLED" ? "#F2C084" : "#865512");
        var accent = Ui.Brush(p.Accent).Color; var top = Color.FromRgb((byte)(accent.R * .78 + 255 * .22), (byte)(accent.G * .78 + 255 * .22), (byte)(accent.B * .78 + 255 * .22));
        r["ActiveGlass"] = new LinearGradientBrush(top, accent, 90);
        r["Glass"] = new LinearGradientBrush(Ui.Brush(p.GlassTop).Color, Ui.Brush(p.GlassBottom).Color, 90);
        ShellTheme.Apply(name); Changed?.Invoke();
    }
    public static event Action? Changed;
}
