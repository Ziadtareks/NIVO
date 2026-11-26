using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio;
using NAudio.Wave;
using Nivo.Core;

namespace Nivo.Services;

internal readonly record struct RealtimeAudioChunk(byte[] PcmBytes, int SampleRate, int Channels);

internal interface IRealtimeAudioTransport : IDisposable
{
    void StartCapture(Action<RealtimeAudioChunk> onChunk, Action<Exception> onCaptureFault);
    void StopCapture();
    void WriteOutput(RealtimeAudioChunk chunk, Action<Exception> onOutputFault);
    void ClearOutput();
    bool HasBufferedOutput { get; }
    void StopOutput();
}

public sealed partial class CodexAccountService
{
    private static readonly TimeSpan RealtimeStartupTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan RealtimeStopRpcTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RealtimeAppendAudioTimeout = TimeSpan.FromSeconds(10);

    public Task<CodexRealtimeSession> StartRealtimeAsync(
        string model,
        string system,
        Action<string, string>? onTranscript,
        Action<string>? onStatus,
        CancellationToken ct) =>
        StartRealtimeAsync(model, system, onTranscript, onStatus, audioTransportFactory: null, ct);

    internal async Task<CodexRealtimeSession> StartRealtimeAsync(
        string model,
        string system,
        Action<string, string>? onTranscript,
        Action<string>? onStatus,
        Func<IRealtimeAudioTransport>? audioTransportFactory,
        CancellationToken ct)
    {
        string bareModel = NormalizeBareModelId(model);
        ct.ThrowIfCancellationRequested();

        using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startupCts.CancelAfter(RealtimeStartupTimeout);
        var startupToken = startupCts.Token;

        SafeNotifyStatus(onStatus, Loc.T("Connecting account voice…", "جاري الاتصال بصوت الحساب…"));

        var rpc = await StartSessionAsync(experimentalApi: true, startupToken).ConfigureAwait(false);
        CodexRealtimeSession? session = null;
        try
        {
            // Authenticate first against the isolated ChatGPT account before creating threads or touching audio.
            var account = await ReadAccountCoreAsync(rpc, startupToken).ConfigureAwait(false);
            EnsureChatGptAccount(account);

            var threadStartParams = new Dictionary<string, object?>
            {
                ["model"] = bareModel,
                ["cwd"] = WorkspaceDirectory,
                ["approvalPolicy"] = "on-request",
                ["approvalsReviewer"] = "user",
                ["sandbox"] = "read-only",
                ["ephemeral"] = true,
                ["developerInstructions"] = BuildRealtimeDeveloperInstructions(system),
                ["config"] = BuildThreadConfigOverrides()
            };

            using var threadDoc = await RequestRealtimeRpcAsync(rpc, "thread/start", threadStartParams, DefaultRpcTimeout, startupToken).ConfigureAwait(false);
            string? threadId = null;
            if (threadDoc.RootElement.TryGetProperty("thread", out var threadObj) && threadObj.ValueKind == JsonValueKind.Object)
                threadId = TryGetString(threadObj, "id");

            if (string.IsNullOrWhiteSpace(threadId))
                throw new InvalidDataException(Loc.T("Codex did not return a thread identifier.", "لم يُرجع Codex معرّف المحادثة."));

            // Subscribe to realtime notifications BEFORE thread/realtime/start so immediate
            // started/error/closed notifications right after the RPC reply are never lost.
            session = new CodexRealtimeSession(rpc, threadId, onTranscript, onStatus);

            var realtimeStartParams = BuildRealtimeStartParams(threadId, system);
            using var _ = await RequestRealtimeRpcAsync(rpc, "thread/realtime/start", realtimeStartParams, DefaultRpcTimeout, startupToken).ConfigureAwait(false);

            // thread/realtime/start acknowledges the request asynchronously; wait for
            // thread/realtime/started before opening any microphone device.
            await session.WaitForStartedAsync(startupToken, ct).ConfigureAwait(false);

            var transport = (audioTransportFactory ?? (() => new NAudioRealtimeAudioTransport()))();
            try
            {
                session.ActivateAudio(transport, ct);
            }
            catch
            {
                try { transport.Dispose(); }
                catch (Exception ex) { Trace.TraceInformation("NIVO Codex realtime audio cleanup skipped ({0}).", ex.GetType().Name); }
                throw;
            }

            return session;
        }
        catch (Exception startupError)
        {
            if (session != null)
            {
                try { await session.StopAsync().ConfigureAwait(false); }
                catch (Exception ex) { Trace.TraceInformation("NIVO Codex realtime startup rollback skipped ({0}).", ex.GetType().Name); }
            }
            else
            {
                try { await rpc.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { Trace.TraceInformation("NIVO Codex RPC startup rollback skipped ({0}).", ex.GetType().Name); }
            }
            if (startupError is OperationCanceledException && !ct.IsCancellationRequested)
                throw new TimeoutException(Loc.T("Codex voice connection timed out.", "انتهت مهلة اتصال صوت Codex."), startupError);
            throw;
        }
    }

    internal static Dictionary<string, object?> BuildRealtimeStartParams(string threadId, string? system)
    {
        // Keep the user's selected text model on thread/start for backing Codex reasoning,
        // and omit realtime model/voice/version so the provider uses its native voice defaults.
        var parameters = new Dictionary<string, object?>
        {
            ["threadId"] = threadId,
            ["outputModality"] = "audio",
            ["transport"] = new { type = "websocket" },
            ["includeStartupContext"] = false,
            ["realtimeStartInstructions"] = BuildRealtimeStartInstructions()
        };

        string prompt = BuildRealtimePrompt(system);
        if (prompt.Length > 0)
            parameters["prompt"] = prompt;

        return parameters;
    }

    internal static string FormatRealtimeFailure(string? raw)
    {
        string detail = SanitizeDiagnostic(raw);
        if (string.IsNullOrWhiteSpace(detail))
            return Loc.T("Codex realtime voice session failed.", "فشلت جلسة صوت Codex الفورية.");

        string lower = detail.ToLowerInvariant();
        if (lower.Contains("quota") ||
            lower.Contains("rate limit") ||
            lower.Contains("usage limit") ||
            lower.Contains("limit exceeded") ||
            lower.Contains("429") ||
            lower.Contains("insufficient") ||
            lower.Contains("billing"))
        {
            return Loc.T(
                "Codex account quota or rate limit rejected realtime voice: ",
                "رفضت حصة حساب Codex أو حد الاستخدام الصوت الفوري: ") + detail;
        }

        if (lower.Contains("experimental") ||
            lower.Contains("method not found") ||
            lower.Contains("unknown method") ||
            lower.Contains("not implemented") ||
            lower.Contains("unsupported"))
        {
            return Loc.T(
                "Experimental Codex realtime voice is not supported by this Codex CLI or account: ",
                "صوت Codex الفوري التجريبي غير مدعوم في هذا الإصدار من Codex CLI أو الحساب: ") + detail;
        }

        if (lower.Contains("api key") ||
            lower.Contains("unauthorized") ||
            lower.Contains("forbidden") ||
            lower.Contains("401") ||
            lower.Contains("403") ||
            lower.Contains("not logged in") ||
            lower.Contains("authentication") ||
            lower.Contains("plan") ||
            lower.Contains("entitlement") ||
            lower.Contains("not enabled") ||
            lower.Contains("not eligible") ||
            lower.Contains("not available") ||
            lower.Contains("unavailable") ||
            lower.Contains("feature"))
        {
            return Loc.T(
                "Codex account rejected experimental realtime voice (no paid API fallback will be used): ",
                "رفض حساب Codex الصوت الفوري التجريبي (لن يتم التحويل إلى API مدفوع): ") + detail;
        }

        return Loc.T("Codex realtime voice error: ", "خطأ في صوت Codex الفوري: ") + detail;
    }

    internal static string SanitizeRealtimeDiagnostic(string? raw) => SanitizeDiagnostic(raw);

    internal static string? TryGetRealtimeString(JsonElement obj, string propertyName) => TryGetString(obj, propertyName);

    internal static TimeSpan RealtimeStopTimeout => RealtimeStopRpcTimeout;

    internal static TimeSpan RealtimeAppendTimeout => RealtimeAppendAudioTimeout;

    private static async Task<JsonDocument> RequestRealtimeRpcAsync(
        JsonRpcSession rpc,
        string method,
        object parameters,
        TimeSpan timeout,
        CancellationToken ct)
    {
        try
        {
            return await rpc.RequestAsync(method, parameters, timeout, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(FormatRealtimeFailure(ex.Message), ex);
        }
    }

    private static string BuildRealtimeDeveloperInstructions(string? system)
    {
        const string voiceNoToolsGuard =
            "All Codex built-in tools, shell commands, file access, computer control, browser control, web search, MCP servers, plugins, and sub-agents are disabled for this live voice session. Never attempt tool calls or claim to execute computer actions; tell the user to run actions through NIVO's normal reviewed task flow.";
        if (string.IsNullOrWhiteSpace(system)) return voiceNoToolsGuard;
        return system.Trim() + "\n\n" + voiceNoToolsGuard;
    }

    private static string BuildRealtimePrompt(string? system)
    {
        const string voiceGuard =
            "This is a live voice conversation. Speak naturally and concisely. No tools or computer actions are enabled in this voice session; direct any computer or file action request to NIVO's reviewed task flow.";
        if (string.IsNullOrWhiteSpace(system)) return voiceGuard;
        return system.Trim() + "\n\n" + voiceGuard;
    }

    private static string BuildRealtimeStartInstructions() =>
        "Live realtime voice session started. All tools and approvals remain disabled; respond with conversation only and direct requested actions to NIVO's normal reviewed task flow.";

    private static void SafeNotifyStatus(Action<string>? onStatus, string status)
    {
        if (onStatus == null || string.IsNullOrWhiteSpace(status)) return;
        try { onStatus(status); }
        catch (Exception ex)
        {
            Trace.TraceWarning("NIVO Codex realtime status callback failed ({0}).", ex.GetType().Name);
        }
    }
}

public sealed class CodexRealtimeSession : IAccountTalkSession
{
    public bool UsesExternalWindow => false;
    private const int MicQueueCapacity = 32;
    private const int PlaybackQueueCapacity = 128;
    private const int MaxAudioChunkBase64Chars = 2_097_152;
    private const int MaxAudioChunkDecodedBytes = 1_572_864;
    private const int MinSampleRate = 8_000;
    private const int MaxSampleRate = 96_000;
    private const int MaxChannels = 2;
    private const int MaxTranscriptChars = 20_000;
    private const int MaxTrackedItems = 256;
    private static readonly long TranscriptDedupeWindowTicks = TimeSpan.FromSeconds(8).Ticks;

    private enum VoiceState { Connecting, Listening, Speaking }
    private readonly record struct RealtimeStartedInfo(string? RealtimeSessionId, string Version);

    private readonly CodexAccountService.JsonRpcSession rpc;
    private readonly string threadId;
    private readonly Action<string, string>? onTranscript;
    private readonly Action<string>? onStatus;
    private readonly Channel<RealtimeAudioChunk> micChannel;
    private readonly Channel<RealtimeAudioChunk> playbackChannel;
    private readonly TaskCompletionSource<RealtimeStartedInfo> startedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource completionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource loopCts = new();
    private readonly object stateGate = new();
    private readonly object transcriptGate = new();
    private readonly Dictionary<string, string> itemRoles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StringBuilder> pendingRoleDeltas = new(StringComparer.Ordinal);

    private CodexAccountService.JsonRpcSession.NotificationSubscription? subscription;
    private IRealtimeAudioTransport? audioTransport;
    private CancellationTokenRegistration lifetimeRegistration;
    private Task? micSenderTask;
    private Task? playbackTask;
    private Task? stopTask;
    private VoiceState currentState = VoiceState.Connecting;
    private string? lastErrorMessage;
    private string? lastDoneUserText;
    private long lastDoneUserTicks;
    private string? lastDoneAssistantText;
    private long lastDoneAssistantTicks;
    private int active;
    private int localStopRequested;
    private int realtimeStarted;

    internal CodexRealtimeSession(
        CodexAccountService.JsonRpcSession rpc,
        string threadId,
        Action<string, string>? onTranscript,
        Action<string>? onStatus)
    {
        this.rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
        this.threadId = threadId ?? throw new ArgumentNullException(nameof(threadId));
        this.onTranscript = onTranscript;
        this.onStatus = onStatus;

        micChannel = Channel.CreateBounded<RealtimeAudioChunk>(new BoundedChannelOptions(MicQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        playbackChannel = Channel.CreateBounded<RealtimeAudioChunk>(new BoundedChannelOptions(PlaybackQueueCapacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        subscription = this.rpc.SubscribeNotifications(OnNotification, OnTransportFault);
    }

    public Task Completion => completionTcs.Task;

    public bool IsActive => Volatile.Read(ref active) == 1 && !completionTcs.Task.IsCompleted;

    public string ThreadId => threadId;

    public string? RealtimeSessionId { get; private set; }

    public string ProtocolVersion { get; private set; } = "";

    public Task StopAsync()
    {
        Volatile.Write(ref localStopRequested, 1);
        return BeginShutdownAsync(fault: null, localStop: true, sendStopRpc: true);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    internal async Task WaitForStartedAsync(CancellationToken startupToken, CancellationToken callerToken)
    {
        using var reg = startupToken.Register(() =>
        {
            if (callerToken.IsCancellationRequested)
                startedTcs.TrySetCanceled(callerToken);
            else
                startedTcs.TrySetException(new TimeoutException(Loc.T(
                    "Timed out waiting for Codex realtime voice to connect.",
                    "انتهت مهلة انتظار اتصال صوت Codex الفوري.")));
        });

        var info = await startedTcs.Task.ConfigureAwait(false);
        RealtimeSessionId = info.RealtimeSessionId;
        ProtocolVersion = info.Version;
    }

    internal void ActivateAudio(IRealtimeAudioTransport transport, CancellationToken lifetimeToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        lock (stateGate)
        {
            lifetimeToken.ThrowIfCancellationRequested();
            if (stopTask != null)
                throw new InvalidOperationException(CodexAccountService.FormatRealtimeFailure(lastErrorMessage ?? "Realtime voice session closed before audio started."));
            audioTransport = transport;
            Volatile.Write(ref active, 1);
            // Shutdown runs on a worker and takes this gate before snapshotting resources.
            // It cannot dispose audio/loops/registration halfway through activation.
            transport.StartCapture(OnMicrophoneChunk, OnAudioDeviceFault);
            playbackTask = Task.Run(RunPlaybackLoopAsync);
            micSenderTask = Task.Run(RunMicSenderLoopAsync);
            lifetimeRegistration = lifetimeToken.Register(() =>
            {
                Volatile.Write(ref localStopRequested, 1);
                _ = BeginShutdownAsync(fault: null, localStop: true, sendStopRpc: true);
            });
            if (stopTask != null)
            {
                lifetimeToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException(CodexAccountService.FormatRealtimeFailure(lastErrorMessage ?? "Voice session stopped during audio activation."));
            }
        }
        TransitionStatus(VoiceState.Listening);
    }

    internal static bool TryParseAudioChunk(JsonElement audioObj, out RealtimeAudioChunk chunk)
    {
        chunk = default;
        if (audioObj.ValueKind != JsonValueKind.Object) return false;

        string? data = CodexAccountService.TryGetRealtimeString(audioObj, "data");
        if (string.IsNullOrWhiteSpace(data) || data.Length > MaxAudioChunkBase64Chars)
            return false;

        if (!audioObj.TryGetProperty("sampleRate", out var srProp) || srProp.ValueKind != JsonValueKind.Number || !srProp.TryGetInt32(out int sampleRate))
            return false;
        if (sampleRate < MinSampleRate || sampleRate > MaxSampleRate)
            return false;

        if (!audioObj.TryGetProperty("numChannels", out var chProp) || chProp.ValueKind != JsonValueKind.Number || !chProp.TryGetInt32(out int channels))
            return false;
        if (channels < 1 || channels > MaxChannels)
            return false;

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(data);
        }
        catch (FormatException)
        {
            return false;
        }

        int blockAlign = channels * 2;
        int validLength = decoded.Length - (decoded.Length % blockAlign);
        if (validLength <= 0 || validLength > MaxAudioChunkDecodedBytes)
            return false;

        if (validLength != decoded.Length)
            Array.Resize(ref decoded, validLength);

        chunk = new RealtimeAudioChunk(decoded, sampleRate, channels);
        return true;
    }

    internal static string? NormalizeTranscriptRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return null;
        string trimmed = role.Trim();
        if (string.Equals(trimmed, "user", StringComparison.OrdinalIgnoreCase)) return "user";
        if (string.Equals(trimmed, "assistant", StringComparison.OrdinalIgnoreCase)) return "assistant";
        return null;
    }

    internal static bool IsUnexpectedCloseReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return false;
        string trimmed = reason.Trim();
        return !trimmed.Equals("requested", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Equals("stopped", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Equals("ended", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Equals("closed", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Equals("normal", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Equals("client stop", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Equals("client_stop", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Equals("client_closed", StringComparison.OrdinalIgnoreCase);
    }

    private void OnMicrophoneChunk(RealtimeAudioChunk chunk)
    {
        if (Volatile.Read(ref active) != 1 || chunk.PcmBytes.Length == 0) return;
        micChannel.Writer.TryWrite(chunk);
    }

    private void OnAudioDeviceFault(Exception error)
    {
        if (Volatile.Read(ref localStopRequested) != 0) return;
        var wrapped = error is InvalidOperationException
            ? error
            : WrapAudioException(Loc.T("Audio device error during live voice session.", "حدث خطأ في جهاز الصوت أثناء المحادثة الصوتية."), error);
        _ = Task.Run(() => BeginShutdownAsync(fault: wrapped, localStop: false, sendStopRpc: true));
    }

    private void OnNotification(string method, JsonElement p)
    {
        if (!method.StartsWith("thread/realtime/", StringComparison.Ordinal))
            return;

        string? eventThreadId = CodexAccountService.TryGetRealtimeString(p, "threadId");
        if (!string.IsNullOrEmpty(eventThreadId) && !string.Equals(eventThreadId, threadId, StringComparison.Ordinal))
            return;

        switch (method)
        {
            case "thread/realtime/started":
            {
                string version = CodexAccountService.TryGetRealtimeString(p, "version") ?? "";
                string? sessionId = CodexAccountService.TryGetRealtimeString(p, "realtimeSessionId");
                RealtimeSessionId = sessionId;
                ProtocolVersion = version;
                Volatile.Write(ref realtimeStarted, 1);
                startedTcs.TrySetResult(new RealtimeStartedInfo(sessionId, version));
                break;
            }

            case "thread/realtime/outputAudio/delta":
            {
                if (p.TryGetProperty("audio", out var audioObj) && TryParseAudioChunk(audioObj, out var chunk))
                {
                    playbackChannel.Writer.TryWrite(chunk);
                    TransitionStatus(VoiceState.Speaking);
                }
                break;
            }

            case "thread/realtime/transcript/delta":
            {
                string? role = NormalizeTranscriptRole(CodexAccountService.TryGetRealtimeString(p, "role"));
                string? delta = CodexAccountService.TryGetRealtimeString(p, "delta");
                if (role != null && !string.IsNullOrEmpty(delta))
                {
                    AppendPendingDelta(role, delta);
                    if (role == "user")
                        TransitionStatus(VoiceState.Listening);
                    else if (role == "assistant")
                        TransitionStatus(VoiceState.Speaking);
                }
                break;
            }

            case "thread/realtime/transcript/done":
            {
                string? role = NormalizeTranscriptRole(CodexAccountService.TryGetRealtimeString(p, "role"));
                string? text = CodexAccountService.TryGetRealtimeString(p, "text");
                if (role != null)
                {
                    ClearPendingDelta(role);
                    EmitFinalTranscript(role, text, fromTranscriptDone: true);
                    if (role == "assistant" && !(audioTransport?.HasBufferedOutput ?? false))
                        TransitionStatus(VoiceState.Listening);
                }
                break;
            }

            case "thread/realtime/item/started":
            {
                if (p.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
                {
                    string? itemType = CodexAccountService.TryGetRealtimeString(item, "type");
                    if (string.Equals(itemType, "transcriptSegment", StringComparison.Ordinal))
                    {
                        string? itemId = CodexAccountService.TryGetRealtimeString(item, "id");
                        string? role = NormalizeTranscriptRole(CodexAccountService.TryGetRealtimeString(item, "role"));
                        if (!string.IsNullOrEmpty(itemId) && role != null)
                        {
                            lock (transcriptGate)
                            {
                                if (itemRoles.Count >= MaxTrackedItems) itemRoles.Clear();
                                itemRoles[itemId] = role;
                            }
                        }
                    }
                }
                break;
            }

            case "thread/realtime/item/transcript/delta":
            {
                string? itemId = CodexAccountService.TryGetRealtimeString(p, "itemId");
                string? role = null;
                if (!string.IsNullOrEmpty(itemId))
                {
                    lock (transcriptGate)
                        itemRoles.TryGetValue(itemId, out role);
                }
                if (role == "assistant")
                    TransitionStatus(VoiceState.Speaking);
                else if (role == "user")
                    TransitionStatus(VoiceState.Listening);
                break;
            }

            case "thread/realtime/item/completed":
            {
                if (p.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
                {
                    string? itemType = CodexAccountService.TryGetRealtimeString(item, "type");
                    if (string.Equals(itemType, "transcriptSegment", StringComparison.Ordinal))
                    {
                        string? itemId = CodexAccountService.TryGetRealtimeString(item, "id");
                        string? role = NormalizeTranscriptRole(CodexAccountService.TryGetRealtimeString(item, "role"));
                        string? text = CodexAccountService.TryGetRealtimeString(item, "text");
                        if (!string.IsNullOrEmpty(itemId))
                        {
                            lock (transcriptGate) itemRoles.Remove(itemId);
                        }
                        if (role != null)
                        {
                            ClearPendingDelta(role);
                            EmitFinalTranscript(role, text, fromTranscriptDone: false);
                        }
                    }
                }
                break;
            }

            case "thread/realtime/itemAdded":
            {
                if (p.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
                {
                    string? itemType = CodexAccountService.TryGetRealtimeString(item, "type");
                    if (string.Equals(itemType, "input_audio_buffer.speech_started", StringComparison.Ordinal) ||
                        string.Equals(itemType, "response.cancelled", StringComparison.Ordinal) ||
                        string.Equals(itemType, "conversation.item.truncated", StringComparison.Ordinal))
                    {
                        ClearPendingPlayback();
                        if (string.Equals(itemType, "input_audio_buffer.speech_started", StringComparison.Ordinal))
                            TransitionStatus(VoiceState.Listening);
                    }
                }
                break;
            }

            case "thread/realtime/error":
            {
                string? message = CodexAccountService.TryGetRealtimeString(p, "message");
                lock (stateGate) lastErrorMessage = message;
                var error = new InvalidOperationException(CodexAccountService.FormatRealtimeFailure(message));
                if (!startedTcs.Task.IsCompleted)
                    startedTcs.TrySetException(error);
                else
                    _ = BeginShutdownAsync(fault: error, localStop: false, sendStopRpc: false);
                break;
            }

            case "thread/realtime/closed":
            {
                string? reason = CodexAccountService.TryGetRealtimeString(p, "reason");
                if (!startedTcs.Task.IsCompleted)
                {
                    string detail;
                    lock (stateGate) detail = !string.IsNullOrWhiteSpace(lastErrorMessage) ? lastErrorMessage! : (reason ?? "Codex realtime voice closed before connecting.");
                    startedTcs.TrySetException(new InvalidOperationException(CodexAccountService.FormatRealtimeFailure(detail)));
                }
                else
                {
                    Exception? closeFault = null;
                    if (Volatile.Read(ref localStopRequested) == 0)
                    {
                        string? priorError;
                        lock (stateGate) priorError = lastErrorMessage;
                        if (!string.IsNullOrWhiteSpace(priorError))
                            closeFault = new InvalidOperationException(CodexAccountService.FormatRealtimeFailure(priorError));
                        else if (IsUnexpectedCloseReason(reason))
                            closeFault = new InvalidOperationException(CodexAccountService.FormatRealtimeFailure(reason));
                    }
                    _ = BeginShutdownAsync(fault: closeFault, localStop: false, sendStopRpc: false);
                }
                break;
            }
        }
    }

    private void OnTransportFault(Exception error)
    {
        var formatted = FormatTransportFault(error);
        if (!startedTcs.Task.IsCompleted)
        {
            startedTcs.TrySetException(formatted);
            return;
        }

        if (Volatile.Read(ref localStopRequested) == 0)
            _ = BeginShutdownAsync(fault: formatted, localStop: false, sendStopRpc: false);
    }

    private async Task RunMicSenderLoopAsync()
    {
        try
        {
            var token = loopCts.Token;
            await foreach (var chunk in micChannel.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                if (Volatile.Read(ref active) != 1 || token.IsCancellationRequested) break;
                if (chunk.PcmBytes.Length == 0 || chunk.Channels <= 0) continue;

                int samplesPerChannel = chunk.PcmBytes.Length / (chunk.Channels * 2);
                if (samplesPerChannel <= 0) continue;

                var payload = new Dictionary<string, object?>
                {
                    ["threadId"] = threadId,
                    ["audio"] = new Dictionary<string, object?>
                    {
                        ["data"] = Convert.ToBase64String(chunk.PcmBytes),
                        ["sampleRate"] = chunk.SampleRate,
                        ["numChannels"] = chunk.Channels,
                        ["samplesPerChannel"] = samplesPerChannel
                    }
                };

                using var _ = await rpc.RequestAsync("thread/realtime/appendAudio", payload, CodexAccountService.RealtimeAppendTimeout, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or JsonException or TimeoutException or ObjectDisposedException)
        {
            if (Volatile.Read(ref localStopRequested) == 0)
            {
                var fault = ex is InvalidOperationException
                    ? new InvalidOperationException(CodexAccountService.FormatRealtimeFailure(ex.Message), ex)
                    : FormatTransportFault(ex);
                _ = BeginShutdownAsync(fault, localStop: false, sendStopRpc: false);
            }
        }
    }

    private async Task RunPlaybackLoopAsync()
    {
        try
        {
            var token = loopCts.Token;
            Task<bool>? pendingRead = null;
            while (!token.IsCancellationRequested)
            {
                pendingRead ??= playbackChannel.Reader.WaitToReadAsync(token).AsTask();
                bool hasItem;
                if (IsSpeakingState())
                {
                    try
                    {
                        hasItem = await pendingRead.WaitAsync(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
                        pendingRead = null;
                    }
                    catch (TimeoutException)
                    {
                        var currentTransport = audioTransport;
                        if (currentTransport == null || !currentTransport.HasBufferedOutput)
                            TransitionStatus(VoiceState.Listening);
                        continue;
                    }
                }
                else
                {
                    hasItem = await pendingRead.ConfigureAwait(false);
                    pendingRead = null;
                }

                if (!hasItem) break;

                while (playbackChannel.Reader.TryRead(out var chunk))
                {
                    if (Volatile.Read(ref active) != 1 || token.IsCancellationRequested) return;
                    var transport = audioTransport;
                    if (transport == null) return;
                    transport.WriteOutput(chunk, OnAudioDeviceFault);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref localStopRequested) == 0)
            {
                var fault = ex is InvalidOperationException
                    ? ex
                    : WrapAudioException(Loc.T("Audio playback failed during live voice session.", "فشل إخراج الصوت أثناء المحادثة الصوتية."), ex);
                _ = BeginShutdownAsync(fault, localStop: false, sendStopRpc: true);
            }
        }
    }

    private bool IsSpeakingState()
    {
        lock (stateGate)
            return Volatile.Read(ref active) == 1 && currentState == VoiceState.Speaking;
    }

    private void ClearPendingPlayback()
    {
        while (playbackChannel.Reader.TryRead(out _)) { }
        try { audioTransport?.ClearOutput(); }
        catch (Exception ex) { Trace.TraceInformation("NIVO Codex realtime clear playback skipped ({0}).", ex.GetType().Name); }
    }

    private void AppendPendingDelta(string role, string delta)
    {
        lock (transcriptGate)
        {
            if (!pendingRoleDeltas.TryGetValue(role, out var sb))
            {
                sb = new StringBuilder();
                pendingRoleDeltas[role] = sb;
            }
            int remaining = MaxTranscriptChars - sb.Length;
            if (remaining > 0)
                sb.Append(delta.Length <= remaining ? delta : delta[..remaining]);
        }
    }

    private void ClearPendingDelta(string role)
    {
        lock (transcriptGate)
        {
            if (pendingRoleDeltas.TryGetValue(role, out var sb))
                sb.Clear();
        }
    }

    private void EmitFinalTranscript(string role, string? rawText, bool fromTranscriptDone)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return;
        string text = rawText.Trim();
        if (text.Length > MaxTranscriptChars) text = text[..MaxTranscriptChars];

        long now = Stopwatch.GetTimestamp();
        lock (transcriptGate)
        {
            if (fromTranscriptDone)
            {
                if (role == "user") { lastDoneUserText = text; lastDoneUserTicks = now; }
                else { lastDoneAssistantText = text; lastDoneAssistantTicks = now; }
            }
            else
            {
                // Avoid duplicate bubbles when both thread/realtime/transcript/done and
                // thread/realtime/item/completed (transcriptSegment) publish the same utterance.
                string? priorText = role == "user" ? lastDoneUserText : lastDoneAssistantText;
                long priorTicks = role == "user" ? lastDoneUserTicks : lastDoneAssistantTicks;
                if (priorText != null &&
                    string.Equals(priorText, text, StringComparison.Ordinal) &&
                    TicksToTimeSpan(now - priorTicks).Ticks <= TranscriptDedupeWindowTicks)
                {
                    return;
                }
            }
        }

        if (onTranscript == null) return;
        try { onTranscript(role, text); }
        catch (Exception ex)
        {
            Trace.TraceWarning("NIVO Codex realtime transcript callback failed ({0}).", ex.GetType().Name);
        }
    }

    private void TransitionStatus(VoiceState next)
    {
        lock (stateGate)
        {
            if (Volatile.Read(ref active) != 1 || currentState == next) return;
            currentState = next;
        }

        string statusText = next switch
        {
            VoiceState.Speaking => Loc.T("Speaking…", "يتحدث…"),
            VoiceState.Listening => Loc.T("Listening · tap Talk again to end", "جاري الاستماع · اضغط Talk مجددًا للإنهاء"),
            _ => Loc.T("Connecting account voice…", "جاري الاتصال بصوت الحساب…")
        };

        if (onStatus == null) return;
        try { onStatus(statusText); }
        catch (Exception ex)
        {
            Trace.TraceWarning("NIVO Codex realtime status callback failed ({0}).", ex.GetType().Name);
        }
    }

    private Task BeginShutdownAsync(Exception? fault, bool localStop, bool sendStopRpc)
    {
        lock (stateGate)
        {
            Volatile.Write(ref active, 0);
            // Never dispose NAudio devices on their own stopped-event callback thread.
            return stopTask ??= Task.Run(() => RunShutdownAsync(fault, localStop, sendStopRpc));
        }
    }

    private async Task RunShutdownAsync(Exception? fault, bool localStop, bool sendStopRpc)
    {
        IRealtimeAudioTransport? transport;
        CancellationTokenRegistration registration;
        Task? micTask, outputTask;
        lock (stateGate)
        {
            transport = audioTransport;
            registration = lifetimeRegistration;
            lifetimeRegistration = default;
            micTask = micSenderTask;
            outputTask = playbackTask;
        }
        Exception? cleanupFailure = null;
        // Cleanup errors must not skip the remaining resources. They are reported
        // through Completion, including failures while stopping at the user's request.
        void Attempt(Action action)
        {
            try { action(); }
            catch (Exception ex) { cleanupFailure ??= ex; Trace.TraceWarning("NIVO voice cleanup failed ({0}).", ex.GetType().Name); }
        }
        async Task AttemptAsync(Func<Task> action)
        {
            try { await action().ConfigureAwait(false); }
            catch (Exception ex) { cleanupFailure ??= ex; Trace.TraceWarning("NIVO voice async cleanup failed ({0}).", ex.GetType().Name); }
        }
        try
        {
            micChannel.Writer.TryComplete();
            playbackChannel.Writer.TryComplete();
            Attempt(() => loopCts.Cancel());
            Attempt(() => registration.Dispose());
            Attempt(() => transport?.StopCapture());
            Attempt(() => transport?.StopOutput());
            await AttemptAsync(() => DrainLoopTaskAsync(micTask)).ConfigureAwait(false);
            await AttemptAsync(() => DrainLoopTaskAsync(outputTask)).ConfigureAwait(false);
            if (sendStopRpc && Volatile.Read(ref realtimeStarted) == 1)
                await AttemptAsync(TryStopRealtimeOnServerAsync).ConfigureAwait(false);
        }
        finally
        {
            Attempt(() => transport?.Dispose());
            Attempt(() => Interlocked.Exchange(ref subscription, null)?.Dispose());
            await AttemptAsync(() => rpc.DisposeAsync().AsTask()).ConfigureAwait(false);
            Attempt(() => loopCts.Dispose());
            Exception? finalError = cleanupFailure ?? (!localStop && Volatile.Read(ref localStopRequested) == 0 ? fault : null);
            if (!startedTcs.Task.IsCompleted)
            {
                if (finalError != null) startedTcs.TrySetException(finalError);
                else startedTcs.TrySetCanceled();
            }
            if (finalError != null) completionTcs.TrySetException(FormatTransportFault(finalError));
            else completionTcs.TrySetResult();
        }
    }

    private async Task TryStopRealtimeOnServerAsync()
    {
        try
        {
            using var stopCts = new CancellationTokenSource(CodexAccountService.RealtimeStopTimeout);
            using var _ = await rpc.RequestAsync("thread/realtime/stop", new { threadId }, CodexAccountService.RealtimeStopTimeout, stopCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or InvalidOperationException or IOException or ObjectDisposedException)
        {
            Trace.TraceInformation("NIVO Codex realtime stop RPC skipped ({0}).", ex.GetType().Name);
        }
    }

    private static async Task DrainLoopTaskAsync(Task? task)
    {
        if (task == null) return;
        try { await task.WaitAsync(TimeSpan.FromMilliseconds(1500)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or InvalidDataException or JsonException or InvalidOperationException or ObjectDisposedException)
        {
            Trace.TraceInformation("NIVO Codex realtime worker drain ended ({0}).", ex.GetType().Name);
        }
    }

    private static Exception FormatTransportFault(Exception error)
    {
        string detail = CodexAccountService.SanitizeRealtimeDiagnostic(error.Message);
        if (string.IsNullOrWhiteSpace(detail))
            return new InvalidOperationException(Loc.T("Codex realtime voice connection closed unexpectedly.", "انقطع اتصال صوت Codex الفوري بشكل غير متوقع."), error);
        return new InvalidOperationException(CodexAccountService.FormatRealtimeFailure(detail), error);
    }

    internal static InvalidOperationException WrapAudioException(string localizedPrefix, Exception inner)
    {
        string detail = CodexAccountService.SanitizeRealtimeDiagnostic(inner.Message);
        string message = string.IsNullOrWhiteSpace(detail) ? localizedPrefix : localizedPrefix + " " + detail;
        return new InvalidOperationException(message, inner);
    }

    private static TimeSpan TicksToTimeSpan(long stopwatchDelta) =>
        TimeSpan.FromSeconds(stopwatchDelta / (double)Stopwatch.Frequency);
}

internal sealed class NAudioRealtimeAudioTransport : IRealtimeAudioTransport
{
    private const int PreferredCaptureSampleRate = 24_000;
    private const int CaptureChannels = 1;
    private const int CaptureBufferMs = 50;
    private const int PlaybackLatencyMs = 100;

    private readonly object captureGate = new();
    private readonly object outputGate = new();

    private WaveInEvent? waveIn;
    private WaveOutEvent? waveOut;
    private BufferedWaveProvider? playbackProvider;
    private int outputSampleRate;
    private int outputChannels;
    private volatile bool captureStopping;
    private volatile bool outputStopping;
    private volatile bool disposed;

    public bool HasBufferedOutput
    {
        get
        {
            lock (outputGate)
                return playbackProvider is { BufferedBytes: > 0 };
        }
    }

    public void StartCapture(Action<RealtimeAudioChunk> onChunk, Action<Exception> onCaptureFault)
    {
        ArgumentNullException.ThrowIfNull(onChunk);
        ArgumentNullException.ThrowIfNull(onCaptureFault);

        lock (captureGate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(NAudioRealtimeAudioTransport));
            if (waveIn != null) return;

            int deviceCount;
            try { deviceCount = WaveInEvent.DeviceCount; }
            catch (Exception ex) when (ex is MmException or COMException or Win32Exception)
            {
                throw CodexRealtimeSession.WrapAudioException(
                    Loc.T("Could not query Windows microphone devices.", "تعذّر فحص أجهزة الميكروفون في ويندوز."), ex);
            }

            if (deviceCount <= 0)
            {
                throw new InvalidOperationException(Loc.T(
                    "No microphone device was detected. Connect a microphone and try again.",
                    "لم يتم العثور على ميكروفون. وصّل ميكروفونًا وحاول مجددًا."));
            }

            captureStopping = false;
            Exception? firstError = null;
            foreach (int rate in new[] { PreferredCaptureSampleRate })
            {
                WaveInEvent? candidate = null;
                try
                {
                    candidate = new WaveInEvent
                    {
                        WaveFormat = new WaveFormat(rate, 16, CaptureChannels),
                        BufferMilliseconds = CaptureBufferMs,
                        NumberOfBuffers = 3
                    };
                    int captureRate = rate;
                    int blockAlign = CaptureChannels * 2;
                    candidate.DataAvailable += (_, e) =>
                    {
                        if (e.BytesRecorded <= 0) return;
                        int validBytes = e.BytesRecorded - (e.BytesRecorded % blockAlign);
                        if (validBytes <= 0) return;
                        byte[] copy = new byte[validBytes];
                        Buffer.BlockCopy(e.Buffer, 0, copy, 0, validBytes);
                        onChunk(new RealtimeAudioChunk(copy, captureRate, CaptureChannels));
                    };
                    candidate.RecordingStopped += (_, e) =>
                    {
                        if (e.Exception == null) return;
                        if (!captureStopping && !disposed) onCaptureFault(e.Exception);
                    };

                    candidate.StartRecording();
                    waveIn = candidate;
                    return;
                }
                catch (Exception ex) when (ex is MmException or InvalidOperationException or COMException or Win32Exception)
                {
                    firstError ??= ex;
                    try { candidate?.Dispose(); }
                    catch (Exception disposeEx) { Trace.TraceInformation("NIVO Codex mic candidate dispose skipped ({0}).", disposeEx.GetType().Name); }
                }
            }

            throw CodexRealtimeSession.WrapAudioException(
                Loc.T("Could not start microphone capture.", "تعذّر بدء التقاط الميكروفون."),
                firstError ?? new InvalidOperationException("Microphone initialization failed."));
        }
    }

    public void StopCapture()
    {
        WaveInEvent? current;
        lock (captureGate)
        {
            captureStopping = true;
            current = waveIn;
            waveIn = null;
        }

        if (current == null) return;
        try { current.StopRecording(); }
        catch (Exception ex) when (ex is MmException or InvalidOperationException or ObjectDisposedException)
        { Trace.TraceInformation("NIVO Codex mic stop skipped ({0}).", ex.GetType().Name); }
        try { current.Dispose(); }
        catch (Exception ex) when (ex is MmException or InvalidOperationException or ObjectDisposedException)
        { Trace.TraceInformation("NIVO Codex mic dispose skipped ({0}).", ex.GetType().Name); }
    }

    public void WriteOutput(RealtimeAudioChunk chunk, Action<Exception> onOutputFault)
    {
        if (chunk.PcmBytes.Length == 0) return;

        WaveOutEvent? obsolete = null;
        lock (outputGate)
        {
            if (disposed || outputStopping) return;
            if (outputSampleRate != chunk.SampleRate || outputChannels != chunk.Channels)
                obsolete = DetachOutputLocked();
        }
        DisposeOutput(obsolete);
        lock (outputGate)
        {
            if (disposed || outputStopping) return;

            if (waveOut == null || playbackProvider == null ||
                outputSampleRate != chunk.SampleRate || outputChannels != chunk.Channels)
            {
                WaveOutEvent? candidate = null;
                try
                {
                    var format = new WaveFormat(chunk.SampleRate, 16, chunk.Channels);
                    var provider = new BufferedWaveProvider(format)
                    {
                        DiscardOnBufferOverflow = true,
                        ReadFully = true,
                        BufferDuration = TimeSpan.FromSeconds(15)
                    };
                    candidate = new WaveOutEvent { DesiredLatency = PlaybackLatencyMs };
                    candidate.PlaybackStopped += (_, e) =>
                    {
                        if (e.Exception == null) return;
                        if (!outputStopping && !disposed) onOutputFault(e.Exception);
                    };
                    candidate.Init(provider);
                    candidate.Play();

                    waveOut = candidate;
                    playbackProvider = provider;
                    outputSampleRate = chunk.SampleRate;
                    outputChannels = chunk.Channels;
                }
                catch (Exception ex) when (ex is MmException or InvalidOperationException or COMException or Win32Exception)
                {
                    try { candidate?.Dispose(); }
                    catch (Exception disposeEx) { Trace.TraceInformation("NIVO Codex audio output cleanup skipped ({0}).", disposeEx.GetType().Name); }
                    throw CodexRealtimeSession.WrapAudioException(
                        Loc.T("Could not start audio playback.", "تعذّر تشغيل إخراج الصوت."), ex);
                }
            }

            int blockAlign = chunk.Channels * 2;
            int validBytes = chunk.PcmBytes.Length - (chunk.PcmBytes.Length % blockAlign);
            if (validBytes > 0)
                playbackProvider.AddSamples(chunk.PcmBytes, 0, validBytes);
        }
    }

    public void ClearOutput()
    {
        lock (outputGate)
            playbackProvider?.ClearBuffer();
    }

    public void StopOutput()
    {
        WaveOutEvent? current;
        lock (outputGate)
        {
            outputStopping = true;
            current = DetachOutputLocked();
        }
        DisposeOutput(current);
    }

    public void Dispose()
    {
        lock (captureGate) disposed = true;
        lock (outputGate) disposed = true;
        StopCapture();
        StopOutput();
    }

    private WaveOutEvent? DetachOutputLocked()
    {
        var current = waveOut;
        waveOut = null;
        playbackProvider = null;
        outputSampleRate = 0;
        outputChannels = 0;
        return current;
    }

    private static void DisposeOutput(WaveOutEvent? current)
    {
        if (current == null) return;
        try { current.Stop(); }
        catch (Exception ex) when (ex is MmException or InvalidOperationException or ObjectDisposedException)
        { Trace.TraceInformation("NIVO Codex audio output stop skipped ({0}).", ex.GetType().Name); }
        try { current.Dispose(); }
        catch (Exception ex) when (ex is MmException or InvalidOperationException or ObjectDisposedException)
        { Trace.TraceInformation("NIVO Codex audio output dispose skipped ({0}).", ex.GetType().Name); }
    }
}
