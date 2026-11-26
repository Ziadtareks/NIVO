using System.Diagnostics;
using System.Security.Principal;
using Nivo.Core;

namespace Nivo.Services;

public sealed record ComputerCommandResult(int ExitCode, string Output, string Error, bool Truncated)
{
    public string Describe() => $"PowerShell exit code: {ExitCode}\n{Output}" + (Error.Length > 0 ? "\nStandard error:\n" + Error : "") + (Truncated ? "\n[Output truncated]" : "");
}

public static class ComputerCommandService
{
    public const int MaxScriptCharacters = 8_000;
    public const int MaxStreamCharacters = 24_000;
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    public static string Validate(string script, string? directory)
    {
        if (string.IsNullOrWhiteSpace(script) || script.Length > MaxScriptCharacters || script.Contains('\0'))
            throw new InvalidDataException("PowerShell commands require a non-empty script within 8,000 characters (the encoded Windows command-line limit).");
        if (script.Any(c => (char.IsControl(c) && c is not ('\r' or '\n' or '\t')) || c is >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069'))
            throw new InvalidDataException("PowerShell scripts must use visible code without bidirectional override/control characters.");
        string path = string.IsNullOrWhiteSpace(directory) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : directory;
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new InvalidDataException("The command working directory must be a fully qualified ordinary directory path.");
        try { path = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new InvalidDataException("The command working directory is invalid.", ex); }
        return path;
    }
    public static async Task<ComputerCommandResult> RunAsync(Settings settings, string script, string? directory, int timeoutSeconds, CancellationToken cancellation)
    {
        if (!settings.ComputerAccessEnabled) throw new InvalidOperationException("Computer access is disabled in Settings → Desktop.");
        cancellation.ThrowIfCancellationRequested();
        string workingDirectory = Validate(script, directory);
        if (timeoutSeconds is < 5 or > 1800) throw new InvalidDataException("Command timeout must be 5–1800 seconds.");
        string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Windows PowerShell is unavailable on this computer.", executable);
        string body = "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::OutputEncoding; $ProgressPreference = 'SilentlyContinue'; $ErrorActionPreference = 'Stop'; $LASTEXITCODE = 0; try { $block = [ScriptBlock]::Create('" + script.Replace("'", "''") + "'); & $block; if (-not $? -and $LASTEXITCODE -eq 0) { exit 1 }; exit $LASTEXITCODE } catch { [Console]::Error.WriteLine(($_ | Out-String)); exit 1 }";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(body));
        if (encoded.Length > 30_000) throw new InvalidDataException("The encoded command is too large for Windows. Split it into smaller reviewed steps.");
        var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded }) start.ArgumentList.Add(argument);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        Process process;
        try
        {
            process = await Task.Run(() => {
                if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException("The command working directory does not exist: " + workingDirectory);
                deadline.Token.ThrowIfCancellationRequested();
                return Process.Start(start) ?? throw new InvalidOperationException("PowerShell could not be started.");
            }, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new TimeoutException($"PowerShell startup exceeded its {timeoutSeconds}-second limit."); }
        using var processLifetime = process;
        process.StandardInput.Close();
        var output = ReadBounded(process.StandardOutput, deadline.Token); var error = ReadBounded(process.StandardError, deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(1), cancellation).ConfigureAwait(false); }
            catch (TimeoutException) { deadline.Cancel(); }
            var stdout = await output.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); var stderr = await error.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            return new(process.ExitCode, ErrorAwarenessService.RedactSensitiveTokens(stdout.Text), ErrorAwarenessService.RedactSensitiveTokens(stderr.Text), stdout.Truncated || stderr.Truncated);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new TimeoutException($"PowerShell exceeded its {timeoutSeconds}-second limit. Stop was requested for the process tree."); }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                { Trace.TraceWarning("NIVO command stop failed ({0}).", ex.GetType().Name); }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (TimeoutException) { Trace.TraceWarning("NIVO command process did not exit within its stop deadline."); }
            }
            // Observe drains even after cancellation; a descendant may still own a pipe.
            try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { Trace.TraceWarning("NIVO command output pipes exceeded their cleanup deadline."); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { Trace.TraceInformation("NIVO command output draining was cancelled."); }
        }
    }
    private static async Task<(string Text, bool Truncated)> ReadBounded(StreamReader reader, CancellationToken cancellation)
    {
        var text = new StringBuilder(); var buffer = new char[2048]; bool truncated = false;
        try
        {
            while (true)
            {
                int read = await reader.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false); if (read == 0) break;
                int keep = Math.Min(read, MaxStreamCharacters - text.Length);
                if (keep > 0) text.Append(buffer, 0, keep);
                if (keep < read) truncated = true;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { truncated = true; }
        catch (IOException) when (cancellation.IsCancellationRequested) { truncated = true; }
        return (text.ToString(), truncated);
    }
}
