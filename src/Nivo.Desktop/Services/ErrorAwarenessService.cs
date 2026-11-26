using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using Nivo.Core;

namespace Nivo.Services;

public sealed record ObservedWindow(IntPtr Handle, string Title, string Process, int Pid);

public sealed record DetectedError(string Kind, string Signature, string Snippet);

public sealed record ErrorTrigger(
    string Kind,
    string Signature,
    string Snippet,
    string WindowTitle,
    string ProcessName,
    TimeSpan Dwell,
    int SampleCount);

public sealed record ErrorProbeResult(
    bool Supported,
    bool Blocked,
    bool Busy,
    ObservedWindow Window,
    string VisibleText,
    string Reason);

public sealed record ErrorObservationResult(
    DetectedError? Detected,
    ErrorTrigger? Trigger,
    string Reason);

public sealed class ErrorAwarenessService
{
    public const int MaxNodes = 160;
    public const int MaxDepth = 10;
    public const int MaxChildrenPerNode = 40;
    public const int MaxCollectedChars = 8_000;
    public const int MaxSnippetChars = 500;
    public static readonly TimeSpan DefaultTraversalBudget = TimeSpan.FromMilliseconds(600);
    public static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromMilliseconds(1200);
    public static readonly TimeSpan DefaultMissGrace = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DefaultGlobalCooldown = TimeSpan.FromMinutes(2);

    private static readonly string[] PasswordHints =
    [
        "password", "passcode", "passphrase", "secret", "pin", "otp", "totp",
        "credit card", "card number", "cvv", "cvc", "ssn", "private key", "api key"
    ];

    private static readonly Regex RuntimeErrorRegex = new(
        @"\b(?<kind>(?:Uncaught\s+)?(?:TypeError|SyntaxError|ReferenceError|RangeError|URIError|EvalError|AttributeError|NameError|ValueError|KeyError|ImportError|ModuleNotFoundError|IndexError|ZeroDivisionError|IndentationError|TabError|RuntimeError|FileNotFoundError|PermissionError|AssertionError|UnboundLocalError|UnhandledPromiseRejection))\s*:\s*\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DotNetExceptionRegex = new(
        @"\b(?<kind>(?:System\.)?(?:NullReferenceException|InvalidOperationException|ArgumentNullException|ArgumentException|FormatException|IndexOutOfRangeException|KeyNotFoundException|StackOverflowException|UnauthorizedAccessException|IOException))\s*:\s*\S+",
        RegexOptions.Compiled);

    private static readonly Regex TracebackHeaderRegex = new(
        @"Traceback\s*\(most recent call last\)\s*:",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CompilerErrorCodeRegex = new(
        @"(?:\berror\s+(?<code>(?:CS|MSB|TS|C|LNK|NETSDK)\d{4})\b|\berror\[(?<code>E\d{4})\]\s*:|\bfatal error\s*:\s*\S+|:\d+(?::\d+)?\s*:\s*(?:fatal\s+)?error\s*:\s*\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RedactSecretRegex = new(
        @"(?i)\b(bearer\s+[A-Za-z0-9._\-]{12,}|(?:sk|pk|ghp|gho|github_pat|xox[baprs])_[A-Za-z0-9_\-]{10,}|(?:password|passwd|pwd|secret|token|api[_-]?key)\s*[:=]\s*[^\s,'""]{4,})",
        RegexOptions.Compiled);

    private static readonly Regex NormalizeTimeRegex = new(@"\b\d{1,2}:\d{2}(?::\d{2})?(?:\.\d+)?\b", RegexOptions.Compiled);
    private static readonly Regex NormalizeHexRegex = new(@"\b0x[0-9a-f]+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NormalizeLineLocRegex = new(@"(?:\bline\s+\d+\b|\(\d+,\s*\d+\)|:\d+(?::\d+)?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CollapseWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private readonly object stateLock = new();
    private readonly Dictionary<string, DateTime> signatureCooldowns = new(StringComparer.Ordinal);
    private string activeSignature = "";
    private DateTime firstSeen = DateTime.MinValue;
    private DateTime lastSeen = DateTime.MinValue;
    private int sampleCount;
    private DateTime lastGlobalTrigger = DateTime.MinValue;
    private int probeInFlight;

    public int MinStableSamples { get; }
    public TimeSpan MissGrace { get; }
    public TimeSpan GlobalCooldown { get; }
    public TimeSpan? DwellOverride { get; }
    public TimeSpan? CooldownOverride { get; }

    public ErrorAwarenessService(
        int minStableSamples = 2,
        TimeSpan? dwellOverride = null,
        TimeSpan? cooldownOverride = null,
        TimeSpan? missGrace = null,
        TimeSpan? globalCooldown = null)
    {
        MinStableSamples = Math.Max(1, minStableSamples);
        DwellOverride = dwellOverride;
        CooldownOverride = cooldownOverride;
        MissGrace = missGrace ?? DefaultMissGrace;
        GlobalCooldown = globalCooldown ?? DefaultGlobalCooldown;
    }

    public bool IsProbeInFlight => Volatile.Read(ref probeInFlight) != 0;

    public void Reset()
    {
        lock (stateLock)
        {
            activeSignature = "";
            firstSeen = DateTime.MinValue;
            lastSeen = DateTime.MinValue;
            sampleCount = 0;
        }
    }

    public static bool IsBlockedWindow(Settings settings, bool incognito, bool autoGaming, string title, string process, int pid)
    {
        if (settings == null || !settings.ErrorAwareness) return true;
        if (incognito || settings.Gaming || autoGaming) return true;
        if (pid <= 0 || pid == Environment.ProcessId) return true;
        title ??= "";
        process ??= "";
        if (CheckInService.IsGame(process, title)) return true;
        if (settings.ExcludedApps != null && settings.ExcludedApps.Any(x =>
                !string.IsNullOrWhiteSpace(x) &&
                (title + " " + process).Contains(x, StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

    public static bool IsPasswordElement(bool isPassword, string? controlTypeProgrammaticName, string? name, string? automationId)
    {
        if (isPassword) return true;
        bool isTextInput = !string.IsNullOrWhiteSpace(controlTypeProgrammaticName) &&
            controlTypeProgrammaticName.Contains("Edit", StringComparison.OrdinalIgnoreCase);
        if (!isTextInput) return false;
        string combined = (name ?? "") + " " + (automationId ?? "");
        return PasswordHints.Any(h => combined.Contains(h, StringComparison.OrdinalIgnoreCase));
    }

    public static string RedactSensitiveTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return RedactSecretRegex.Replace(text, "[REDACTED]");
    }

    public static DetectedError? DetectError(string? visibleText)
    {
        if (string.IsNullOrWhiteSpace(visibleText)) return null;
        string safe = RedactSensitiveTokens(visibleText);
        var rawLines = safe.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var lines = new List<string>(Math.Min(rawLines.Length, 240));
        foreach (var raw in rawLines)
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;
            lines.Add(trimmed.Length > 400 ? trimmed[..400] : trimmed);
            if (lines.Count >= 240) break;
        }
        if (lines.Count == 0) return null;

        int matchIndex = -1;
        string? kind = null;
        string? coreLine = null;

        // Scan from bottom up so the most recent visible terminal/editor error wins.
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            string line = lines[i];
            var rt = RuntimeErrorRegex.Match(line);
            if (rt.Success)
            {
                matchIndex = i;
                kind = NormalizeKind(rt.Groups["kind"].Value);
                coreLine = line[rt.Index..];
                break;
            }
            var dn = DotNetExceptionRegex.Match(line);
            if (dn.Success)
            {
                matchIndex = i;
                kind = NormalizeKind(dn.Groups["kind"].Value);
                coreLine = line[dn.Index..];
                break;
            }
            var cc = CompilerErrorCodeRegex.Match(line);
            if (cc.Success)
            {
                matchIndex = i;
                string code = cc.Groups["code"].Value;
                kind = string.IsNullOrWhiteSpace(code) ? "CompilerError" : "CompilerError:" + code.ToUpperInvariant();
                coreLine = line;
                break;
            }
        }

        if (matchIndex < 0)
        {
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (TracebackHeaderRegex.IsMatch(lines[i]))
                {
                    matchIndex = i;
                    kind = "Traceback";
                    coreLine = i + 1 < lines.Count ? lines[i] + " " + lines[Math.Min(lines.Count - 1, i + 2)] : lines[i];
                    break;
                }
            }
        }

        if (matchIndex < 0 || kind == null || coreLine == null) return null;

        int start = Math.Max(0, matchIndex - 2);
        int end = Math.Min(lines.Count - 1, matchIndex + 2);
        var snippetBuilder = new StringBuilder();
        for (int i = start; i <= end; i++)
        {
            if (snippetBuilder.Length > 0) snippetBuilder.AppendLine();
            snippetBuilder.Append(lines[i]);
            if (snippetBuilder.Length >= MaxSnippetChars) break;
        }
        string snippet = snippetBuilder.ToString();
        if (snippet.Length > MaxSnippetChars) snippet = snippet[..MaxSnippetChars];

        string signature = BuildSignature(kind, coreLine);
        return new DetectedError(kind, signature, snippet);
    }

    public ErrorObservationResult Observe(Store store, bool isQuiet, ObservedWindow window, string? visibleText, DateTime now)
    {
        var settings = store.Data.Settings;
        if (isQuiet || IsBlockedWindow(settings, store.Incognito, false, window.Title, window.Process, window.Pid))
        {
            Reset();
            return new(null, null, "blocked");
        }

        var detected = DetectError(visibleText);
        if (detected == null)
        {
            lock (stateLock)
            {
                if (!string.IsNullOrEmpty(activeSignature) && now - lastSeen > MissGrace)
                {
                    activeSignature = "";
                    firstSeen = DateTime.MinValue;
                    lastSeen = DateTime.MinValue;
                    sampleCount = 0;
                }
            }
            return new(null, null, "no-error");
        }

        TimeSpan dwell = DwellOverride ?? TimeSpan.FromSeconds(Math.Clamp(settings.ErrorDwellSeconds, 5, 300));
        TimeSpan cooldown = CooldownOverride ?? TimeSpan.FromSeconds(Math.Clamp(settings.ErrorCooldownSeconds, 30, 3600));

        lock (stateLock)
        {
            if (!string.Equals(activeSignature, detected.Signature, StringComparison.Ordinal))
            {
                activeSignature = detected.Signature;
                firstSeen = now;
                lastSeen = now;
                sampleCount = 1;
                return new(detected, null, "tracking-new");
            }

            if (now - lastSeen > MissGrace)
            {
                // Error disappeared beyond the debounce grace window and later reappeared: restart dwell.
                firstSeen = now;
                lastSeen = now;
                sampleCount = 1;
                return new(detected, null, "restarted-after-gap");
            }

            lastSeen = now;
            sampleCount++;
            TimeSpan elapsed = now - firstSeen;

            if (sampleCount < MinStableSamples || elapsed < dwell)
                return new(detected, null, "dwelling");

            if (lastGlobalTrigger != DateTime.MinValue && now - lastGlobalTrigger < GlobalCooldown)
                return new(detected, null, "global-cooldown");

            if (signatureCooldowns.TryGetValue(detected.Signature, out var previous) && now - previous < cooldown)
                return new(detected, null, "signature-cooldown");

            signatureCooldowns[detected.Signature] = now;
            lastGlobalTrigger = now;
            PruneCooldowns(now, cooldown);

            return new(detected, new ErrorTrigger(
                detected.Kind,
                detected.Signature,
                detected.Snippet,
                window.Title,
                window.Process,
                elapsed,
                sampleCount), "triggered");
        }
    }

    public async Task<ErrorProbeResult> ProbeForegroundWindowAsync(
        Settings settings,
        bool incognito,
        bool autoGaming,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        var window = ReadForegroundWindow();
        if (IsBlockedWindow(settings, incognito, autoGaming, window.Title, window.Process, window.Pid))
            return new(false, true, false, window, "", "Window is excluded, quiet, gaming, own-app, or opt-in is off.");

        if (window.Handle == IntPtr.Zero)
            return new(false, false, false, window, "", "No foreground window.");

        if (Interlocked.CompareExchange(ref probeInFlight, 1, 0) != 0)
            return new(false, false, true, window, "", "Previous UI Automation probe is still finishing.");

        var effectiveTimeout = timeout ?? DefaultProbeTimeout;
        var localCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        localCts.CancelAfter(effectiveTimeout);
        var token = localCts.Token;

        var worker = Task.Run(() =>
        {
            try
            {
                return ExtractVisibleTextBounded(window, settings, incognito, autoGaming, DefaultTraversalBudget, token);
            }
            finally
            {
                Volatile.Write(ref probeInFlight, 0);
                localCts.Dispose();
            }
        });

        var completed = await Task.WhenAny(worker, Task.Delay(effectiveTimeout, ct)).ConfigureAwait(false);
        if (completed != worker)
        {
            // Keep probeInFlight=1 until the worker exits its finally block so stuck probes never pile up.
            try { localCts.Cancel(); } catch (ObjectDisposedException) { Trace.TraceInformation("NIVO error probe completed while timeout cancellation was being delivered."); }
            return new(false, false, true, window, "", "UI Automation probe timed out.");
        }

        return await worker.ConfigureAwait(false);
    }

    private static ErrorProbeResult ExtractVisibleTextBounded(
        ObservedWindow window,
        Settings settings,
        bool incognito,
        bool autoGaming,
        TimeSpan budget,
        CancellationToken ct)
    {
        try
        {
            var root = AutomationElement.FromHandle(window.Handle);
            if (root == null)
                return new(false, false, false, window, "", "UI Automation does not expose this window.");

            var sw = Stopwatch.StartNew();
            var sb = new StringBuilder();
            var stack = new Stack<(AutomationElement Element, int Depth)>();
            stack.Push((root, 0));
            var walker = TreeWalker.ControlViewWalker;
            int visited = 0;

            while (stack.Count > 0 && visited < MaxNodes && sb.Length < MaxCollectedChars)
            {
                if (ct.IsCancellationRequested || sw.Elapsed >= budget) break;
                var (el, depth) = stack.Pop();
                visited++;

                bool isPassword;
                bool isOffscreen;
                string? controlType;
                string? name;
                string? automationId;
                try
                {
                    var current = el.Current;
                    isPassword = current.IsPassword;
                    isOffscreen = current.IsOffscreen;
                    controlType = current.ControlType?.ProgrammaticName;
                    name = current.Name;
                    automationId = current.AutomationId;
                }
                catch (ElementNotAvailableException) { continue; }
                catch (COMException) { continue; }
                catch (InvalidOperationException) { continue; }

                if (isOffscreen || IsPasswordElement(isPassword, controlType, name, automationId))
                    continue;

                AppendElementText(el, controlType, name, sb);

                if (depth >= MaxDepth || sb.Length >= MaxCollectedChars) continue;

                try
                {
                    var child = walker.GetFirstChild(el);
                    int childCount = 0;
                    var children = new List<AutomationElement>();
                    while (child != null && childCount < MaxChildrenPerNode)
                    {
                        if (ct.IsCancellationRequested || sw.Elapsed >= budget) break;
                        children.Add(child);
                        childCount++;
                        child = walker.GetNextSibling(child);
                    }
                    for (int i = children.Count - 1; i >= 0; i--)
                        stack.Push((children[i], depth + 1));
                }
                catch (Exception ex) when (ExpectedAutomationFailure(ex)) { TraceProbeFailure(ex); }
            }

            // Re-verify foreground window did not switch to an excluded/private app mid-probe.
            var after = ReadForegroundWindow();
            if (after.Handle != window.Handle && IsBlockedWindow(settings, incognito, autoGaming, after.Title, after.Process, after.Pid))
                return new(false, true, false, after, "", "Focus shifted to a blocked window during probe.");

            string text = sb.ToString().Trim();
            if (text.Length == 0)
                return new(false, false, false, window, "", "No visible UI Automation text available.");

            return new(true, false, false, window, text, "ok");
        }
        catch (ElementNotAvailableException)
        {
            return new(false, false, false, window, "", "Target window is no longer available.");
        }
        catch (COMException)
        {
            return new(false, false, false, window, "", "UI Automation is unsupported or blocked for this window.");
        }
        catch (UnauthorizedAccessException)
        {
            return new(false, false, false, window, "", "Window access denied.");
        }
        catch (NotSupportedException)
        {
            return new(false, false, false, window, "", "UI Automation is unsupported for this window.");
        }
    }

    private static void AppendElementText(AutomationElement el, string? controlType, string? name, StringBuilder sb)
    {
        if (sb.Length >= MaxCollectedChars) return;
        bool addedPatternText = false;
        bool hasTextPattern = false;

        try
        {
            if (el.TryGetCurrentPattern(TextPattern.Pattern, out var textObj) && textObj is TextPattern tp)
            {
                hasTextPattern = true;
                var ranges = tp.GetVisibleRanges();
                if (ranges != null && ranges.Length > 0)
                {
                    foreach (var range in ranges)
                    {
                        if (sb.Length >= MaxCollectedChars) break;
                        string chunk = range.GetText(Math.Min(1500, MaxCollectedChars - sb.Length));
                        if (!string.IsNullOrWhiteSpace(chunk))
                        {
                            sb.AppendLine(chunk);
                            addedPatternText = true;
                        }
                    }
                }
                // No DocumentRange fallback: it can contain offscreen or hidden document text.
            }
        }
        catch (Exception ex) when (ExpectedAutomationFailure(ex)) { TraceProbeFailure(ex); }

        if (!hasTextPattern && !addedPatternText && sb.Length < MaxCollectedChars)
        {
            try
            {
                if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var valObj) && valObj is ValuePattern vp)
                {
                    string val = vp.Current.Value ?? "";
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        int take = Math.Min(Math.Min(val.Length, 1200), MaxCollectedChars - sb.Length);
                        sb.AppendLine(val[..take]);
                        addedPatternText = true;
                    }
                }
            }
            catch (Exception ex) when (ExpectedAutomationFailure(ex)) { TraceProbeFailure(ex); }
        }

        if (!addedPatternText && !string.IsNullOrWhiteSpace(name) && sb.Length < MaxCollectedChars)
        {
            // Skip repeating top-level window title chrome alone; collect labels, list items, panes, dialogs, and text blocks.
            if (controlType == null || !controlType.EndsWith(".Window", StringComparison.Ordinal))
            {
                int take = Math.Min(Math.Min(name.Length, 600), MaxCollectedChars - sb.Length);
                sb.AppendLine(name[..take]);
            }
        }
    }

    private static string NormalizeKind(string raw)
    {
        raw = raw.Trim();
        if (raw.StartsWith("Uncaught ", StringComparison.OrdinalIgnoreCase))
            raw = raw[9..].Trim();
        if (raw.StartsWith("System.", StringComparison.Ordinal))
            raw = raw[7..];
        return raw;
    }

    private static string BuildSignature(string kind, string coreLine)
    {
        string normalized = coreLine.ToLowerInvariant();
        normalized = NormalizeTimeRegex.Replace(normalized, "<time>");
        normalized = NormalizeHexRegex.Replace(normalized, "<hex>");
        normalized = NormalizeLineLocRegex.Replace(normalized, "<loc>");
        normalized = CollapseWhitespaceRegex.Replace(normalized, " ").Trim();
        if (normalized.Length > 140) normalized = normalized[..140];
        return kind.ToLowerInvariant() + "|" + normalized;
    }

    private void PruneCooldowns(DateTime now, TimeSpan cooldown)
    {
        if (signatureCooldowns.Count <= 64) return;
        foreach (var key in signatureCooldowns.Where(kv => now - kv.Value >= cooldown).Select(kv => kv.Key).ToArray())
            signatureCooldowns.Remove(key);
        while (signatureCooldowns.Count > 64)
        {
            var oldest = signatureCooldowns.OrderBy(kv => kv.Value).First().Key;
            signatureCooldowns.Remove(oldest);
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);

    public static ObservedWindow ReadForegroundWindow()
    {
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero) return new(IntPtr.Zero, "", "", 0);
        var text = new StringBuilder(1024);
        _ = GetWindowText(handle, text, text.Capacity);
        _ = GetWindowThreadProcessId(handle, out uint pid);
        string name = "";
        try { if (pid != 0) using (var p = Process.GetProcessById((int)pid)) name = p.ProcessName; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { Trace.TraceInformation("NIVO foreground process is unavailable ({0}).", ex.GetType().Name); return new(handle, text.ToString(), "", 0); }
        return new(handle, text.ToString(), name, (int)pid);
    }

    private static bool ExpectedAutomationFailure(Exception ex) => ex is ElementNotAvailableException or COMException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException;
    private static void TraceProbeFailure(Exception ex) => Trace.TraceInformation("NIVO skipped unavailable UI Automation content ({0}).", ex.GetType().Name);
}
