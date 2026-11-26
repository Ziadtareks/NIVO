using System.Windows.Input;
using System.Windows.Media.Effects;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using Nivo.Core;

namespace Nivo.UI;

public static class Ui
{
    public static void StyleWindow(Window window)
    {
        window.FontFamily = new FontFamily("Segoe UI"); window.FontSize = 14;
        window.SetResourceReference(Control.ForegroundProperty, "Ink");
        if (!window.AllowsTransparency) window.SetResourceReference(Control.BackgroundProperty, "WindowBackground");
    }
    public static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    public static TextBlock Text(string text, double size = 14, string? color = null, FontWeight? weight = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, color == null ? "Ink" : color == "#8ADBD5" ? "Accent" : color is "#D9B794" or "#CCBCA3" ? "Warning" : "Muted");
        return block;
    }
    public static TextBlock Glyph(string glyph, double size = 18) => new() { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = size, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    public static Button Btn(string text, Action action, bool primary = false)
    {
        var b = new Button { Content = text };
        if (primary) b.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        Motion.Button(b); b.Click += (_, _) => { try { action(); } catch (Exception e) { Error(e); } }; return b;
    }
    public static Button AsyncBtn(string text, Func<Task> action, bool primary = false)
    {
        var b = Btn(text, () => { }, primary);
        b.Click += async (_, _) => { b.IsEnabled = false; try { await action(); } catch (OperationCanceledException) { } catch (Exception e) { Error(e); } finally { b.IsEnabled = true; } }; return b;
    }
    public static Button InterruptibleAsyncBtn(string text, Func<Task> action, Func<bool>? canInterrupt = null)
    {
        var button = Btn(text, () => { });
        button.Click += async (_, _) =>
        {
            bool disable = canInterrupt?.Invoke() == false;
            if (disable) button.IsEnabled = false;
            try { await action(); } catch (OperationCanceledException) { } catch (Exception ex) { Error(ex); }
            finally { if (disable) button.IsEnabled = true; }
        };
        return button;
    }
    public static Border Card(UIElement child, Thickness? padding = null)
    { var border = new Border { Child = child, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = padding ?? new Thickness(22), Margin = new Thickness(0, 0, 0, 16) }; border.SetResourceReference(Border.BackgroundProperty, "Panel"); border.SetResourceReference(Border.BorderBrushProperty, "Hairline"); return border; }
    public static StackPanel Stack(params UIElement[] children) { var s = new StackPanel(); foreach (var c in children) s.Children.Add(c); return s; }
    public static StackPanel Row(params UIElement[] children) { var s = new StackPanel { Orientation = Orientation.Horizontal }; foreach (var c in children) s.Children.Add(c); return s; }
    public static void Space(Panel panel, int height = 16) => panel.Children.Add(new Border { Height = height });
    public static void Error(Exception e) => MessageBox.Show(e.Message, Loc.T("NIVO · Something needs your attention", "نيڤو · تنبيه"), MessageBoxButton.OK, MessageBoxImage.Information);
    public static bool Confirm(string message, string? title = null) => MessageBox.Show(message, title ?? Loc.T("NIVO · Review action", "نيڤو · تأكيد الإجراء"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    public static string? PickFile(string filter = "All supported files|*.txt;*.md;*.pdf;*.docx;*.pptx;*.xlsx;*.csv;*.json;*.png;*.jpg;*.mp4;*.mov;*.mkv|All files|*.*")
    { var dialog = new Microsoft.Win32.OpenFileDialog { Filter = filter }; return dialog.ShowDialog() == true ? dialog.FileName : null; }
    public static string? Prompt(string title, string label, string value = "", bool multiline = false)
    {
        var w = new Window { Title = title, Width = 500, Height = multiline ? 370 : 245, WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize, FlowDirection = Loc.Flow };
        StyleWindow(w);
        var box = new TextBox { Text = value, AcceptsReturn = multiline, Height = multiline ? 170 : 44, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        string? result = null; var panel = Stack(Text(label), box, Row(Btn(Loc.T("Save", "حفظ"), () => { if (!string.IsNullOrWhiteSpace(box.Text)) { result = box.Text; w.DialogResult = true; } }, true), Btn(Loc.T("Cancel", "إلغاء"), () => w.Close()))); panel.Margin = new Thickness(24); w.Content = panel; w.ShowDialog(); return result;
    }
    public static FrameworkElement Orb(double size = 80)
    {
        var grid = new Grid { Width = size, Height = size, FlowDirection = FlowDirection.LeftToRight };
        grid.Children.Add(new System.Windows.Shapes.Ellipse { Fill = new LinearGradientBrush(Brush("#78C6FF").Color, Brush("#1669CC").Color, 65), Stroke = Brush("#CEEEFF"), StrokeThickness = 1 });
        grid.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M 12,34 L 12,14 C 12,8 18,8 22,14 L 34,34 L 34,10"), Stroke = Brushes.White, StrokeThickness = 4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Stretch = Stretch.Uniform, Width = size * .44, Height = size * .48 });
        grid.Children.Add(new System.Windows.Shapes.Ellipse { Width = size * .6, Height = size * .21, Fill = new LinearGradientBrush(Brush("#80FFFFFF").Color, Colors.Transparent, 90), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, size * .1, 0, 0), IsHitTestVisible = false });
        return grid;
    }
    public static void Flat(Button button) { button.SetResourceReference(FrameworkElement.StyleProperty, "IconButton"); button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0); }
    public static void Elevate(Border border) => border.Effect = new DropShadowEffect { Color = Color.FromRgb(32, 76, 126), BlurRadius = 16, ShadowDepth = 3, Opacity = .18 };
    public static bool InButton(DependencyObject? source)
    {
        while (source != null) { if (source is System.Windows.Controls.Primitives.ButtonBase) return true; source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source); }
        return false;
    }
    public static string[] PickFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "Documents, images and videos|*.txt;*.md;*.pdf;*.docx;*.pptx;*.xlsx;*.csv;*.json;*.png;*.jpg;*.jpeg;*.mp4;*.mov;*.mkv|All files|*.*" };
        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }
    public static void ApplyTheme(string name) => Theme.Apply(name);
}
