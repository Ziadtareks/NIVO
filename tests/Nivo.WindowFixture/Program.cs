using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

internal static class Program
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length != 5 || !args.All(a => int.TryParse(a, out _))) return;
        int[] values = args.Select(int.Parse).ToArray();
        if (values[0] <= 0 || values[3] is < 320 or > 16000 || values[4] is < 240 or > 16000) return;
        using var parent = Process.GetProcessById(values[0]);
        var app = new Application();
        var panel = new StackPanel { Width = 540, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = "NIVO synthetic capture / error fixture", FontSize = 22, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 18) });
        panel.Children.Add(new TextBox { Text = "TypeError: syntheticValue is not a function", IsReadOnly = true, FontSize = 18, Height = 80, Padding = new Thickness(12), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new PasswordBox { Password = "NIVO_PRIVATE_PASSWORD", Margin = new Thickness(0, 12, 0, 0) });
        panel.Children.Add(new TextBlock { Text = "NIVO_HIDDEN_SENTINEL", Visibility = Visibility.Collapsed });
        var window = new Window { Title = "NIVO Synthetic Target", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, Background = new SolidColorBrush(Color.FromRgb(0x27, 0x74, 0xA8)), Content = panel };
        window.SourceInitialized += (_, _) => {
            if (!SetWindowPos(new WindowInteropHelper(window).Handle, new IntPtr(-1), values[1], values[2], values[3], values[4], 0x0040))
                throw new System.ComponentModel.Win32Exception();
        };
        var lifetime = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => { if (parent.HasExited || lifetime.Elapsed > TimeSpan.FromMinutes(2)) window.Close(); };
        window.Closed += (_, _) => timer.Stop();
        timer.Start(); app.Run(window);
    }
}
