using System.Text.Json;

// Native voice process fixture: no audio devices, sockets, or visible windows.
if (args.Contains("--version"))
{
    if (Environment.GetEnvironmentVariable("NIVO_FIXTURE_VERSION_PID") is { Length: > 0 } pidPath)
        File.WriteAllText(pidPath, Environment.ProcessId.ToString());
    if (Environment.GetEnvironmentVariable("NIVO_FIXTURE_VERSION_DELAY") == "1") await Task.Delay(10000);
    Console.WriteLine("1.5.13");
    if (Environment.GetEnvironmentVariable("NIVO_FIXTURE_VERSION_ERROR") == "1") Environment.ExitCode = 1;
    return;
}
if (args.Contains("mic-serve") || args.Contains("--settings"))
{
    bool bridge = args.Contains("mic-serve");
    File.WriteAllText(Path.Combine(Environment.CurrentDirectory, bridge ? "bridge.pid" : "cli.pid"), Environment.ProcessId.ToString());
    if (bridge) Console.WriteLine("listening 127.0.0.1:fixture");
    else
    {
        string home = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
        string linked = Path.Combine(home, ".gemini", "antigravity-cli", "fixture-preserved");
        if (!File.Exists(linked) || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CONNECTION")) ||
            !(Environment.GetEnvironmentVariable("ANTIGRAVITY_MIC") ?? "").StartsWith("127.0.0.1:")) return;
        File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "verified"), "No hardware used");
        File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "console-state.json"), JsonSerializer.Serialize(new {
            redirectedInput = Console.IsInputRedirected, redirectedOutput = Console.IsOutputRedirected,
            visible = TerminalVisibility.IsWindowVisible(TerminalVisibility.GetConsoleWindow()) }));
        Console.WriteLine("? for shortcuts");
        // Native startup paints an empty prompt before the model/backend is ready.
        await Task.Delay(900);
        Console.WriteLine("? for shortcuts                         Fixture model · ready");
        string? command = Console.ReadLine();
        if (command != "/voice live") throw new Exception("The private terminal did not receive the exact voice-start command");
        File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "voice-started"), command);
        if (Environment.GetEnvironmentVariable("NIVO_FIXTURE_VOICE_FAIL") == "1") Console.WriteLine("Live mode isn't available in this session.");
        else Console.WriteLine("Started voice chat · fixture");
    }
    for (int tick = 0; tick < 2400; tick++)
    {
        if (!bridge && File.Exists(Path.Combine(Environment.CurrentDirectory, "stop-voice")))
        {
            File.Delete(Path.Combine(Environment.CurrentDirectory, "stop-voice"));
            Console.WriteLine("Stopped voice chat · fixture");
        }
        if (File.Exists(Path.Combine(Environment.CurrentDirectory, bridge ? "crash-bridge" : "close-cli")))
        { Environment.ExitCode = bridge ? 3 : 0; return; }
        await Task.Delay(50);
    }
    return;
}

// A deterministic process-boundary fixture. Never used by the shipped application.
if (args.Contains("app-server") || args.Contains("acp"))
{
    await AccountFixture.Run(args.Contains("acp"), args.Contains("--login"));
    return;
}

if (args.Contains("models"))
{
    if (!args.Contains("--standalone")) throw new Exception("Catalog must use the supplied credential/config environment instead of an existing server");
    Console.WriteLine("opencode/deepseek-v4.1-flash-free\nopencode/nemotron-3.5-super-vl-free\nopencode/gpt-6.2-sol");
    string configPath = Path.Combine(Environment.CurrentDirectory, ".opencode", "opencode.json");
    using var catalogConfig = JsonDocument.Parse(File.ReadAllText(configPath));
    bool providerRestricted = catalogConfig.RootElement.TryGetProperty("enabled_providers", out var enabled) && !enabled.EnumerateArray().Any(x => x.GetString() == "google");
    if (!providerRestricted && Environment.GetEnvironmentVariable("GOOGLE_API_KEY") == "synthetic-google-key-for-fixture") Console.WriteLine("google/gemini-3.5-flash-lite");
    return;
}
var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(Environment.CurrentDirectory, ".opencode", "opencode.json"))).RootElement;
var modelIndex = Array.IndexOf(args, "--model");
if (modelIndex < 0 || args[modelIndex + 1] != config.GetProperty("model").GetString()) throw new Exception("Explicit model/config mismatch");
if (config.GetProperty("agents").GetProperty("nivo-local").GetProperty("permissions")[0].GetProperty("effect").GetString() != "deny") throw new Exception("Tools were not denied");
if (config.GetProperty("agents").GetProperty("nivo-local").GetProperty("steps").GetInt32() < 2) throw new Exception("First request would receive an unsupported final-step assistant prefill");
if (!args.Contains("--title") || !args.Contains("--standalone")) throw new Exception("Maintenance/title safeguards missing");
var prompt = args[^1];
for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--file" && args[i + 1].EndsWith(".txt")) prompt = File.ReadAllText(args[i + 1]);
string commandText = prompt;
if (prompt.TrimStart().StartsWith("{"))
{
    using var requestEnvelope = JsonDocument.Parse(prompt);
    var requestRoot = requestEnvelope.RootElement;
    if (requestRoot.TryGetProperty("userRequest", out var currentRequest) || requestRoot.TryGetProperty("request", out currentRequest))
        if (currentRequest.ValueKind == JsonValueKind.String) commandText = currentRequest.GetString() ?? "";
    if (requestRoot.TryGetProperty("userReferenceData", out var reference) && reference.ValueKind == JsonValueKind.String && commandText.Contains("supplied user-provided content"))
    {
        string supplied = reference.GetString() ?? "";
        if (supplied.Contains("FIXTURE_CLIPBOARD_SLOW")) commandText = "FIXTURE_CLIPBOARD_SLOW";
        if (supplied.Contains("FIXTURE_UNTRUSTED_TRANSFORM")) commandText = "FIXTURE_UNTRUSTED_TRANSFORM";
    }
}

if (commandText.Contains("FIXTURE_AUTO_IMAGE"))
{
    int imageIndex = Array.IndexOf(args, "--file");
    if (imageIndex < 0 || !File.Exists(args[imageIndex + 1]) || !args[imageIndex + 1].EndsWith(".png") || args[modelIndex + 1] != "opencode/nemotron-3.5-super-vl-free") throw new Exception("Direct image sharing must use the actual PNG and configured vision model");
    Console.WriteLine(JsonSerializer.Serialize(new { type = "text", part = new { text = "AUTO_IMAGE_OK" } })); return;
}
if (commandText.Contains("FIXTURE_QUOTA_TEXT"))
{
    Console.WriteLine(JsonSerializer.Serialize(new { type = "text", part = new { text = "Explain rate limit and RESOURCE_EXHAUSTED without treating this ordinary reply as an API error." } })); return;
}
if (commandText.Contains("FIXTURE_MODEL"))
{
    Console.WriteLine(JsonSerializer.Serialize(new { type = "text", part = new { text = "MODEL:" + args[modelIndex + 1] } })); return;
}
if (commandText.Contains("FIXTURE_NESTED_QUOTA"))
{
    if (args[modelIndex + 1] == "opencode/deepseek-v4.1-flash-free") Console.WriteLine(JsonSerializer.Serialize(new { type = "error", error = new { name = "APIError", data = new { statusCode = 429, message = "RESOURCE_EXHAUSTED: Too Many Requests", retryAfterMs = 600000 } } }));
    else Console.WriteLine(JsonSerializer.Serialize(new { type = "text", part = new { text = "NESTED_FALLBACK_OK" } })); return;
}
if (commandText.Contains("FIXTURE_CLIPBOARD_SLOW"))
{
    await Task.Delay(450);
    Console.WriteLine(JsonSerializer.Serialize(new { type = "text", part = new { text = "Clipboard transformation result" } })); return;
}
if (commandText.Contains("FIXTURE_LONG_REPLY"))
{
    Console.WriteLine(JsonSerializer.Serialize(new { type = "text", part = new { text = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"Line {i}: A detailed answer that stays selectable and scrollable inside the compact question view.")) } })); return;
}
if (commandText.Contains("detectedErrorKind"))
{
    if (prompt.Contains("NIVO_PRIVATE_PASSWORD") || prompt.Contains("NIVO_HIDDEN_SENTINEL")) throw new Exception("Private fixture content escaped UI Automation filtering");
    Console.WriteLine(JsonSerializer.Serialize(new { type = "text", part = new { text = "{\"suggestion\":\"The function value is not callable. Attach the source for a reviewed fix.\"}" } })); return;
}
if (commandText.Contains("FIXTURE_GOOGLE_ENV"))
{
    string marker = Environment.GetEnvironmentVariable("GOOGLE_API_KEY") == "synthetic-google-key-for-fixture" ? "GOOGLE_ENV_PRESENT" : "GOOGLE_ISOLATED";
    Console.WriteLine(JsonSerializer.Serialize(new { type = "text", part = new { text = marker } })); return;
}
if (commandText.Contains("FIXTURE_EMPTY_CHUNK_RETRY"))
{
    string seen = Path.Combine(Environment.CurrentDirectory, ".fixture-empty-seen");
    bool first = !File.Exists(seen); File.WriteAllText(seen, "seen");
    Console.WriteLine(JsonSerializer.Serialize(new { type = "text", part = new { text = first ? "" : "EMPTY_RETRY_OK" } })); return;
}
if (commandText.Contains("FIXTURE_EMPTY_CHUNK_QUOTA"))
{
    Console.WriteLine(JsonSerializer.Serialize(new { type = "text", part = new { text = "" } }));
    if (args[modelIndex + 1] == "opencode/deepseek-v4.1-flash-free") Console.WriteLine(JsonSerializer.Serialize(new { type = "error", error = new { status = 429, message = "quota exhausted" } }));
    else Console.WriteLine(JsonSerializer.Serialize(new { type = "text", part = new { text = "EMPTY_CHUNK_FALLBACK_OK" } })); return;
}
if (commandText.Contains("FIXTURE_TYPED_EVENTS"))
{
    foreach (string line in new[] { "true", "0", "[]", "{\"type\":1}", "{\"type\":\"text\",\"part\":[]}", "{\"type\":\"text\",\"part\":{\"text\":3}}" }) Console.WriteLine(line);
    Console.WriteLine(JsonSerializer.Serialize(new { type = "text", part = new { text = "TYPED_EVENTS_OK" } })); return;
}
if (commandText.Contains("FIXTURE_TOO_LARGE")) { Console.WriteLine(JsonSerializer.Serialize(new { type = "text", part = new { text = new string('x', 200001) } })); return; }
if (commandText.Contains("FIXTURE_LOCK_CAPTURE")) { await Task.Delay(500); Console.WriteLine(JsonSerializer.Serialize(new { type = "text", part = new { text = "LOCK_CAPTURE_OK" } })); return; }
if (commandText == "FIXTURE_UNTRUSTED_TRANSFORM")
{
    string policy = config.GetProperty("agents").GetProperty("nivo-local").GetProperty("system").GetString() ?? "";
    if (!policy.Contains("No planning/actions in this call")) throw new Exception("A transform must not use the computer planner");
    Console.WriteLine(JsonSerializer.Serialize(new { type = "text", part = new { text = "TRANSFORM_ONLY_OK" } })); return;
}
if (commandText.Contains("FIXTURE_COMPUTER_"))
{
    using var envelope = JsonDocument.Parse(prompt);
    string answer;
    if (envelope.RootElement.TryGetProperty("executionResults", out var actual))
    {
        if (!actual.ToString().Contains("written by NIVO")) throw new Exception("Observed results must include actual command output");
        answer = "Actual command result confirmed.";
    }
    else if (commandText == "FIXTURE_COMPUTER_ANSWER") answer = "{\"answer\":\"Conversational answer without commands.\",\"steps\":[]}";
    else if (commandText == "FIXTURE_COMPUTER_MEMORY")
    {
        if (!envelope.RootElement.GetProperty("rememberedFacts").ToString().Contains("scratch preference")) throw new Exception("Local memory was lost in computer-enabled chat");
        answer = "{\"answer\":\"Memory context retained.\",\"steps\":[]}";
    }
    else
    {
        string path = commandText[(commandText.IndexOf(':') + 1)..];
        string literal = path.Replace("'", "''");
        var commandSteps = new List<object> { new { action = "powershell", title = "Scratch write", text = "[System.IO.File]::WriteAllText('" + literal + "', 'written by NIVO'); Get-Content -LiteralPath '" + literal + "'", target = Path.GetDirectoryName(path), timeoutSeconds = 30 } };
        if (commandText.StartsWith("FIXTURE_COMPUTER_PARTIAL:")) commandSteps.Add(new { action = "powershell", title = "Scratch failure", text = "throw 'second step failed'", target = Path.GetDirectoryName(path), timeoutSeconds = 30 });
        answer = JsonSerializer.Serialize(new { summary = "Write a scratch test file", steps = commandSteps });
    }
    Console.WriteLine(JsonSerializer.Serialize(new { type = "text", part = new { text = answer } })); return;
}
if (commandText.Contains("FIXTURE_WINDOWS_"))
{
    string system = config.GetProperty("agents").GetProperty("nivo-local").GetProperty("system").GetString() ?? "";
    if (!system.Contains("WINDOWS SUPPORT") || !system.Contains("operatingSystemVersion") || !system.Contains("NO direct filesystem")) throw new Exception("Windows expertise/runtime/capability policy was not supplied");
    if (system.Contains("UNTRUSTED_CONTEXT_INSTRUCTION")) throw new Exception("External context was promoted to system authority");
    using var envelope = JsonDocument.Parse(prompt);
    string request = envelope.RootElement.TryGetProperty("request", out var agentRequest) ? agentRequest.GetString()! : envelope.RootElement.GetProperty("userRequest").GetString()!;
    string answer;
    if (request == "FIXTURE_WINDOWS_SYSTEM") answer = "WINDOWS_POLICY_OK";
    else if (request == "FIXTURE_WINDOWS_FOLLOWUP")
    {
        var history = envelope.RootElement.GetProperty("recentConversation");
        if (!history.EnumerateArray().Any(m => m.GetProperty("Text").GetString() == "FIXTURE_WINDOWS_CLARIFY") || !history.EnumerateArray().Any(m => m.GetProperty("Role").GetString() == "assistant" && m.GetProperty("Text").GetString() == "Which application has the problem?")) throw new Exception("Agent follow-up lost the original request or its clarification");
        answer = "{\"clarification\":\"Which error message do you see?\",\"steps\":[]}";
    }
    else if (request == "FIXTURE_WINDOWS_CONTEXT")
    {
        if (!envelope.RootElement.GetProperty("userSuppliedContext").GetString()!.Contains("UNTRUSTED_CONTEXT_INSTRUCTION")) throw new Exception("Reference context was lost");
        answer = "{\"clarification\":\"Please provide the affected application.\",\"steps\":[]}";
    }
    else answer = "{\"clarification\":\"Which application has the problem?\",\"steps\":[]}";
    Console.WriteLine(JsonSerializer.Serialize(new { type = "text", part = new { text = answer } })); return;
}
if (commandText.Contains("FIXTURE_ERROR")) { Console.WriteLine(JsonSerializer.Serialize(new { type = "error", error = new { type = "provider.free-limit", message = "fixture quota exhausted", status = 429, retryAfterMs = 3600000 } })); return; }
if (commandText.Contains("FIXTURE_QUOTA_FALLBACK"))
{
    if (args[modelIndex + 1] == "opencode/deepseek-v4.1-flash-free")
    {
        Console.WriteLine(JsonSerializer.Serialize(new { type = "error", error = new { type = "provider.free-limit", message = "Free usage limit reached", status = 429, retryAfterMs = 600000 } }));
        return;
    }
    Console.WriteLine(JsonSerializer.Serialize(new { type = "text", part = new { text = "FALLBACK_OK:" + args[modelIndex + 1] } }));
    return;
}
if (commandText.Contains("FIXTURE_VISION_QUOTA"))
{
    if (args[modelIndex + 1] == "opencode/nemotron-3.5-super-vl-free") Console.WriteLine(JsonSerializer.Serialize(new { type = "error", error = new { type = "provider.free-limit", message = "Vision quota", status = 429 } }));
    else Console.WriteLine(JsonSerializer.Serialize(new { type = "text", part = new { text = "VISION_BACKUP" } }));
    return;
}
if (commandText.Contains("FIXTURE_DELAY")) { await Task.Delay(30000); return; }
if (commandText.Contains("FIXTURE_AGENT_PLAN")) { Console.WriteLine(JsonSerializer.Serialize(new { type = "text", part = new { text = "{\"summary\":\"Reviewed note\",\"steps\":[{\"action\":\"note\",\"title\":\"Approved fixture\",\"text\":\"Visible content before execution\"}]}" } })); return; }
if (commandText.Contains("FIXTURE_IMAGE_PLAN"))
{
    int imageIndex = Array.IndexOf(args, "--file");
    if (imageIndex < 0 || !File.Exists(args[imageIndex + 1]) || !args[imageIndex + 1].EndsWith(".png") || args[modelIndex + 1] != "opencode/nemotron-3.5-super-vl-free") throw new Exception("Image planning must use its actual image and the vision model");
    Console.WriteLine(JsonSerializer.Serialize(new { type = "text", part = new { text = "{\"summary\":\"Image plan\",\"steps\":[{\"action\":\"note\",\"title\":\"Image\",\"text\":\"Selected image\"}]}" } })); return;
}
Console.WriteLine(JsonSerializer.Serialize(new { type = "text", part = new { text = "NIVO_" } }));
Console.WriteLine(JsonSerializer.Serialize(new { type = "text", part = new { text = "READY" } }));

internal static class TerminalVisibility
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] internal static extern IntPtr GetConsoleWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] [return:System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] internal static extern bool IsWindowVisible(IntPtr window);
}
