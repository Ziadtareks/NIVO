using Nivo.Core;
using System.Windows.Threading;
namespace Nivo.UI;

public sealed class SuggestionWindow : Window
{
    public SuggestionWindow(string text, Action act)
    {
        Title = Loc.T("NIVO · A small thought", "نيڤو · اقتراح سريع"); Width = 390; SizeToContent = SizeToContent.Height; ShowActivated = false; Topmost = true; ShowInTaskbar = false; WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        FlowDirection = Loc.Flow;
        Left = SystemParameters.WorkArea.Right - Width - 20;
        var message = new ScrollViewer { Content = Ui.Text(text, 14), MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var card = Ui.Card(Ui.Stack(Ui.Text(Loc.T("A little help?", "هل أساعدك؟"), 18, weight: FontWeights.SemiBold), new Border { Height = 8 }, message, new Border { Height = 16 }, Ui.Row(Ui.Btn(Loc.T("Do it", "ساعدني"), () => { Close(); act(); }, true), Ui.Btn(Loc.T("Not now", "ليس الآن"), Close))), new Thickness(20)); card.Margin = new Thickness(10); card.SetResourceReference(Border.BackgroundProperty, "Glass"); Ui.Elevate(card); Content = card; Ui.StyleWindow(this);
        SizeChanged += (_, _) => Top = SystemParameters.WorkArea.Bottom - ActualHeight - 100;
        Loaded += (_, _) => Motion.Enter(card);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) }; timer.Tick += (_, _) => Close(); Closed += (_, _) => timer.Stop(); timer.Start();
    }
}
