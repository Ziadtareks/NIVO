using Nivo.Core;

namespace Nivo.UI;

public sealed partial class MainWindow
{
    private StackPanel MemoryPage()
    {
        var p = PageHeader(Loc.T("A little universe of you.", "خريطة ذاكرتك المحلية."), Loc.T("Facts you choose to keep. Rotate the map, then select a memory to edit it.", "الحقائق التي تختار حفظها على جهازك. دوّر الخريطة واضغط على أي نقطة لتعديلها أو حذفها."));
        p.Children.Add(Ui.Row(Ui.Btn(Loc.T("Add a memory", "إضافة ذاكرة"), () => AddMemoryDialog(), true), Ui.Btn(Loc.T("Delete all memories", "حذف جميع الذكريات"), () => { if (Ui.Confirm(Loc.T("Permanently delete every saved memory?", "حذف جميع الذكريات المحفوظة نهائيًا؟"))) { controller.Store.Data.Memories.Clear(); controller.Store.Save(); } })));
        var map = new MemoryMap(controller.Store.Data.Memories, EditMemory); p.Children.Add(map);

        var starters = new WrapPanel { Margin = new Thickness(0, 12, 0, 4) };
        foreach (var sample in new[] {
            (Loc.T("Prefer concise answers", "أفضّل الإجابات المختصرة والمباشرة"), "Core Memory"),
            (Loc.T("My work / study focus is…", "مجال عملي أو دراستي هو: "), "Core Memory"),
            (Loc.T("Upcoming deadline…", "موعد تسليم مهم قادم: "), "Active Now")
        })
        {
            var sText = sample.Item1; var sTier = sample.Item2;
            var chip = Ui.Btn("+ " + sText, () => AddMemoryDialog(sText, sTier));
            chip.SetResourceReference(StyleProperty, "PillButton"); chip.Padding = new Thickness(12, 5, 12, 5); chip.FontSize = 11.5;
            starters.Children.Add(chip);
        }
        p.Children.Add(starters);

        var search = new TextBox { ToolTip = Loc.T("Search local memories", "ابحث في ذكرياتك المحلية"), Margin = new Thickness(0, 6, 0, 12) };
        p.Children.Add(Ui.Text(Loc.T("Search memories", "البحث في الذاكرة"), 12, "#9CB3C3")); p.Children.Add(search);
        var list = new StackPanel(); p.Children.Add(list);
        void Render()
        {
            list.Children.Clear();
            foreach (var memory in controller.Store.Data.Memories.Where(m => m.Text.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                int tierIdx = Math.Max(0, Array.IndexOf(MemoryMap.Tiers, memory.Tier));
                var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Ui.Brush(MemoryMap.TierColors[tierIdx]), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                var meta = Ui.Row(dot, Ui.Text(MemoryMap.TierLabel(memory.Tier) + "  ·  " + memory.Created.ToString("d"), 11, "#92B4C5"));
                var row = Ui.Stack(meta, Ui.Text(memory.Text, 15));
                row.Children.Add(new Border { Height = 8 });
                row.Children.Add(Ui.Row(Ui.Btn(Loc.T("Edit memory", "تعديل"), () => EditMemory(memory)), Ui.Btn(Loc.T("Delete", "حذف"), () => { if (Ui.Confirm(Loc.T("Delete this memory?", "حذف هذه الذاكرة؟"))) { controller.Store.Data.Memories.Remove(memory); controller.Store.Save(); } })));
                list.Children.Add(Ui.Card(row));
            }
            if (controller.Store.Data.Memories.Count == 0) list.Children.Add(Ui.Text(Loc.T("Your map starts empty. Use the quick chips above or type \"remember …\" in chat.", "خريطتك تبدأ فارغة. استخدم الاقتراحات السريعة بالأعلى أو اكتب «تذكر …» في المحادثة."), 14, "#9CB3C3"));
        }
        search.TextChanged += (_, _) => Render(); Render(); return p;
    }
    private void AddMemoryDialog(string initialText = "", string initialTier = "Recent Memory")
    {
        if (controller.Store.Incognito) throw new InvalidOperationException(Loc.T("Memory is paused in Incognito.", "الذاكرة متوقفة في الوضع الخفي."));
        var w = new Window { Title = Loc.T("NIVO · Remember something", "نيڤو · حفظ في الذاكرة"), Width = 520, Height = 395, WindowStartupLocation = WindowStartupLocation.CenterScreen, FlowDirection = Loc.Flow };
        Ui.StyleWindow(w);
        var text = new TextBox { Text = initialText, Height = 150, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var tier = new ComboBox { ItemsSource = MemoryMap.Tiers, SelectedItem = initialTier };
        w.Content = new Border { Padding = new Thickness(24), Child = Ui.Stack(Ui.Text(Loc.T("What should NIVO remember?", "ما الذي تريد أن يتذكره نيڤو؟")), text, Ui.Text(Loc.T("Memory tier", "مستوى الذاكرة"), 12, "#9CB3C3"), tier, Ui.Row(Ui.Btn(Loc.T("Save", "حفظ"), () => { if (string.IsNullOrWhiteSpace(text.Text)) return; controller.Store.Data.Memories.Add(new() { Text = text.Text.Trim(), Tier = (string)tier.SelectedItem }); controller.Store.Save(); w.Close(); }, true), Ui.Btn(Loc.T("Cancel", "إلغاء"), () => w.Close()))) };
        w.ShowDialog();
    }
    private void EditMemory(Memory memory)
    {
        var w = new Window { Title = Loc.T("NIVO · Memory", "نيڤو · تعديل ذاكرة"), Width = 520, Height = 395, WindowStartupLocation = WindowStartupLocation.CenterScreen, FlowDirection = Loc.Flow };
        Ui.StyleWindow(w);
        var text = new TextBox { Text = memory.Text, Height = 150, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var tier = new ComboBox { ItemsSource = MemoryMap.Tiers, SelectedItem = memory.Tier };
        w.Content = new Border { Padding = new Thickness(24), Child = Ui.Stack(text, tier, Ui.Row(Ui.Btn(Loc.T("Save", "حفظ"), () => { if (string.IsNullOrWhiteSpace(text.Text)) return; memory.Text = text.Text.Trim(); memory.Tier = (string)tier.SelectedItem; controller.Store.Save(); w.Close(); }, true), Ui.Btn(Loc.T("Delete", "حذف"), () => { if (Ui.Confirm(Loc.T("Delete this memory?", "حذف هذه الذاكرة؟"))) { controller.Store.Data.Memories.Remove(memory); controller.Store.Save(); w.Close(); } }))) }; w.ShowDialog();
    }
}
