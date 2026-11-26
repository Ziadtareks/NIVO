using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Nivo.Core;
using Nivo.UI;

namespace Nivo.Services;

public sealed partial class AntigravityAccountService(Store store)
{
    private const string AppDataDirName = "antigravity-acp";
    private const string AuthMethodPersonal = "oauth-personal";
    private const string CodeRedirectUri = "https://antigravity.google/oauth-callback";
    private const string SshIsolatedConnection = "127.0.0.1 0 127.0.0.1 0";
    private const int MaxAnswerChars = 200_000;
    private const int MaxLineChars = 4_000_000;
    private const int MaxAuthorizationUrlLength = 16_384;
    private const long MaxImageBytes = 10 * 1024 * 1024;
    private static readonly TimeSpan CodeLoginUrlStartupTimeout = TimeSpan.FromSeconds(30);

    private static readonly string[] AuthUrlPrefixes =
    [
        "Open the following URL in your local browser to authenticate:",
        "Open the following link to authenticate the ACP server:"
    ];

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

    private static readonly string[] IsolatedAuthArtifactFiles =
    [
        "antigravity-oauth-token",
        "jetski-standalone-oauth-token",
        "acp-auth-state.json",
        "needs-auth-cache.json",
        "mcp_oauth_tokens.json",
        "acp_token.json"
    ];

    private static readonly HashSet<string> AllowedRuntimeWorkspaceDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".agents",
        ".gemini"
    };

    private string ProfileRoot => Path.Combine(store.Root, "accounts", "antigravity");
    private string AcpDirectory => Path.Combine(ProfileRoot, AppDataDirName);

    public static string FindExecutable(string configured = "")
    {
        configured = (configured ?? "").Trim().Trim('"').Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!File.Exists(configured) || !configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Choose the actual agy.exe executable, not a .cmd or .ps1 wrapper.");
            return Path.GetFullPath(configured);
        }

        foreach (string candidate in DiscoverExecutableCandidates())
        {
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        throw new FileNotFoundException(
            "Antigravity CLI (agy.exe v1.5+ with ACP support) was not found. Install Antigravity CLI or choose agy.exe in Settings.");
    }

    private static IEnumerable<string> DiscoverExecutableCandidates()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // Prefer T3 Code's managed agy.exe binary (v1.5+ with ACP support); only the public binary path is read, never tokens or userdata.
        string t3ManagedDir = Path.Combine(userProfile, ".t3", "tools", "antigravity-acp", "win32-x64");
        string activeJson = Path.Combine(t3ManagedDir, "active.json");
        if (File.Exists(activeJson))
        {
            string? activeExe = TryResolveT3ManagedExecutable(t3ManagedDir, activeJson);
            if (activeExe != null) yield return activeExe;
        }

        string versionsDir = Path.Combine(t3ManagedDir, "versions");
        if (Directory.Exists(versionsDir))
        {
            foreach (string dir in Directory.EnumerateDirectories(versionsDir))
            {
                string name = Path.GetFileName(dir);
                if (!Sha256HexRegex().IsMatch(name)) continue;
                string exe = Path.Combine(dir, "agy.exe");
                if (File.Exists(exe)) yield return exe;
            }
        }

        yield return Path.Combine(localAppData, "agy", "bin", "agy.exe");

        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string clean = dir.Trim().Trim('"').Trim();
            if (clean.Length == 0 || clean.IndexOfAny(Path.GetInvalidPathChars()) >= 0) continue;
            yield return Path.Combine(clean, "agy.exe");
        }
    }

    private static string? TryResolveT3ManagedExecutable(string managedDir, string activeJsonPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(activeJsonPath));
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("releaseId", out var releaseIdEl) &&
                releaseIdEl.ValueKind == JsonValueKind.String)
            {
                string? releaseId = releaseIdEl.GetString()?.Trim();
                if (!string.IsNullOrEmpty(releaseId) && Sha256HexRegex().IsMatch(releaseId))
                {
                    string exe = Path.Combine(managedDir, "versions", releaseId, "agy.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.TraceInformation("NIVO skipped unreadable Antigravity active.json ({0}).", ex.GetType().Name);
        }
        return null;
    }

    public Task LoginAsync(Action<string> openAuthUrl, CancellationToken ct) =>
        LoginAsync(openAuthUrl, readAuthorizationCode: null, ct);

    public async Task LoginAsync(
        Action<string> openAuthUrl,
        Func<string, CancellationToken, Task<string>>? readAuthorizationCode,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(openAuthUrl);
        using var scope = PrepareIsolatedRun();
        string executable = FindExecutable(store.Data.Settings.AntigravityCliPath);

        // First check whether NIVO's isolated file-backed profile is already authenticated (always with SSH_CONNECTION set
        // so agy 1.5+ uses isolated file storage in AcpDirectory rather than Windows Credential Manager).
        bool needsInteractiveCodeLogin = false;
        await using (var probeClient = StartAcpClient(scope, onAuthUrl: null, executableOverride: executable))
        {
            var init = await probeClient.InitializeAsync(ct);
            if (!init.HasAuthMethod(AuthMethodPersonal))
                throw new InvalidOperationException("This Antigravity CLI runtime does not advertise Google OAuth ('oauth-personal') sign-in.");

            try
            {
                await probeClient.RequestAsync("authenticate", new { methodId = AuthMethodPersonal }, ct);
                var session = await probeClient.RequestAsync("session/new", new { cwd = scope.WorkspaceDir, mcpServers = Array.Empty<object>() }, ct);
                string? sessionId = ReadString(session, "sessionId");
                if (!string.IsNullOrWhiteSpace(sessionId))
                    await probeClient.TryCloseSessionAsync(sessionId);
                return;
            }
            catch (AcpRpcException ex) when (RequiresInteractiveCodeLogin(ex))
            {
                needsInteractiveCodeLogin = true;
            }
            catch (AcpRpcException ex)
            {
                throw new InvalidOperationException(FormatAuthFailure(ex), ex);
            }
        }

        if (!needsInteractiveCodeLogin)
            return;

        // Run `agy acp --login` with the exact same isolated SSH_CONNECTION environment so Google OAuth persists
        // into NIVO's private file token storage instead of colliding with OS Credential Manager.
        await RunInteractiveCodeLoginAsync(scope, executable, openAuthUrl, readAuthorizationCode, ct);

        // Verify a fresh isolated ACP process (same environment as ModelsAsync/AskAsync) can authenticate and open a session.
        await using var verifyClient = StartAcpClient(scope, onAuthUrl: null, executableOverride: executable);
        var verifyInit = await verifyClient.InitializeAsync(ct);
        if (!verifyInit.HasAuthMethod(AuthMethodPersonal))
            throw new InvalidOperationException("This Antigravity CLI runtime does not advertise Google OAuth ('oauth-personal') sign-in.");

        try
        {
            await verifyClient.RequestAsync("authenticate", new { methodId = AuthMethodPersonal }, ct);
            var session = await verifyClient.RequestAsync("session/new", new { cwd = scope.WorkspaceDir, mcpServers = Array.Empty<object>() }, ct);
            string? sessionId = ReadString(session, "sessionId");
            if (!string.IsNullOrWhiteSpace(sessionId))
                await verifyClient.TryCloseSessionAsync(sessionId);
        }
        catch (AcpRpcException ex)
        {
            throw new InvalidOperationException(FormatAuthFailure(ex), ex);
        }
    }

    public async Task<IReadOnlyList<AccountModel>> ModelsAsync(CancellationToken ct)
    {
        using var scope = PrepareIsolatedRun();
        await using var client = StartAcpClient(scope, onAuthUrl: null);
        var init = await client.InitializeAsync(ct);

        JsonElement session;
        try
        {
            if (init.HasAuthMethod(AuthMethodPersonal))
                await client.RequestAsync("authenticate", new { methodId = AuthMethodPersonal }, ct);
            session = await client.RequestAsync("session/new", new { cwd = scope.WorkspaceDir, mcpServers = Array.Empty<object>() }, ct);
        }
        catch (AcpRpcException ex)
        {
            throw new InvalidOperationException(FormatSessionFailure(ex), ex);
        }

        string? sessionId = ReadString(session, "sessionId");
        try
        {
            return ParseAccountModels(session, init.SupportsImageInput);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(sessionId))
                await client.TryCloseSessionAsync(sessionId);
        }
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        string? exe = null;
        try { exe = FindExecutable(store.Data.Settings.AntigravityCliPath); }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
        {
            Trace.TraceInformation("NIVO Antigravity logout proceeding with local profile cleanup only ({0}).", ex.GetType().Name);
        }

        if (exe != null && Directory.Exists(AcpDirectory))
        {
            try
            {
                using var scope = PrepareIsolatedRun();
                await using var client = StartAcpClient(scope, onAuthUrl: null, executableOverride: exe);
                var init = await client.InitializeAsync(ct);
                if (init.SupportsLogout)
                    await client.RequestAsync("logout", new { }, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or AcpRpcException or System.ComponentModel.Win32Exception)
            {
                Trace.TraceWarning("NIVO Antigravity ACP logout returned an error; clearing isolated token files ({0}).", ex.Message);
            }
        }

        // Remove only NIVO's own isolated file-storage token and auth-state artifacts; never touch ~/.gemini, ~/.t3, or OS Credential Manager.
        foreach (string artifactFile in IsolatedAuthArtifactFiles)
        {
            string full = Path.Combine(AcpDirectory, artifactFile);
            if (File.Exists(full))
            {
                File.Delete(full);
            }
        }
    }

    public async Task<string> AskAsync(string model, string prompt, string system, string? image, Action<string>? onText, CancellationToken ct)
    {
        model = (model ?? "").Trim();
        if (!IsValidBareModelId(model))
            throw new InvalidDataException("Choose a valid Antigravity model ID.");

        using var scope = PrepareIsolatedRun();
        await using var client = StartAcpClient(scope, onAuthUrl: null);
        var init = await client.InitializeAsync(ct);

        if (image != null && !init.SupportsImageInput)
            throw new InvalidOperationException("This Antigravity CLI runtime did not advertise image input support.");

        string hostSystem = BuildHostSystemPrompt(system);
        Dictionary<string, object> newSessionParams = new()
        {
            ["cwd"] = scope.WorkspaceDir,
            ["mcpServers"] = Array.Empty<object>(),
            ["disableBuiltInTools"] = true
        };
        if (init.SupportsSystemPromptAppend && !string.IsNullOrWhiteSpace(hostSystem))
        {
            newSessionParams["_meta"] = new
            {
                antigravity = new
                {
                    systemPrompt = new { append = hostSystem }
                }
            };
        }

        JsonElement session;
        try
        {
            if (init.HasAuthMethod(AuthMethodPersonal))
                await client.RequestAsync("authenticate", new { methodId = AuthMethodPersonal }, ct);
            session = await client.RequestAsync("session/new", newSessionParams, ct);
        }
        catch (AcpRpcException ex)
        {
            throw new InvalidOperationException(FormatSessionFailure(ex), ex);
        }

        string sessionId = ReadString(session, "sessionId")
            ?? throw new InvalidDataException("Antigravity ACP did not return a session ID.");

        try
        {
            await EnsureDefaultModeAsync(client, sessionId, session, ct);
            await SelectModelAsync(client, sessionId, session, model, ct);

            var promptBlocks = await BuildPromptBlocksAsync(
                init.SupportsSystemPromptAppend ? prompt : PrependSystemInstructions(hostSystem, prompt),
                image,
                ct);

            var answer = new StringBuilder();
            var answerLock = new object();
            string? toolViolation = null;
            using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            client.SetSessionUpdateHandler(notification =>
            {
                if (notification.ValueKind != JsonValueKind.Object) return;
                if (ReadString(notification, "sessionId") is { } sid && !string.Equals(sid, sessionId, StringComparison.Ordinal))
                    return;
                if (!notification.TryGetProperty("update", out var update) || update.ValueKind != JsonValueKind.Object)
                    return;

                string updateType = ReadString(update, "sessionUpdate") ?? "";
                if (updateType is "tool_call" or "tool_call_update")
                {
                    // Hard stop if the agent attempts to run any tool despite deny-all settings.
                    string? status = ReadString(update, "status");
                    if (!string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.CompareExchange(ref toolViolation, "Antigravity attempted to execute a tool while tools are disabled in NIVO.", null);
                        try { turnCts.Cancel(); } catch (ObjectDisposedException) { }
                    }
                    return;
                }

                if (updateType == "agent_message_chunk" &&
                    update.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.Object &&
                    string.Equals(ReadString(content, "type"), "text", StringComparison.Ordinal) &&
                    content.TryGetProperty("text", out var textEl) &&
                    textEl.ValueKind == JsonValueKind.String)
                {
                    string chunk = textEl.GetString() ?? "";
                    if (chunk.Length == 0) return;

                    bool exceededLimit = false;
                    lock (answerLock)
                    {
                        if (chunk.Length > MaxAnswerChars - answer.Length)
                            exceededLimit = true;
                        else
                            answer.Append(chunk);
                    }

                    if (exceededLimit)
                    {
                        Interlocked.CompareExchange(ref toolViolation, "Antigravity reply exceeded the application size limit.", null);
                        try { turnCts.Cancel(); } catch (ObjectDisposedException) { }
                        return;
                    }

                    onText?.Invoke(chunk);
                }
            });

            JsonElement promptResult;
            try
            {
                promptResult = await client.RequestAsync("session/prompt", new { sessionId, prompt = promptBlocks }, turnCts.Token);
            }
            catch (OperationCanceledException) when (Volatile.Read(ref toolViolation) is { } violation && !ct.IsCancellationRequested)
            {
                client.SetSessionUpdateHandler(null);
                await client.TryCancelSessionAsync(sessionId);
                throw new InvalidOperationException(violation);
            }
            catch (OperationCanceledException)
            {
                client.SetSessionUpdateHandler(null);
                await client.TryCancelSessionAsync(sessionId);
                throw;
            }
            catch (AcpRpcException ex)
            {
                client.SetSessionUpdateHandler(null);
                throw new InvalidOperationException(FormatPromptFailure(model, ex), ex);
            }

            // Detach handler before reading answer snapshot so late session/update notifications cannot race StringBuilder.
            client.SetSessionUpdateHandler(null);

            if (Volatile.Read(ref toolViolation) is { } postViolation)
                throw new InvalidOperationException(postViolation);

            if (client.DeniedToolRequestCount > 0)
                throw new InvalidOperationException("Antigravity requested a tool or permission while tools are disabled in NIVO.");

            EnsureWorkspaceUnmodified(scope.WorkspaceDir);

            string? stopReason = ReadString(promptResult, "stopReason");
            if (!string.Equals(stopReason, "end_turn", StringComparison.Ordinal))
                throw new InvalidOperationException($"Antigravity ({model}) did not complete the response ({stopReason ?? "unknown status"}).");

            string result;
            lock (answerLock)
            {
                result = answer.ToString();
            }

            if (string.IsNullOrWhiteSpace(result))
                throw new InvalidOperationException($"Antigravity ({model}) did not return an answer.");

            return result;
        }
        finally
        {
            client.SetSessionUpdateHandler(null);
            await client.TryCloseSessionAsync(sessionId);
        }
    }

    private async Task RunInteractiveCodeLoginAsync(
        IsolatedRunScope scope,
        string executable,
        Action<string> openAuthUrl,
        Func<string, CancellationToken, Task<string>>? readAuthorizationCode,
        CancellationToken ct)
    {
        var psi = CreateAcpProcessStartInfo(executable, scope, extraArgs: ["--login"]);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Cannot start Antigravity sign-in process.");
        using var ioCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var authUrlTcs = new TaskCompletionSource<CodeAuthPending>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outputTail = new StringBuilder();
        var outputLock = new object();

        void AppendDiagnostic(string line)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("https://accounts.google.com/", StringComparison.OrdinalIgnoreCase))
                return;
            lock (outputLock)
            {
                if (outputTail.Length < 1200)
                {
                    if (outputTail.Length > 0) outputTail.AppendLine();
                    outputTail.Append(trimmed.Length > 300 ? trimmed[..300] : trimmed);
                }
            }
        }

        void HandleLoginLine(string line)
        {
            if (TryExtractLoginAuthUrlCandidate(line, out string candidate))
            {
                if (TryParseCodeAuthUrl(candidate, out var pending))
                {
                    authUrlTcs.TrySetResult(pending);
                }
                else
                {
                    authUrlTcs.TrySetException(new InvalidDataException("Antigravity returned an invalid Google sign-in URL."));
                }
                return;
            }

            AppendDiagnostic(line);
        }

        async Task ReadStreamAsync(StreamReader reader)
        {
            try
            {
                while (!ioCts.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(ioCts.Token);
                    if (line == null) break;
                    if (line.Length > MaxLineChars)
                    {
                        authUrlTcs.TrySetException(new InvalidDataException("Antigravity sign-in output exceeded the allowed line size."));
                        break;
                    }
                    HandleLoginLine(line);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) when (ioCts.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (ioCts.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                authUrlTcs.TrySetException(new InvalidOperationException("Antigravity sign-in stream failed: " + ex.Message, ex));
            }
        }

        var stdoutTask = Task.Run(() => ReadStreamAsync(process.StandardOutput));
        var stderrTask = Task.Run(() => ReadStreamAsync(process.StandardError));
        var exitTask = process.WaitForExitAsync(CancellationToken.None);

        try
        {
            CodeAuthPending pending;
            using (var startupTimeout = new CancellationTokenSource(CodeLoginUrlStartupTimeout))
            using (var startupCts = CancellationTokenSource.CreateLinkedTokenSource(ct, startupTimeout.Token))
            {
                var completed = await Task.WhenAny(authUrlTcs.Task, exitTask, Task.Delay(Timeout.Infinite, startupCts.Token));
                if (completed == authUrlTcs.Task)
                {
                    pending = await authUrlTcs.Task;
                }
                else if (completed == exitTask)
                {
                    await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(TimeSpan.FromSeconds(1)));
                    if (authUrlTcs.Task.IsCompleted)
                    {
                        pending = await authUrlTcs.Task;
                    }
                    else
                    {
                        throw new InvalidOperationException(BuildCodeLoginExitMessage(outputTail, outputLock, process.ExitCode));
                    }
                }
                else
                {
                    ct.ThrowIfCancellationRequested();
                    throw new TimeoutException("Antigravity did not produce a Google sign-in link in time. Try connecting again.");
                }
            }

            openAuthUrl(pending.AuthorizationUrl);

            string rawResponse = readAuthorizationCode != null
                ? await readAuthorizationCode(pending.AuthorizationUrl, ct)
                : await PromptForAuthorizationCodeAsync(pending, openAuthUrl, ct);

            if (!TryNormalizeCodeLoginResponse(pending, rawResponse, out string normalizedResponse))
                throw new InvalidDataException("Paste the authorization code from the Antigravity sign-in page, or that page's full redirect URL.");

            await process.StandardInput.WriteLineAsync(normalizedResponse.AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
            process.StandardInput.Close();

            await process.WaitForExitAsync(ct);
            await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(TimeSpan.FromSeconds(2)));

            if (process.ExitCode != 0)
                throw new InvalidOperationException(BuildCodeLoginExitMessage(outputTail, outputLock, process.ExitCode));
        }
        finally
        {
            try { ioCts.Cancel(); } catch (ObjectDisposedException) { }
            try { process.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                Trace.TraceWarning("Antigravity login process termination failed ({0}).", ex.GetType().Name);
            }
            try
            {
                if (!process.HasExited)
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or OperationCanceledException) { }
            try { await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or InvalidOperationException) { }
        }
    }

    private static async Task<string> PromptForAuthorizationCodeAsync(
        CodeAuthPending pending,
        Action<string> openAuthUrl,
        CancellationToken ct)
    {
        var app = Application.Current;
        if (app?.Dispatcher == null)
        {
            throw new InvalidOperationException(
                "Google sign-in requires entering the authorization code shown on antigravity.google/oauth-callback, which needs the desktop window.");
        }

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Window? dialog = null;

        await app.Dispatcher.InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
                return;
            }

            var w = new Window
            {
                Title = Loc.T("Antigravity · Complete Google sign-in", "Antigravity · إكمال تسجيل الدخول بحساب Google"),
                Width = 540,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                FlowDirection = Loc.Flow
            };
            Ui.StyleWindow(w);

            var owner = app.Windows.OfType<MainWindow>().FirstOrDefault(win => win.IsVisible)
                ?? app.Windows.OfType<Window>().FirstOrDefault(win => win.IsVisible && !win.Topmost);
            if (owner != null) w.Owner = owner;
            else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var codeBox = new TextBox
            {
                FlowDirection = FlowDirection.LeftToRight,
                Height = 44,
                Margin = new Thickness(0, 10, 0, 8)
            };
            var validation = Ui.Text("", 12, "#D9B794");
            validation.Visibility = Visibility.Collapsed;
            validation.Margin = new Thickness(0, 0, 0, 8);

            void Submit()
            {
                if (!TryNormalizeCodeLoginResponse(pending, codeBox.Text, out string normalized))
                {
                    validation.Text = Loc.T(
                        "Paste the authorization code from the Antigravity page, or that page's full URL.",
                        "الصق رمز التفويض من صفحة Antigravity أو الرابط الكامل للصفحة.");
                    validation.Visibility = Visibility.Visible;
                    codeBox.Focus();
                    return;
                }
                tcs.TrySetResult(normalized);
                w.Close();
            }

            var submitBtn = Ui.Btn(Loc.T("Continue", "متابعة"), Submit, primary: true);
            submitBtn.IsDefault = true;
            var reopenBtn = Ui.Btn(Loc.T("Open sign-in link again", "فتح رابط الدخول مجددًا"), () => openAuthUrl(pending.AuthorizationUrl));
            var cancelBtn = Ui.Btn(Loc.T("Cancel", "إلغاء"), () =>
            {
                tcs.TrySetCanceled(ct);
                w.Close();
            });
            cancelBtn.IsCancel = true;

            var panel = Ui.Stack(
                Ui.Text(Loc.T(
                    "Google sign-in opened in your browser. After signing in, copy the authorization code (or the full URL) from the Antigravity page and paste it below.",
                    "تم فتح تسجيل دخول Google في المتصفح. بعد تسجيل الدخول، انسخ رمز التفويض (أو الرابط الكامل) من صفحة Antigravity والصقه هنا."), 13),
                codeBox,
                validation,
                Ui.Row(submitBtn, reopenBtn, cancelBtn));
            panel.Margin = new Thickness(24);
            w.Content = panel;
            w.Closed += (_, _) => tcs.TrySetCanceled(ct);

            dialog = w;
            w.Show();
            w.Activate();
            codeBox.Focus();
        });

        using var reg = ct.Register(() =>
        {
            tcs.TrySetCanceled(ct);
            if (dialog != null)
            {
                _ = app.Dispatcher.InvokeAsync(() =>
                {
                    try { if (dialog.IsLoaded) dialog.Close(); }
                    catch (InvalidOperationException) { }
                });
            }
        });

        return await tcs.Task;
    }

    private static string BuildCodeLoginExitMessage(StringBuilder outputTail, object outputLock, int exitCode)
    {
        string detail;
        lock (outputLock) detail = ErrorAwarenessService.RedactSensitiveTokens(outputTail.ToString().Trim());
        if (detail.Contains("unknown command", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("Usage of agy", StringComparison.OrdinalIgnoreCase))
            return "This Antigravity CLI version does not support 'agy acp --login'. Update agy to v1.5+ or set the agy.exe path in Settings.";
        if (detail.Contains("cancelled", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("no authorization code", StringComparison.OrdinalIgnoreCase))
            return "Google sign-in was cancelled before completing authorization.";
        if (detail.Length > 0)
            return "Antigravity Google sign-in failed: " + detail;
        return $"Antigravity Google sign-in exited with code {exitCode}. Start sign-in again.";
    }

    private static bool RequiresInteractiveCodeLogin(AcpRpcException ex) =>
        ex.Code == -32000 ||
        ex.Message.Contains("No browser is available here", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Authentication required", StringComparison.OrdinalIgnoreCase);

    private static bool TryExtractLoginAuthUrlCandidate(string line, out string candidate)
    {
        candidate = "";
        if (TryExtractAuthLineUrl(line, out candidate))
            return true;

        string trimmed = (line ?? "").Trim();
        if (trimmed.StartsWith("https://accounts.google.com/", StringComparison.OrdinalIgnoreCase))
        {
            candidate = trimmed;
            return true;
        }
        return false;
    }

    private static bool TryExtractAuthLineUrl(string line, out string url)
    {
        url = "";
        if (string.IsNullOrWhiteSpace(line)) return false;
        string trimmed = line.Trim();
        foreach (string prefix in AuthUrlPrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                string rest = trimmed[prefix.Length..].Trim();
                if (rest.Length > 0)
                {
                    url = rest;
                    return true;
                }
            }
        }
        return false;
    }

    private static bool TryParseCodeAuthUrl(string raw, out CodeAuthPending pending)
    {
        pending = default;
        string trimmed = (raw ?? "").Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxAuthorizationUrlLength || trimmed.Any(char.IsWhiteSpace))
            return false;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return false;
        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "accounts.google.com", StringComparison.OrdinalIgnoreCase) ||
            uri.UserInfo.Length != 0 ||
            uri.Fragment.Length != 0 ||
            !string.Equals(uri.AbsolutePath, "/o/oauth2/auth", StringComparison.Ordinal))
            return false;

        var query = ParseQueryParameters(uri.Query);
        if (!TryGetSingleParam(query, "response_type", out string responseType) ||
            !string.Equals(responseType, "code", StringComparison.Ordinal))
            return false;
        if (!TryGetSingleParam(query, "state", out string state) ||
            state.Length == 0 || state.Length > 512 || state.Any(char.IsWhiteSpace))
            return false;
        if (!TryGetSingleParam(query, "redirect_uri", out string redirectUri) ||
            !string.Equals(redirectUri, CodeRedirectUri, StringComparison.Ordinal))
            return false;

        pending = new CodeAuthPending(uri.AbsoluteUri, redirectUri, state);
        return true;
    }

    internal static bool TryNormalizeCodeLoginResponse(string authorizationUrl, string rawResponse, out string normalized)
    {
        normalized = "";
        return TryParseCodeAuthUrl(authorizationUrl, out var pending) &&
            TryNormalizeCodeLoginResponse(pending, rawResponse, out normalized);
    }

    private static bool TryNormalizeCodeLoginResponse(CodeAuthPending pending, string rawResponse, out string normalized)
    {
        normalized = "";
        string value = (rawResponse ?? "").Trim();
        if (value.Length == 0 || value.Length > MaxAuthorizationUrlLength || value.Any(char.IsWhiteSpace))
            return false;

        if (CodeTokenRegex().IsMatch(value))
        {
            normalized = value;
            return true;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;

        string originAndPath = $"{uri.Scheme.ToLowerInvariant()}://{uri.Authority}{uri.AbsolutePath}";
        if (!string.Equals(originAndPath, pending.RedirectUri, StringComparison.Ordinal) ||
            uri.UserInfo.Length != 0 ||
            uri.Fragment.Length != 0)
            return false;

        var query = ParseQueryParameters(uri.Query);
        if (!TryGetSingleParam(query, "state", out string state) ||
            !string.Equals(state, pending.State, StringComparison.Ordinal))
            return false;
        if (!TryGetSingleParam(query, "code", out string code) || code.Length == 0)
            return false;

        normalized = value;
        return true;
    }

    private static Dictionary<string, List<string>> ParseQueryParameters(string query)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query)) return result;
        ReadOnlySpan<char> span = query.AsSpan();
        if (span[0] == '?') span = span[1..];
        if (span.IsEmpty) return result;

        foreach (string pair in span.ToString().Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string rawKey = eq >= 0 ? pair[..eq] : pair;
            string rawVal = eq >= 0 ? pair[(eq + 1)..] : "";
            string key = DecodeQueryComponent(rawKey);
            string val = DecodeQueryComponent(rawVal);
            if (!result.TryGetValue(key, out var list))
            {
                list = [];
                result[key] = list;
            }
            list.Add(val);
        }

        return result;
    }

    private static string DecodeQueryComponent(string value)
    {
        try { return Uri.UnescapeDataString(value.Replace('+', ' ')); }
        catch (UriFormatException) { return ""; }
    }

    private static bool TryGetSingleParam(Dictionary<string, List<string>> query, string key, out string value)
    {
        value = "";
        if (!query.TryGetValue(key, out var list) || list.Count != 1)
            return false;
        value = list[0];
        return true;
    }

    private static string BuildHostSystemPrompt(string system)
    {
        const string noToolsConstraint =
            "Tools, filesystem access, shell commands, subagents, MCP servers, and web browsing are disabled in this NIVO session. Answer directly using only the provided text and image attachments.";
        return string.IsNullOrWhiteSpace(system) ? noToolsConstraint : system.Trim() + "\n\n" + noToolsConstraint;
    }

    private static string PrependSystemInstructions(string system, string prompt) =>
        string.IsNullOrWhiteSpace(system)
            ? prompt
            : $"[System instructions]\n{system}\n\n[User request]\n{prompt}";

    private static async Task EnsureDefaultModeAsync(AcpClient client, string sessionId, JsonElement session, CancellationToken ct)
    {
        if (session.TryGetProperty("modes", out var modes) && modes.ValueKind == JsonValueKind.Object)
        {
            string? currentMode = ReadString(modes, "currentModeId");
            if (string.Equals(currentMode, "default", StringComparison.Ordinal)) return;

            bool hasDefault = false;
            if (modes.TryGetProperty("availableModes", out var available) && available.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in available.EnumerateArray())
                {
                    if (string.Equals(ReadString(entry, "id"), "default", StringComparison.Ordinal))
                    {
                        hasDefault = true;
                        break;
                    }
                }
            }
            if (hasDefault)
            {
                await client.RequestAsync("session/set_mode", new { sessionId, modeId = "default" }, ct);
            }
        }
    }

    private static async Task SelectModelAsync(AcpClient client, string sessionId, JsonElement session, string model, CancellationToken ct)
    {
        var available = ParseAccountModels(session, supportsImageInput: false);
        var modelConfig = FindModelConfigOption(session);

        if (modelConfig.HasValue)
        {
            string configId = ReadString(modelConfig.Value, "id") ?? "model";
            string? current = ReadString(modelConfig.Value, "currentValue");
            if (available.Count > 0 && !available.Any(m => string.Equals(m.Id, model, StringComparison.Ordinal)))
                throw new InvalidOperationException($"Antigravity model '{model}' is unavailable for this Google account. Refresh models in Settings.");
            if (string.Equals(current, model, StringComparison.Ordinal)) return;

            try
            {
                await client.RequestAsync("session/set_config_option", new { sessionId, configId, value = model }, ct);
            }
            catch (AcpRpcException ex)
            {
                throw new InvalidOperationException($"Could not select Antigravity model '{model}': {ex.Message}", ex);
            }
            return;
        }

        if (session.TryGetProperty("models", out var modelsObj) && modelsObj.ValueKind == JsonValueKind.Object)
        {
            string? current = ReadString(modelsObj, "currentModelId");
            if (available.Count > 0 && !available.Any(m => string.Equals(m.Id, model, StringComparison.Ordinal)))
                throw new InvalidOperationException($"Antigravity model '{model}' is unavailable for this Google account. Refresh models in Settings.");
            if (string.Equals(current, model, StringComparison.Ordinal)) return;

            try
            {
                await client.RequestAsync("session/set_model", new { sessionId, modelId = model }, ct);
            }
            catch (AcpRpcException ex)
            {
                throw new InvalidOperationException($"Could not select Antigravity model '{model}': {ex.Message}", ex);
            }
            return;
        }

        throw new InvalidOperationException("Antigravity session did not expose a model selector.");
    }

    private static async Task<object[]> BuildPromptBlocksAsync(string prompt, string? imagePath, CancellationToken ct)
    {
        var blocks = new List<object>();
        if (!string.IsNullOrWhiteSpace(prompt))
            blocks.Add(new { type = "text", text = prompt });

        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            var info = new FileInfo(imagePath);
            if (!info.Exists)
                throw new FileNotFoundException("The attached image file could not be found.", imagePath);
            if (info.Length <= 0 || info.Length > MaxImageBytes)
                throw new InvalidOperationException("Antigravity image attachments must be between 1 byte and 10 MiB.");

            byte[] bytes = await File.ReadAllBytesAsync(imagePath, ct);
            if (bytes.Length > MaxImageBytes)
                throw new InvalidOperationException("Antigravity image attachments must be at most 10 MiB.");

            string mimeType = DetectSupportedImageMime(bytes, imagePath)
                ?? throw new InvalidDataException("Unsupported image format for Antigravity. Supported formats: PNG, JPEG, WebP, BMP.");

            blocks.Add(new
            {
                type = "image",
                data = Convert.ToBase64String(bytes),
                mimeType
            });
        }

        if (blocks.Count == 0)
            throw new InvalidDataException("A prompt requires text or an image attachment.");

        return blocks.ToArray();
    }

    private static string? DetectSupportedImageMime(byte[] bytes, string path)
    {
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.Length >= 12 &&
            bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
            bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
            return "image/webp";
        if (bytes.Length >= 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
            return "image/bmp";

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => null
        };
    }

    private static void EnsureWorkspaceUnmodified(string workspaceDir)
    {
        if (!Directory.Exists(workspaceDir)) return;
        foreach (string entry in Directory.EnumerateFileSystemEntries(workspaceDir))
        {
            string name = Path.GetFileName(entry);
            if (!AllowedRuntimeWorkspaceDirs.Contains(name) || !Directory.Exists(entry))
                throw new InvalidOperationException("Antigravity attempted to create files in the isolated workspace while tools are disabled.");
        }
    }

    private static JsonElement? FindModelConfigOption(JsonElement session)
    {
        if (!session.TryGetProperty("configOptions", out var options) || options.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object) continue;
            string? id = ReadString(option, "id");
            string? category = ReadString(option, "category");
            string? type = ReadString(option, "type");
            if (string.Equals(type, "select", StringComparison.Ordinal) &&
                (string.Equals(id, "model", StringComparison.Ordinal) || string.Equals(category, "model", StringComparison.Ordinal)))
                return option;
        }
        return null;
    }

    public static IReadOnlyList<AccountModel> ParseAccountModels(JsonElement session, bool supportsImageInput)
    {
        var rawEntries = new List<(string Id, string Name)>();

        var modelConfig = FindModelConfigOption(session);
        string? defaultModel = modelConfig.HasValue ? ReadString(modelConfig.Value, "currentValue") : null;
        if (defaultModel == null && session.TryGetProperty("models", out var defaults)) defaultModel = ReadString(defaults, "currentModelId");
        if (modelConfig.HasValue &&
            modelConfig.Value.TryGetProperty("options", out var optionsArr) &&
            optionsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in optionsArr.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (entry.TryGetProperty("value", out _))
                {
                    string id = (ReadString(entry, "value") ?? "").Trim();
                    string name = (ReadString(entry, "name") ?? "").Trim();
                    if (id.Length > 0) rawEntries.Add((id, name));
                }
                else if (entry.TryGetProperty("options", out var grouped) && grouped.ValueKind == JsonValueKind.Array)
                {
                    foreach (var sub in grouped.EnumerateArray())
                    {
                        if (sub.ValueKind != JsonValueKind.Object) continue;
                        string id = (ReadString(sub, "value") ?? "").Trim();
                        string name = (ReadString(sub, "name") ?? "").Trim();
                        if (id.Length > 0) rawEntries.Add((id, name));
                    }
                }
            }
        }
        else if (session.TryGetProperty("models", out var modelsObj) &&
                 modelsObj.ValueKind == JsonValueKind.Object &&
                 modelsObj.TryGetProperty("availableModels", out var availableArr) &&
                 availableArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in availableArr.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                string id = (ReadString(entry, "modelId") ?? "").Trim();
                string name = (ReadString(entry, "name") ?? "").Trim();
                if (id.Length > 0) rawEntries.Add((id, name));
            }
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<AccountModel>();
        foreach (var (id, rawName) in rawEntries)
        {
            if (!IsValidBareModelId(id) || !seenIds.Add(id)) continue;
            string baseName = rawName.Length > 0 ? rawName : id;
            string displayName = seenNames.Add(baseName) ? baseName : $"{baseName} ({id})";
            result.Add(new AccountModel(id, displayName, supportsImageInput, string.Equals(id, defaultModel, StringComparison.Ordinal)));
        }

        return result;
    }

    private static bool IsValidBareModelId(string? model)
    {
        if (string.IsNullOrWhiteSpace(model) || model.Length > 180 || model.Contains('/') || !char.IsLetterOrDigit(model[0]))
            return false;
        foreach (char c in model)
            if (!char.IsLetterOrDigit(c) && c is not ('.' or '_' or '-' or ':' or '@'))
                return false;
        return true;
    }

    private IsolatedRunScope PrepareIsolatedRun()
    {
        string runId = Guid.NewGuid().ToString("N");
        string tempDir = Path.Combine(ProfileRoot, "tmp", runId);
        string workspaceDir = Path.Combine(ProfileRoot, "workspaces", runId);
        string configDir = Path.Combine(ProfileRoot, "config");

        Directory.CreateDirectory(ProfileRoot);
        Directory.CreateDirectory(AcpDirectory);
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(workspaceDir);

        // Ensure no global hooks or MCP server configs exist inside NIVO's private GEMINI_HOME.
        foreach (string name in new[] { "hooks.json", "mcp_config.json" })
        {
            string file = Path.Combine(configDir, name);
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        // Disable the built-in swarm plugin in config/config.json before agy's plugin migration can enable it.
        var configPayload = new
        {
            plugins = new
            {
                swarm = new { enabled = false }
            }
        };
        WriteJsonAtomic(Path.Combine(configDir, "config.json"), configPayload, runId);

        // Write deny-all settings.json atomically: Deny > Ask > Allow precedence blocks all tools before execution.
        var settingsPayload = new
        {
            auth = new { type = AuthMethodPersonal },
            agentMode = "",
            toolPermission = "strict",
            allowNonWorkspaceAccess = false,
            enableTerminalSandbox = false,
            includeDefaultCustomizations = false,
            permissions = new
            {
                allow = Array.Empty<string>(),
                ask = Array.Empty<string>(),
                deny = HardDenyRules
            }
        };
        WriteJsonAtomic(Path.Combine(AcpDirectory, "settings.json"), settingsPayload, runId);

        return new IsolatedRunScope(ProfileRoot, tempDir, workspaceDir);
    }

    private static void WriteJsonAtomic(string destinationPath, object payload, string runId)
    {
        string tmpPath = destinationPath + "." + runId + ".tmp";
        try
        {
            File.WriteAllText(
                tmpPath,
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }) + "\n",
                new UTF8Encoding(false));
            File.Move(tmpPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); }
                catch (IOException ex) { Trace.TraceWarning("Antigravity temporary config cleanup failed ({0}).", ex.GetType().Name); }
            }
        }
    }

    private AcpClient StartAcpClient(IsolatedRunScope scope, Action<string>? onAuthUrl, string? executableOverride = null)
    {
        string executable = executableOverride ?? FindExecutable(store.Data.Settings.AntigravityCliPath);
        var psi = CreateAcpProcessStartInfo(executable, scope, extraArgs: null);
        var process = Process.Start(psi) ?? throw new InvalidOperationException("Cannot start Antigravity ACP server.");
        return new AcpClient(process, onAuthUrl);
    }

    private static ProcessStartInfo CreateAcpProcessStartInfo(string executable, IsolatedRunScope scope, string[]? extraArgs)
    {
        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = scope.WorkspaceDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // Exact T3 isolation pattern: --gemini-dir takes the absolute isolated root; --app-data-dir MUST be relative.
        psi.ArgumentList.Add("acp");
        psi.ArgumentList.Add("--gemini-dir");
        psi.ArgumentList.Add(scope.ProfileRoot);
        psi.ArgumentList.Add("--app-data-dir");
        psi.ArgumentList.Add(AppDataDirName);
        psi.ArgumentList.Add("--include-default-customizations=false");
        psi.ArgumentList.Add("--disable-telemetry");
        if (extraArgs != null)
        {
            foreach (string arg in extraArgs)
                psi.ArgumentList.Add(arg);
        }

        // Strip all provider/auth/gateway/permission/display overrides before building our verified isolated environment.
        foreach (string key in psi.Environment.Keys.Where(ShouldStripEnvironmentKey).ToArray())
            psi.Environment.Remove(key);

        psi.Environment["GEMINI_HOME"] = scope.ProfileRoot;
        psi.Environment["AGY_CLI_DISABLE_AUTO_UPDATE"] = "1";
        psi.Environment["TEMP"] = scope.TempDir;
        psi.Environment["TMP"] = scope.TempDir;
        // agy 1.5.13 selects file-based token storage inside AcpDirectory when SSH_CONNECTION is set, avoiding OS Credential Manager.
        psi.Environment["SSH_CONNECTION"] = SshIsolatedConnection;

        return psi;
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

    private static string FormatSessionFailure(AcpRpcException ex)
    {
        if (ex.Code == -32000 ||
            ex.Message.Contains("Authentication required", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("No browser is available here", StringComparison.OrdinalIgnoreCase))
            return "Antigravity is not signed in. Click 'Connect account' to sign in with Google.";
        if (ex.Message.Contains("SUBSCRIPTION_REQUIRED", StringComparison.OrdinalIgnoreCase))
            return "Google requires an eligible Antigravity subscription for this account.";
        return "Antigravity could not start a session: " + ex.Message;
    }

    private static string FormatAuthFailure(AcpRpcException ex)
    {
        if (ex.Message.Contains("SUBSCRIPTION_REQUIRED", StringComparison.OrdinalIgnoreCase))
            return "Google requires an eligible Antigravity subscription for this account.";
        if (ex.Message.Contains("access_denied", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("denied access", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
            return "Google sign-in was not approved. Click 'Connect account' to try again.";
        if (ex.Code == -32603 && ex.Method == "session/new")
            return "Antigravity signed in, but could not open a session or load models.";
        return "Antigravity Google sign-in failed: " + ex.Message;
    }

    private static string FormatPromptFailure(string model, AcpRpcException ex)
    {
        if (ex.Code == -32000 ||
            ex.Message.Contains("Authentication required", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("No browser is available here", StringComparison.OrdinalIgnoreCase))
            return "Antigravity sign-in expired. Reconnect your Google account in Settings.";
        if (ex.Message.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            return $"Antigravity quota or rate limit reached for {model}. Try again later or switch models.";
        return $"Antigravity ({model}) failed: {ex.Message}";
    }

    private static string? ReadString(JsonElement obj, string property) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(property, out var prop) &&
        prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256HexRegex();

    [GeneratedRegex(@"^[\w./~-]{1,2048}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodeTokenRegex();

    private readonly record struct CodeAuthPending(string AuthorizationUrl, string RedirectUri, string State);

    private sealed class IsolatedRunScope(string profileRoot, string tempDir, string workspaceDir) : IDisposable
    {
        public string ProfileRoot { get; } = profileRoot;
        public string TempDir { get; } = tempDir;
        public string WorkspaceDir { get; } = workspaceDir;

        public void Dispose()
        {
            TryDeleteDirectory(TempDir);
            TryDeleteDirectory(WorkspaceDir);
        }

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Trace.TraceWarning("Antigravity temporary directory cleanup failed ({0}).", ex.GetType().Name); }
        }
    }

    private sealed record InitializeSummary(
        bool SupportsImageInput,
        bool SupportsAudioInput,
        bool SupportsSystemPromptAppend,
        bool SupportsLogout,
        HashSet<string> AuthMethods)
    {
        public bool HasAuthMethod(string id) => AuthMethods.Contains(id);
    }

    private sealed class AcpRpcException(string method, int code, string message) : InvalidOperationException(message)
    {
        public string Method { get; } = method;
        public int Code { get; } = code;
    }

    private sealed class AcpClient : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Action<string>? onAuthUrl;
        private readonly SemaphoreSlim writeLock = new(1, 1);
        private readonly ConcurrentDictionary<int, (string Method, TaskCompletionSource<JsonElement> Tcs)> pending = new();
        private readonly CancellationTokenSource ioCts = new();
        private readonly StringBuilder stderrTail = new();
        private readonly Task stdoutLoop;
        private readonly Task stderrLoop;
        private Action<JsonElement>? sessionUpdateHandler;
        private int nextId;
        private int deniedToolRequests;
        private int disposed;
        private Exception? connectionError;

        public int DeniedToolRequestCount => Volatile.Read(ref deniedToolRequests);

        public AcpClient(Process process, Action<string>? onAuthUrl)
        {
            this.process = process;
            this.onAuthUrl = onAuthUrl;
            stdoutLoop = Task.Run(ReadStdoutLoopAsync);
            stderrLoop = Task.Run(ReadStderrLoopAsync);
        }

        public void SetSessionUpdateHandler(Action<JsonElement>? handler) =>
            Volatile.Write(ref sessionUpdateHandler, handler);

        public async Task<InitializeSummary> InitializeAsync(CancellationToken ct)
        {
            var result = await RequestAsync("initialize", new
            {
                protocolVersion = 1,
                clientCapabilities = new
                {
                    fs = new { readTextFile = false, writeTextFile = false },
                    terminal = false
                },
                clientInfo = new
                {
                    name = "nivo-local",
                    title = "NIVO Local",
                    version = typeof(AntigravityAccountService).Assembly.GetName().Version?.ToString(3) ?? "1.6.0"
                }
            }, ct);

            bool imageInput = false;
            bool audioInput = false;
            bool systemPromptAppend = false;
            bool supportsLogout = false;

            if (result.TryGetProperty("agentCapabilities", out var caps) && caps.ValueKind == JsonValueKind.Object)
            {
                if (caps.TryGetProperty("promptCapabilities", out var promptCaps) && promptCaps.ValueKind == JsonValueKind.Object)
                {
                    imageInput = promptCaps.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.True;
                    audioInput = promptCaps.TryGetProperty("audio", out var aud) && aud.ValueKind == JsonValueKind.True;
                }
                if (caps.TryGetProperty("auth", out var authCaps) && authCaps.ValueKind == JsonValueKind.Object &&
                    authCaps.TryGetProperty("logout", out var logoutCaps) && logoutCaps.ValueKind != JsonValueKind.Null && logoutCaps.ValueKind != JsonValueKind.False)
                {
                    supportsLogout = true;
                }
                if (caps.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object &&
                    meta.TryGetProperty("antigravity", out var agyMeta) && agyMeta.ValueKind == JsonValueKind.Object &&
                    agyMeta.TryGetProperty("systemPrompt", out var spMeta) && spMeta.ValueKind == JsonValueKind.Object &&
                    spMeta.TryGetProperty("append", out var appendEl) && appendEl.ValueKind == JsonValueKind.True)
                {
                    systemPromptAppend = true;
                }
            }

            var authMethods = new HashSet<string>(StringComparer.Ordinal);
            if (result.TryGetProperty("authMethods", out var methods) && methods.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in methods.EnumerateArray())
                {
                    if (ReadString(entry, "id") is { Length: > 0 } id)
                        authMethods.Add(id);
                }
            }

            return new InitializeSummary(imageInput, audioInput, systemPromptAppend, supportsLogout, authMethods);
        }

        public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken ct)
        {
            int id = Interlocked.Increment(ref nextId);
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = (method, tcs);
            if (Volatile.Read(ref connectionError) is { } closedError)
            {
                pending.TryRemove(id, out _);
                throw new InvalidOperationException(closedError.Message, closedError);
            }

            try
            {
                await WriteJsonLineAsync(new
                {
                    jsonrpc = "2.0",
                    id,
                    method,
                    @params = parameters ?? new { }
                }, ct);

                using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
                return await tcs.Task;
            }
            catch
            {
                pending.TryRemove(id, out _);
                throw;
            }
        }

        public async Task TryCancelSessionAsync(string sessionId)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await WriteJsonLineAsync(new
                {
                    jsonrpc = "2.0",
                    method = "session/cancel",
                    @params = new { sessionId }
                }, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or TimeoutException or AcpRpcException or System.ComponentModel.Win32Exception) { Trace.TraceInformation("Antigravity transport cleanup failed ({0}).", ex.GetType().Name); }
        }

        public async Task TryCloseSessionAsync(string sessionId)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await RequestAsync("session/close", new { sessionId }, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or TimeoutException or AcpRpcException or System.ComponentModel.Win32Exception) { Trace.TraceInformation("Antigravity transport cleanup failed ({0}).", ex.GetType().Name); }
        }

        private async Task WriteJsonLineAsync(object payload, CancellationToken ct)
        {
            string line = JsonSerializer.Serialize(payload);
            await writeLock.WaitAsync(ct);
            try
            {
                await process.StandardInput.WriteLineAsync(line.AsMemory(), ct);
                await process.StandardInput.FlushAsync(ct);
            }
            finally
            {
                writeLock.Release();
            }
        }

        private void DispatchAuthUrlOrFault(string url)
        {
            if (onAuthUrl == null)
            {
                FaultAllPending(new InvalidOperationException("Antigravity is not signed in. Click 'Connect account' to sign in with Google."));
                return;
            }

            try
            {
                onAuthUrl(url);
            }
            catch (Exception ex)
            {
                FaultAllPending(ex);
                throw;
            }
        }

        private async Task ReadStdoutLoopAsync()
        {
            try
            {
                while (!ioCts.IsCancellationRequested)
                {
                    string? line = await process.StandardOutput.ReadLineAsync(ioCts.Token);
                    if (line == null) break;
                    if (line.Length == 0) continue;
                    if (line.Length > MaxLineChars)
                    {
                        FaultAllPending(new InvalidDataException("Antigravity ACP returned an oversized message."));
                        return;
                    }

                    if (TryExtractAuthLineUrl(line, out string authUrl))
                    {
                        DispatchAuthUrlOrFault(authUrl);
                        continue;
                    }

                    JsonDocument doc;
                    try { doc = JsonDocument.Parse(line); }
                    catch (JsonException) { continue; }

                    using (doc)
                    {
                        var root = doc.RootElement;
                        if (root.ValueKind != JsonValueKind.Object) continue;

                        bool hasId = root.TryGetProperty("id", out var idEl) && idEl.ValueKind is JsonValueKind.Number or JsonValueKind.String;
                        bool hasMethod = root.TryGetProperty("method", out var methodEl) && methodEl.ValueKind == JsonValueKind.String;

                        if (hasId && !hasMethod)
                        {
                            HandleResponse(idEl, root);
                        }
                        else if (hasId && hasMethod)
                        {
                            await HandleServerRequestAsync(idEl.Clone(), methodEl.GetString() ?? "");
                        }
                        else if (hasMethod && string.Equals(methodEl.GetString(), "session/update", StringComparison.Ordinal))
                        {
                            if (root.TryGetProperty("params", out var paramsEl))
                                Volatile.Read(ref sessionUpdateHandler)?.Invoke(paramsEl);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ioCts.IsCancellationRequested) { }
            catch (Exception ex)
            {
                FaultAllPending(ex is InvalidDataException or InvalidOperationException ? ex : new InvalidOperationException("Antigravity ACP stream failed: " + ex.Message, ex));
                return;
            }

            string errDetail = BuildExitMessage();
            FaultAllPending(new InvalidOperationException(errDetail));
        }

        private void HandleResponse(JsonElement idEl, JsonElement root)
        {
            if (idEl.ValueKind != JsonValueKind.Number || !idEl.TryGetInt32(out int id)) return;
            if (!pending.TryRemove(id, out var entry)) return;

            if (root.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.Object)
            {
                int code = errorEl.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number && codeEl.TryGetInt32(out int c) ? c : -32603;
                string message = ErrorAwarenessService.RedactSensitiveTokens(ReadString(errorEl, "message") ?? "Antigravity ACP returned an error.");
                if (message.Length > 1200) message = message[..1200];
                entry.Tcs.TrySetException(new AcpRpcException(entry.Method, code, message));
                return;
            }

            if (root.TryGetProperty("result", out var resultEl))
                entry.Tcs.TrySetResult(resultEl.Clone());
            else
                entry.Tcs.TrySetResult(default);
        }

        private async Task HandleServerRequestAsync(JsonElement rawId, string method)
        {
            Interlocked.Increment(ref deniedToolRequests);
            object idValue = rawId.ValueKind == JsonValueKind.Number && rawId.TryGetInt64(out long n)
                ? n
                : rawId.ValueKind == JsonValueKind.String ? rawId.GetString() ?? "" : rawId;

            try
            {
                if (string.Equals(method, "session/request_permission", StringComparison.Ordinal))
                {
                    await WriteJsonLineAsync(new
                    {
                        jsonrpc = "2.0",
                        id = idValue,
                        result = new { outcome = new { outcome = "cancelled" } }
                    }, ioCts.Token);
                    return;
                }

                await WriteJsonLineAsync(new
                {
                    jsonrpc = "2.0",
                    id = idValue,
                    error = new
                    {
                        code = -32601,
                        message = "Tools and interactive client capabilities are disabled in NIVO."
                    }
                }, ioCts.Token);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or TimeoutException or AcpRpcException or System.ComponentModel.Win32Exception) { Trace.TraceInformation("Antigravity transport cleanup failed ({0}).", ex.GetType().Name); }
        }

        private async Task ReadStderrLoopAsync()
        {
            try
            {
                while (!ioCts.IsCancellationRequested)
                {
                    string? line = await process.StandardError.ReadLineAsync(ioCts.Token);
                    if (line == null) break;
                    if (TryExtractAuthLineUrl(line, out string authUrl))
                    {
                        DispatchAuthUrlOrFault(authUrl);
                        continue;
                    }
                    lock (stderrTail)
                    {
                        if (stderrTail.Length < 1200)
                        {
                            if (stderrTail.Length > 0) stderrTail.AppendLine();
                            stderrTail.Append(line.Length > 300 ? line[..300] : line);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ioCts.IsCancellationRequested) { }
            catch (Exception ex) when ((ex is IOException or ObjectDisposedException) && (ioCts.IsCancellationRequested || Volatile.Read(ref disposed) != 0)) { }
            catch (Exception ex)
            {
                FaultAllPending(ex);
            }
        }

        private string BuildExitMessage()
        {
            string stderr;
            lock (stderrTail) stderr = ErrorAwarenessService.RedactSensitiveTokens(stderrTail.ToString().Trim());
            if (stderr.Contains("unknown command", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("Usage of agy", StringComparison.OrdinalIgnoreCase))
                return "This Antigravity CLI version does not support 'agy acp'. Update agy to v1.5+ or set the agy.exe path in Settings.";
            if (stderr.Length > 0)
                return "Antigravity ACP server exited unexpectedly: " + stderr;
            return "Antigravity ACP server closed the connection unexpectedly.";
        }

        private void FaultAllPending(Exception ex)
        {
            Interlocked.CompareExchange(ref connectionError, ex, null);
            foreach (var key in pending.Keys.ToArray())
            {
                if (pending.TryRemove(key, out var entry))
                    entry.Tcs.TrySetException(ex);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { ioCts.Cancel(); } catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or TimeoutException or AcpRpcException or System.ComponentModel.Win32Exception) { Trace.TraceInformation("Antigravity transport cleanup failed ({0}).", ex.GetType().Name); }
            try { process.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or TimeoutException or AcpRpcException or System.ComponentModel.Win32Exception) { Trace.TraceInformation("Antigravity transport cleanup failed ({0}).", ex.GetType().Name); }
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { Trace.TraceWarning("Antigravity process termination failed ({0}).", ex.GetType().Name); }

            try
            {
                if (!process.HasExited)
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or TimeoutException or AcpRpcException or System.ComponentModel.Win32Exception) { Trace.TraceInformation("Antigravity transport cleanup failed ({0}).", ex.GetType().Name); }

            try { await Task.WhenAll(stdoutLoop, stderrLoop).WaitAsync(TimeSpan.FromSeconds(1)); } catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or TimeoutException or AcpRpcException or System.ComponentModel.Win32Exception) { Trace.TraceInformation("Antigravity transport cleanup failed ({0}).", ex.GetType().Name); }

            FaultAllPending(new ObjectDisposedException(nameof(AcpClient)));
            process.Dispose();
            ioCts.Dispose();
            writeLock.Dispose();
        }
    }
}
