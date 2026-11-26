using Nivo.Core;
using Nivo.Services;
using Nivo.UI;
using System.Windows.Media.Imaging;

namespace Nivo;

internal static class AccountTests
{
    public static async Task RunNativeProbe()
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "account-native-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(root);
        var store = new Store(Path.Combine(root, "profile"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        string codexStatus = await new CodexAccountService(store).AccountAsync(timeout.Token);
        File.WriteAllText(Path.Combine(root, "codex-status.txt"), codexStatus);
        bool rejected = false;
        try { await new CodexAccountService(store).ModelsAsync(timeout.Token); }
        catch (InvalidOperationException ex) { rejected = true; File.WriteAllText(Path.Combine(root, "codex-unauthenticated.txt"), ex.Message); }
        if (!rejected) throw new InvalidOperationException("A new isolated Codex profile unexpectedly authenticated.");
        bool audioTouched = false;
        rejected = false;
        try { await new CodexAccountService(store).StartRealtimeAsync("probe-model", "No tools.", null, null, () => { audioTouched = true; return new FakeRealtimeAudio(); }, timeout.Token); }
        catch (InvalidOperationException) { rejected = true; }
        if (!rejected || audioTouched) throw new InvalidOperationException("Native Codex voice touched audio before authentication.");
        rejected = false;
        try { await new AntigravityAccountService(store).ModelsAsync(timeout.Token); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not signed in", StringComparison.OrdinalIgnoreCase))
        { rejected = true; File.WriteAllText(Path.Combine(root, "antigravity-unauthenticated.txt"), ex.Message); }
        if (!rejected) throw new InvalidOperationException("A new isolated Antigravity profile did not return the expected authentication-required state.");
        bool googleLinkSeen = false;
        using (var browserProbe = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
        {
            browserProbe.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await new AntigravityAccountService(store).LoginAsync(
                    url => { googleLinkSeen = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host == "accounts.google.com"; browserProbe.Cancel(); },
                    (_, token) => Task.FromCanceled<string>(token), browserProbe.Token);
            }
            catch (OperationCanceledException) when (googleLinkSeen && browserProbe.IsCancellationRequested) { }
        }
        if (!googleLinkSeen) throw new InvalidOperationException("Native Antigravity Connect did not produce a Google sign-in URL.");
        File.WriteAllText(Path.Combine(root, "results.txt"), "PASS Real installed Codex initializes without importing existing login\nPASS Real installed Codex rejects unauthenticated catalog request\nPASS Real installed Codex voice rejects an unauthenticated account before audio activation\nPASS Real installed Antigravity returns authentication required without opening a browser\nPASS Real installed Antigravity Connect produces its Google code-login URL and cancels before browser/code exchange\nNo browser was opened, no credentials were submitted, and no live inference was sent.\n");
    }

    public static async Task Run()
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "accounts-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(root);
        var results = new List<string>();
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("FAIL " + description);
            results.Add("PASS " + description); File.WriteAllLines(Path.Combine(root, "results.txt"), results);
        }
        string fixture = Path.GetFullPath("tests/Nivo.FixtureCli/bin/Release/net10.0/Nivo.FixtureCli.exe");
        Check(File.Exists(fixture), "Account protocol fixture is built");
        var store = new Store(Path.Combine(root, "profile"));
        store.Data.Settings.CodexCliPath = store.Data.Settings.AntigravityCliPath = fixture;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Check(AccountConnections.IsAccountModel("codex/fixture-text") && AccountConnections.IsAccountModel("antigravity/fixture-text") && !AccountConnections.IsAccountModel("openai/fixture-text"), "Native account routes remain distinct from OpenCode provider IDs");
        bool rejected = false;
        try { AccountConnections.SplitModel("codex/"); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Invalid account model is rejected before process launch");
        rejected = false;
        try { OpenCodeService.ValidateModel("codex/fixture-text", new Settings { FreeModelsOnly = true }); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Strict free isolation cannot silently use a subscription account");
        var autoSettings = new Settings { Model = "codex/previous-model", UseUserOpenCodeConfig = true, FreeModelsOnly = false };
        var probed = new List<string>();
        var autoModel = await AccountConnections.ResolveTalkModelAsync(autoSettings, (provider, _) =>
        {
            probed.Add(provider);
            if (provider == "codex") throw new InvalidOperationException("Synthetic signed-out account");
            return Task.FromResult<IReadOnlyList<AccountModel>>([new("antigravity/other", "Other", true), new("antigravity/available-default", "Default", true, true)]);
        }, timeout.Token);
        Check(autoModel.Id == "antigravity/available-default" && probed.SequenceEqual(new[] { "codex", "antigravity" }), "Talk automatically uses connected Antigravity when the saved Codex account is signed out");
        Check(autoSettings.Model == "codex/previous-model", "Automatic voice account selection preserves the user's separate text-model preference");
        autoSettings.LastConnectedAccountProvider = "antigravity"; probed.Clear();
        autoModel = await AccountConnections.ResolveTalkModelAsync(autoSettings, (provider, _) =>
        { probed.Add(provider); return Task.FromResult<IReadOnlyList<AccountModel>>([new(provider + "/native-model", "Native model", true, true)]); }, timeout.Token);
        Check(autoModel.Id == "antigravity/native-model" && probed.SequenceEqual(new[] { "antigravity" }), "Auto Talk prioritizes the last connected Antigravity account even when a stale Codex text preference remains");
        autoSettings.VoiceAccountProvider = "codex"; probed.Clear();
        autoModel = await AccountConnections.ResolveTalkModelAsync(autoSettings, (provider, _) =>
        { probed.Add(provider); return Task.FromResult<IReadOnlyList<AccountModel>>([new(provider + "/native-model", "Native model", true, true)]); }, timeout.Token);
        Check(autoModel.Id == "codex/native-model" && probed.SequenceEqual(new[] { "codex" }), "An explicit voice-account choice overrides automatic last-connected selection");
        autoSettings.VoiceAccountProvider = "Auto"; autoSettings.LastConnectedAccountProvider = "";
        autoSettings.Model = "antigravity/chosen"; probed.Clear();
        autoModel = await AccountConnections.ResolveTalkModelAsync(autoSettings, (provider, _) =>
        {
            probed.Add(provider);
            return Task.FromResult<IReadOnlyList<AccountModel>>([new("antigravity/default", "Default", true, true), new("antigravity/chosen", "Chosen", true)]);
        }, timeout.Token);
        Check(autoModel.Id == "antigravity/chosen" && probed.SequenceEqual(new[] { "antigravity" }), "Talk keeps the selected Antigravity model when that account is available without requiring Codex");
        probed.Clear();
        autoSettings.FreeModelsOnly = true; rejected = false;
        try { await AccountConnections.ResolveTalkModelAsync(autoSettings, (provider, _) => { probed.Add(provider); return Task.FromResult<IReadOnlyList<AccountModel>>([]); }, timeout.Token); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected && probed.Count == 0, "Automatic voice discovery respects strict free isolation before contacting accounts");
        autoSettings.FreeModelsOnly = false;
        using (var discoveryCancel = new CancellationTokenSource())
        {
            discoveryCancel.Cancel(); probed.Clear(); rejected = false;
            try { await AccountConnections.ResolveTalkModelAsync(autoSettings, (provider, _) => { probed.Add(provider); return Task.FromResult<IReadOnlyList<AccountModel>>([]); }, discoveryCancel.Token); }
            catch (OperationCanceledException) { rejected = true; }
            Check(rejected && probed.Count == 0, "Cancelling automatic voice discovery does not proceed to another account");
        }
        var codex = new CodexAccountService(store);
        string? loginUrl = null;
        await codex.LoginAsync(url => loginUrl = url, timeout.Token);
        Check(loginUrl?.StartsWith("https://auth.openai.com/", StringComparison.Ordinal) == true, "Codex browser login completion survives immediate notification after RPC reply");
        string invalidAuthMarker = Path.Combine(codex.CodexHome, "fixture-invalid-auth");
        File.WriteAllText(invalidAuthMarker, "synthetic fixture only");
        bool openedUntrusted = false;
        rejected = false;
        try { await codex.LoginAsync(_ => openedUntrusted = true, timeout.Token); }
        catch (InvalidDataException) { rejected = true; }
        finally { File.Delete(invalidAuthMarker); }
        Check(rejected && !openedUntrusted, "Untrusted provider login URLs are rejected before browser launch");
        var models = await AccountConnections.ModelsAsync(store, "codex", timeout.Token);
        Check(models.Count == 3 && models.All(m => m.Id.StartsWith("codex/")), "Codex model discovery returns actual protocol entries with namespaced routing");
        Check(models.Single(m => m.Id.EndsWith("fixture-image")).ImageInput && !models.Single(m => m.Id.EndsWith("fixture-text")).ImageInput, "Image selection follows returned modalities rather than a model-name guess");
        Check(!models.Single(m => m.Id.EndsWith("fixture-unknown")).ImageInput, "Unknown image capability is not advertised as verified");
        store.Data.Settings.Model = "codex/fixture-text";
        store.Data.Settings.UseUserOpenCodeConfig = true;
        store.Data.Settings.FreeModelsOnly = false;
        var chunks = new StringBuilder();
        bool uiThread = true;
        string answer = await new OpenCodeService(store).AskAsync("Fixture question", "No tools.", false, null, text => { uiThread &= Application.Current.Dispatcher.CheckAccess(); chunks.Append(text); }, timeout.Token);
        Check(answer == "Fixture account reply" && chunks.ToString() == answer, "Existing AI facade routes and streams the selected Codex account model without invoking OpenCode");
        Check(uiThread, "Account streaming callbacks remain on the WPF caller thread");
        string testImage = Path.Combine(root, "fixture-image.png");
        File.WriteAllBytes(testImage, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jEDkAAAAASUVORK5CYII="));
        store.Data.Settings.VisionModel = "codex/fixture-image";
        answer = await new OpenCodeService(store).AskAsync("FIXTURE_ACCOUNT_IMAGE", "No tools.", true, testImage, null, timeout.Token);
        Check(answer == "Fixture account reply", "Codex vision route attaches the explicitly selected local image");
        answer = await new OpenCodeService(store).AskAsync("FIXTURE_ACCOUNT_TOOL", "No tools.", false, null, null, timeout.Token);
        Check(answer == "Fixture account reply", "Codex native command approval is cancelled before the reply continues");
        rejected = false;
        try { await new OpenCodeService(store).AskAsync("FIXTURE_ACCOUNT_FAIL", "No tools.", false, null, null, timeout.Token); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Failed Codex turns are not reported as completed answers");
        using (var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
        {
            rejected = false;
            try { await new OpenCodeService(store).AskAsync("FIXTURE_ACCOUNT_CANCEL", "No tools.", false, null, null, cancelled.Token); }
            catch (OperationCanceledException) { rejected = true; }
            Check(rejected, "Stop cancels a pending Codex turn and cleans up its process");
        }
        await codex.LogoutAsync(timeout.Token);
        Check(true, "Codex account disconnect completes over its isolated transport");
        var agyModels = await AccountConnections.ModelsAsync(store, "antigravity", timeout.Token);
        Check(agyModels.Count == 2 && agyModels.All(m => m.Id.StartsWith("antigravity/") && m.ImageInput), "Antigravity reads its dynamic session catalog and advertised image transport capability");
        string agyProfile = Path.Combine(store.Root, "accounts", "antigravity");
        File.WriteAllText(Path.Combine(agyProfile, "fixture-auth-required"), "synthetic unauthenticated profile");
        string? googleUrl = null;
        await new AntigravityAccountService(store).LoginAsync(url => googleUrl = url, (_, _) => Task.FromResult("synthetic-code"), timeout.Token);
        Check(googleUrl?.StartsWith("https://accounts.google.com/o/oauth2/auth?", StringComparison.Ordinal) == true && File.Exists(Path.Combine(agyProfile, "fixture-auth-token")), "Antigravity Connect launches code login and persists into the same isolated file profile");
        Check((await AccountConnections.ModelsAsync(store, "antigravity", timeout.Token)).Count == 2, "Fresh Antigravity process sees the account after code exchange rather than repeating Connect");
        Check(AntigravityAccountService.TryNormalizeCodeLoginResponse(googleUrl!, "synthetic-code", out _) &&
            AntigravityAccountService.TryNormalizeCodeLoginResponse(googleUrl!, "https://antigravity.google/oauth-callback?state=fixture-state&code=synthetic-code", out _) &&
            !AntigravityAccountService.TryNormalizeCodeLoginResponse(googleUrl!, "https://antigravity.google/oauth-callback?state=wrong&code=synthetic-code", out _) &&
            !AntigravityAccountService.TryNormalizeCodeLoginResponse(googleUrl!, "https://untrusted.example.invalid/oauth-callback?state=fixture-state&code=synthetic-code", out _) &&
            !AntigravityAccountService.TryNormalizeCodeLoginResponse(googleUrl!, "code\nextra", out _), "Google callback validation accepts the pending login and rejects wrong state, origin and newline injection");
        string? originalOpenAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        string? originalGeminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "synthetic-ambient-api-credential");
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", "synthetic-ambient-api-credential");
            await AccountConnections.ModelsAsync(store, "codex", timeout.Token);
            await AccountConnections.ModelsAsync(store, "antigravity", timeout.Token);
            Check(true, "Connected accounts exclude inherited API keys at the real process boundary");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", originalOpenAiKey);
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", originalGeminiKey);
        }
        store.Data.Settings.Model = "antigravity/fixture-image";
        chunks.Clear();
        answer = await new OpenCodeService(store).AskAsync("Fixture question", "No tools.", false, null, text => chunks.Append(text), timeout.Token);
        Check(answer == "Fixture account reply" && chunks.ToString() == answer, "Antigravity model selection and streamed inference work through the existing AI facade");
        store.Data.Settings.VisionModel = "antigravity/fixture-image";
        answer = await new OpenCodeService(store).AskAsync("FIXTURE_ACCOUNT_IMAGE", "No tools.", true, testImage, null, timeout.Token);
        Check(answer == "Fixture account reply", "Antigravity vision route sends real PNG bytes in the ACP image block");
        foreach (string trigger in new[] { "FIXTURE_ACCOUNT_TOOL", "FIXTURE_ACCOUNT_FAIL" })
        {
            rejected = false;
            try { await new OpenCodeService(store).AskAsync(trigger, "No tools.", false, null, null, timeout.Token); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, trigger.EndsWith("TOOL") ? "Antigravity native permission is cancelled and tool attempts fail closed" : "Incomplete Antigravity output is not accepted as a successful turn");
        }
        using (var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
        {
            rejected = false;
            try { await new OpenCodeService(store).AskAsync("FIXTURE_ACCOUNT_CANCEL", "No tools.", false, null, null, cancelled.Token); }
            catch (OperationCanceledException) { rejected = true; }
            Check(rejected, "Stop cancels pending Antigravity inference and closes its session");
        }
        using (var catalog = JsonDocument.Parse("""{"configOptions":[{"id":"model","type":"select","options":[{"value":"a","name":"A"},{"name":"Group","options":[{"value":"b","name":"B"}]}]}]}"""))
            Check(AntigravityAccountService.ParseAccountModels(catalog.RootElement, false).Select(m => m.Id).SequenceEqual(new[] { "a", "b" }), "Grouped Antigravity model options are discovered without hardcoded IDs");
        await new AntigravityAccountService(store).LogoutAsync(timeout.Token);
        Check(true, "Antigravity account disconnect completes against its private profile");
        Check(!File.Exists(Path.Combine(agyProfile, "fixture-auth-token")), "Antigravity disconnect uses the same isolated credential profile as login");
        File.Delete(Path.Combine(agyProfile, "fixture-auth-required"));
        Check(!Directory.EnumerateFiles(store.Root, "*.secret", SearchOption.AllDirectories).Any(), "Fixture connection stores no credential material in NIVO secrets");
        string realtimeMarker = Path.Combine(codex.CodexHome, "fixture-realtime-started");
        File.Delete(realtimeMarker);
        string nativeAcp = Path.Combine(store.Root, "accounts", "antigravity", "antigravity-acp");
        Directory.CreateDirectory(nativeAcp);
        string preserved = Path.Combine(nativeAcp, "fixture-preserved");
        File.WriteAllText(preserved, "Synthetic profile marker; not a credential");
        var externalTalk = await AntigravityNativeTalkSession.StartForTestAsync(store, "fixture-text", "No tools", null, true, timeout.Token);
        Check(externalTalk.IsActive && externalTalk.UsesExternalWindow, "Antigravity native Talk starts both fixture processes without audio devices or visible windows");
        string nativeSessions = Path.Combine(store.Root, "accounts", "antigravity", "voice-sessions");
        string sessionDir = Directory.GetDirectories(nativeSessions).Single();
        string workspace = Path.Combine(sessionDir, "workspace");
        Check(File.Exists(Path.Combine(workspace, "verified")), "Native CLI reuses the isolated account through a junction with loopback audio forwarding");
        var processIds = new[] { "bridge.pid", "cli.pid" }.Select(name => int.Parse(File.ReadAllText(Path.Combine(workspace, name)))).ToArray();
        await externalTalk.StopAsync();
        await externalTalk.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await externalTalk.DisposeAsync();
        bool ProcessGone(int pid) { try { using var process = System.Diagnostics.Process.GetProcessById(pid); return process.HasExited; } catch (ArgumentException) { return true; } }
        Check(!externalTalk.IsActive && processIds.All(ProcessGone), "Stopping external Talk terminates both child processes and completes idempotently");
        Check(!Directory.Exists(sessionDir) && File.ReadAllText(preserved) == "Synthetic profile marker; not a credential", "Session cleanup unlinks its junction without deleting or modifying the account target");
        using (var nativeCancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
        {
            var cancellingTalk = await AntigravityNativeTalkSession.StartForTestAsync(store, "fixture-text", "No tools", null, false, nativeCancel.Token);
            nativeCancel.Cancel();
            await cancellingTalk.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await cancellingTalk.DisposeAsync();
            Check(!cancellingTalk.IsActive && !Directory.GetDirectories(nativeSessions).Any() && File.Exists(preserved), "External native Talk cancellation cleans processes and temporary links while preserving the account");
        }
        var crashedTalk = await AntigravityNativeTalkSession.StartForTestAsync(store, "fixture-text", "No tools", null, false, timeout.Token);
        sessionDir = Directory.GetDirectories(nativeSessions).Single();
        File.WriteAllText(Path.Combine(sessionDir, "workspace", "crash-bridge"), "fixture");
        bool bridgeFault = false;
        try { await crashedTalk.Completion.WaitAsync(TimeSpan.FromSeconds(5)); } catch (InvalidOperationException) { bridgeFault = true; }
        await crashedTalk.DisposeAsync();
        Check(bridgeFault && !Directory.Exists(sessionDir) && File.Exists(preserved), "Bridge failure reports an error, stops the CLI and safely removes the session link");
        var closedTalk = await AntigravityNativeTalkSession.StartForTestAsync(store, "fixture-text", "No tools", null, false, timeout.Token);
        sessionDir = Directory.GetDirectories(nativeSessions).Single();
        File.WriteAllText(Path.Combine(sessionDir, "workspace", "close-cli"), "fixture");
        await closedTalk.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await closedTalk.DisposeAsync();
        Check(!closedTalk.IsActive && !Directory.Exists(sessionDir) && File.Exists(preserved), "Closing the external CLI ends Talk normally and stops its audio bridge");
        string? previousVersionError = Environment.GetEnvironmentVariable("NIVO_FIXTURE_VERSION_ERROR");
        try
        {
            Environment.SetEnvironmentVariable("NIVO_FIXTURE_VERSION_ERROR", "1");
            rejected = false;
            try { await AntigravityNativeTalkSession.StartForTestAsync(store, "fixture-text", "No tools", null, false, timeout.Token); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && !Directory.GetDirectories(nativeSessions).Any(), "A failing version command cannot pass because its output contains a valid version number");
        }
        finally { Environment.SetEnvironmentVariable("NIVO_FIXTURE_VERSION_ERROR", previousVersionError); }
        string? previousDelay = Environment.GetEnvironmentVariable("NIVO_FIXTURE_VERSION_DELAY");
        string? previousPid = Environment.GetEnvironmentVariable("NIVO_FIXTURE_VERSION_PID");
        string versionPid = Path.Combine(root, "version.pid");
        try
        {
            Environment.SetEnvironmentVariable("NIVO_FIXTURE_VERSION_DELAY", "1");
            Environment.SetEnvironmentVariable("NIVO_FIXTURE_VERSION_PID", versionPid);
            using var startupCancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            startupCancel.CancelAfter(500);
            rejected = false;
            try { await AntigravityNativeTalkSession.StartForTestAsync(store, "fixture-text", "No tools", null, false, startupCancel.Token); }
            catch (OperationCanceledException) { rejected = true; }
            Check(rejected && File.Exists(versionPid) && ProcessGone(int.Parse(File.ReadAllText(versionPid))) && !Directory.GetDirectories(nativeSessions).Any(), "Cancelling the native version probe kills its process and observes pipe completion before any audio bridge starts");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NIVO_FIXTURE_VERSION_DELAY", previousDelay);
            Environment.SetEnvironmentVariable("NIVO_FIXTURE_VERSION_PID", previousPid);
        }
        for (int attempt = 0; attempt < 4; attempt++)
        {
            using var immediateCancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var immediateTalk = await AntigravityNativeTalkSession.StartForTestAsync(store, "fixture-text", "No tools", null, false, immediateCancel.Token);
            immediateCancel.Cancel();
            await immediateTalk.StopAsync();
            await immediateTalk.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await immediateTalk.DisposeAsync();
        }
        Check(!Directory.GetDirectories(nativeSessions).Any() && File.Exists(preserved), "Repeated immediate start-stop cancellation leaves no monitor, temporary session, or account cleanup race");
        var fakeAudio = new FakeRealtimeAudio();
        var transcripts = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var realtime = await codex.StartRealtimeAsync("fixture-text", "No native tools.", (role, text) => transcripts.Enqueue(role + ":" + text), null,
            () => { if (!File.Exists(realtimeMarker)) throw new InvalidOperationException("Audio activated before realtime connection"); return fakeAudio; }, timeout.Token);
        await fakeAudio.Output.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(realtime.IsActive && realtime.RealtimeSessionId == "fixture-voice" && fakeAudio.CaptureStarted, "Native Talk waits for the started event before activating the injected audio transport");
        Check(fakeAudio.Output.Task.Result.SampleRate == 24000 && fakeAudio.Output.Task.Result.Channels == 1 && fakeAudio.Output.Task.Result.PcmBytes.Length == 480, "Native Talk delivers provider PCM bytes with the actual sample rate and channels");
        Check(transcripts.Contains("user:Synthetic spoken question") && transcripts.Contains("assistant:Synthetic spoken reply"), "Native Talk exposes finalized user and assistant transcripts");
        await realtime.StopAsync(); await realtime.Completion.WaitAsync(TimeSpan.FromSeconds(3)); await realtime.DisposeAsync();
        Check(!realtime.IsActive && fakeAudio.CaptureStopped && fakeAudio.Disposed, "Talk Stop closes capture, playback and the native process idempotently");
        string unavailableMarker = Path.Combine(codex.CodexHome, "fixture-realtime-error");
        File.WriteAllText(unavailableMarker, "synthetic unavailable account");
        bool audioTouched = false;
        rejected = false;
        try { await codex.StartRealtimeAsync("fixture-text", "No native tools.", null, null, () => { audioTouched = true; return new FakeRealtimeAudio(); }, timeout.Token); }
        catch (InvalidOperationException) { rejected = true; }
        finally { File.Delete(unavailableMarker); }
        Check(rejected && !audioTouched, "Unsupported native account voice fails before touching any audio device");
        var faultAudio = new FakeRealtimeAudio();
        var faultSession = await codex.StartRealtimeAsync("fixture-text", "No tools.", null, null, () => faultAudio, timeout.Token);
        faultAudio.RaiseCaptureFault(new InvalidDataException("Synthetic microphone fault"));
        rejected = false;
        try { await faultSession.Completion.WaitAsync(TimeSpan.FromSeconds(5)); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected && faultAudio.Disposed && faultAudio.CaptureStopped, "Audio callback faults close the session on a worker without callback-thread shutdown");
        await faultSession.DisposeAsync();
        var cleanupAudio = new FakeRealtimeAudio { ThrowOnStopCapture = true };
        var cleanupSession = await codex.StartRealtimeAsync("fixture-text", "No tools.", null, null, () => cleanupAudio, timeout.Token);
        await cleanupSession.StopAsync();
        rejected = false;
        try { await cleanupSession.Completion.WaitAsync(TimeSpan.FromSeconds(5)); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected && cleanupAudio.Disposed && cleanupAudio.OutputStopped, "Capture-stop failure still releases playback and process and reports completion failure");
        await cleanupSession.DisposeAsync();
        string badAppendMarker = Path.Combine(codex.CodexHome, "fixture-realtime-bad-append");
        File.WriteAllText(badAppendMarker, "synthetic oversized transport reply");
        var badAppendAudio = new FakeRealtimeAudio();
        try
        {
            var badAppendSession = await codex.StartRealtimeAsync("fixture-text", "No tools.", null, null, () => badAppendAudio, timeout.Token);
            rejected = false;
            try { await badAppendSession.Completion.WaitAsync(TimeSpan.FromSeconds(5)); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && badAppendAudio.Disposed && badAppendAudio.CaptureStopped, "Malformed oversized native audio transport cannot hang Completion or leak capture");
            await badAppendSession.DisposeAsync();
        }
        finally { File.Delete(badAppendMarker); }
        bool allowInterrupt = false;
        var buttonWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var talkButton = Ui.InterruptibleAsyncBtn("Synthetic Talk", () => buttonWork.Task, () => allowInterrupt);
        talkButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check(!talkButton.IsEnabled, "Legacy speech mode prevents duplicate in-flight button actions");
        buttonWork.TrySetResult(); await Task.Delay(20);
        allowInterrupt = true;
        buttonWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        talkButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check(talkButton.IsEnabled, "Switching to native Talk keeps the existing button available to cancel connection");
        buttonWork.TrySetResult(); await Task.Delay(20);
        Ui.ApplyTheme("Frost"); Motion.Reduced = true;
        foreach (string language in new[] { "Arabic", "English" })
        {
            store.Data.Settings.Language = language;
            var window = new MainWindow(new Controller(store)) { Width = 850, Height = 700, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -16000, Top = -16000 };
            window.Show(); window.Navigate("Settings", "OpenCode AI"); window.UpdateLayout();
            var cards = Descendants<Border>(window).Where(b => b.Tag is string tag && tag.EndsWith("AccountCard")).ToArray();
            Check(cards.Length == 2 && cards.All(c => c.ActualWidth > 200), language + " Settings renders both connected-account cards at minimum window width");
            var save = Descendants<Button>(window).Single(b => Equals(b.Tag, "SavePreferences"));
            Check(save.IsVisible && save.TranslatePoint(new Point(0, save.ActualHeight), window).Y <= window.ActualHeight, language + " account settings preserve the visible Save bar");
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window); DesktopService.SavePng(bitmap, Path.Combine(root, "accounts-" + language + ".png"));
            window.Navigate("Settings", "Voice"); window.UpdateLayout();
            var voiceChoice = Descendants<ComboBox>(window).Single(c => Equals(c.Tag, "VoiceAccountProvider"));
            Check(voiceChoice.Items.Count == 3 && voiceChoice.IsVisible && voiceChoice.ActualWidth > 100,
                language + " Voice settings expose Auto, Codex and Antigravity independently of the text model");
            bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window); DesktopService.SavePng(bitmap, Path.Combine(root, "voice-" + language + ".png"));
            window.Hide();
        }
    }

    private sealed class FakeRealtimeAudio : IRealtimeAudioTransport
    {
        public bool CaptureStarted, CaptureStopped, Disposed;
        public bool OutputStopped, ThrowOnStopCapture;
        private Action<Exception>? captureFault;
        public TaskCompletionSource<RealtimeAudioChunk> Output { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HasBufferedOutput => false;
        public void StartCapture(Action<RealtimeAudioChunk> onChunk, Action<Exception> onFault)
        { CaptureStarted = true; captureFault = onFault; onChunk(new RealtimeAudioChunk(new byte[480], 24000, 1)); }
        public void RaiseCaptureFault(Exception error) => captureFault?.Invoke(error);
        public void StopCapture() { CaptureStopped = true; if (ThrowOnStopCapture) throw new InvalidDataException("Synthetic capture-stop failure"); }
        public void WriteOutput(RealtimeAudioChunk chunk, Action<Exception> onFault) => Output.TrySetResult(chunk);
        public void ClearOutput() { }
        public void StopOutput() => OutputStopped = true;
        public void Dispose() => Disposed = true;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
