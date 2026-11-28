using Nivo.Core;
using System.Diagnostics;

namespace Nivo.Services;

// Interprets only the provider's documented terminal voice markers. Terminal text is never executed.
internal sealed class NativeVoiceHandshake
{
    private readonly object gate = new();
    private readonly StringBuilder recent = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception?> ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool requested;
    public Task Ready => ready.Task;
    public Task Started => started.Task;
    public Task<Exception?> Ended => ended.Task;
    public bool NeedsAnnouncementDismiss { get { lock (gate) return recent.ToString().Contains("esc to dismiss", StringComparison.OrdinalIgnoreCase); } }

    public void ClearAnnouncement() { lock (gate) recent.Clear(); }
    public void BeginVoiceRequest() { lock (gate) { requested = true; recent.Clear(); } }
    public void Observe(string text)
    {
        lock (gate)
        {
            recent.Append(text);
            if (recent.Length > 24_000) recent.Remove(0, recent.Length - 24_000);
            string current = recent.ToString();
            if (!requested)
            {
                // The first prompt is rendered before native model/backend initialization finishes.
                // Only a complete footer carrying a model status is a usable prompt (observed on CLI 1.5.18).
                if (HasModelStatusLine(current)) ready.TrySetResult();
                else if (current.Contains("Select login method:", StringComparison.OrdinalIgnoreCase))
                    Fail(Loc.T("Reconnect Antigravity in AI & accounts before starting Talk.", "أعد ربط Antigravity من إعدادات الذكاء والحسابات قبل بدء Talk."));
                return;
            }
            if (current.Contains("Started voice chat", StringComparison.OrdinalIgnoreCase)) started.TrySetResult();
            if (started.Task.IsCompletedSuccessfully)
            {
                if (current.Contains("Stopped voice chat", StringComparison.OrdinalIgnoreCase)) ended.TrySetResult(null);
                return;
            }
            if (current.Contains("Live mode isn't available", StringComparison.OrdinalIgnoreCase)
                || current.Contains("Live mode isn’t available", StringComparison.OrdinalIgnoreCase))
            {
                Fail(Loc.T(
                    "Antigravity reports that native Live voice is unavailable in this session. Your account can still support text and images without Live voice. This message does not establish a microphone-permission problem. Check voice availability with Antigravity; NIVO cannot enable a provider-disabled feature. No API or other account was substituted.",
                    "Antigravity بيبلغ إن الصوت الأصلي Live غير متاح في الجلسة دي. الحساب ممكن يدعم النص والصور من غير Live. الرسالة دي مش دليل على مشكلة صلاحية الميكروفون. تحقق من إتاحة الصوت عند Antigravity؛ نيڤو ما يقدرش يفعّل ميزة المزوّد قافلها. لم يتم التحويل إلى API أو حساب آخر."));
                return;
            }
            if (current.Contains("Couldn't open the microphone", StringComparison.OrdinalIgnoreCase)
                || current.Contains("Couldn’t open the microphone", StringComparison.OrdinalIgnoreCase))
            {
                Fail(Loc.T(
                    "Antigravity could not open its audio connection. Stop any other voice/dictation session, check Windows microphone access, then retry Talk. If this repeats, the native local audio bridge may be unavailable.",
                    "Antigravity لم يتمكن من فتح اتصال الصوت. أوقف أي جلسة صوت أو إملاء أخرى، وتحقق من إتاحة الميكروفون في Windows، ثم جرّب Talk مجددًا. إذا تكرر الخطأ فقد يكون جسر الصوت المحلي غير متاح."));
                return;
            }
            foreach (string marker in new[] { "Usage limit reached", "Failed to start voice", "Failed to start live", "couldn't start voice", "failed to connect", "needs your workstation", "microphone blocked", "microphone access denied", "permission denied" })
                if (current.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    Fail(Loc.T("Antigravity voice could not start: ", "تعذّر بدء صوت Antigravity: ") + marker + Loc.T(". Check account voice availability and microphone permissions.", ". تحقق من إتاحة الصوت للحساب وصلاحية الميكروفون."));
                    break;
                }
        }
    }
    internal static bool HasModelStatusLine(string text)
    {
        const string marker = "? for shortcuts";
        int start = 0;
        while (start < text.Length)
        {
            int end = text.IndexOfAny(['\r', '\n'], start);
            if (end < 0) return false; // A terminal read can split the model name across chunks.
            string line = text[start..end];
            int index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && line[(index + marker.Length)..].Any(char.IsLetterOrDigit)) return true;
            start = end + 1;
        }
        return false;
    }

    private void Fail(string message)
    {
        var error = new InvalidOperationException(message);
        // Ready/Started are success-only signals: errors use Ended, avoiding unobserved faulted tasks.
        ended.TrySetResult(error);
    }

    public static async Task WaitAsync(Task target, HiddenConsoleProcess console, Process process, Task<Exception?> failure, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            Task exited = process.WaitForExitAsync(deadline.Token);
            Task winner = await Task.WhenAny(target, failure, exited, console.OutputCompleted, Task.Delay(Timeout.Infinite, deadline.Token)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (winner == target) { await target.ConfigureAwait(false); return; }
            if (winner == failure) throw await failure.ConfigureAwait(false) ?? new InvalidOperationException(Loc.T("Antigravity ended voice before it connected.", "انتهى صوت Antigravity قبل اكتمال الاتصال."));
            if (deadline.IsCancellationRequested) throw new TimeoutException(Loc.T("Antigravity voice did not become ready. Check the connected account and try Talk again.", "صوت Antigravity لم يجهز في المهلة المحددة. تحقق من الحساب المتصل وجرّب Talk مرة أخرى."));
            if (winner == console.OutputCompleted) await console.OutputCompleted.ConfigureAwait(false);
            throw new InvalidOperationException(Loc.T("The internal Antigravity voice connection closed before it was ready.", "أُغلق اتصال صوت Antigravity الداخلي قبل اكتماله."));
        }
        finally { deadline.Cancel(); }

    }
}
