using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Nivo.Core;

namespace Nivo.Services;

public sealed partial class CodexAccountService
{
    private const int MaxStdoutLineChars = 4_000_000;
    private const int MaxAnswerChars = 200_000;
    private const int MaxStderrChars = 4_096;
    private const int MaxModelPages = 50;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan DefaultRpcTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LoginCeilingTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TurnCeilingTimeout = TimeSpan.FromMinutes(5);

    private static readonly string[] AllowedAuthHosts = ["auth.openai.com", "chatgpt.com"];

    private static readonly string[] SessionConfigOverrides =
    [
        "cli_auth_credentials_store=\"file\"",
        "forced_login_method=\"chatgpt\"",
        "sandbox_mode=\"read-only\"",
        "approval_policy=\"on-request\"",
        "approvals_reviewer=\"user\"",
        "web_search=\"disabled\"",
        "allow_login_shell=false",
        "check_for_update_on_startup=false",
        "analytics.enabled=false",
        "history.persistence=\"none\"",
        "project_doc_max_bytes=0",
        "project_root_markers=[]",
        "suppress_unstable_features_warning=true",
        "include_apps_instructions=false",
        "include_collaboration_mode_instructions=false",
        "include_environment_context=false",
        "include_permissions_instructions=false",
        "default_permissions=\"nivo_deny\"",
        "shell_environment_policy.inherit=\"none\"",
        "mcp_servers={}",
        "computer_use.default_app_access=\"deny\"",
        "browser_use.default_origin_policy.access=\"deny\"",
        "apps._default.enabled=false",
        "apps._default.destructive_enabled=false",
        "apps._default.open_world_enabled=false",
        "features.shell_tool=false",
        "features.unified_exec=false",
        "features.unified_exec_tty=false",
        "features.shell_snapshot=false",
        "features.view_image=false",
        "features.apps=false",
        "features.plugins=false",
        "features.remote_plugin=false",
        "features.plugin_discovery_prefetch=false",
        "features.plugin_sharing=false",
        "features.hooks=false",
        "features.multi_agent=false",
        "features.multi_agent_v2=false",
        "features.computer_use=false",
        "features.browser_use=false",
        "features.browser_use_external=false",
        "features.browser_use_full_cdp_access=false",
        "features.browser_annotation_api=false",
        "features.image_generation=false",
        "features.sleep_tool=false",
        "features.skill_search=false",
        "features.skill_mcp_dependency_install=false",
        "features.tool_suggest=false",
        "features.tool_call_mcp_elicitation=false",
        "features.code_mode=false",
        "features.code_mode_host=false",
        "features.code_mode_only=false",
        "features.goals=false",
        "features.guardian_approval=false",
        "features.daemon_auto_start=false",
        "features.in_app_browser=false",
        "features.in_app_local_automation=false",
        "features.in_app_chat=false",
        "features.in_app_dictation=false",
        "features.in_app_updates=false",
        "features.in_app_voice=false",
        "features.memories=false",
        "features.skip_host_skill_discovery=true",
        "features.incremental_tools=false"
    ];

    private const string IsolatedConfigToml = """
        cli_auth_credentials_store = "file"
        forced_login_method = "chatgpt"
        sandbox_mode = "read-only"
        approval_policy = "on-request"
        approvals_reviewer = "user"
        web_search = "disabled"
        allow_login_shell = false
        check_for_update_on_startup = false
        project_doc_max_bytes = 0
        project_root_markers = []
        suppress_unstable_features_warning = true
        include_apps_instructions = false
        include_collaboration_mode_instructions = false
        include_environment_context = false
        include_permissions_instructions = false
        default_permissions = "nivo_deny"

        [analytics]
        enabled = false

        [history]
        persistence = "none"

        [shell_environment_policy]
        inherit = "none"

        [permissions.nivo_deny.network]
        enabled = false

        [permissions.nivo_deny.filesystem]
        ":root" = "none"

        [computer_use]
        default_app_access = "deny"

        [browser_use.default_origin_policy]
        access = "deny"

        [apps._default]
        enabled = false
        destructive_enabled = false
        open_world_enabled = false

        [mcp_servers]

        [features]
        shell_tool = false
        unified_exec = false
        unified_exec_tty = false
        shell_snapshot = false
        view_image = false
        apps = false
        plugins = false
        remote_plugin = false
        plugin_discovery_prefetch = false
        plugin_sharing = false
        hooks = false
        multi_agent = false
        multi_agent_v2 = false
        computer_use = false
        browser_use = false
        browser_use_external = false
        browser_use_full_cdp_access = false
        browser_annotation_api = false
        image_generation = false
        sleep_tool = false
        skill_search = false
        skill_mcp_dependency_install = false
        tool_suggest = false
        tool_call_mcp_elicitation = false
        code_mode = false
        code_mode_host = false
        code_mode_only = false
        goals = false
        guardian_approval = false
        daemon_auto_start = false
        in_app_browser = false
        in_app_local_automation = false
        in_app_chat = false
        in_app_dictation = false
        in_app_updates = false
        in_app_voice = false
        memories = false
        skip_host_skill_discovery = true
        incremental_tools = false
        """;

    private static readonly Regex SecretBearerRegex = new(@"(?i)\bBearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.Compiled);
    private static readonly Regex JwtRegex = new(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b", RegexOptions.Compiled);
    private static readonly Regex ApiKeyRegex = new(@"\bsk-[A-Za-z0-9_-]{12,}\b", RegexOptions.Compiled);
    private static readonly Regex SecretFieldRegex = new(@"(?i)([""']?(?:access_?token|refresh_?token|id_?token|auth_?url|authorization|api_?key|client_secret|code_verifier)[""']?\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^\s,&;}\]]+)", RegexOptions.Compiled);
    private static readonly Regex UrlQuerySecretRegex = new(@"(?i)([?&](?:code|state|token|access_token|refresh_token|id_token|code_challenge|nonce)=)[^&\s""']+", RegexOptions.Compiled);

    private readonly Store store;
    private readonly string? cliPathOverride;

    public CodexAccountService(Store store, string? cliPath = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        cliPathOverride = string.IsNullOrWhiteSpace(cliPath) ? null : cliPath.Trim();
    }

    public string CodexHome => Path.Combine(store.Root, "accounts", "codex");

    public string WorkspaceDirectory => Path.Combine(CodexHome, "workspace");

    public static string FindExecutable(string configured = "")
    {
        configured = (configured ?? "").Trim().Trim('"').Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!File.Exists(configured) || !configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(Loc.T(
                    "Choose the actual codex.exe executable (not a .cmd or .ps1 wrapper).",
                    "اختر ملف codex.exe التنفيذي الفعلي (وليس ملف .cmd أو .ps1)."));
            return Path.GetFullPath(configured);
        }

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var pathCandidates = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().Trim('"').Trim())
            .Where(p => p.Length > 0 && p.IndexOfAny(Path.GetInvalidPathChars()) < 0)
            .Select(p => Path.Combine(p, "codex.exe"));

        var knownCandidates = new[]
        {
            Path.Combine(appData, "npm", "codex.exe"),
            Path.Combine(appData, "npm", "node_modules", "@openai", "codex", "bin", "codex.exe"),
            Path.Combine(localAppData, "Programs", "codex", "codex.exe"),
            Path.Combine(userProfile, ".codex", "bin", "codex.exe")
        };

        return pathCandidates.Concat(knownCandidates).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(Loc.T(
                "Codex CLI (codex.exe) was not found. Install Codex CLI or choose codex.exe in Settings.",
                "لم يتم العثور على Codex CLI (codex.exe). ثبّت Codex CLI أو حدّد مسار codex.exe من الإعدادات."));
    }

    public static bool IsValidAuthUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 4096) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (uri.UserInfo.Length != 0 || !uri.IsDefaultPort) return false;
        return AllowedAuthHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<string> AccountAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(DefaultRpcTimeout);

        await using var session = await StartSessionAsync(linked.Token).ConfigureAwait(false);
        var info = await ReadAccountCoreAsync(session, linked.Token).ConfigureAwait(false);
        return FormatAccountStatus(info);
    }

    public async Task LoginAsync(Action<string> openAuthUrl, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(openAuthUrl);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(LoginCeilingTimeout);
        var token = linked.Token;

        await using var session = await StartSessionAsync(token).ConfigureAwait(false);

        // Register completion tracking BEFORE account/login/start so an immediate
        // account/login/completed notification right after the RPC response is never missed.
        var loginWatcher = session.CreateLoginWatcher();
        string? loginId = null;

        try
        {
            using var startDoc = await session.RequestAsync("account/login/start", new { type = "chatgpt" }, DefaultRpcTimeout, token).ConfigureAwait(false);
            var result = startDoc.RootElement;

            string? responseType = TryGetString(result, "type");
            if (!string.Equals(responseType, "chatgpt", StringComparison.Ordinal))
                throw new InvalidOperationException(Loc.T(
                    "Codex did not start the ChatGPT browser sign-in flow.",
                    "لم يبدأ Codex تدفّق تسجيل الدخول عبر متصفح ChatGPT."));

            loginId = TryGetString(result, "loginId");
            string? authUrl = TryGetString(result, "authUrl");

            if (string.IsNullOrWhiteSpace(loginId) || !IsValidAuthUrl(authUrl))
                throw new InvalidDataException(Loc.T(
                    "Codex returned an invalid or unrecognized sign-in URL.",
                    "أرجع Codex رابط تسجيل دخول غير صالح أو غير موثوق."));

            try
            {
                openAuthUrl(authUrl!.Trim());
            }
            catch
            {
                await TryCancelLoginAsync(session, loginId).ConfigureAwait(false);
                throw;
            }

            LoginCompletionEvent completion;
            try
            {
                completion = await loginWatcher.WaitAsync(loginId, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await TryCancelLoginAsync(session, loginId).ConfigureAwait(false);
                throw;
            }

            if (!completion.Success)
            {
                string detail = SanitizeDiagnostic(completion.Error);
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                    ? Loc.T("Codex sign-in did not complete.", "لم يكتمل تسجيل الدخول إلى Codex.")
                    : Loc.T("Codex sign-in failed: ", "فشل تسجيل الدخول إلى Codex: ") + detail);
            }

            var account = await ReadAccountCoreAsync(session, token).ConfigureAwait(false);
            EnsureChatGptAccount(account);
        }
        finally
        {
            session.RemoveLoginWatcher(loginWatcher);
        }
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(DefaultRpcTimeout);

        await using var session = await StartSessionAsync(linked.Token).ConfigureAwait(false);
        using var _ = await session.RequestAsync("account/logout", new { }, DefaultRpcTimeout, linked.Token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AccountModel>> ModelsAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(60));
        var token = linked.Token;

        await using var session = await StartSessionAsync(token).ConfigureAwait(false);
        var account = await ReadAccountCoreAsync(session, token).ConfigureAwait(false);
        EnsureChatGptAccount(account);

        var models = new List<AccountModel>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;

        for (int page = 0; page < MaxModelPages; page++)
        {
            object requestParams = cursor == null
                ? new { includeHidden = false }
                : new { cursor, includeHidden = false };

            using var pageDoc = await session.RequestAsync("model/list", requestParams, DefaultRpcTimeout, token).ConfigureAwait(false);
            var root = pageDoc.RootElement;

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (item.TryGetProperty("hidden", out var hiddenProp) && hiddenProp.ValueKind == JsonValueKind.True) continue;

                    string bareId = (TryGetString(item, "model") ?? TryGetString(item, "id") ?? "").Trim();
                    if (!IsValidBareModelId(bareId) || !seenIds.Add(bareId)) continue;

                    string displayName = (TryGetString(item, "displayName") ?? "").Trim();
                    if (displayName.Length == 0) displayName = bareId;

                    // Only advertise ImageInput when the protocol explicitly returns "image" in inputModalities.
                    // Missing or unknown inputModalities MUST remain false.
                    bool imageInput = HasExplicitImageModality(item);

                    bool isDefault = item.TryGetProperty("isDefault", out var defaultValue) && defaultValue.ValueKind == JsonValueKind.True;
                    models.Add(new AccountModel(bareId, displayName, imageInput, isDefault));
                }
            }

            string? nextCursor = TryGetString(root, "nextCursor");
            if (string.IsNullOrWhiteSpace(nextCursor) || !seenCursors.Add(nextCursor))
                break;

            cursor = nextCursor;
        }

        return models;
    }

    public async Task<string> AskAsync(string model, string prompt, string system, string? image, Action<string>? onText, CancellationToken ct)
    {
        string bareModel = NormalizeBareModelId(model);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidDataException(Loc.T("Prompt cannot be empty.", "لا يمكن أن يكون الطلب فارغًا."));

        string? fullImagePath = null;
        if (!string.IsNullOrWhiteSpace(image))
        {
            string candidate = image.Trim();
            if (!File.Exists(candidate))
                throw new FileNotFoundException(Loc.T("Attached image file was not found.", "لم يتم العثور على ملف الصورة المرفق."), candidate);
            fullImagePath = Path.GetFullPath(candidate);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TurnCeilingTimeout);
        var token = linked.Token;

        await using var session = await StartSessionAsync(token).ConfigureAwait(false);
        var account = await ReadAccountCoreAsync(session, token).ConfigureAwait(false);
        EnsureChatGptAccount(account);

        var threadStartParams = new Dictionary<string, object?>
        {
            ["model"] = bareModel,
            ["cwd"] = WorkspaceDirectory,
            ["approvalPolicy"] = "on-request",
            ["approvalsReviewer"] = "user",
            ["sandbox"] = "read-only",
            ["ephemeral"] = true,
            ["developerInstructions"] = BuildDeveloperInstructions(system),
            ["config"] = BuildThreadConfigOverrides()
        };

        using var threadDoc = await session.RequestAsync("thread/start", threadStartParams, DefaultRpcTimeout, token).ConfigureAwait(false);
        string? threadId = null;
        if (threadDoc.RootElement.TryGetProperty("thread", out var threadObj) && threadObj.ValueKind == JsonValueKind.Object)
            threadId = TryGetString(threadObj, "id");

        if (string.IsNullOrWhiteSpace(threadId))
            throw new InvalidDataException(Loc.T("Codex did not return a thread identifier.", "لم يُرجع Codex معرّف المحادثة."));

        // Register turn event capture BEFORE turn/start to avoid losing immediate deltas/completion notifications.
        var turnWatcher = session.CreateTurnWatcher(threadId, onText);
        string? turnId = null;

        try
        {
            var inputs = new List<object>
            {
                new { type = "text", text = prompt }
            };
            if (fullImagePath != null)
                inputs.Add(new { type = "localImage", path = fullImagePath });

            var turnStartParams = new Dictionary<string, object?>
            {
                ["threadId"] = threadId,
                ["model"] = bareModel,
                ["cwd"] = WorkspaceDirectory,
                ["approvalPolicy"] = "on-request",
                ["approvalsReviewer"] = "user",
                ["sandboxPolicy"] = new { type = "readOnly", networkAccess = false },
                ["input"] = inputs
            };

            using var turnDoc = await session.RequestAsync("turn/start", turnStartParams, DefaultRpcTimeout, token).ConfigureAwait(false);
            if (turnDoc.RootElement.TryGetProperty("turn", out var turnObj) && turnObj.ValueKind == JsonValueKind.Object)
                turnId = TryGetString(turnObj, "id");

            if (string.IsNullOrWhiteSpace(turnId))
                throw new InvalidDataException(Loc.T("Codex did not return a turn identifier.", "لم يُرجع Codex معرّف الدور."));

            turnWatcher.BindTurnId(turnId);

            try
            {
                string answer = await turnWatcher.WaitAsync(token).ConfigureAwait(false);
                if (answer.Length == 0)
                    throw new InvalidOperationException(Loc.T(
                        $"Codex ({bareModel}) completed without returning an answer.",
                        $"انتهى Codex ({bareModel}) دون إرجاع رد."));
                return answer;
            }
            catch (OperationCanceledException)
            {
                await TryInterruptTurnAsync(session, threadId, turnId).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            session.RemoveTurnWatcher(turnWatcher);
        }
    }

    private string ResolveCliPath()
    {
        string configured = cliPathOverride ?? store.Data.Settings.CodexCliPath ?? "";
        return FindExecutable(configured);
    }

    private void PrepareIsolatedHome()
    {
        Directory.CreateDirectory(CodexHome);
        Directory.CreateDirectory(WorkspaceDirectory);

        string configPath = Path.Combine(CodexHome, "config.toml");
        string tempPath = configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, IsolatedConfigToml, Utf8NoBom);
            File.Move(tempPath, configPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Trace.TraceInformation("NIVO Codex temporary config cleanup skipped ({0}).", ex.GetType().Name); }
        }
    }

    private ProcessStartInfo CreateStartInfo()
    {
        PrepareIsolatedHome();
        string exe = ResolveCliPath();

        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = WorkspaceDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom
        };

        info.ArgumentList.Add("app-server");
        info.ArgumentList.Add("--listen");
        info.ArgumentList.Add("stdio://");
        foreach (string item in SessionConfigOverrides)
        {
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(item);
        }

        // Clear inherited API keys and Codex/OpenAI overrides so isolated ChatGPT account
        // sessions can never silently bill an inherited OPENAI_API_KEY or read ~/.codex.
        foreach (string key in info.Environment.Keys.Where(ShouldStripEnvironmentVariable).ToArray())
            info.Environment.Remove(key);

        info.Environment["CODEX_HOME"] = CodexHome;
        return info;
    }

    private static bool ShouldStripEnvironmentVariable(string key) =>
        key.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("CODEX_", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("CHATGPT_", StringComparison.OrdinalIgnoreCase) ||
        key.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase) ||
        key.EndsWith("_ACCESS_TOKEN", StringComparison.OrdinalIgnoreCase) ||
        key.EndsWith("_AUTH_TOKEN", StringComparison.OrdinalIgnoreCase);

    private Task<JsonRpcSession> StartSessionAsync(CancellationToken ct) =>
        StartSessionAsync(experimentalApi: false, ct);

    private async Task<JsonRpcSession> StartSessionAsync(bool experimentalApi, CancellationToken ct)
    {
        var startInfo = CreateStartInfo();
        var session = JsonRpcSession.Start(startInfo);
        try
        {
            string version = typeof(CodexAccountService).Assembly.GetName().Version?.ToString(3) ?? "1.5.0";
            using var _ = await session.RequestAsync("initialize", new
            {
                clientInfo = new
                {
                    name = "nivo-local",
                    title = "NIVO Local",
                    version
                },
                capabilities = new
                {
                    experimentalApi
                }
            }, DefaultRpcTimeout, ct).ConfigureAwait(false);

            await session.NotifyAsync("initialized", null, ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<AccountReadInfo> ReadAccountCoreAsync(JsonRpcSession session, CancellationToken ct)
    {
        using var doc = await session.RequestAsync("account/read", new { refreshToken = false }, DefaultRpcTimeout, ct).ConfigureAwait(false);
        var root = doc.RootElement;

        if (!root.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.Object)
            return new AccountReadInfo(null, null, null, null);

        return new AccountReadInfo(
            TryGetString(account, "type"),
            TryGetString(account, "email"),
            TryGetString(account, "planType"),
            TryGetString(account, "workspaceName"));
    }

    private static void EnsureChatGptAccount(AccountReadInfo account)
    {
        if (string.Equals(account.Type, "chatgpt", StringComparison.Ordinal)) return;

        if (string.IsNullOrWhiteSpace(account.Type))
            throw new InvalidOperationException(Loc.T(
                "Not signed in to Codex with a ChatGPT account. Connect your account in Settings first.",
                "غير مسجّل الدخول إلى Codex بحساب ChatGPT. اربط حسابك من الإعدادات أولًا."));

        throw new InvalidOperationException(Loc.T(
            $"Unsupported Codex authentication mode ({SanitizeLabel(account.Type, 32)}). Sign in with a ChatGPT browser account in Settings.",
            $"وضع مصادقة Codex غير مدعوم ({SanitizeLabel(account.Type, 32)}). سجّل الدخول بحساب ChatGPT من الإعدادات."));
    }

    private static string FormatAccountStatus(AccountReadInfo account)
    {
        if (string.IsNullOrWhiteSpace(account.Type))
            return Loc.T("Not signed in.", "غير مسجّل الدخول.");

        if (!string.Equals(account.Type, "chatgpt", StringComparison.Ordinal))
            return Loc.T(
                "Not signed in with a ChatGPT account.",
                "غير مسجّل الدخول بحساب ChatGPT.");

        string email = SanitizeLabel(account.Email, 120);
        string plan = SanitizeLabel(account.PlanType, 48);
        string workspace = SanitizeLabel(account.WorkspaceName, 80);

        var parts = new List<string>();
        if (email.Length > 0) parts.Add(email);
        if (workspace.Length > 0) parts.Add(workspace);
        if (plan.Length > 0 && !string.Equals(plan, "unknown", StringComparison.OrdinalIgnoreCase)) parts.Add(plan);

        string suffix = parts.Count > 0 ? " · " + string.Join(" · ", parts) : "";
        return Loc.T("Connected (ChatGPT" + suffix + ")", "متصل (ChatGPT" + suffix + ")");
    }

    private static async Task TryCancelLoginAsync(JsonRpcSession session, string? loginId)
    {
        if (string.IsNullOrWhiteSpace(loginId)) return;
        try
        {
            using var cancelCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var _ = await session.RequestAsync("account/login/cancel", new { loginId }, TimeSpan.FromSeconds(2), cancelCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or InvalidOperationException or IOException or ObjectDisposedException)
        {
            Trace.TraceInformation("NIVO Codex login cancel skipped ({0}).", ex.GetType().Name);
        }
    }

    private static async Task TryInterruptTurnAsync(JsonRpcSession session, string threadId, string? turnId)
    {
        if (string.IsNullOrWhiteSpace(turnId)) return;
        try
        {
            using var cancelCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            using var _ = await session.RequestAsync("turn/interrupt", new { threadId, turnId }, TimeSpan.FromMilliseconds(1500), cancelCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or InvalidOperationException or IOException or ObjectDisposedException)
        {
            Trace.TraceInformation("NIVO Codex turn interrupt skipped ({0}).", ex.GetType().Name);
        }
    }

    private static string BuildDeveloperInstructions(string? system)
    {
        const string noToolsGuard = "All Codex built-in tools, shell commands, file access, web search, MCP servers, plugins, and sub-agents are disabled for this session. Do not attempt tool calls; reply directly with text only.";
        if (string.IsNullOrWhiteSpace(system)) return noToolsGuard;
        return system.Trim() + "\n\n" + noToolsGuard;
    }

    private static Dictionary<string, object> BuildThreadConfigOverrides() => new()
    {
        ["sandbox_mode"] = "read-only",
        ["approval_policy"] = "on-request",
        ["approvals_reviewer"] = "user",
        ["web_search"] = "disabled",
        ["allow_login_shell"] = false,
        ["default_permissions"] = "nivo_deny",
        ["include_apps_instructions"] = false,
        ["include_collaboration_mode_instructions"] = false,
        ["include_environment_context"] = false,
        ["include_permissions_instructions"] = false,
        ["mcp_servers"] = new Dictionary<string, object>(),
        ["features"] = new Dictionary<string, object>
        {
            ["shell_tool"] = false,
            ["unified_exec"] = false,
            ["unified_exec_tty"] = false,
            ["shell_snapshot"] = false,
            ["view_image"] = false,
            ["apps"] = false,
            ["plugins"] = false,
            ["remote_plugin"] = false,
            ["hooks"] = false,
            ["multi_agent"] = false,
            ["multi_agent_v2"] = false,
            ["computer_use"] = false,
            ["browser_use"] = false,
            ["image_generation"] = false,
            ["sleep_tool"] = false,
            ["skill_search"] = false,
            ["tool_suggest"] = false,
            ["tool_call_mcp_elicitation"] = false,
            ["code_mode"] = false,
            ["code_mode_host"] = false,
            ["code_mode_only"] = false,
            ["goals"] = false,
            ["guardian_approval"] = false,
            ["memories"] = false,
            ["skip_host_skill_discovery"] = true,
            ["incremental_tools"] = false
        }
    };

    private static string NormalizeBareModelId(string? model)
    {
        string value = (model ?? "").Trim();
        if (value.StartsWith("codex/", StringComparison.Ordinal))
            value = value["codex/".Length..].Trim();

        if (!IsValidBareModelId(value))
            throw new InvalidDataException(Loc.T("Invalid Codex model identifier.", "معرّف موديل Codex غير صالح."));
        return value;
    }

    private static bool IsValidBareModelId(string model)
    {
        if (string.IsNullOrWhiteSpace(model) || model.Length > 200 || !char.IsLetterOrDigit(model[0])) return false;
        foreach (char c in model)
            if (!char.IsLetterOrDigit(c) && c is not ('.' or '_' or '-' or ':' or '@')) return false;
        return true;
    }

    private static bool HasExplicitImageModality(JsonElement modelElement)
    {
        if (!modelElement.TryGetProperty("inputModalities", out var modalities) || modalities.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var entry in modalities.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String &&
                string.Equals(entry.GetString(), "image", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? TryGetString(JsonElement obj, string propertyName) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(propertyName, out var prop) &&
        prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static string SanitizeLabel(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string cleaned = SanitizeDiagnostic(text);
        var sb = new StringBuilder(Math.Min(cleaned.Length, maxLength));
        foreach (char c in cleaned)
        {
            if (char.IsControl(c)) continue;
            sb.Append(c);
            if (sb.Length >= maxLength) break;
        }
        return sb.ToString().Trim();
    }

    private static string SanitizeDiagnostic(string? raw, int maxLength = 600)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string redacted = SecretBearerRegex.Replace(raw, "Bearer [REDACTED]");
        redacted = JwtRegex.Replace(redacted, "[REDACTED_JWT]");
        redacted = ApiKeyRegex.Replace(redacted, "[REDACTED_KEY]");
        redacted = SecretFieldRegex.Replace(redacted, "$1[REDACTED]");
        redacted = UrlQuerySecretRegex.Replace(redacted, "$1[REDACTED]");
        redacted = redacted.Trim();
        return redacted.Length <= maxLength ? redacted : redacted[..maxLength] + "…";
    }

    private sealed record AccountReadInfo(string? Type, string? Email, string? PlanType, string? WorkspaceName);
    internal sealed record LoginCompletionEvent(string? LoginId, bool Success, string? Error);

    internal sealed class LoginWatcher
    {
        private readonly object gate = new();
        private readonly List<LoginCompletionEvent> buffered = [];
        private string? targetLoginId;
        private TaskCompletionSource<LoginCompletionEvent>? waiter;
        private Exception? fault;

        public void OnLoginCompleted(LoginCompletionEvent evt)
        {
            TaskCompletionSource<LoginCompletionEvent>? toComplete = null;
            lock (gate)
            {
                if (waiter != null && Matches(evt, targetLoginId))
                {
                    toComplete = waiter;
                    waiter = null;
                }
                else
                {
                    buffered.Add(evt);
                }
            }
            toComplete?.TrySetResult(evt);
        }

        public void Fail(Exception ex)
        {
            TaskCompletionSource<LoginCompletionEvent>? toFail = null;
            lock (gate)
            {
                fault ??= ex;
                toFail = waiter;
                waiter = null;
            }
            toFail?.TrySetException(ex);
        }

        public async Task<LoginCompletionEvent> WaitAsync(string loginId, CancellationToken ct)
        {
            TaskCompletionSource<LoginCompletionEvent> tcs;
            lock (gate)
            {
                for (int i = 0; i < buffered.Count; i++)
                {
                    if (Matches(buffered[i], loginId))
                    {
                        var hit = buffered[i];
                        buffered.RemoveAt(i);
                        return hit;
                    }
                }
                if (fault != null) throw fault;

                targetLoginId = loginId;
                tcs = new TaskCompletionSource<LoginCompletionEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
                waiter = tcs;
            }

            using var reg = ct.Register(() =>
            {
                lock (gate) { if (ReferenceEquals(waiter, tcs)) waiter = null; }
                tcs.TrySetCanceled(ct);
            });

            return await tcs.Task.ConfigureAwait(false);
        }

        private static bool Matches(LoginCompletionEvent evt, string? expectedLoginId) =>
            string.IsNullOrWhiteSpace(evt.LoginId) ||
            string.IsNullOrWhiteSpace(expectedLoginId) ||
            string.Equals(evt.LoginId, expectedLoginId, StringComparison.Ordinal);
    }

    internal sealed class TurnWatcher
    {
        private readonly object gate = new();
        private readonly string threadId;
        private readonly Action<string>? onText;
        private readonly List<BufferedTurnEvent> preBindEvents = [];
        private readonly StringBuilder streamedAnswer = new();
        private readonly TaskCompletionSource<string> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private string? boundTurnId;
        private string? fallbackCompletedText;
        private string? lastErrorMessage;
        private bool finished;

        public TurnWatcher(string threadId, Action<string>? onText)
        {
            this.threadId = threadId;
            this.onText = onText;
        }

        public string ThreadId => threadId;

        public void OnEvent(BufferedTurnEvent evt)
        {
            List<string>? chunksToEmit = null;
            string? completedAnswer = null;
            Exception? errorToFail = null;

            lock (gate)
            {
                if (finished) return;

                if (boundTurnId == null)
                {
                    if (preBindEvents.Count < 4096) preBindEvents.Add(evt);
                    return;
                }

                ApplyLocked(evt, ref chunksToEmit, ref completedAnswer, ref errorToFail);
            }

            DispatchCallbacks(chunksToEmit, completedAnswer, errorToFail);
        }

        public void BindTurnId(string turnId)
        {
            List<string>? chunksToEmit = null;
            string? completedAnswer = null;
            Exception? errorToFail = null;

            lock (gate)
            {
                if (finished) return;
                boundTurnId = turnId;
                foreach (var evt in preBindEvents)
                {
                    if (finished) break;
                    ApplyLocked(evt, ref chunksToEmit, ref completedAnswer, ref errorToFail);
                }
                preBindEvents.Clear();
            }

            DispatchCallbacks(chunksToEmit, completedAnswer, errorToFail);
        }

        public void Fail(Exception ex)
        {
            lock (gate)
            {
                if (finished) return;
                finished = true;
            }
            completion.TrySetException(ex);
        }

        public async Task<string> WaitAsync(CancellationToken ct)
        {
            using var reg = ct.Register(() =>
            {
                lock (gate) { finished = true; }
                completion.TrySetCanceled(ct);
            });
            return await completion.Task.ConfigureAwait(false);
        }

        private void ApplyLocked(BufferedTurnEvent evt, ref List<string>? chunksToEmit, ref string? completedAnswer, ref Exception? errorToFail)
        {
            if (!string.IsNullOrEmpty(evt.TurnId) && !string.Equals(evt.TurnId, boundTurnId, StringComparison.Ordinal))
                return;

            switch (evt.Kind)
            {
                case TurnEventKind.AgentDelta:
                    if (!string.IsNullOrEmpty(evt.Text))
                    {
                        if (evt.Text.Length > MaxAnswerChars - streamedAnswer.Length)
                        {
                            finished = true;
                            errorToFail = new InvalidDataException(Loc.T("Reply exceeded the application limit.", "تجاوز الرد الحد المسموح به في التطبيق."));
                            return;
                        }
                        streamedAnswer.Append(evt.Text);
                        chunksToEmit ??= [];
                        chunksToEmit.Add(evt.Text);
                    }
                    break;

                case TurnEventKind.AgentItemCompleted:
                    if (!string.IsNullOrEmpty(evt.Text))
                    {
                        if (evt.Text.Length > MaxAnswerChars)
                        {
                            finished = true;
                            errorToFail = new InvalidDataException(Loc.T("Reply exceeded the application limit.", "تجاوز الرد الحد المسموح به في التطبيق."));
                            return;
                        }
                        fallbackCompletedText = evt.Text;
                    }
                    break;

                case TurnEventKind.Error:
                    if (!evt.WillRetry && !string.IsNullOrWhiteSpace(evt.Text))
                        lastErrorMessage = evt.Text;
                    break;

                case TurnEventKind.TurnCompleted:
                    finished = true;
                    if (string.Equals(evt.Status, "completed", StringComparison.Ordinal))
                    {
                        if (streamedAnswer.Length > 0)
                        {
                            completedAnswer = streamedAnswer.ToString();
                        }
                        else
                        {
                            string fallback = !string.IsNullOrEmpty(evt.Text) ? evt.Text : (fallbackCompletedText ?? "");
                            if (fallback.Length > MaxAnswerChars)
                            {
                                errorToFail = new InvalidDataException(Loc.T("Reply exceeded the application limit.", "تجاوز الرد الحد المسموح به في التطبيق."));
                                return;
                            }
                            if (fallback.Length > 0)
                            {
                                streamedAnswer.Append(fallback);
                                chunksToEmit ??= [];
                                chunksToEmit.Add(fallback);
                            }
                            completedAnswer = fallback;
                        }
                    }
                    else if (string.Equals(evt.Status, "interrupted", StringComparison.Ordinal))
                    {
                        string detail = SanitizeDiagnostic(evt.Error ?? lastErrorMessage);
                        errorToFail = new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                            ? Loc.T("Codex interrupted the turn before completion.", "قاطع Codex الدور قبل اكتماله.")
                            : Loc.T("Codex interrupted the turn: ", "قاطع Codex الدور: ") + detail);
                    }
                    else
                    {
                        string detail = SanitizeDiagnostic(evt.Error ?? lastErrorMessage);
                        errorToFail = new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                            ? Loc.T("Codex turn failed.", "فشل دور Codex.")
                            : Loc.T("Codex error: ", "خطأ من Codex: ") + detail);
                    }
                    break;
            }
        }

        private void DispatchCallbacks(List<string>? chunksToEmit, string? completedAnswer, Exception? errorToFail)
        {
            if (chunksToEmit != null && onText != null)
            {
                foreach (string chunk in chunksToEmit)
                {
                    try { onText(chunk); }
                    catch (Exception ex)
                    {
                        lock (gate) { finished = true; }
                        completion.TrySetException(ex);
                        return;
                    }
                }
            }

            if (errorToFail != null)
                completion.TrySetException(errorToFail);
            else if (completedAnswer != null)
                completion.TrySetResult(completedAnswer);
        }
    }

    internal enum TurnEventKind { AgentDelta, AgentItemCompleted, Error, TurnCompleted }

    internal readonly record struct BufferedTurnEvent(
        TurnEventKind Kind,
        string? TurnId,
        string? Text,
        string? Status,
        string? Error,
        bool WillRetry);

    internal sealed class JsonRpcSession : IAsyncDisposable
    {
        internal sealed class NotificationSubscription : IDisposable
        {
            private readonly JsonRpcSession owner;
            private readonly Action<string, JsonElement> onNotification;
            private readonly Action<Exception> onFault;
            private int disposed;

            internal NotificationSubscription(JsonRpcSession owner, Action<string, JsonElement> onNotification, Action<Exception> onFault)
            {
                this.owner = owner;
                this.onNotification = onNotification;
                this.onFault = onFault;
            }

            internal void DispatchNotification(string method, JsonElement parameters)
            {
                if (Volatile.Read(ref disposed) != 0) return;
                try { onNotification(method, parameters); }
                catch (Exception ex)
                {
                    Trace.TraceWarning("NIVO Codex notification handler failed ({0}).", ex.GetType().Name);
                }
            }

            internal void DispatchFault(Exception error)
            {
                if (Volatile.Read(ref disposed) != 0) return;
                try { onFault(error); }
                catch (Exception ex)
                {
                    Trace.TraceWarning("NIVO Codex fault handler failed ({0}).", ex.GetType().Name);
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                    owner.RemoveNotificationSubscription(this);
            }
        }

        private readonly Process process;
        private readonly SemaphoreSlim writeGate = new(1, 1);
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonDocument>> pending = new();
        private readonly object watcherGate = new();
        private readonly List<LoginWatcher> loginWatchers = [];
        private readonly List<TurnWatcher> turnWatchers = [];
        private readonly List<NotificationSubscription> notificationSubscriptions = [];
        private readonly StringBuilder stderrBuffer = new();
        private readonly object stderrGate = new();
        private readonly CancellationTokenSource sessionCts = new();
        private readonly Task stdoutTask;
        private readonly Task stderrTask;

        private Exception? terminalFault;
        private long nextRequestId;
        private int disposed;

        private JsonRpcSession(Process process)
        {
            this.process = process;
            stdoutTask = Task.Run(ReadStdoutLoopAsync);
            stderrTask = Task.Run(ReadStderrLoopAsync);
        }

        public static JsonRpcSession Start(ProcessStartInfo startInfo)
        {
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(Loc.T("Cannot start Codex app-server.", "تعذّر تشغيل خادم Codex."));
            return new JsonRpcSession(process);
        }

        public LoginWatcher CreateLoginWatcher()
        {
            var watcher = new LoginWatcher();
            lock (watcherGate) loginWatchers.Add(watcher);
            return watcher;
        }

        public void RemoveLoginWatcher(LoginWatcher watcher)
        {
            lock (watcherGate) loginWatchers.Remove(watcher);
        }

        public TurnWatcher CreateTurnWatcher(string threadId, Action<string>? onText)
        {
            var watcher = new TurnWatcher(threadId, onText);
            lock (watcherGate) turnWatchers.Add(watcher);
            return watcher;
        }

        public void RemoveTurnWatcher(TurnWatcher watcher)
        {
            lock (watcherGate) turnWatchers.Remove(watcher);
        }

        public NotificationSubscription SubscribeNotifications(Action<string, JsonElement> onNotification, Action<Exception> onFault)
        {
            ArgumentNullException.ThrowIfNull(onNotification);
            ArgumentNullException.ThrowIfNull(onFault);

            var subscription = new NotificationSubscription(this, onNotification, onFault);
            Exception? existingFault;
            lock (watcherGate)
            {
                existingFault = terminalFault;
                if (existingFault == null)
                    notificationSubscriptions.Add(subscription);
            }

            if (existingFault != null)
                subscription.DispatchFault(existingFault);
            return subscription;
        }

        private void RemoveNotificationSubscription(NotificationSubscription subscription)
        {
            lock (watcherGate) notificationSubscriptions.Remove(subscription);
        }

        public async Task<JsonDocument> RequestAsync(string method, object? parameters, TimeSpan timeout, CancellationToken ct)
        {
            long id = Interlocked.Increment(ref nextRequestId);
            var tcs = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!pending.TryAdd(id, tcs))
                throw new InvalidOperationException("Duplicate JSON-RPC request identifier.");

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, sessionCts.Token);
            linked.CancelAfter(timeout);

            try
            {
                await WriteJsonLineAsync(new Dictionary<string, object?>
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["method"] = method,
                    ["params"] = parameters ?? new { }
                }, linked.Token).ConfigureAwait(false);

                using var reg = linked.Token.Register(() =>
                {
                    if (pending.TryRemove(id, out var removed))
                    {
                        if (ct.IsCancellationRequested)
                            removed.TrySetCanceled(ct);
                        else
                            removed.TrySetException(new TimeoutException(Loc.T(
                                $"Codex request '{method}' timed out.",
                                $"انتهت مهلة طلب Codex ({method}).")));
                    }
                });

                return await tcs.Task.ConfigureAwait(false);
            }
            catch
            {
                if (pending.TryRemove(id, out var leftover) && leftover.Task.IsCompletedSuccessfully)
                    leftover.Task.Result.Dispose();
                throw;
            }
        }

        public Task NotifyAsync(string method, object? parameters, CancellationToken ct)
        {
            var payload = new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method
            };
            if (parameters != null) payload["params"] = parameters;
            return WriteJsonLineAsync(payload, ct);
        }

        private async Task WriteJsonLineAsync(object message, CancellationToken ct)
        {
            string json = JsonSerializer.Serialize(message);
            await writeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await process.StandardInput.WriteAsync(json.AsMemory(), ct).ConfigureAwait(false);
                await process.StandardInput.WriteAsync("\n".AsMemory(), ct).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                throw BuildTransportClosedException("Codex connection closed while sending a request.", ex);
            }
            finally
            {
                writeGate.Release();
            }
        }

        private async Task ReadStdoutLoopAsync()
        {
            Exception? terminalError = null;
            try
            {
                while (true)
                {
                    string? line = await process.StandardOutput.ReadLineAsync(sessionCts.Token).ConfigureAwait(false);
                    if (line == null) break;
                    if (line.Length == 0) continue;
                    if (line.Length > MaxStdoutLineChars)
                        throw new InvalidDataException("Codex returned an oversized JSON-RPC message.");

                    HandleIncomingLine(line);
                }
            }
            catch (OperationCanceledException)
            {
                terminalError = new OperationCanceledException();
            }
            catch (Exception ex)
            {
                terminalError = ex;
            }
            finally
            {
                FailAllWaiters(terminalError ?? BuildTransportClosedException("Codex app-server exited before completing the request.", null));
            }
        }

        private async Task ReadStderrLoopAsync()
        {
            try
            {
                char[] chunk = new char[1024];
                while (true)
                {
                    int read = await process.StandardError.ReadAsync(chunk.AsMemory(), sessionCts.Token).ConfigureAwait(false);
                    if (read <= 0) break;

                    lock (stderrGate)
                    {
                        int remaining = MaxStderrChars - stderrBuffer.Length;
                        if (remaining > 0)
                            stderrBuffer.Append(chunk, 0, Math.Min(read, remaining));
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                Trace.TraceInformation("NIVO Codex stderr reader stopped ({0}).", ex.GetType().Name);
            }
        }

        private void HandleIncomingLine(string line)
        {
            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException ex)
            {
                Trace.TraceInformation("NIVO Codex ignored non-JSON stdout line ({0}).", ex.GetType().Name);
                return;
            }

            bool ownershipTransferred = false;
            try
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;

                bool hasMethod = root.TryGetProperty("method", out var methodProp) && methodProp.ValueKind == JsonValueKind.String;
                bool hasId = root.TryGetProperty("id", out var idProp) && idProp.ValueKind is JsonValueKind.Number or JsonValueKind.String;

                if (hasMethod && hasId)
                {
                    // Server-initiated RPC request (approval / tool call / elicitation): always reject immediately.
                    string method = methodProp.GetString() ?? "";
                    JsonElement idClone = idProp.Clone();
                    _ = RejectServerRequestAsync(idClone, method);
                    return;
                }

                if (hasId && !hasMethod)
                {
                    // Response to one of our outgoing JSON-RPC requests.
                    if (TryReadRequestId(idProp, out long id) && pending.TryRemove(id, out var tcs))
                    {
                        if (root.TryGetProperty("error", out var errorObj) && errorObj.ValueKind == JsonValueKind.Object)
                        {
                            string message = TryGetString(errorObj, "message") ?? "Codex returned a JSON-RPC error.";
                            tcs.TrySetException(new InvalidOperationException(SanitizeDiagnostic(message)));
                        }
                        else if (root.TryGetProperty("result", out var resultObj))
                        {
                            var resultDoc = JsonDocument.Parse(resultObj.GetRawText());
                            if (!tcs.TrySetResult(resultDoc))
                                resultDoc.Dispose();
                            ownershipTransferred = false;
                        }
                        else
                        {
                            var emptyDoc = JsonDocument.Parse("{}");
                            if (!tcs.TrySetResult(emptyDoc))
                                emptyDoc.Dispose();
                        }
                    }
                    return;
                }

                if (hasMethod)
                {
                    string method = methodProp.GetString() ?? "";
                    root.TryGetProperty("params", out var paramsElement);
                    HandleNotification(method, paramsElement);
                }
            }
            finally
            {
                if (!ownershipTransferred) doc.Dispose();
            }
        }

        private async Task RejectServerRequestAsync(JsonElement requestId, string method)
        {
            try
            {
                object response = method switch
                {
                    "item/commandExecution/requestApproval" => new Dictionary<string, object?>
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = requestId,
                        ["result"] = new { decision = "cancel" }
                    },
                    "item/fileChange/requestApproval" => new Dictionary<string, object?>
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = requestId,
                        ["result"] = new { decision = "cancel" }
                    },
                    "item/permissions/requestApproval" => new Dictionary<string, object?>
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = requestId,
                        ["result"] = new { permissions = new { }, scope = "turn" }
                    },
                    "item/tool/call" => new Dictionary<string, object?>
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = requestId,
                        ["result"] = new
                        {
                            success = false,
                            contentItems = new[] { new { type = "inputText", text = "Tools are disabled in NIVO." } }
                        }
                    },
                    "mcpServer/elicitation/request" => new Dictionary<string, object?>
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = requestId,
                        ["result"] = new { action = "cancel", content = (object?)null }
                    },
                    "applyPatchApproval" or "execCommandApproval" => new Dictionary<string, object?>
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = requestId,
                        ["result"] = new { decision = "abort" }
                    },
                    _ => new Dictionary<string, object?>
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = requestId,
                        ["error"] = new { code = -32601, message = "Tools and server requests are disabled in NIVO." }
                    }
                };

                await WriteJsonLineAsync(response, sessionCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException or ObjectDisposedException)
            {
                Trace.TraceInformation("NIVO Codex server-request rejection skipped during shutdown ({0}).", ex.GetType().Name);
            }
        }

        private void HandleNotification(string method, JsonElement p)
        {
            if (p.ValueKind != JsonValueKind.Object) return;

            NotificationSubscription[] subscribers;
            lock (watcherGate)
                subscribers = notificationSubscriptions.Count == 0 ? [] : [.. notificationSubscriptions];
            foreach (var sub in subscribers)
                sub.DispatchNotification(method, p);

            switch (method)
            {
                case "account/login/completed":
                {
                    bool success = p.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True;
                    var evt = new LoginCompletionEvent(TryGetString(p, "loginId"), success, TryGetString(p, "error"));
                    LoginWatcher[] snapshot;
                    lock (watcherGate) snapshot = [.. loginWatchers];
                    foreach (var w in snapshot) w.OnLoginCompleted(evt);
                    break;
                }

                case "item/agentMessage/delta":
                {
                    string? threadId = TryGetString(p, "threadId");
                    string? turnId = TryGetString(p, "turnId");
                    string? delta = TryGetString(p, "delta");
                    if (!string.IsNullOrEmpty(threadId) && !string.IsNullOrEmpty(delta))
                        DispatchTurnEvent(threadId, new BufferedTurnEvent(TurnEventKind.AgentDelta, turnId, delta, null, null, false));
                    break;
                }

                case "item/completed":
                {
                    string? threadId = TryGetString(p, "threadId");
                    string? turnId = TryGetString(p, "turnId");
                    if (!string.IsNullOrEmpty(threadId) &&
                        p.TryGetProperty("item", out var item) &&
                        item.ValueKind == JsonValueKind.Object &&
                        string.Equals(TryGetString(item, "type"), "agentMessage", StringComparison.Ordinal))
                    {
                        string? text = TryGetString(item, "text");
                        if (!string.IsNullOrEmpty(text))
                            DispatchTurnEvent(threadId, new BufferedTurnEvent(TurnEventKind.AgentItemCompleted, turnId, text, null, null, false));
                    }
                    break;
                }

                case "error":
                {
                    string? threadId = TryGetString(p, "threadId");
                    string? turnId = TryGetString(p, "turnId");
                    bool willRetry = p.TryGetProperty("willRetry", out var retry) && retry.ValueKind == JsonValueKind.True;
                    string? message = ExtractTurnErrorMessage(p);
                    if (!string.IsNullOrEmpty(threadId) && !string.IsNullOrWhiteSpace(message))
                        DispatchTurnEvent(threadId, new BufferedTurnEvent(TurnEventKind.Error, turnId, message, null, message, willRetry));
                    break;
                }

                case "turn/completed":
                {
                    string? threadId = TryGetString(p, "threadId");
                    if (!string.IsNullOrEmpty(threadId) && p.TryGetProperty("turn", out var turn) && turn.ValueKind == JsonValueKind.Object)
                    {
                        string? turnId = TryGetString(turn, "id");
                        string? status = TryGetString(turn, "status");
                        string? error = ExtractTurnErrorMessage(turn);
                        string? fallbackText = ExtractAgentMessageFromItems(turn);
                        DispatchTurnEvent(threadId, new BufferedTurnEvent(TurnEventKind.TurnCompleted, turnId, fallbackText, status, error, false));
                    }
                    break;
                }
            }
        }

        private void DispatchTurnEvent(string threadId, BufferedTurnEvent evt)
        {
            TurnWatcher[] snapshot;
            lock (watcherGate) snapshot = [.. turnWatchers];
            foreach (var w in snapshot)
            {
                if (string.Equals(w.ThreadId, threadId, StringComparison.Ordinal))
                    w.OnEvent(evt);
            }
        }

        private static string? ExtractTurnErrorMessage(JsonElement container)
        {
            if (!container.TryGetProperty("error", out var err) || err.ValueKind != JsonValueKind.Object)
                return null;

            string? message = TryGetString(err, "message");
            string? details = TryGetString(err, "additionalDetails");
            if (!string.IsNullOrWhiteSpace(message) && !string.IsNullOrWhiteSpace(details))
                return message + " (" + details + ")";
            return !string.IsNullOrWhiteSpace(message) ? message : details;
        }

        private static string? ExtractAgentMessageFromItems(JsonElement turn)
        {
            if (!turn.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return null;

            StringBuilder? sb = null;
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!string.Equals(TryGetString(item, "type"), "agentMessage", StringComparison.Ordinal)) continue;
                string? text = TryGetString(item, "text");
                if (string.IsNullOrEmpty(text)) continue;
                sb ??= new StringBuilder();
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(text);
            }
            return sb?.ToString();
        }

        private static bool TryReadRequestId(JsonElement idProp, out long id)
        {
            if (idProp.ValueKind == JsonValueKind.Number && idProp.TryGetInt64(out id))
                return true;
            if (idProp.ValueKind == JsonValueKind.String && long.TryParse(idProp.GetString(), out id))
                return true;
            id = 0;
            return false;
        }

        private InvalidOperationException BuildTransportClosedException(string prefix, Exception? inner)
        {
            string stderr;
            lock (stderrGate) stderr = SanitizeDiagnostic(stderrBuffer.ToString());
            string message = string.IsNullOrWhiteSpace(stderr) ? prefix : prefix + " " + stderr;
            return inner == null ? new InvalidOperationException(message) : new InvalidOperationException(message, inner);
        }

        private void FailAllWaiters(Exception error)
        {
            foreach (var kv in pending)
            {
                if (pending.TryRemove(kv.Key, out var tcs))
                    tcs.TrySetException(error);
            }

            LoginWatcher[] lSnapshot;
            TurnWatcher[] tSnapshot;
            NotificationSubscription[] nSnapshot;
            lock (watcherGate)
            {
                terminalFault ??= error;
                lSnapshot = [.. loginWatchers];
                tSnapshot = [.. turnWatchers];
                nSnapshot = notificationSubscriptions.Count == 0 ? [] : [.. notificationSubscriptions];
            }
            foreach (var w in lSnapshot) w.Fail(error);
            foreach (var w in tSnapshot) w.Fail(error);
            foreach (var n in nSnapshot) n.DispatchFault(error);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;

            try { process.StandardInput.Close(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            { Trace.TraceInformation("NIVO Codex stdin close skipped ({0}).", ex.GetType().Name); }

            try
            {
                if (!process.HasExited)
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
            { Trace.TraceInformation("NIVO Codex graceful exit wait ended ({0}).", ex.GetType().Name); }

            StopProcess(process);
            sessionCts.Cancel();

            try
            {
                if (!process.HasExited)
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
            { Trace.TraceWarning("NIVO Codex process did not exit within the cleanup deadline ({0}).", ex.GetType().Name); }

            try { await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or InvalidOperationException or ObjectDisposedException or InvalidDataException)
            { Trace.TraceInformation("NIVO Codex stream drain ended ({0}).", ex.GetType().Name); }

            process.Dispose();
            writeGate.Dispose();
            sessionCts.Dispose();
        }

        private static void StopProcess(Process process)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            { Trace.TraceInformation("NIVO Codex process stop unavailable ({0}).", ex.GetType().Name); }
        }
    }
}
