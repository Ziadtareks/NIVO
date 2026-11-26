using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Nivo.Core;

namespace Nivo.Services;

/// <summary>
/// Launches the official interactive Antigravity CLI (v1.5+) in an external native Windows console window
/// backed by NIVO's existing isolated file-stored profile (without copying or reading raw auth tokens)
/// and a loopback-only <c>agy mic-serve</c> bridge for <c>/voice live</c> / <c>F6</c> audio input and playback.
/// </summary>
public sealed partial class AntigravityNativeTalkSession : IAccountTalkSession
{
    private const string AppDataDirName = "antigravity-acp";
    private const string CliAppDataDirName = "antigravity-cli";
    private const string SshIsolatedConnection = "127.0.0.1 0 127.0.0.1 0";
    private const int StatusControlCExit = unchecked((int)0xC000013A);
    private const int MaxDiagnosticChars = 1200;

    private static readonly Version MinimumVoiceCliVersion = new(1, 5, 0);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan VersionCheckTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MicServeStartupCheckDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan CliStartupStabilityDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan ProcessStopTimeout = TimeSpan.FromSeconds(2);

    private static readonly string[] RemovedEnvPrefixes =
    [
        "ANTIGRAVITY_",
        "AGY_",
        "GEMINI_",
        "GOOGLE_",
        "GCLOUD_",
        "CLOUDSDK_",
        "JETSKI_"
    ];

    private static readonly HashSet<string> RemovedEnvKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "BROWSER",
        "DISPLAY",
        "WAYLAND_DISPLAY",
        "SSH_CONNECTION",
        "SSH_CLIENT",
        "SSH_TTY",
        "PYTHONUNBUFFERED",
        "ELECTRON_RUN_AS_NODE",
        "CHROME_CRASHPAD_PIPE_NAME",
        "BAICODE_ENDPOINT_URL",
        "CRM_ENDPOINT_URL",
        "CLOUD_SHELL",
        "WSL_INTEROP"
    };

    private static readonly string[] HardDenyRules =
    [
        "read_file(*)",
        "write_file(*)",
        "command(*)",
        "read_url(*)",
        "execute_url(*)",
        "download_url(*)",
        "mcp(*)"
    ];

    private readonly Process cliProcess;
    private readonly Process micServeProcess;
    private readonly VoiceSessionScope scope;
    private readonly CancellationTokenSource monitorCts = new();
    private readonly CancellationTokenSource micReaderCts;
    private readonly TaskCompletionSource completionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StringBuilder micServeDiagnostics = new();
    private readonly object micDiagLock = new();
    private readonly object shutdownGate = new();
    private readonly Task micStdoutTask;
    private readonly Task micStderrTask;
    private CancellationTokenRegistration lifetimeRegistration;
    private Task? shutdownTask;
    private Task monitorTask = Task.CompletedTask;
    private int active = 1;
    private int localStopRequested;

    private AntigravityNativeTalkSession(
        Process cliProcess,
        Process micServeProcess,
        VoiceSessionScope scope,
        string bareModel,
        int micPort,
        CancellationTokenSource micReaderCts,
        Task micStdoutTask,
        Task micStderrTask,
        StringBuilder micServeDiagnostics,
        object micDiagLock)
    {
        this.cliProcess = cliProcess;
        this.micServeProcess = micServeProcess;
        this.scope = scope;
        Model = bareModel;
        MicServePort = micPort;
        this.micReaderCts = micReaderCts;
        this.micStdoutTask = micStdoutTask;
        this.micStderrTask = micStderrTask;
        this.micServeDiagnostics = micServeDiagnostics;
        this.micDiagLock = micDiagLock;
    }

    public Task Completion => completionTcs.Task;

    public bool IsActive => Volatile.Read(ref active) == 1 && !completionTcs.Task.IsCompleted;

    public bool UsesExternalWindow => true;

    public string Model { get; }

    public int MicServePort { get; }

    public static Task<AntigravityNativeTalkSession> StartAsync(
        Store store,
        string model,
        string system,
        Action<string>? onStatus,
        CancellationToken ct) =>
        StartCoreAsync(store, model, system, onStatus, createCliWindow: true, verifyAuthCatalog: true, ct);

    internal static Task<AntigravityNativeTalkSession> StartForTestAsync(
        Store store,
        string model,
        string system,
        Action<string>? onStatus,
        bool verifyAuthCatalog,
        CancellationToken ct) =>
        StartCoreAsync(store, model, system, onStatus, createCliWindow: false, verifyAuthCatalog: verifyAuthCatalog, ct);

    private static async Task<AntigravityNativeTalkSession> StartCoreAsync(
        Store store,
        string model,
        string system,
        Action<string>? onStatus,
        bool createCliWindow,
        bool verifyAuthCatalog,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        _ = system; // Interactive `agy` CLI does not expose a public system-prompt flag; tool isolation is enforced via `--settings`.
        ct.ThrowIfCancellationRequested();

        string bareModel = NormalizeOptionalBareModel(model);
        string executable = AntigravityAccountService.FindExecutable(store.Data.Settings.AntigravityCliPath);

        SafeNotifyStatus(onStatus, Loc.T(
            "Checking Antigravity CLI voice support…",
            "جاري التحقق من دعم الصوت في Antigravity CLI…"));

        await EnsureVoiceCapableCliVersionAsync(executable, ct).ConfigureAwait(false);

        if (verifyAuthCatalog)
        {
            SafeNotifyStatus(onStatus, Loc.T(
                "Verifying connected Antigravity account…",
                "جاري التحقق من حساب Antigravity المتصل…"));

            var models = await new AntigravityAccountService(store).ModelsAsync(ct).ConfigureAwait(false);
            if (models.Count == 0)
            {
                throw new InvalidOperationException(Loc.T(
                    "Antigravity account is signed in, but returned no available models.",
                    "حساب Antigravity مسجّل الدخول، لكنه لم يُرجع موديلات متاحة."));
            }

            if (bareModel.Length > 0 && !models.Any(m => string.Equals(m.Id, bareModel, StringComparison.Ordinal)))
            {
                bareModel = (models.FirstOrDefault(m => m.IsDefault) ?? models[0]).Id;
            }
        }

        ct.ThrowIfCancellationRequested();

        string profileRoot = Path.Combine(store.Root, "accounts", "antigravity");
        string acpDirectory = Path.Combine(profileRoot, AppDataDirName);
        if (!Directory.Exists(acpDirectory))
        {
            throw new InvalidOperationException(Loc.T(
                "Antigravity is not connected in NIVO's isolated profile. Connect Antigravity in Settings first.",
                "حساب Antigravity غير متصل في ملف NIVO المعزول. اربط Antigravity من الإعدادات أولًا."));
        }

        VoiceSessionScope? scope = null;
        Process? micServeProcess = null;
        Process? cliProcess = null;
        CancellationTokenSource? micReaderCts = null;
        Task? micStdoutTask = null;
        Task? micStderrTask = null;

        try
        {
            scope = PrepareVoiceSessionScope(profileRoot, acpDirectory);
            int micPort = AllocateLoopbackPort();

            SafeNotifyStatus(onStatus, Loc.T(
                "Starting Antigravity local audio bridge…",
                "جاري تشغيل جسر الصوت المحلي لـ Antigravity…"));

            var micDiag = new StringBuilder();
            var micDiagLock = new object();
            var micReadyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var micPsi = CreateMicServeStartInfo(executable, scope, micPort);
            micServeProcess = Process.Start(micPsi)
                ?? throw new InvalidOperationException(Loc.T(
                    "Could not start Antigravity local audio bridge (agy mic-serve).",
                    "تعذّر تشغيل جسر الصوت المحلي لـ Antigravity (agy mic-serve)."));

            micReaderCts = new CancellationTokenSource();
            var stdout = micServeProcess.StandardOutput;
            var stderr = micServeProcess.StandardError;
            var readerToken = micReaderCts.Token;
            micStdoutTask = Task.Run(() => DrainMicServeStreamAsync(stdout, micDiag, micDiagLock, micReadyTcs, readerToken));
            micStderrTask = Task.Run(() => DrainMicServeStreamAsync(stderr, micDiag, micDiagLock, micReadyTcs, readerToken));

            // Never TCP-probe mic-serve's port: connecting immediately opens the microphone stream.
            var micExitTask = micServeProcess.WaitForExitAsync(CancellationToken.None);
            var micInitWinner = await Task.WhenAny(
                micReadyTcs.Task,
                micExitTask,
                Task.Delay(MicServeStartupCheckDelay, ct)).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            if (micInitWinner == micExitTask || micServeProcess.HasExited)
            {
                await Task.WhenAny(Task.WhenAll(micStdoutTask, micStderrTask), Task.Delay(TimeSpan.FromMilliseconds(400))).ConfigureAwait(false);
                string detail = ReadDiagnostics(micDiag, micDiagLock);
                throw new InvalidOperationException(Loc.T(
                    "Antigravity local audio bridge (agy mic-serve) failed to start: ",
                    "فشل تشغيل جسر الصوت المحلي لـ Antigravity (agy mic-serve): ") +
                    (detail.Length > 0 ? detail : $"exit code {micServeProcess.ExitCode}"));
            }

            SafeNotifyStatus(onStatus, Loc.T(
                "Opening external Antigravity CLI voice window…",
                "جاري فتح نافذة Antigravity CLI الصوتية الخارجية…"));

            var cliPsi = CreateInteractiveCliStartInfo(executable, scope, bareModel, micPort, createCliWindow);
            cliProcess = Process.Start(cliPsi)
                ?? throw new InvalidOperationException(Loc.T(
                    "Could not open the external Antigravity CLI window.",
                    "تعذّر فتح نافذة Antigravity CLI الخارجية."));

            var cliExitTask = cliProcess.WaitForExitAsync(CancellationToken.None);
            var cliInitWinner = await Task.WhenAny(
                cliExitTask,
                micExitTask,
                Task.Delay(CliStartupStabilityDelay, ct)).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            if (cliInitWinner == micExitTask || micServeProcess.HasExited)
            {
                string detail = ReadDiagnostics(micDiag, micDiagLock);
                throw new InvalidOperationException(Loc.T(
                    "Antigravity local audio bridge exited during launch: ",
                    "توقف جسر الصوت المحلي لـ Antigravity أثناء التشغيل: ") +
                    (detail.Length > 0 ? detail : $"exit code {micServeProcess.ExitCode}"));
            }

            if (cliInitWinner == cliExitTask || cliProcess.HasExited)
            {
                string logDetail = TryReadLogTail(scope.SessionLogPath);
                throw new InvalidOperationException(Loc.T(
                    "External Antigravity CLI exited immediately during startup: ",
                    "أُغلقت نافذة Antigravity CLI الخارجية فورًا أثناء البدء: ") +
                    (logDetail.Length > 0 ? logDetail : $"exit code {cliProcess.ExitCode}"));
            }

            var session = new AntigravityNativeTalkSession(
                cliProcess,
                micServeProcess,
                scope,
                bareModel,
                micPort,
                micReaderCts,
                micStdoutTask,
                micStderrTask,
                micDiag,
                micDiagLock);

            // Transfer ownership to the session instance; startup catch block must not tear them down now.
            cliProcess = null;
            micServeProcess = null;
            scope = null;
            micReaderCts = null;

            session.AttachLifetimeAndMonitor(ct);
            if (ct.IsCancellationRequested)
            {
                await session.StopAsync().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }

            SafeNotifyStatus(onStatus, Loc.T(
                "Antigravity CLI opened (external window) · press F6 or type /voice live · tap Talk to stop",
                "تم فتح Antigravity CLI في نافذة خارجية · اضغط F6 أو اكتب /voice live · اضغط Talk للإنهاء"));

            return session;
        }
        catch
        {
            if (micReaderCts != null)
            {
                try { micReaderCts.Cancel(); } catch (ObjectDisposedException) { }
            }

            await TerminateProcessSafelyAsync(cliProcess).ConfigureAwait(false);
            await TerminateProcessSafelyAsync(micServeProcess).ConfigureAwait(false);

            if (micStdoutTask != null && micStderrTask != null)
            {
                try { await Task.WhenAll(micStdoutTask, micStderrTask).WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or InvalidOperationException) { }
            }

            cliProcess?.Dispose();
            micServeProcess?.Dispose();
            micReaderCts?.Dispose();
            scope?.Dispose();
            throw;
        }
    }

    private void AttachLifetimeAndMonitor(CancellationToken lifetimeToken)
    {
        lock (shutdownGate)
        {
            lifetimeRegistration = lifetimeToken.Register(() =>
            {
                Volatile.Write(ref localStopRequested, 1);
                _ = BeginShutdownAsync(fault: null, localStop: true);
            });
            if (shutdownTask == null) monitorTask = Task.Run(MonitorProcessesAsync);
        }
    }

    private async Task MonitorProcessesAsync()
    {
        try
        {
            var cliExitTask = cliProcess.WaitForExitAsync(monitorCts.Token);
            var micExitTask = micServeProcess.WaitForExitAsync(monitorCts.Token);

            var winner = await Task.WhenAny(cliExitTask, micExitTask).ConfigureAwait(false);
            if (Volatile.Read(ref localStopRequested) != 0 || monitorCts.IsCancellationRequested)
            {
                _ = BeginShutdownAsync(fault: null, localStop: true);
                return;
            }

            if (winner == cliExitTask)
            {
                int exitCode = 0;
                try { exitCode = cliProcess.ExitCode; } catch (InvalidOperationException) { }

                Exception? fault = null;
                if (exitCode != 0 && exitCode != StatusControlCExit)
                {
                    string detail = TryReadLogTail(scope.SessionLogPath);
                    fault = new InvalidOperationException(Loc.T(
                        "External Antigravity CLI exited unexpectedly: ",
                        "أُغلقت نافذة Antigravity CLI الخارجية بشكل غير متوقع: ") +
                        (detail.Length > 0 ? detail : $"exit code {exitCode}"));
                }

                _ = BeginShutdownAsync(fault, localStop: false);
            }
            else
            {
                int exitCode = 0;
                try { exitCode = micServeProcess.ExitCode; } catch (InvalidOperationException) { }
                string detail = ReadDiagnostics(micServeDiagnostics, micDiagLock);
                var fault = new InvalidOperationException(Loc.T(
                    "Antigravity local audio bridge (agy mic-serve) stopped unexpectedly: ",
                    "توقف جسر الصوت المحلي لـ Antigravity (agy mic-serve) بشكل غير متوقع: ") +
                    (detail.Length > 0 ? detail : $"exit code {exitCode}"));

                _ = BeginShutdownAsync(fault, localStop: false);
            }
        }
        catch (OperationCanceledException)
        {
            _ = BeginShutdownAsync(fault: null, localStop: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException)
        {
            _ = BeginShutdownAsync(ex, localStop: false);
        }
    }

    public Task StopAsync()
    {
        Volatile.Write(ref localStopRequested, 1);
        return BeginShutdownAsync(fault: null, localStop: true);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private Task BeginShutdownAsync(Exception? fault, bool localStop)
    {
        lock (shutdownGate)
        {
            if (shutdownTask != null)
                return shutdownTask;

            Volatile.Write(ref active, 0);
            shutdownTask = Task.Run(() => ExecuteShutdownAsync(fault, localStop));
            return shutdownTask;
        }
    }

    private async Task ExecuteShutdownAsync(Exception? fault, bool localStop)
    {
        try
        {
            CancellationTokenRegistration registration;
            lock (shutdownGate) registration = lifetimeRegistration;
            registration.Dispose();
            try { monitorCts.Cancel(); } catch (ObjectDisposedException) { }
            try { micReaderCts.Cancel(); } catch (ObjectDisposedException) { }

            await TerminateProcessSafelyAsync(cliProcess).ConfigureAwait(false);
            await TerminateProcessSafelyAsync(micServeProcess).ConfigureAwait(false);
            try { await monitorTask.WaitAsync(ProcessStopTimeout).ConfigureAwait(false); }
            catch (TimeoutException ex)
            {
                fault ??= ex;
                Trace.TraceWarning("NIVO Antigravity process monitor did not settle before cleanup.");
            }

            try { await Task.WhenAll(micStdoutTask, micStderrTask).WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or InvalidOperationException) { }

            cliProcess.Dispose();
            micServeProcess.Dispose();
            monitorCts.Dispose();
            micReaderCts.Dispose();
            scope.Dispose();
        }
        finally
        {
            if (fault != null && !localStop)
                completionTcs.TrySetException(fault);
            else
                completionTcs.TrySetResult();
        }
    }

    private static async Task EnsureVoiceCapableCliVersionAsync(string executable, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("--version");
        StripAmbientEnvironment(psi);
        psi.Environment["AGY_CLI_DISABLE_AUTO_UPDATE"] = "1";

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Cannot start Antigravity CLI to verify its version.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(VersionCheckTimeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TerminateProcessSafelyAsync(process).ConfigureAwait(false);
            try { await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(ProcessStopTimeout).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or TimeoutException)
            { Trace.TraceInformation("NIVO Antigravity version streams ended during cancellation ({0}).", ex.GetType().Name); }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("Timed out checking Antigravity CLI version.");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException("Antigravity CLI version check failed (exit " + process.ExitCode + ").");
        string combined = (await stdoutTask.ConfigureAwait(false) + "\n" + await stderrTask.ConfigureAwait(false)).Trim();
        var match = SemVerRegex().Match(combined);
        if (!match.Success || !Version.TryParse(match.Value, out var parsed))
        {
            throw new InvalidOperationException(Loc.T(
                "Could not verify Antigravity CLI version (v1.5.0+ is required for native Voice Mode).",
                "تعذّر التحقق من إصدار Antigravity CLI (يتطلب الإصدار v1.5.0 أو أحدث لوضع الصوت الأصلي)."));
        }

        if (parsed < MinimumVoiceCliVersion)
        {
            throw new InvalidOperationException(Loc.T(
                $"Antigravity CLI v1.5.0 or later is required for native Voice Mode (/voice live). Found v{parsed}.",
                $"يتطلب وضع الصوت الأصلي (/voice live) إصدار Antigravity CLI v1.5.0 أو أحدث. الإصدار الموجود v{parsed}."));
        }
    }

    private static ProcessStartInfo CreateMicServeStartInfo(string executable, VoiceSessionScope scope, int port)
    {
        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = scope.WorkspaceDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        psi.ArgumentList.Add("mic-serve");
        psi.ArgumentList.Add("--addr");
        psi.ArgumentList.Add($"127.0.0.1:{port}");

        // Strip ambient provider/auth/SSH variables; keep Windows user/system audio vars intact for WASAPI/WinMM.
        StripAmbientEnvironment(psi);
        psi.Environment["AGY_CLI_DISABLE_AUTO_UPDATE"] = "1";
        psi.Environment["TEMP"] = scope.TempDir;
        psi.Environment["TMP"] = scope.TempDir;

        return psi;
    }

    internal static ProcessStartInfo CreateInteractiveCliStartInfo(
        string executable,
        VoiceSessionScope scope,
        string bareModel,
        int micPort,
        bool createCliWindow)
    {
        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = scope.WorkspaceDir,
            UseShellExecute = false,
            CreateNoWindow = !createCliWindow,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };

        // Session-only settings file (--settings): enforces deny-all tool rules and workspace trust without mutating settings.json.
        psi.ArgumentList.Add("--settings");
        psi.ArgumentList.Add(scope.SessionSettingsPath);
        psi.ArgumentList.Add("--log-file");
        psi.ArgumentList.Add(scope.SessionLogPath);

        if (!string.IsNullOrWhiteSpace(bareModel))
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(bareModel);
        }

        StripAmbientEnvironment(psi);

        // Verified on agy 1.5.13: root interactive CLI resolves appDataDir via os.UserHomeDir() (USERPROFILE on Windows)
        // to <USERPROFILE>\.gemini\antigravity-cli. Pointing USERPROFILE at FakeHomeDir whose .gemini\antigravity-cli is an
        // NTFS directory junction to NIVO's antigravity-acp directory reuses the live file token without copying or relogin.
        psi.Environment["USERPROFILE"] = scope.FakeHomeDir;
        psi.Environment["HOME"] = scope.FakeHomeDir;
        psi.Environment["GEMINI_HOME"] = scope.ProfileRoot;
        // SSH_CONNECTION selects file-based token storage (antigravity-oauth-token) instead of Windows Credential Manager.
        psi.Environment["SSH_CONNECTION"] = SshIsolatedConnection;
        // ANTIGRAVITY_MIC routes interactive /voice live & F6 microphone capture and spoken playback to local agy mic-serve.
        psi.Environment["ANTIGRAVITY_MIC"] = $"127.0.0.1:{micPort}";
        psi.Environment["AGY_CLI_DISABLE_AUTO_UPDATE"] = "1";
        psi.Environment["TEMP"] = scope.TempDir;
        psi.Environment["TMP"] = scope.TempDir;

        return psi;
    }

    private static VoiceSessionScope PrepareVoiceSessionScope(string profileRoot, string acpDirectory)
    {
        string sessionId = Guid.NewGuid().ToString("N");
        string sessionDir = Path.Combine(profileRoot, "voice-sessions", sessionId);
        string fakeHomeDir = Path.Combine(sessionDir, "home");
        string fakeGeminiDir = Path.Combine(fakeHomeDir, ".gemini");
        string fakeConfigDir = Path.Combine(fakeGeminiDir, "config");
        string junctionPath = Path.Combine(fakeGeminiDir, CliAppDataDirName);
        string workspaceDir = Path.Combine(sessionDir, "workspace");
        string tempDir = Path.Combine(sessionDir, "tmp");
        string sessionSettingsPath = Path.Combine(sessionDir, "session-settings.json");
        string sessionLogPath = Path.Combine(tempDir, "agy-voice.log");

        Directory.CreateDirectory(fakeConfigDir);
        Directory.CreateDirectory(workspaceDir);
        Directory.CreateDirectory(tempDir);

        try
        {
            // Disable swarm plugin and prevent legacy migration inside the isolated session home.
            var configPayload = new
            {
                plugins = new
                {
                    swarm = new { enabled = false }
                }
            };
            File.WriteAllText(
                Path.Combine(fakeConfigDir, "config.json"),
                JsonSerializer.Serialize(configPayload, new JsonSerializerOptions { WriteIndented = true }) + "\n",
                Utf8NoBom);
            File.WriteAllText(Path.Combine(fakeConfigDir, ".migrated"), "", Utf8NoBom);

            // Session-only strict no-tools settings verified against antigravity.google/schemas/v1/settings.json.
            var sessionSettingsPayload = new
            {
                agentMode = "",
                toolPermission = "strict",
                allowNonWorkspaceAccess = false,
                enableTerminalSandbox = false,
                showFeedbackSurvey = false,
                trustedWorkspaces = new[] { workspaceDir },
                permissions = new
                {
                    allow = Array.Empty<string>(),
                    ask = Array.Empty<string>(),
                    deny = HardDenyRules
                }
            };
            File.WriteAllText(
                sessionSettingsPath,
                JsonSerializer.Serialize(sessionSettingsPayload, new JsonSerializerOptions { WriteIndented = true }) + "\n",
                Utf8NoBom);

            // Create a non-admin NTFS directory junction linking <FakeHomeDir>\.gemini\antigravity-cli -> <AcpDirectory>.
            CreateDirectoryJunction(junctionPath, acpDirectory);

            return new VoiceSessionScope(
                profileRoot,
                sessionDir,
                fakeHomeDir,
                junctionPath,
                workspaceDir,
                tempDir,
                sessionSettingsPath,
                sessionLogPath);
        }
        catch
        {
            SafeCleanupSessionDirectories(junctionPath, sessionDir);
            throw;
        }
    }

    private static int AllocateLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task DrainMicServeStreamAsync(
        StreamReader reader,
        StringBuilder diagnostics,
        object diagLock,
        TaskCompletionSource readyTcs,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                if (trimmed.Contains("127.0.0.1:", StringComparison.Ordinal) ||
                    trimmed.Contains("listening", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Contains("serving", StringComparison.OrdinalIgnoreCase))
                {
                    readyTcs.TrySetResult();
                }

                lock (diagLock)
                {
                    if (diagnostics.Length < MaxDiagnosticChars)
                    {
                        if (diagnostics.Length > 0) diagnostics.AppendLine();
                        diagnostics.Append(trimmed.Length > 300 ? trimmed[..300] : trimmed);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private static string ReadDiagnostics(StringBuilder diagnostics, object diagLock)
    {
        lock (diagLock)
            return ErrorAwarenessService.RedactSensitiveTokens(diagnostics.ToString().Trim());
    }

    private static string TryReadLogTail(string logPath)
    {
        try
        {
            if (!File.Exists(logPath)) return "";
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 16_384) stream.Seek(-16_384, SeekOrigin.End);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = reader.ReadToEnd().Split('\n')
                .Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(6)
                .Select(l => l.Length > 240 ? l[..240] : l);
            return ErrorAwarenessService.RedactSensitiveTokens(string.Join(" | ", lines));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static async Task TerminateProcessSafelyAsync(Process? process)
    {
        if (process == null) return;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            Trace.TraceWarning("NIVO Antigravity voice process termination skipped ({0}).", ex.GetType().Name);
        }

        try
        {
            if (!process.HasExited)
                await process.WaitForExitAsync().WaitAsync(ProcessStopTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or OperationCanceledException or Win32Exception) { }
    }

    private static void StripAmbientEnvironment(ProcessStartInfo psi)
    {
        foreach (string key in psi.Environment.Keys.Where(ShouldStripEnvironmentKey).ToArray())
            psi.Environment.Remove(key);
    }

    private static bool ShouldStripEnvironmentKey(string key)
    {
        if (RemovedEnvKeys.Contains(key))
            return true;
        foreach (string prefix in RemovedEnvPrefixes)
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        return key.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("_ACCESS_TOKEN", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("_AUTH_TOKEN", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeOptionalBareModel(string? model)
    {
        string trimmed = (model ?? "").Trim();
        if (trimmed.Length == 0) return "";
        if (trimmed.Length > 180 || trimmed.Contains('/') || !char.IsLetterOrDigit(trimmed[0]))
            throw new InvalidDataException("Choose a valid Antigravity model ID.");
        foreach (char c in trimmed)
        {
            if (!char.IsLetterOrDigit(c) && c is not ('.' or '_' or '-' or ':' or '@'))
                throw new InvalidDataException("Choose a valid Antigravity model ID.");
        }
        return trimmed;
    }

    private static void SafeNotifyStatus(Action<string>? onStatus, string status)
    {
        if (onStatus == null || string.IsNullOrWhiteSpace(status)) return;
        try { onStatus(status); }
        catch (Exception ex)
        {
            Trace.TraceWarning("NIVO Antigravity voice status callback failed ({0}).", ex.GetType().Name);
        }
    }

    // Creates a standard non-admin NTFS Directory Junction (IO_REPARSE_TAG_MOUNT_POINT) without invoking cmd.exe.
    internal static void CreateDirectoryJunction(string junctionPath, string targetDirectory)
    {
        string fullJunction = Path.GetFullPath(junctionPath);
        string fullTarget = Path.GetFullPath(targetDirectory);
        if (!Directory.Exists(fullTarget))
            throw new DirectoryNotFoundException("Antigravity isolated profile directory does not exist.");

        Directory.CreateDirectory(fullJunction);
        try
        {
            string printName = fullTarget;
            string substituteName = @"\??\" + fullTarget;
            byte[] substituteBytes = Encoding.Unicode.GetBytes(substituteName);
            byte[] printBytes = Encoding.Unicode.GetBytes(printName);

            // REPARSE_DATA_BUFFER for MountPointReparseBuffer:
            // ReparseTag(4) + ReparseDataLength(2) + Reserved(2) +
            // SubstituteNameOffset(2) + SubstituteNameLength(2) + PrintNameOffset(2) + PrintNameLength(2) +
            // PathBuffer: SubstituteName + NUL(2) + PrintName + NUL(2)
            int pathBufferLength = substituteBytes.Length + 2 + printBytes.Length + 2;
            int reparseDataLength = 8 + pathBufferLength;
            byte[] buffer = new byte[8 + reparseDataLength];

            BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), IoReparseTagMountPoint);
            BitConverter.TryWriteBytes(buffer.AsSpan(4, 2), (ushort)reparseDataLength);
            BitConverter.TryWriteBytes(buffer.AsSpan(6, 2), (ushort)0);
            BitConverter.TryWriteBytes(buffer.AsSpan(8, 2), (ushort)0);
            BitConverter.TryWriteBytes(buffer.AsSpan(10, 2), (ushort)substituteBytes.Length);
            BitConverter.TryWriteBytes(buffer.AsSpan(12, 2), (ushort)(substituteBytes.Length + 2));
            BitConverter.TryWriteBytes(buffer.AsSpan(14, 2), (ushort)printBytes.Length);

            Buffer.BlockCopy(substituteBytes, 0, buffer, 16, substituteBytes.Length);
            Buffer.BlockCopy(printBytes, 0, buffer, 16 + substituteBytes.Length + 2, printBytes.Length);

            using SafeFileHandle handle = CreateFile(
                fullJunction,
                GenericWrite,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);

            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to open Antigravity profile junction directory.");

            if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"Failed to link Antigravity isolated profile via NTFS directory junction (Win32 error {err}).");
            }
        }
        catch
        {
            try { if (Directory.Exists(fullJunction)) Directory.Delete(fullJunction, recursive: false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    internal static void SafeCleanupSessionDirectories(string junctionPath, string sessionDir)
    {
        string fullSession = Path.GetFullPath(sessionDir);
        string fullJunction = Path.GetFullPath(junctionPath);
        string expectedJunction = Path.Combine(fullSession, "home", ".gemini", CliAppDataDirName);
        if (!Guid.TryParseExact(Path.GetFileName(fullSession), "N", out _) ||
            !string.Equals(Path.GetFileName(Path.GetDirectoryName(fullSession)), "voice-sessions", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(fullJunction, expectedJunction, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing cleanup outside the exact temporary Antigravity voice session.");

        // Always remove the reparse point non-recursively FIRST so NIVO's real antigravity-acp target is never traversed or modified.
        if (Directory.Exists(junctionPath))
        {
            try
            {
                var attrs = File.GetAttributes(junctionPath);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(junctionPath, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("NIVO Antigravity voice junction cleanup skipped ({0}).", ex.GetType().Name);
            }
        }

        // Refuse recursive cleanup of sessionDir if the junction is somehow still present.
        if (Directory.Exists(junctionPath))
            return;

        // Windows can keep a terminated process's empty working directory delete-pending briefly.
        // Retry only transient sharing/lock/directory-not-empty errors; preserve all path/link guards above.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(sessionDir)) Directory.Delete(sessionDir, recursive: true);
                return;
            }
            catch (IOException ex) when (attempt < 5 && (ex.HResult & 0xffff) is 32 or 33 or 145)
            { Thread.Sleep(25 * (attempt + 1)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("NIVO Antigravity voice session directory cleanup skipped ({0}, 0x{1:X8}).", ex.GetType().Name, ex.HResult);
                return;
            }
        }
    }

    internal sealed class VoiceSessionScope(
        string profileRoot,
        string sessionDir,
        string fakeHomeDir,
        string junctionPath,
        string workspaceDir,
        string tempDir,
        string sessionSettingsPath,
        string sessionLogPath) : IDisposable
    {
        public string ProfileRoot { get; } = profileRoot;
        public string SessionDir { get; } = sessionDir;
        public string FakeHomeDir { get; } = fakeHomeDir;
        public string JunctionPath { get; } = junctionPath;
        public string WorkspaceDir { get; } = workspaceDir;
        public string TempDir { get; } = tempDir;
        public string SessionSettingsPath { get; } = sessionSettingsPath;
        public string SessionLogPath { get; } = sessionLogPath;

        public void Dispose() => SafeCleanupSessionDirectories(JunctionPath, SessionDir);
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+", RegexOptions.CultureInvariant)]
    private static partial Regex SemVerRegex();

    private const uint IoReparseTagMountPoint = 0xA0000003;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[] lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}
