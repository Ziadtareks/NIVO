using Nivo.Core;
using Nivo.Services;
using Nivo.UI;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Nivo;

public partial class App : Application
{
    public bool Exiting { get; private set; }
    public MainWindow Hub { get; private set; } = null!;
    public Controller Controller { get; private set; } = null!;
    public IslandWindow? Island => island;
    private EdgeGlowWindow? edge; private readonly CheckInService checkIns = new(); private TaskDockWindow? taskDock; private IslandWindow? island; private DockWindow? dock; private AskWindow? ask;
    private DesktopService.Hotkeys? hotkeys; private Forms.NotifyIcon? tray; private Mutex? mutex;
    private readonly DispatcherTimer shellTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private DateTime lastScan = DateTime.Now; private string lastSuggestedTitle = "";
    private EventWaitHandle? quitSignal; private RegisteredWaitHandle? quitWait;
    private CancellationTokenSource? speechRequest;
    private MediaPlayer? player; private string? voiceFile;
    private bool capturing;
    private bool islandHiddenByUser;
    private bool automaticGameMode;
    private bool shellSuppressed;
    private bool applyingShellDecision;
    private ExternalSuppressionState externalSuppression = ExternalSuppressionState.None;
    internal void ControllerForTests(Controller c) { Controller = c; }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--quit")) { try { using var signal = EventWaitHandle.OpenExisting("Local\\NIVO.Local.Quit"); signal.Set(); } catch (WaitHandleCannotBeOpenedException) { } Shutdown(); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--taskbar-guard" && int.TryParse(e.Args[1], out int pid)) { await Task.Run(() => TaskbarService.Guard(pid)); Shutdown(); return; }
        if (e.Args.Contains("--desktop-acceptance"))
        {
            try { await DesktopAcceptance.Run(e.Args.Contains("--gemini")); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory("artifacts"); File.WriteAllText("artifacts/desktop-failure.txt", ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--live-acceptance"))
        {
            try { Shutdown(await LiveAcceptance.Run(e.Args.Contains("--gemini"), e.Args.Contains("--presentation-only")) ? 0 : 2); } catch (Exception ex) { Directory.CreateDirectory("artifacts"); File.WriteAllText("artifacts/live-failure.txt", ex.ToString()); Shutdown(1); } return;
        }
        if (e.Args.Contains("--account-probe") || e.Args.Contains("--account-test") || e.Args.Contains("--self-test") || e.Args.Contains("--cli-test") || e.Args.Contains("--motion-test") || e.Args.Contains("--assistant-test") || e.Args.Contains("--computer-test"))
        {
            Directory.CreateDirectory("artifacts"); File.Delete("artifacts/test-failure.txt"); File.WriteAllText("artifacts/test-lifecycle.log", "starting checks\n");
            try { if (e.Args.Contains("--account-probe")) await AccountTests.RunNativeProbe(); else if (e.Args.Contains("--account-test")) await AccountTests.Run(); else if (e.Args.Contains("--computer-test")) await SelfTests.RunComputer(); else if (e.Args.Contains("--assistant-test")) await SelfTests.RunAssistant(); else if (e.Args.Contains("--motion-test")) await SelfTests.RunMotion(); else await SelfTests.Run(e.Args.Contains("--cli-test")); FinishSelfTest(0); }
            catch (Exception ex) { File.WriteAllText("artifacts/test-failure.txt", ex.ToString()); FinishSelfTest(1); }
            return;
        }
        mutex = new Mutex(true, "Local\\NIVO.Local.Desktop", out bool first);
        if (!first) { MessageBox.Show("NIVO is already running. Open it from the tray icon or press Alt+Shift+S."); Shutdown(); return; }
        quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\NIVO.Local.Quit"); quitWait = ThreadPool.RegisterWaitForSingleObject(quitSignal, (_, _) => Dispatcher.BeginInvoke(new Action(Quit)), null, Timeout.Infinite, false);
        try
        {
            var store = new Store();
            Loc.Apply(store.Data.Settings.Language);
            Controller = new(store); Hub = new(Controller); MainWindow = Hub;
            Motion.FollowWindows = Controller.Store.Data.Settings.FollowWindowsAnimations; Motion.Reduced = Controller.Store.Data.Settings.ReduceMotion; Ui.ApplyTheme(Controller.Store.Data.Settings.Theme); Hub.Show();
            ConfigureHotkeys();
            Controller.Changed += () => { if (Exiting) return; hotkeys?.SetEmergencyStop(Controller.Busy && Controller.TaskActive);
                if (Controller.TaskActive && Controller.Busy && Controller.TaskState == "Working" && !Controller.IsQuiet && !shellSuppressed && !capturing && Controller.Store.Data.Settings.EdgeGlow && Motion.Enabled) { edge ??= new EdgeGlowWindow(); edge.Show(); } else edge?.Hide(); if (Controller.TaskState.Length > 0) { taskDock ??= new TaskDockWindow(Controller); taskDock.Update(); } };
            Controller.Suggestion += suggestion => ShowSuggestion(suggestion);
            Theme.Changed += () => { if (!Exiting) dock?.ApplyTheme(); };
            SetupTray(); RefreshShell(); shellTimer.Tick += ShellTimerTick; shellTimer.Start(); timer.Tick += TimerTick; timer.Start();
        }
        catch (Exception ex) { Ui.Error(ex); Quit(); }
        DispatcherUnhandledException += (_, args) => { Ui.Error(args.Exception); args.Handled = true; };
        SessionEnding += (_, _) => Quit();
    }
    private void FinishSelfTest(int code)
    {
        const string path = "artifacts/test-lifecycle.log";
        File.AppendAllText(path, $"checks returned: {code}\n"); Exiting = true; Motion.Reduced = true;
        File.AppendAllText(path, "motion stopped\n");
        foreach (Window window in Windows.Cast<Window>().ToArray())
        {
            string name = window.GetType().Name;
            window.Closing += (_, _) => File.AppendAllText(path, "closing " + name + "\n");
            window.Closed += (_, _) => File.AppendAllText(path, "closed " + name + "\n");
        }
        Exit += (_, _) => File.AppendAllText(path, "application exit\n");
        Shutdown(code); File.AppendAllText(path, "shutdown requested\n");
    }
    public void SuspendHotkeys() { hotkeys?.Dispose(); hotkeys = null; }
    public void ConfigureHotkeys()
    {
        if (Exiting || Hub == null) return;
        hotkeys?.Dispose(); hotkeys = new(Hub, Controller.Store.Data.Settings);
        hotkeys.Pressed += async id => { if (capturing && id is 1 or 2) return; try { switch (id) { case 1: ShowAsk(); break; case 2: ToggleIsland(); break; case 3: Controller.ToggleIncognito(); break; case 4: await CaptureAsync(true); break; case 5: StopAll(); break; } } catch (Exception ex) { Ui.Error(ex); } };
        if (hotkeys.Failed.Count > 0) Controller.SetStatus(Loc.T("A shortcut is in use by another app. Change it in Settings or use the tray.", "أحد الاختصارات مستخدم بواسطة تطبيق آخر. يمكنك تغييره من الإعدادات."));
    }
    private void SetupTray()
    {
        if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
        var icon = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
        tray = new Forms.NotifyIcon { Icon = icon, Text = Loc.T("NIVO Local", "نيڤو المحلي"), Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Loc.T("Open NIVO", "فتح نيڤو"), null, (_, _) => Dispatcher.Invoke(() => Hub.ShowAssistant()));
        menu.Items.Add(Loc.T("Silent Ask", "السؤال الصامت"), null, (_, _) => Dispatcher.Invoke(ShowAsk));
        menu.Items.Add(Loc.T("Incognito", "الوضع الخفي"), null, (_, _) => Dispatcher.Invoke(Controller.ToggleIncognito));
        menu.Items.Add(Loc.T("Quit", "خروج"), null, (_, _) => Dispatcher.Invoke(Quit));
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => Hub.ShowAssistant());
    }
    public void ApplyLanguage(string language, bool save = true)
    {
        if (Controller == null || Exiting) return;
        Controller.Store.Data.Settings.Language = language;
        Loc.Apply(language);
        if (save) Controller.Store.Save();
        bool allowShell = !capturing && !shellSuppressed;
        if (island != null) { bool vis = island.IsVisible; island.Close(); island = CreateIsland(); if (vis && allowShell && !islandHiddenByUser) ShowWindowGuarded(island); }
        if (dock != null) { bool vis = dock.IsVisible; dock.Close(); dock = CreateDock(); if (vis && allowShell && Controller.Store.Data.Settings.DesktopEnabled) ShowWindowGuarded(dock); }
        if (ask != null) { ask.ForceClose(); ask = null; }
        if (taskDock != null) { taskDock.Close(); taskDock = null; }
        if (tray != null) SetupTray();
        Hub?.RebuildChrome();
        Controller.Notify();
    }
    public void ShowAsk() { ask ??= new(Controller); ask.Refresh(Controller); ask.Show(); ask.Activate(); }
    public void ToggleIsland()
    {
        if (capturing || Exiting || Controller == null) return;
        var decision = SyncShellVisibility(forceDockRefresh: false, updateDockContext: false);
        if (decision.Suppressed) return;
        islandHiddenByUser = island?.IsVisible == true;
        var s = Controller.Store.Data.Settings;
        var updated = ShellVisibilityPolicy.Decide(decision.ExternalState, s.DesktopEnabled, s.ReplaceTaskbar, s.Gaming, islandHiddenByUser, capturing: false);
        ApplyShellDecision(updated, forceDockRefresh: false, updateDockContext: false);
    }
    public void RefreshShell()
    {
        if (Exiting || capturing || Controller == null) return;
        SyncShellVisibility(forceDockRefresh: true, updateDockContext: false);
    }
    private IslandWindow CreateIsland()
    {
        var w = new IslandWindow(Controller);
        w.IsVisibleChanged += (_, _) =>
        {
            if (!w.IsVisible || Exiting || applyingShellDecision) return;
            if (capturing || islandHiddenByUser || shellSuppressed || EvaluateShellVisibility(updateController: false, captureActive: false).Decision.Suppressed)
                w.Hide();
        };
        return w;
    }
    private DockWindow CreateDock()
    {
        var w = new DockWindow(Controller);
        w.IsVisibleChanged += (_, _) =>
        {
            if (!w.IsVisible || Exiting || applyingShellDecision) return;
            if (capturing || Controller == null || !Controller.Store.Data.Settings.DesktopEnabled || shellSuppressed || EvaluateShellVisibility(updateController: false, captureActive: false).Decision.Suppressed)
                w.Hide();
        };
        return w;
    }
    private void ShowWindowGuarded(Window window)
    {
        bool previous = applyingShellDecision;
        applyingShellDecision = true;
        try { if (!window.IsVisible) window.Show(); }
        finally { applyingShellDecision = previous; }
    }
    private (ForegroundWindowInfo Active, ShellVisibilityDecision Decision) EvaluateShellVisibility(bool updateController, bool captureActive)
    {
        var active = DesktopService.ForegroundWindow();
        int ownPid = Environment.ProcessId;
        bool previousSourceStillSuppressed = false;
        if ((active.Handle == IntPtr.Zero || ShellVisibilityPolicy.IsOwnWindow(active, ownPid))
            && externalSuppression.Any
            && externalSuppression.SourceHandle != IntPtr.Zero)
        {
            var source = DesktopService.InspectWindow(externalSuppression.SourceHandle, checkD3dState: false);
            previousSourceStillSuppressed = ShellVisibilityPolicy.IsSourceWindowStillSuppressed(source, externalSuppression, ownPid);
        }

        var settings = Controller.Store.Data.Settings;
        var decision = ShellVisibilityPolicy.Evaluate(new ShellVisibilityInput(
            DesktopEnabled: settings.DesktopEnabled,
            ReplaceTaskbar: settings.ReplaceTaskbar,
            ManualGaming: settings.Gaming,
            IslandHiddenByUser: islandHiddenByUser,
            Capturing: captureActive,
            OwnProcessId: ownPid,
            Foreground: active,
            PreviousExternal: externalSuppression,
            PreviousSourceStillSuppressed: previousSourceStillSuppressed));

        if (updateController)
        {
            externalSuppression = decision.ExternalState;
            shellSuppressed = decision.Suppressed;
            bool game = decision.ExternalState.AutoGaming;
            bool modeChanged = UpdateAutomaticGameMode(game);
            if (Controller.AutoGaming != game || modeChanged)
            {
                Controller.AutoGaming = game; Controller.Notify();
            }
        }
        return (active, decision);
    }
    internal bool UpdateAutomaticGameMode(bool game)
    {
        if (game && !automaticGameMode && !Controller.Busy && Controller.Mode == "General")
        { Controller.Mode = "Game"; automaticGameMode = true; return true; }
        if (!game && automaticGameMode)
        {
            automaticGameMode = false;
            if (Controller.Mode == "Game") { Controller.Mode = "General"; return true; }
        }
        return false;
    }
    private ShellVisibilityDecision SyncShellVisibility(bool forceDockRefresh, bool updateDockContext)
    {
        var (_, decision) = SyncShellVisibilityWithForeground(forceDockRefresh, updateDockContext);
        return decision;
    }
    private (ForegroundWindowInfo Active, ShellVisibilityDecision Decision) SyncShellVisibilityWithForeground(bool forceDockRefresh, bool updateDockContext)
    {
        var (active, decision) = EvaluateShellVisibility(updateController: true, captureActive: capturing);
        ApplyShellDecision(decision, forceDockRefresh, updateDockContext);
        return (active, decision);
    }
    private void ApplyShellDecision(ShellVisibilityDecision decision, bool forceDockRefresh, bool updateDockContext)
    {
        if (decision.Suppressed && AccountTalkActive) RequestAccountTalkStop();
        externalSuppression = decision.ExternalState;
        shellSuppressed = decision.Suppressed;

        if (decision.ShowIsland)
        {
            island ??= CreateIsland();
            ShowWindowGuarded(island);
        }
        else if (island is { IsVisible: true })
        {
            island.Hide();
        }

        if (decision.ShowDock)
        {
            bool created = dock == null;
            dock ??= CreateDock();
            bool wasVisible = dock.IsVisible;
            if (forceDockRefresh && !created) dock.Refresh();
            ShowWindowGuarded(dock);
            if (updateDockContext || (!wasVisible && !forceDockRefresh && !created))
                dock.UpdateContext();
        }
        else if (dock is { IsVisible: true })
        {
            dock.Hide();
        }

        if (decision.Suppressed) edge?.Hide();
        TaskbarService.SetHidden(decision.HideTaskbar);
    }
    public async Task<bool> CaptureAsync(bool crop, bool forceHover = false, bool openAsk = true, bool immediateCrop = false)
    {
        if (capturing) throw new InvalidOperationException(Loc.T("Finish or cancel the current selection first.", "أكمل التحديد الحالي أو ألغِه أولًا."));
        capturing = true;
        try { return await CaptureCoreAsync(crop, forceHover, openAsk, immediateCrop); }
        finally { capturing = false; }
    }

    private async Task<bool> CaptureCoreAsync(bool crop, bool forceHover, bool openAsk, bool immediateCrop)
    {
        if (Controller.Busy) throw new InvalidOperationException(Loc.T("Stop the current request before taking another capture.", "أوقف الطلب الحالي قبل أخذ لقطة جديدة."));
        if (Controller.Store.Incognito) throw new InvalidOperationException(Loc.T("Screen capture is paused in Incognito.", "التقاط الشاشة متوقف في الوضع الخفي."));

        bool IsExcluded((string Title, string Process, int Pid) win) =>
            win.Pid != Environment.ProcessId &&
            Controller.Store.Data.Settings.ExcludedApps.Any(x =>
                !string.IsNullOrWhiteSpace(x) &&
                (win.Title + " " + win.Process).Contains(x, StringComparison.OrdinalIgnoreCase));

        var active = DesktopService.ActiveWindow();
        System.Diagnostics.Trace.TraceInformation("NIVO capture entry: targetOwn={0}, excluded={1}.", active.Pid == Environment.ProcessId, IsExcluded(active));
        if (IsExcluded(active)) throw new InvalidOperationException(Loc.T("This application is excluded from screen access.", "هذا التطبيق مستثنى من الوصول للشاشة."));

        var visible = Windows.Cast<Window>().Where(w => w.IsVisible).ToArray();
        bool pointMode = (forceHover || Controller.Store.Data.Settings.Hover) && !crop;
        BitmapSource? capturedImage = null;
        string cropAction = "Ask";

        try
        {
            foreach (var w in visible) w.Hide();
            DesktopService.FlushCompositor();
            await Task.Delay(220);
            if (Controller.Store.Incognito)
                throw new InvalidOperationException(Loc.T("Screen capture is paused in Incognito.", "التقاط الشاشة متوقف في الوضع الخفي."));

            var target = DesktopService.ActiveWindow();
            System.Diagnostics.Trace.TraceInformation("NIVO capture reveal: targetOwn={0}, excluded={1}.", target.Pid == Environment.ProcessId, IsExcluded(target));
            if (IsExcluded(target))
                throw new InvalidOperationException(Loc.T("The window revealed behind NIVO is excluded from capture.", "النافذة الظاهرة خلف نيڤو مستثناة من الالتقاط."));

            if (pointMode)
            {
                var picker = new PointPickerWindow();
                bool? picked = picker.ShowDialog();
                System.Diagnostics.Trace.TraceInformation("NIVO point picker result: accepted={0}, confirmed={1}.", picked, picker.Confirmed);
                picker.Hide();
                if (picked != true || picker.SelectedRegion is not { IsEmpty: false } region || picker.SelectedPoint is not { } pickedPoint)
                    return false;

                // Ensure the picker overlay is fully torn down and composited away before capturing desktop pixels.
                await Dispatcher.Yield(DispatcherPriority.Render);
                DesktopService.FlushCompositor();
                await Task.Delay(80);

                if (Controller.Store.Incognito)
                    throw new InvalidOperationException(Loc.T("Screen capture is paused in Incognito.", "التقاط الشاشة متوقف في الوضع الخفي."));

                var postPickActive = DesktopService.ActiveWindow();
                var underPointer = DesktopService.WindowAtPoint(pickedPoint);
                System.Diagnostics.Trace.TraceInformation("NIVO capture confirmation: activeExcluded={0}, pointedExcluded={1}.", IsExcluded(postPickActive), IsExcluded(underPointer));
                if (IsExcluded(postPickActive) || IsExcluded(underPointer))
                    throw new InvalidOperationException(Loc.T("The targeted application is excluded from screen access.", "التطبيق المستهدف مستثنى من الوصول للشاشة."));

                capturedImage = DesktopService.CaptureRegion(region);
            }
            else
            {
                var fullImage = DesktopService.CaptureMonitorAtCursor(out var monitorBounds);
                if (crop)
                {
                    var window = new CropWindow(fullImage, immediateCrop, monitorBounds);
                    if (window.ShowDialog() != true || window.Result == null) return false;
                    capturedImage = window.Result;
                    cropAction = window.SelectedAction;
                }
                else
                {
                    capturedImage = fullImage;
                }
            }
        }
        finally
        {
            var (_, postCaptureShell) = EvaluateShellVisibility(updateController: true, captureActive: false);
            bool previousApplying = applyingShellDecision;
            applyingShellDecision = true;
            try
            {
                foreach (var w in visible)
                {
                    if (ReferenceEquals(w, dock) && !postCaptureShell.ShowDock) continue;
                    if (ReferenceEquals(w, island) && !postCaptureShell.ShowIsland) continue;
                    if (ReferenceEquals(w, edge) && (postCaptureShell.Suppressed || Controller.IsQuiet)) continue;
                    try { w.Show(); } catch (InvalidOperationException) { System.Diagnostics.Trace.TraceInformation("NIVO capture restore skipped a window closed during selection."); }
                }
            }
            finally { applyingShellDecision = previousApplying; }

            if (postCaptureShell.Suppressed)
            {
                dock?.Hide();
                island?.Hide();
                edge?.Hide();
                TaskbarService.SetHidden(false);
            }
            else
            {
                TaskbarService.SetHidden(postCaptureShell.HideTaskbar);
            }
        }

        // All post-capture UI, prompts, and follow-ups run strictly AFTER hidden NIVO windows are restored.
        if (capturedImage == null) return false;

        if (cropAction is "Copy" or "Copy as image")
        {
            System.Windows.Clipboard.SetImage(capturedImage);
            Controller.Capture = null;
            Controller.SetStatus(Loc.T("Region copied to clipboard · no AI request", "تم نسخ الجزء المحدد للحافظة دون إرسال للذكاء الاصطناعي"));
            if (island is { IsVisible: true } && !shellSuppressed && !islandHiddenByUser) island.ShowCompleted(Loc.T("Region copied to clipboard", "تم نسخ الجزء المحدد للحافظة"));
            return true;
        }

        if (cropAction is "Note" or "Task")
        {
            string kindLabelEn = cropAction == "Task" ? "task" : "note";
            string kindLabelAr = cropAction == "Task" ? "مهمة" : "ملاحظة";
            string? title = Ui.Prompt(
                Loc.T($"Save image as a {kindLabelEn}", $"حفظ الصورة كـ{kindLabelAr}"),
                Loc.T("Title · saved locally without AI", "العنوان · تُحفظ محليًا بدون ذكاء اصطناعي"),
                "");
            if (!string.IsNullOrWhiteSpace(title))
            {
                if (Controller.Store.Incognito)
                    throw new InvalidOperationException(Loc.T("Local workspace writes are paused in Incognito.", "حفظ العناصر محليًا متوقف في الوضع الخفي."));
                Controller.Capture = capturedImage;
                try
                {
                    CaptureActions.SaveImage(Controller, cropAction, title);
                    string? savedFile = Controller.Store.Data.Workspace.LastOrDefault()?.ReferenceFile;
                    if (island is { IsVisible: true } && !shellSuppressed && !islandHiddenByUser) island.ShowCompleted(title.Trim(), savedFile);
                }
                finally
                {
                    Controller.Capture = null;
                    Controller.Notify();
                }
            }
            else
            {
                Controller.Capture = null;
                Controller.Notify();
            }
            return !string.IsNullOrWhiteSpace(title);
        }

        bool isPromptAction = cropAction is "Explain" or "Translate" or "Summarize" or "Fact-check" or "Abbreviation" or "Table";
        Controller.Capture = capturedImage;
        Controller.Mode = cropAction == "Fact-check" ? "Web" : (crop ? "Crop" : "Screen");

        if (isPromptAction)
        {
            string prompt = CaptureActions.Prompt(cropAction, Controller.Store.Data.Settings.Language);
            Controller.Draft = prompt;
            Controller.Notify();
            // The picker is gone and hidden windows restored before consent or submission.
            if (island != null && island.IsVisible && !shellSuppressed && !islandHiddenByUser) await island.OpenAskWithPrompt(prompt, true, allowPlanning: false);
            else ShowAsk();
            return true;
        }

        Controller.Notify();
        if (openAsk)
        {
            ShowAsk();
            if (ask?.IsVisible == true) ask.Activate();
        }
        return true;
    }
    private void ShellTimerTick(object? sender, EventArgs e)
    {
        if (capturing || Exiting || Controller == null) return;
        SyncShellVisibility(forceDockRefresh: false, updateDockContext: false);
    }
    internal async void TimerTick(object? sender, EventArgs e)
    {
        if (capturing || Exiting || Controller == null) return;
        var (active, decision) = SyncShellVisibilityWithForeground(forceDockRefresh: false, updateDockContext: true);
        if (active.Pid != Environment.ProcessId)
        {
            if (!Controller.Store.Data.Settings.ExcludedApps.Any(x => !string.IsNullOrWhiteSpace(x) && (active.Title + " " + active.Process).Contains(x, StringComparison.OrdinalIgnoreCase))) { Controller.LastExternalTitle = active.Title; Controller.LastExternalProcess = active.Process; }
            else { Controller.LastExternalTitle = ""; Controller.LastExternalProcess = ""; }
        }
        bool quietOrSuppressed = Controller.IsQuiet || decision.Suppressed;
        var localNudge = checkIns.Check(Controller.Store, Controller.LastExternalTitle, quietOrSuppressed || Controller.Busy || active.Pid == Environment.ProcessId, DateTime.Now);
        if (localNudge != null) ShowSuggestion(localNudge);
        if (!decision.Suppressed)
        {
            foreach (var item in Controller.Store.Data.Workspace.Where(t => !t.Done && !t.Notified && t.Due.HasValue && t.Due <= DateTime.Now).ToArray())
            { item.Notified = true; Controller.Store.Save(); ShowSuggestion(Loc.T("Reminder: ", "تذكير: ") + item.Title); }
        }
        bool titleDue = (DateTime.Now - lastScan).TotalSeconds >= Controller.Store.Data.Settings.ScanSeconds;
        if (titleDue) lastScan = DateTime.Now;
        bool checkTitle = titleDue && Controller.Store.Data.Settings.Proactive && Controller.LastExternalTitle != lastSuggestedTitle;
        if (!Controller.Busy && !decision.Suppressed && active.Pid != Environment.ProcessId && (checkTitle || Controller.Store.Data.Settings.ErrorAwareness))
        {
            if (checkTitle && !Controller.IsQuiet) lastSuggestedTitle = Controller.LastExternalTitle;
            await Controller.CheckSuggestionAsync(checkTitle);
        }
    }
    internal void ShowSuggestion(string text)
    {
        if (capturing || Exiting || Controller == null) return;
        var decision = SyncShellVisibility(forceDockRefresh: false, updateDockContext: false);
        if (decision.Suppressed) return;
        tray?.ShowBalloonTip(7000, Loc.T("NIVO", "نيڤو"), text, Forms.ToolTipIcon.Info);
        Action accept = () => { Controller.Mode = "Agent"; Controller.Draft = text; Controller.Notify(); ShowAsk(); ask?.Refresh(Controller); };
        if (decision.ShowIsland && island is { IsVisible: true, IsExpanded: false } && !Controller.Busy) island.ShowSuggestion(text, accept);
        else { var window = new SuggestionWindow(text, accept); window.Show(); }
    }
    public async Task ReadAloud(string text)
    {
        StopAudio(); var request = new CancellationTokenSource(TimeSpan.FromSeconds(90)); speechRequest = request;
        try
        {
            string file = await Controller.Voice.Speak(text, request.Token); if (request.IsCancellationRequested) { if (File.Exists(file)) File.Delete(file); return; }
            voiceFile = file; player = new MediaPlayer(); player.MediaEnded += (_, _) => StopAudio(); player.MediaFailed += (_, _) => StopAudio(); player.Open(new Uri(voiceFile)); Controller.SetStatus("Speaking…"); player.Play();
        }
        finally { if (speechRequest == request) speechRequest = null; request.Dispose(); }
    }
    private void StopAudio()
    {
        speechRequest?.Cancel(); player?.Stop(); player?.Close(); player = null;
        if (voiceFile != null && File.Exists(voiceFile)) { try { File.Delete(voiceFile); } catch { } } voiceFile = null;
        if (Controller != null && Controller.Status.StartsWith("Speaking", StringComparison.Ordinal)) Controller.SetStatus("Ready when you are");
    }
    public void StopAll() { RequestAccountTalkStop(); Controller.Stop(); Controller.Voice.CancelRecording(); StopAudio(); foreach (var review in Windows.OfType<ReviewPlanWindow>().ToArray()) review.Close(); }
    public async void Quit()
    {
        if (Exiting) return;
        Exiting = true; shellTimer.Stop(); timer.Stop(); Controller?.Stop(); Controller?.Voice.CancelRecording(); StopAudio(); hotkeys?.Dispose(); TaskbarService.Restore();
        try { await StopAccountTalkAsync(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException or TimeoutException)
        { System.Diagnostics.Trace.TraceWarning("NIVO voice shutdown during quit ended ({0}).", ex.GetType().Name); }
        quitWait?.Unregister(null); quitWait = null; quitSignal?.Dispose(); quitSignal = null;
        if (tray != null) { tray.Visible = false; tray.Icon?.Dispose(); tray.Dispose(); tray = null; } mutex?.Dispose(); mutex = null; Shutdown();
    }
}
