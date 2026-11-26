using Nivo.Core;
using Nivo.Services;

namespace Nivo.UI;

public sealed class ReviewPlanWindow : Window
{
    private bool approved;
    public static Task<bool> ReviewAsync(ActionPlan plan, CancellationToken ct)
    {
        var window = new ReviewPlanWindow(plan) { ShowActivated = false };
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ct.Register(() => window.Dispatcher.BeginInvoke(new Action(() => window.Close())));
        window.Closed += (_, _) => { registration.Dispose(); completion.TrySetResult(window.approved && !ct.IsCancellationRequested); };
        window.Show(); Motion.Enter((FrameworkElement)window.Content, 10); return completion.Task;
    }
    public static bool HasInPlaceEdits(ActionPlan plan) =>
        plan.Steps.Any(s => string.Equals(s.Action, "edit_file", StringComparison.Ordinal));

    public static bool HasComputerCommands(ActionPlan plan) => plan.Steps.Any(s => s.Action == "powershell");
    public static string ConsentNotice(ActionPlan plan) => HasComputerCommands(plan)
        ? Loc.T("This plan runs the complete PowerShell commands shown below with NIVO's current Windows permissions. Commands may change files, applications and settings; their output will be sent to the selected AI model for explanation. Review the exact script and working directory before allowing it. Stop requests cancellation; completed changes remain.", "هذه الخطة تشغّل أوامر PowerShell المعروضة كاملة بصلاحيات نيڤو الحالية في Windows. قد تغيّر ملفات وبرامج وإعدادات، وتُرسل نتائجها للموديل المختار للشرح. راجع النص ومجلد العمل قبل السماح. الإيقاف يطلب إلغاء التنفيذ ولا يتراجع عن تغييرات اكتملت.") + "\n" + Loc.T(ComputerCommandService.IsAdministrator() ? "Runs as Administrator." : "Runs as Standard User.", ComputerCommandService.IsAdministrator() ? "يعمل بصلاحيات Administrator." : "يعمل بصلاحيات المستخدم العادي.") + (HasInPlaceEdits(plan) ? "\n" + Loc.T("Attached original-file edits also require their shown hash and verified backup.", "تعديلات الملفات الأصلية المرفقة مرتبطة أيضًا بالبصمة المعروضة ونسخة احتياطية موثقة.") : "")
        : HasInPlaceEdits(plan)
        ? Loc.T(
            "Warning: This plan modifies an original attached file in place. A backup copy is saved automatically in NIVO's Backups folder before writing, and the edit aborts if the file changed since planning.",
            "تنبيه: هذه الخطة ستعدّل ملفًا أصليًا مرفقًا في مكانه. تُحفظ نسخة احتياطية تلقائيًا في مجلد Backups قبل الكتابة، ويُلغى التعديل إذا تغيّر الملف بعد التخطيط.")
        : Loc.T(
            "Only the listed actions will run. Original files are preserved; outputs use new filenames.",
            "ستُنفَّذ الخطوات المذكورة فقط مع الحفاظ على ملفاتك الأصلية وحفظ المخرجات بأسماء جديدة.");

    private ReviewPlanWindow(ActionPlan plan)
    {
        bool hasEdits = HasInPlaceEdits(plan);
        Title = Loc.T("NIVO · Review every step", "نيڤو · مراجعة خطوات المهمة"); Width = 720; Height = 650; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FlowDirection = Loc.Flow;
        Ui.StyleWindow(this);
        var root = new DockPanel { Margin = new Thickness(28) };
        var allowLabel = HasComputerCommands(plan) ? Loc.T("Allow & run commands", "السماح وتشغيل الأوامر") : hasEdits
            ? Loc.T("Allow & modify original file", "السماح وتعديل الملف الأصلي")
            : Loc.T("Allow these steps", "السماح بهذه الخطوات");
        var allowBtn = Ui.Btn(allowLabel, () => { approved = true; Close(); }, true); allowBtn.Tag = "Allow";
        var notice = Ui.Text(ConsentNotice(plan), 12, hasEdits || HasComputerCommands(plan) ? "#F2C97D" : "#9CABBC");
        notice.Tag = "ConsentNotice";
        var footer = Ui.Stack(notice, Ui.Row(allowBtn, Ui.Btn(Loc.T("Cancel", "إلغاء"), () => Close())));
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var contents = Ui.Stack(Ui.Text(Loc.T("Your task, before it runs.", "مهمتك قبل التنفيذ."), 28, weight: FontWeights.SemiBold), Ui.Text(plan.Summary, 16)); Ui.Space(contents);
        foreach (var step in plan.Steps)
        {
            bool isEdit = string.Equals(step.Action, "edit_file", StringComparison.Ordinal);
            var text = Ui.Stack(Ui.Text(step.Action + "  /  " + step.Title, 17, isEdit || step.Action == "powershell" ? "#F2C97D" : "#8ADBD5"));
            if (isEdit)
                text.Children.Add(Ui.Text(Loc.T("Modifies original attached file in place · automatic backup saved before write", "يعدّل الملف الأصلي المرفق في مكانه · تُحفظ نسخة احتياطية تلقائيًا قبل الكتابة"), 12, "#F2C97D"));
            if (step.Action != "powershell" && !string.IsNullOrEmpty(step.Target)) text.Children.Add(Ui.Text(step.Target, 13));
            if (isEdit && !string.IsNullOrEmpty(step.ExpectedSha256))
                text.Children.Add(Ui.Text(Loc.T("Expected SHA-256: ", "بصمة الملف المتوقعة SHA-256: ") + step.ExpectedSha256[..Math.Min(16, step.ExpectedSha256.Length)] + "…", 11, "#9CABBC"));
            if (!string.IsNullOrEmpty(step.When)) text.Children.Add(Ui.Text(step.When));
            if (isEdit || step.Action == "powershell")
            {
                if (step.Action == "powershell") text.Children.Add(Ui.Text(Loc.T($"PowerShell · timeout {step.TimeoutSeconds}s · working directory: {(string.IsNullOrWhiteSpace(step.Target) ? "user profile" : step.Target)}", $"PowerShell · المهلة {step.TimeoutSeconds} ثانية · مجلد العمل: {(string.IsNullOrWhiteSpace(step.Target) ? "مجلد المستخدم" : step.Target)}"), 12, "#F2C97D"));
                text.Children.Add(Ui.Text(step.Action == "powershell" ? Loc.T("Complete command to execute:", "الأمر الكامل الذي سيتم تشغيله:") : Loc.T("Full replacement content:", "المحتوى الكامل البديل للملف:"), 12, "#9CABBC"));
                text.Children.Add(new TextBox
                {
                    Text = step.Text,
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    FontFamily = new FontFamily("Consolas"),
                    FlowDirection = FlowDirection.LeftToRight,
                    MaxHeight = 300,
                    TextWrapping = step.Action == "powershell" ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    HorizontalScrollBarVisibility = step.Action == "powershell" ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                });
            }
            else if (!string.IsNullOrEmpty(step.Text))
            {
                text.Children.Add(new TextBox { Text = step.Text, IsReadOnly = true, AcceptsReturn = true, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            }
            foreach (var slide in step.Slides) text.Children.Add(Ui.Text(slide.Title + "\n" + string.Join("\n", slide.Bullets), 13));
            contents.Children.Add(Ui.Card(text));
        }
        root.Children.Add(new ScrollViewer { Content = contents, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = root;
    }
}
