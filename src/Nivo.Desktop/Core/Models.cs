namespace Nivo.Core;

public sealed class Settings
{
    public bool PendingModelDiscovery { get; set; }
    public string Model { get; set; } = "opencode/deepseek-v4.1-flash-free";
    public string FallbackModel { get; set; } = "";
    public string VisionFallbackModel { get; set; } = "";
    public string VisionModel { get; set; } = "opencode/nemotron-3.5-super-vl-free";
    public string CliPath { get; set; } = "";
    public string CodexCliPath { get; set; } = "";
    public string AntigravityCliPath { get; set; } = "";
    public bool UseUserOpenCodeConfig { get; set; } = true;
    public bool FreeModelsOnly { get; set; }
    public string Theme { get; set; } = "Frost";
    public string Persona { get; set; } = "Chill Smart";
    public string Language { get; set; } = "Arabic";
    public Dictionary<string, string> Shortcuts { get; set; } = new() { ["ask"] = "Alt+Shift+S", ["island"] = "Ctrl+Alt+Space", ["incognito"] = "Ctrl+Shift+P", ["crop"] = "Alt+Shift+C" };
    public bool ReduceMotion { get; set; }
    public bool FollowWindowsAnimations { get; set; } = true;
    public bool DesktopEnabled { get; set; } = true;
    public bool ReplaceTaskbar { get; set; }
    public bool EdgeGlow { get; set; } = true;
    public string FocusProfile { get; set; } = "";
    public List<string> DistractionKeywords { get; set; } = ["YouTube", "Reddit", "TikTok"];
    public bool Proactive { get; set; }
    public int ScanSeconds { get; set; } = 120;
    public bool ErrorAwareness { get; set; }
    public int ErrorDwellSeconds { get; set; } = 20;
    public int ErrorCooldownSeconds { get; set; } = 300;
    public bool Gaming { get; set; }
    public bool Focus { get; set; }
    public bool Hover { get; set; }
    public bool ConfirmImageSharing { get; set; }
    public bool ComputerAccessEnabled { get; set; }
    public bool VoiceEnabled { get; set; }
    public bool MatchSpokenLanguage { get; set; } = true;
    public bool UseAccountVoice { get; set; } = true;
    public string VoiceAccountProvider { get; set; } = "Auto";
    public string LastConnectedAccountProvider { get; set; } = "";
    public string TtsEndpoint { get; set; } = "";
    public string SttEndpoint { get; set; } = "";
    public string TtsModel { get; set; } = "";
    public string SttModel { get; set; } = "";
    public string Voice { get; set; } = "";
    public string GmailClientId { get; set; } = "";
    public List<string> ExcludedApps { get; set; } = ["keepass", "1password", "bitwarden", "bank"];
}
public sealed class Memory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public string Tier { get; set; } = "Recent Memory";
    public DateTime Created { get; set; } = DateTime.Now;
}
public sealed class WorkItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "Task";
    public string ReferenceFile { get; set; } = "";
    public DateTime? Due { get; set; }
    public bool Done { get; set; }
    public bool Notified { get; set; }
}
public sealed class ChatMessage
{
    public string Role { get; set; } = "user";
    public string Text { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
}
public sealed class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "New conversation";
    public List<ChatMessage> Messages { get; set; } = [];
}
public sealed class Launcher
{
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
    public string Glyph { get; set; } = "\uE8A5";
    public bool Pinned { get; set; } = true;
}
public sealed class Routine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<string> Targets { get; set; } = [];
    public bool Focus { get; set; }
    public string FocusProfile { get; set; } = "";
    public string Music { get; set; } = "";
    public List<SavedWindow> Windows { get; set; } = [];
}
public sealed class SavedWindow
{
    public string Executable { get; set; } = "";
    public string Title { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}
public sealed class TaskRecord
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Name { get; set; } = "";
    public string Status { get; set; } = "Working";
    public string Detail { get; set; } = "";
}
public sealed class AppData
{
    public Settings Settings { get; set; } = new();
    public List<Memory> Memories { get; set; } = [];
    public List<WorkItem> Workspace { get; set; } = [];
    public List<Conversation> Conversations { get; set; } = [];
    public List<Routine> Routines { get; set; } = [];
    public List<TaskRecord> Tasks { get; set; } = [];
    public List<Launcher> Launchers { get; set; } = [
        new() { Name = "Files", Target = "explorer.exe", Glyph = "\uE8B7" },
        new() { Name = "Browser", Target = "https://www.google.com", Glyph = "\uE774" },
        new() { Name = "Notes", Target = "notepad.exe", Glyph = "\uE70B" }
    ];
}
