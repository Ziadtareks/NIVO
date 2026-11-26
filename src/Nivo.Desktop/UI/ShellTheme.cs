namespace Nivo.UI;

public static class ShellTheme
{
    private static string current = "Frost";
    private static readonly Dictionary<(string Role, string Color), SolidColorBrush> brushes = [];
    public static readonly LinearGradientBrush Glass = new(Colors.White, Color.FromRgb(219, 237, 250), 90);
    public static SolidColorBrush Ink(string color) => Get("ink", color);
    public static SolidColorBrush Surface(string color) => Get("surface", color);
    public static SolidColorBrush Line(string color) => Get("line", color);
    private static SolidColorBrush Get(string role, string color)
    {
        var key = (role, color); if (!brushes.TryGetValue(key, out var brush)) brushes[key] = brush = new SolidColorBrush(Resolve(role, color)); return brush;
    }
    private static Color Resolve(string role, string value)
    {
        var original = Ui.Brush(value).Color;
        var palette = Theme.Palette(current);
        Color result;
        if (role == "ink")
        {
            // Dark lettering on the cyan action/hover pills stays dark in every theme.
            if (original.R < 40 && original.G < 65 && original.B < 90) return original;
            result = Ui.Brush(original.R > 210 && original.G > 210 ? palette.Ink : palette.Muted).Color;
        }
        else if (role == "line") result = Ui.Brush(palette.Line).Color;
        else
        {
            if (original.G > 140 && original.B > 140 && original.R < 130) return original;
            result = Ui.Brush(original.R > 35 ? palette.Selected : palette.Panel).Color;
        }
        result.A = original.A; return result;
    }
    public static void Apply(string name)
    {
        current = name;
        foreach (var pair in brushes) pair.Value.Color = Resolve(pair.Key.Role, pair.Key.Color);
        var palette = Theme.Palette(name);
        Glass.GradientStops[0].Color = Ui.Brush(palette.GlassTop).Color; Glass.GradientStops[1].Color = Ui.Brush(palette.GlassBottom).Color;
    }
}
