namespace Nivo.Services;

public interface IAccountTalkSession : IAsyncDisposable
{
    Task Completion { get; }
    bool IsActive { get; }
    bool UsesExternalWindow { get; }
    Task StopAsync();
}
