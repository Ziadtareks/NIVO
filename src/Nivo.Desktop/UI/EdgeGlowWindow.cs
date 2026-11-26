using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Animation;
namespace Nivo.UI;

public sealed class EdgeGlowWindow : Window
{
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetStyle(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetStyle(IntPtr handle, int index, int style);
    private readonly Border glow;
    public EdgeGlowWindow()
    {
        Title = "NIVO · Agent activity"; Left = 0; Top = 0; Width = SystemParameters.PrimaryScreenWidth; Height = SystemParameters.PrimaryScreenHeight; ShowActivated = false; ShowInTaskbar = false; Topmost = true; WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize; IsHitTestVisible = false;
        glow = new Border { BorderThickness = new Thickness(2), BorderBrush = Ui.Brush("#42C6D4"), CornerRadius = new CornerRadius(12), Margin = new Thickness(5), Opacity = .45, Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Ui.Brush("#42C6D4").Color, BlurRadius = 15, ShadowDepth = 0, Opacity = .65 } }; Content = glow;
        SourceInitialized += (_, _) => { var handle = new WindowInteropHelper(this).Handle; SetStyle(handle, -20, GetStyle(handle, -20) | 0x20 | 0x80 | 0x08000000); };
        IsVisibleChanged += (_, _) => { glow.BeginAnimation(UIElement.OpacityProperty, null); if (IsVisible && Motion.Enabled) { var pulse = new DoubleAnimation(.3, .65, TimeSpan.FromSeconds(1.7)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() }; Timeline.SetDesiredFrameRate(pulse, 30); glow.BeginAnimation(UIElement.OpacityProperty, pulse); } };
    }
    internal int NativeExtendedStyle => GetStyle(new WindowInteropHelper(this).Handle, -20);
}
