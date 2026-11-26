using System.Text.Json;

internal static class AccountFixture
{
    private static void Emit(object message) => Console.WriteLine(JsonSerializer.Serialize(message));
    public static async Task Run(bool acp, bool login = false)
    {
        if (Environment.GetEnvironmentVariable("OPENAI_API_KEY") != null || Environment.GetEnvironmentVariable("GEMINI_API_KEY") != null)
            throw new Exception("Account process inherited an API credential");
        string agyProfile = Environment.GetEnvironmentVariable("GEMINI_HOME") ?? "";
        if (acp && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CONNECTION"))) throw new Exception("Antigravity account process did not select isolated file credential storage");
        if (login)
        {
            Console.WriteLine("Open this link in a browser to log in with Google:");
            Console.WriteLine("https://accounts.google.com/o/oauth2/auth?response_type=code&redirect_uri=https%3A%2F%2Fantigravity.google%2Foauth-callback&state=fixture-state");
            string? code = await Console.In.ReadLineAsync();
            if (code != "synthetic-code") throw new Exception("Incorrect synthetic OAuth code");
            File.WriteAllText(Path.Combine(agyProfile, "fixture-auth-token"), "synthetic fixture only; not a credential");
            return;
        }
        string? line;
        while ((line = await Console.In.ReadLineAsync()) != null)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("method", out var methodElement)) continue;
            string method = methodElement.GetString() ?? "";
            if (!root.TryGetProperty("id", out var id)) continue;
            string request = root.TryGetProperty("params", out var requestParams) ? requestParams.GetRawText() : "";
            void Result(object result) => Emit(new { jsonrpc = "2.0", id = id.Clone(), result });
            switch (method)
            {
                case "initialize":
                    if (acp) Result(new { protocolVersion = 1, agentCapabilities = new { promptCapabilities = new { image = true, audio = false }, auth = new { logout = new { } } }, authMethods = new[] { new { id = "oauth-personal", name = "Google" } } });
                    else Result(new { userAgent = "nivo-fixture", platformFamily = "windows", platformOs = "windows" });
                    break;
                case "account/read": Result(new { account = new { type = "chatgpt", email = "fixture@example.invalid", planType = "test" }, requiresOpenaiAuth = true }); break;
                case "account/login/start":
                    bool invalidAuth = File.Exists(Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? "", "fixture-invalid-auth"));
                    Result(new { type = "chatgpt", loginId = "fixture-login", authUrl = invalidAuth ? "https://untrusted.example.invalid/authorize" : "https://auth.openai.com/authorize?state=fixture" });
                    Emit(new { method = "account/login/completed", @params = new { loginId = "fixture-login", success = true, error = (string?)null } });
                    break;
                case "authenticate":
                    if (File.Exists(Path.Combine(agyProfile, "fixture-auth-required")) && !File.Exists(Path.Combine(agyProfile, "fixture-auth-token")))
                        Emit(new { jsonrpc = "2.0", id = id.Clone(), error = new { code = -32603, message = "No browser is available here (this is a remote (SSH) session). Run agy acp --login." } });
                    else Result(new { });
                    break;
                case "account/logout": Result(new { }); break;
                case "logout": File.Delete(Path.Combine(agyProfile, "fixture-auth-token")); Result(new { }); break;
                case "model/list":
                    if (request.Contains("fixture-next"))
                    {
                        Result(new { data = new object[] { new { id = "fixture-unknown", model = "fixture-unknown", displayName = "Fixture unknown", hidden = false } }, nextCursor = (string?)null });
                        break;
                    }
                    Result(new { data = new object[] {
                        new { id = "fixture-text", model = "fixture-text", displayName = "Fixture text", inputModalities = new[] { "text" }, hidden = false },
                        new { id = "fixture-image", model = "fixture-image", displayName = "Fixture image", inputModalities = new[] { "text", "image" }, hidden = false }
                    }, nextCursor = "fixture-next" });
                    break;
                case "thread/start": Result(new { thread = new { id = "fixture-thread" } }); break;
                case "thread/realtime/start":
                    if (File.Exists(Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? "", "fixture-realtime-error")))
                    {
                        Emit(new { jsonrpc = "2.0", id = id.Clone(), error = new { code = -32000, message = "Native account voice is not available for this account" } });
                        break;
                    }
                    if (root.GetProperty("params").GetProperty("outputModality").GetString() != "audio") throw new Exception("Talk did not request native audio output");
                    Result(new { });
                    await Task.Delay(120);
                    File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? "", "fixture-realtime-started"), "synthetic started notification follows");
                    Emit(new { method = "thread/realtime/started", @params = new { threadId = "fixture-thread", version = "v1", realtimeSessionId = "fixture-voice" } });
                    Emit(new { method = "thread/realtime/transcript/done", @params = new { threadId = "fixture-thread", role = "user", text = "Synthetic spoken question" } });
                    Emit(new { method = "thread/realtime/transcript/done", @params = new { threadId = "fixture-thread", role = "assistant", text = "Synthetic spoken reply" } });
                    Emit(new { method = "thread/realtime/outputAudio/delta", @params = new { threadId = "fixture-thread", audio = new { data = Convert.ToBase64String(new byte[480]), sampleRate = 24000, numChannels = 1, samplesPerChannel = 240 } } });
                    break;
                case "thread/realtime/appendAudio":
                    if (File.Exists(Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? "", "fixture-realtime-bad-append"))) Console.WriteLine(new string('x', 4_000_001));
                    else Result(new { });
                    break;
                case "thread/realtime/appendText": case "thread/realtime/appendSpeech": Result(new { }); break;
                case "thread/realtime/stop":
                    Result(new { });
                    Emit(new { method = "thread/realtime/closed", @params = new { threadId = "fixture-thread", reason = "client stop" } });
                    break;
                case "turn/start":
                    if (request.Contains("FIXTURE_ACCOUNT_IMAGE") && !root.GetProperty("params").GetProperty("input").EnumerateArray().Any(i => i.GetProperty("type").GetString() == "localImage" && File.Exists(i.GetProperty("path").GetString())))
                        throw new Exception("Codex image input was not attached");
                    Result(new { turn = new { id = "fixture-turn", status = "inProgress" } });
                    if (request.Contains("FIXTURE_ACCOUNT_CANCEL")) break;
                    if (request.Contains("FIXTURE_ACCOUNT_FAIL"))
                    {
                        Emit(new { method = "turn/completed", @params = new { threadId = "fixture-thread", turn = new { id = "fixture-turn", status = "failed", error = new { message = "Fixture account failure" } } } });
                        break;
                    }
                    if (request.Contains("FIXTURE_ACCOUNT_TOOL"))
                    {
                        Emit(new { jsonrpc = "2.0", id = 800, method = "item/commandExecution/requestApproval", @params = new { threadId = "fixture-thread", turnId = "fixture-turn", itemId = "fixture-command", command = "DO NOT RUN" } });
                        using var denial = JsonDocument.Parse(await Console.In.ReadLineAsync() ?? "{}");
                        if (denial.RootElement.GetProperty("result").GetProperty("decision").GetString() != "cancel") throw new Exception("Native Codex command was not denied");
                    }
                    Emit(new { method = "item/agentMessage/delta", @params = new { threadId = "fixture-thread", turnId = "fixture-turn", itemId = "fixture-message", delta = "Fixture account reply" } });
                    Emit(new { method = "item/completed", @params = new { threadId = "fixture-thread", turnId = "fixture-turn", item = new { type = "agentMessage", id = "fixture-message", text = "Fixture account reply" } } });
                    Emit(new { method = "turn/completed", @params = new { threadId = "fixture-thread", turn = new { id = "fixture-turn", status = "completed", error = (object?)null } } });
                    break;
                case "session/new":
                    Result(new { sessionId = "fixture-session", models = new { currentModelId = "fixture-text", availableModels = new[] { new { modelId = "fixture-text", name = "Fixture text" }, new { modelId = "fixture-image", name = "Fixture image" } } }, modes = new { currentModeId = "plan", availableModes = new[] { new { id = "plan", name = "Plan" } } } });
                    break;
                case "session/set_model": case "session/set_mode": case "session/set_config_option": case "session/close": Result(new { }); break;
                case "session/prompt":
                    if (request.Contains("FIXTURE_ACCOUNT_IMAGE") && !root.GetProperty("params").GetProperty("prompt").EnumerateArray().Any(i => i.GetProperty("type").GetString() == "image" && i.GetProperty("mimeType").GetString() == "image/png" && Convert.FromBase64String(i.GetProperty("data").GetString()!)[0] == 0x89))
                        throw new Exception("Antigravity image bytes were not attached");
                    if (request.Contains("FIXTURE_ACCOUNT_CANCEL")) break;
                    if (request.Contains("FIXTURE_ACCOUNT_TOOL"))
                    {
                        Emit(new { jsonrpc = "2.0", id = 800, method = "session/request_permission", @params = new { sessionId = "fixture-session", toolCall = new { toolCallId = "fixture-command", title = "DO NOT RUN" }, options = Array.Empty<object>() } });
                        using var denial = JsonDocument.Parse(await Console.In.ReadLineAsync() ?? "{}");
                        if (denial.RootElement.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString() != "cancelled") throw new Exception("Native Antigravity permission was not denied");
                    }
                    Emit(new { method = "session/update", @params = new { sessionId = "fixture-session", update = new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "Fixture account reply" } } } });
                    Result(new { stopReason = request.Contains("FIXTURE_ACCOUNT_FAIL") ? "max_tokens" : "end_turn" });
                    break;
                default: Emit(new { jsonrpc = "2.0", id = id.Clone(), error = new { code = -32601, message = "Fixture unsupported method: " + method } }); break;
            }
        }
    }
}
