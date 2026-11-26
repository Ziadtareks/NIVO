using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Nivo.Core;
using Nivo.Services;
using Nivo.UI;

namespace Nivo;

// Explicit opt-in native desktop checks. The external fixture covers the captured monitor;
// neither the real clipboard nor an unrelated application's pixels are used as test data.
internal static class DesktopAcceptance
{
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

    public static async Task Run(bool gemini)
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "desktop-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(root); File.WriteAllText("artifacts/latest-desktop.txt", root);
        if (File.Exists("artifacts/desktop-failure.txt")) File.Delete("artifacts/desktop-failure.txt");
        var checks = new List<string>();
        using var trace = new TextWriterTraceListener(Path.Combine(root, "native-trace.log")); Trace.Listeners.Add(trace);
        void Check(bool value, string description)
        {
            if (!value) throw new InvalidOperationException("FAIL: " + description);
            checks.Add("PASS " + description); File.WriteAllLines(Path.Combine(root, "results.txt"), checks);
        }
        var app = (App)Application.Current;
        var store = new Store(Path.Combine(root, "data")); store.Data.Settings.Language = "English";
        store.Data.Settings.ErrorAwareness = true; store.Data.Settings.ErrorDwellSeconds = 5; store.Data.Settings.Proactive = false;
        var c = new Controller(store); app.ControllerForTests(c); Loc.Apply("English"); Ui.ApplyTheme("Frost"); Motion.Reduced = true;
        string cliFixture = Path.GetFullPath("tests/Nivo.FixtureCli/bin/Release/net10.0/Nivo.FixtureCli.exe");
        string windowFixture = Path.GetFullPath("tests/Nivo.WindowFixture/bin/Release/net10.0-windows10.0.19041.0/Nivo.WindowFixture.exe");
        if (!File.Exists(windowFixture) || (!gemini && !File.Exists(cliFixture))) throw new FileNotFoundException("Build Nivo.WindowFixture and Nivo.FixtureCli before native desktop acceptance.");
        string? temporaryGoogleKey = null;
        if (gemini)
        {
            string key = new Store().GetSecret("google");
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Configure the Google AI Studio key before live desktop acceptance.");
            temporaryGoogleKey = key; store.Data.Settings.Model = "google/gemini-3.5-flash-lite";
            store.Data.Settings.UseUserOpenCodeConfig = true; store.Data.Settings.FreeModelsOnly = false;
        }
        else store.Data.Settings.CliPath = cliFixture;
        store.Data.Settings.FallbackModel = store.Data.Settings.VisionFallbackModel = "";
        var oldPointer = DesktopService.CursorPosition();
        var b = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        var monitor = new PixelRect(b.X, b.Y, b.Width, b.Height);
        var point = new PixelPoint(b.X + b.Width / 2, b.Y + b.Height / 3);
        var start = new ProcessStartInfo(windowFixture) { UseShellExecute = false, CreateNoWindow = true };
        foreach (int value in new[] { Environment.ProcessId, b.X, b.Y, b.Width, b.Height }) start.ArgumentList.Add(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var target = Process.Start(start) ?? throw new InvalidOperationException("Cannot start synthetic desktop target.");
        void PlaceTarget(bool fullscreen)
        {
            int inset = fullscreen ? 0 : 32;
            if (!SetWindowPos(target.MainWindowHandle, IntPtr.Zero, b.X + inset, b.Y + inset, b.Width - 2 * inset, b.Height - 2 * inset, 0x0014))
                throw new System.ComponentModel.Win32Exception();
            DesktopService.FlushCompositor();
        }
        var control = new Window { Title = "NIVO acceptance controls", Width = 300, Height = 100, Left = b.Left + 20, Top = b.Top + 20, Topmost = true, ShowActivated = false, Content = new TextBlock { Text = "Synthetic desktop acceptance in progress", Margin = new Thickness(12) } };
        try
        {
            if (temporaryGoogleKey != null) { store.SetSecret("google", temporaryGoogleKey); temporaryGoogleKey = null; }
            for (int i = 0; i < 80 && target.MainWindowHandle == IntPtr.Zero && !target.HasExited; i++) { await Task.Delay(100); target.Refresh(); }
            if (target.HasExited || target.MainWindowHandle == IntPtr.Zero) throw new InvalidOperationException("Synthetic target did not open.");
            Check(GetWindowRect(target.MainWindowHandle, out var rect) && rect.Left == b.Left && rect.Top == b.Top && rect.Right == b.Right && rect.Bottom == b.Bottom, "External synthetic window covers the entire captured monitor");
            Check(SetCursorPos(point.X, point.Y), "Pointer placed on synthetic target");
            DesktopService.FocusWindow(target.MainWindowHandle); await Task.Delay(250); DesktopService.FlushCompositor();
            Check(DesktopService.WindowAtPoint(point).Pid == target.Id, "Capture point belongs to the external synthetic fixture");
            var baseline = DesktopService.CaptureRegion(CaptureGeometry.ClampRegionAroundPoint(point, monitor));
            Check(BlueCorners(baseline), "Synthetic capture bounds contain only the fixture's known blue background at every corner");
            app.RefreshShell();
            Check(!app.Windows.OfType<DockWindow>().Any(w => w.IsVisible) && app.Island?.IsVisible != true, "Full-monitor external fixture suppresses Dock and Island");
            PlaceTarget(fullscreen: false); app.RefreshShell();
            app.ToggleIsland(); app.RefreshShell();
            Check(app.Island is { IsVisible: false }, "Refreshing desktop settings preserves an explicitly hidden Island");
            app.ToggleIsland();
            app.ShowAsk(); var languageAsk = app.Windows.OfType<AskWindow>().Single();
            app.ApplyLanguage("Arabic", false); app.ApplyLanguage("English", false);
            Check(!app.Windows.Cast<Window>().Contains(languageAsk), "Language changes permanently close the old Silent Ask rather than retaining a subscribed hidden window");
            var previousIsland = app.Island!; app.ApplyLanguage("English", false);
            previousIsland.ShowCompleted("Synthetic conversion completed after language change");
            Check(app.Island != previousIsland && Descendants<TextBlock>(app.Island!).Any(x => x.Text == "Synthetic conversion completed after language change"), "Completion arriving on an old Island is forwarded to the current language instance");
            app.Island!.SwitchView("command");
            foreach (string language in new[] { "Arabic", "English" })
            {
                app.ApplyLanguage(language, false);
                var liveDock = app.Windows.OfType<DockWindow>().Single(); liveDock.Refresh([]); liveDock.UpdateLayout();
                var pinned = Descendants<Button>(liveDock).Where(button => button.ToolTip is string tip && tip.EndsWith(" · pinned")).ToArray();
                foreach (var button in new[] { pinned.First(), pinned.Last() })
                {
                    var screenPoint = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
                    Check(SetCursorPos((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y)), language + " native pointer moves onto a dock app");
                    await Task.Delay(200); liveDock.UpdateLayout();
                    string name = ((string)button.ToolTip)[..^" · pinned".Length];
                    var currentPointer = DesktopService.CursorPosition();
                    Trace.TraceInformation("NIVO native hover: locale={0}, expected={1}, actual={2}, labelCenter={3}, iconCenter={4}, mouseOver={5}, cursorOwn={6}, plannedPointer={7}, actualPointer={8}.", language, name, liveDock.HoverPillLabel, liveDock.HoverPillRootCenterX, button.TranslatePoint(new Point(button.ActualWidth / 2, 0), liveDock).X, button.IsMouseOver, DesktopService.WindowAtPoint(currentPointer).Pid == Environment.ProcessId, screenPoint, currentPointer);
                    Check(liveDock.HoverPillLabel == name && Math.Abs(liveDock.HoverPillRootCenterX - button.TranslatePoint(new Point(button.ActualWidth / 2, 0), liveDock).X) < 2, language + " real mouse hover places the app name directly above its icon");
                }
                var dockImage = new RenderTargetBitmap((int)liveDock.ActualWidth, (int)liveDock.ActualHeight, 96, 96, PixelFormats.Pbgra32); dockImage.Render(liveDock);
                DesktopService.SavePng(dockImage, Path.Combine(root, "native-dock-hover-" + language.ToLowerInvariant() + ".png"));
            }
            SetCursorPos(point.X, point.Y);
            PlaceTarget(fullscreen: true);
            control.Show();

            async Task<bool> DrivePoint(Key key, bool concurrent = false)
            {
                Exception? driverError = null; bool driven = false; var deadline = Stopwatch.StartNew();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                timer.Tick += async (_, _) => {
                    if (driven) return;
                    var picker = app.Windows.OfType<PointPickerWindow>().FirstOrDefault(w => w.IsVisible && w.IsLoaded);
                    if (deadline.Elapsed > TimeSpan.FromSeconds(8)) { driverError = new TimeoutException("Point picker did not become ready for input."); timer.Stop(); if (picker != null) KeyTo(picker, Key.Escape); return; }
                    if (picker == null) return;
                    if (key != Key.Escape && (!picker.IsActive || !picker.IsArmed || Mouse.LeftButton != MouseButtonState.Released)) return;
                    driven = true;
                    try
                    {
                        if (concurrent)
                        {
                            app.TimerTick(null, EventArgs.Empty);
                            app.ShowSuggestion("Synthetic notification during selection");
                            Check(!app.Windows.OfType<DockWindow>().Any(w => w.IsVisible) && !app.Windows.OfType<IslandWindow>().Any(w => w.IsVisible) && !app.Windows.OfType<SuggestionWindow>().Any(w => w.IsVisible), "Desktop timer and incoming suggestions cannot reveal NIVO shell windows during selection");
                            bool rejected = false; try { await app.CaptureAsync(false, true, false); } catch (InvalidOperationException) { rejected = true; }
                            Check(rejected, "A second capture cannot open another picker during an active selection");
                        }
                        Trace.TraceInformation("NIVO native driver: key={0}, leftButton={1}, active={2}.", key, Mouse.LeftButton, picker.IsActive);
                        KeyTo(picker, key);
                        Trace.TraceInformation("NIVO native driver after key: confirmed={0}, visible={1}.", picker.Confirmed, picker.IsVisible);
                    }
                    catch (Exception ex) { driverError = ex; KeyTo(picker, Key.Escape); }
                };
                timer.Start();
                try { bool result = await app.CaptureAsync(false, true, false); if (driverError != null) throw new InvalidOperationException("Native point-picker driver failed.", driverError); return result; }
                finally { timer.Stop(); }
            }

            c.Capture = baseline; string modeBefore = c.Mode;
            Check(!await DrivePoint(Key.Escape, true) && ReferenceEquals(c.Capture, baseline) && c.Mode == modeBefore && control.IsVisible, "Point cancellation preserves the old attachment, reports false and restores NIVO windows");
            Check(await DrivePoint(Key.Enter) && c.Capture != null && BlueCorners(c.Capture) && control.IsVisible, "Confirmed native point capture contains the fixture without picker overlay and restores NIVO windows");
            var scale = DesktopService.MonitorDpiScaleAt(point);
            Check(c.Capture!.PixelWidth == Math.Min(b.Width, (int)Math.Round(760 * scale.ScaleX)) && c.Capture.PixelHeight == Math.Min(b.Height, (int)Math.Round(460 * scale.ScaleY)), "Native point capture uses the spotlight's DPI-scaled region rather than the full monitor");
            DesktopService.SavePng(c.Capture!, Path.Combine(root, "native-point.png"));
            var captured = c.Capture;
            var cropTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            cropTimer.Tick += (_, _) => { var crop = app.Windows.OfType<CropWindow>().FirstOrDefault(w => w.IsVisible && w.IsLoaded); if (crop != null) KeyTo(crop, Key.Escape); };
            cropTimer.Start();
            try { Check(!await app.CaptureAsync(true, false, false) && ReferenceEquals(c.Capture, captured) && control.IsVisible, "Native crop Escape reports cancellation without replacing the existing capture"); }
            finally { cropTimer.Stop(); }

            var from = new PixelPoint(b.Left + b.Width / 5, b.Top + b.Height / 5);
            var to = new PixelPoint(b.Left + b.Width * 11 / 20, b.Top + b.Height * 13 / 20);
            int phase = 0; bool mouseDown = false; Exception? dragError = null;
            DateTime? dragDeadline = null;
            var dragTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            dragTimer.Tick += (_, _) => {
                var crop = app.Windows.OfType<CropWindow>().FirstOrDefault(w => w.IsVisible && w.IsLoaded); if (crop == null) return;
                try
                {
                    dragDeadline ??= DateTime.UtcNow.AddSeconds(8);
                    if (DateTime.UtcNow > dragDeadline) throw new TimeoutException($"Native drag did not reach its observable completion state (phase {phase}, captured={Mouse.Captured != null}, left={Mouse.LeftButton}).");
                    if (phase == 0) { crop.Activate(); Check(SetCursorPos(from.X, from.Y), "Native crop drag starts inside the synthetic target"); phase++; }
                    else if (phase == 1) { if (GetForegroundWindow() != new WindowInteropHelper(crop).Handle) return; mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouseDown = true; phase++; }
                    else if (phase == 2) {
                        if (Mouse.Captured is not DependencyObject owner || Window.GetWindow(owner) != crop) return;
                        if (!SetCursorPos(to.X, to.Y)) throw new InvalidOperationException("Cannot move the synthetic drag pointer."); phase++;
                    }
                    else if (phase == 3) {
                        double expectedWidth = (to.X - from.X) * crop.ActualWidth / b.Width;
                        double expectedHeight = (to.Y - from.Y) * crop.ActualHeight / b.Height;
                        if (!Descendants<System.Windows.Shapes.Rectangle>(crop).Any(r => Math.Abs(r.Width - expectedWidth) < 1.5 && Math.Abs(r.Height - expectedHeight) < 1.5)) return;
                        mouse_event(4, 0, 0, 0, UIntPtr.Zero); mouseDown = false; phase++;
                    }
                    else if (phase == 4) {
                        var askButton = Descendants<Button>(crop).SingleOrDefault(x => Equals(x.Tag, "Ask") && x.IsVisible);
                        if (askButton == null) return;
                        Check(CropWindow.ToolbarActions.All(action => Descendants<Button>(crop).Any(x => Equals(x.Tag, action) && x.IsVisible)), "All nine native crop actions appear after a real pointer drag");
                        phase++; askButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    }
                }
                catch (Exception ex) { dragError = ex; if (mouseDown) { mouse_event(4, 0, 0, 0, UIntPtr.Zero); mouseDown = false; } KeyTo(crop, Key.Escape); }
            };
            dragTimer.Start();
            try
            {
                bool selected = await app.CaptureAsync(true, false, false);
                if (dragError != null) throw new InvalidOperationException("Native crop drag driver failed.", dragError);
                Trace.TraceInformation("NIVO native crop output: selected={0}, width={1}, height={2}, expectedWidth={3}, expectedHeight={4}, blueCorners={5}.", selected, c.Capture?.PixelWidth, c.Capture?.PixelHeight, to.X - from.X, to.Y - from.Y, c.Capture != null && BlueCorners(c.Capture));
                Check(selected && c.Capture != null && Math.Abs(c.Capture.PixelWidth - (to.X - from.X)) <= 2 && Math.Abs(c.Capture.PixelHeight - (to.Y - from.Y)) <= 2 && BlueCorners(c.Capture), "Real crop drag selects the expected physical pixels without selection overlays");
                DesktopService.SavePng(c.Capture!, Path.Combine(root, "native-crop.png"));
            }
            finally { dragTimer.Stop(); if (mouseDown) mouse_event(4, 0, 0, 0, UIntPtr.Zero); }
            c.Capture = null;
            store.Incognito = true; bool privateBlocked = false; try { await app.CaptureAsync(false, true, false); } catch (InvalidOperationException) { privateBlocked = true; }
            Check(privateBlocked && control.IsVisible, "Incognito blocks native capture without hiding NIVO controls"); store.Incognito = false;
            store.Data.Settings.ExcludedApps.Add("NIVO Synthetic Target"); control.Activate(); await Task.Delay(100);
            Check(DesktopService.WindowAtPoint(DesktopService.CursorPosition()).Pid == target.Id, "Excluded-window test targets the synthetic fixture after the crop drag");
            bool excluded = false; bool? excludedCaptureResult = null; try { excludedCaptureResult = await DrivePoint(Key.Enter); } catch (InvalidOperationException ex) when (ex.Message.Contains("excluded", StringComparison.OrdinalIgnoreCase)) { excluded = true; }
            Trace.TraceInformation("NIVO native exclusion check: blocked={0}, captureResult={1}, controlsVisible={2}.", excluded, excludedCaptureResult, control.IsVisible);
            Check(excluded && control.IsVisible, "Excluded target behind NIVO is blocked and hidden controls are restored"); store.Data.Settings.ExcludedApps.Remove("NIVO Synthetic Target");

            // Decline only our own real sharing dialog; use an injected clipboard sink throughout.
            var originalCapture = baseline; c.Capture = originalCapture; c.Mode = "Web";
            bool previousImageConfirmation = store.Data.Settings.ConfirmImageSharing;
            store.Data.Settings.ConfirmImageSharing = true; // Exercise the optional confirmation explicitly.
            BitmapSource? copiedClipboardImage = null;
            var clipboard = new IslandWindow(c, () => (null, baseline), (_, image) => copiedClipboardImage = image) { Left = b.Left + 20, Top = b.Top + 140 };
            var decline = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) }; bool declinedOwnDialog = false;
            decline.Tick += (_, _) =>
            {
                var dialog = FindWindow("#32770", "NIVO · Share this capture?");
                if (dialog == IntPtr.Zero) return;
                GetWindowThreadProcessId(dialog, out uint pid);
                if (pid != Environment.ProcessId) return;
                declinedOwnDialog = PostMessage(dialog, 0x111, new IntPtr(7), IntPtr.Zero); // WM_COMMAND / IDNO
                decline.Stop();
            };
            try
            {
                clipboard.Show(); clipboard.SwitchView("clipboard"); decline.Start();
                Descendants<Button>(clipboard).Single(x => x.IsVisible && Equals(x.Content, "Explain")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check(declinedOwnDialog && c.Current.Messages.Count == 0 && !c.Busy && c.Mode == "Web" && ReferenceEquals(c.Capture, originalCapture), "Declining actual clipboard image sharing preserves prior capture/mode and makes no model request");
                Descendants<Button>(clipboard).Single(x => x.IsVisible && Equals(x.Content, "Copy")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check(ReferenceEquals(copiedClipboardImage, baseline), "Clipboard Copy after declined sharing retains its original image without writing the real clipboard");
            }
            finally { decline.Stop(); clipboard.Close(); c.Capture = null; c.Mode = "General"; store.Data.Settings.ConfirmImageSharing = previousImageConfirmation; }

            control.Hide(); app.Island?.Hide(); DesktopService.FocusWindow(target.MainWindowHandle); await Task.Delay(250);
            Check(DesktopService.ActiveWindow().Pid == target.Id, "Error-awareness foreground is the isolated synthetic target");
            PlaceTarget(fullscreen: false); DesktopService.FocusWindow(target.MainWindowHandle); await Task.Delay(200);
            var awareness = new ErrorAwarenessService();
            var probe = await awareness.ProbeForegroundWindowAsync(store.Data.Settings, false, false, CancellationToken.None);
            File.WriteAllText(Path.Combine(root, "uia-status.txt"), probe.Reason);
            Check(probe.Supported && probe.Window.Pid == target.Id && probe.VisibleText.Contains("TypeError"), "Windows UI Automation reads the real fixture's visible error text");
            Check(!probe.VisibleText.Contains("NIVO_PRIVATE_PASSWORD") && !probe.VisibleText.Contains("NIVO_HIDDEN_SENTINEL"), "Native UI Automation excludes password and hidden fixture content");
            string? suggestion = null; c.Suggestion += text =>
            {
                suggestion = text;
                IntPtr foregroundBefore = GetForegroundWindow(); app.ShowSuggestion(text);
                Trace.TraceInformation("NIVO native suggestion focus: before={0}, after={1}, target={2}.", foregroundBefore, GetForegroundWindow(), target.MainWindowHandle);
            };
            await c.CheckSuggestionAsync(false); Check(suggestion == null, "A first native error observation does not immediately produce a suggestion");
            // UIA providers can become briefly unavailable while the window/accessibility tree settles.
            // Exercise the same bounded periodic sampling as production, not exactly two timing-sensitive probes.
            for (int i = 0; i < 4 && suggestion == null; i++) { await Task.Delay(5300); await c.CheckSuggestionAsync(false); }
            Check(!string.IsNullOrWhiteSpace(suggestion), gemini ? "Live Gemini produces help after sustained visible synthetic error" : "CLI fixture produces help after sustained visible synthetic error");
            var popup = app.Windows.OfType<SuggestionWindow>().Single(w => w.IsVisible);
            Trace.TraceInformation("NIVO native suggestion assertion: current={0}, popup={1}, target={2}, showActivated={3}.", GetForegroundWindow(), new WindowInteropHelper(popup).Handle, target.MainWindowHandle, popup.ShowActivated);
            Check(!popup.ShowActivated && DesktopService.ActiveWindow().Pid == target.Id, "Error suggestion appears without taking foreground focus");
            Descendants<Button>(popup).Single(x => Equals(x.Content, "Do it")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var ask = app.Windows.OfType<AskWindow>().Single(w => w.IsVisible);
            Check(c.Mode == "Agent" && c.Draft == suggestion && Descendants<TextBox>(ask).Any(x => x.Text == suggestion), "Accepting native error help opens the actual Agent composer with the suggestion draft");
            Check(c.Current.Messages.Count == 0 && store.Data.Workspace.Count == 0, "Native acceptance never submitted or executed an unreviewed action");
            File.WriteAllText(Path.Combine(root, "scope.txt"), gemini ? "Native primary-monitor and UI Automation acceptance with one synthetic-error Gemini request; no fallback, real clipboard untouched." : "Native primary-monitor and UI Automation acceptance with deterministic CLI fixture; no live AI or real clipboard.");
        }
        finally
        {
            foreach (Window window in app.Windows.Cast<Window>().ToArray()) window.Hide();
            control.Close();
            if (!target.HasExited) { target.CloseMainWindow(); if (!await Task.Run(() => target.WaitForExit(3000))) { target.Kill(true); await target.WaitForExitAsync(); } }
            SetCursorPos(oldPointer.X, oldPointer.Y);
            Trace.Flush(); Trace.Listeners.Remove(trace);
            if (gemini) store.SetSecret("google", "");
        }
    }

    private static void KeyTo(Window window, Key key) => window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    private static bool BlueCorners(BitmapSource source)
    {
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        foreach (var (x, y) in new[] { (0, 0), (bitmap.PixelWidth - 1, 0), (0, bitmap.PixelHeight - 1), (bitmap.PixelWidth - 1, bitmap.PixelHeight - 1) })
        {
            byte[] pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            if (Math.Abs(pixel[0] - 0xA8) > 3 || Math.Abs(pixel[1] - 0x74) > 3 || Math.Abs(pixel[2] - 0x27) > 3) return false;
        }
        return true;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T result) yield return result; foreach (var next in Descendants<T>(child)) yield return next; }
    }
}
