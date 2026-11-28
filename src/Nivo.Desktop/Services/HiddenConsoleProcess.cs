using System.IO;
using System.ComponentModel;
using System.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Nivo.Services;

// A private Windows pseudoconsole: the child sees a real terminal, but no terminal window is created.
internal sealed class HiddenConsoleProcess : IAsyncDisposable
{
    private readonly FileStream input;
    private readonly FileStream output;
    private readonly SemaphoreSlim inputGate = new(1, 1);
    private readonly IntPtr pseudoConsole;
    private readonly object disposeGate = new();
    private Task? disposeTask;
    private int stopping;
    public Process Process { get; }
    public Task OutputCompleted { get; }

    private HiddenConsoleProcess(Process process, IntPtr console, SafeFileHandle inputHandle, SafeFileHandle outputHandle, Action<string> onOutput)
    {
        Process = process; pseudoConsole = console;
        input = new FileStream(inputHandle, FileAccess.Write, 4096, isAsync: false);
        output = new FileStream(outputHandle, FileAccess.Read, 4096, isAsync: false);
        OutputCompleted = Task.Factory.StartNew(() => ReadOutput(onOutput), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public static HiddenConsoleProcess Start(ProcessStartInfo info, Action<string> onOutput)
    {
        if (info.Arguments.Length != 0) throw new ArgumentException("Internal voice processes require structured arguments.", nameof(info));
        if (!Path.IsPathFullyQualified(info.FileName) || !File.Exists(info.FileName)) throw new FileNotFoundException("The voice runtime executable was not found.");
        IntPtr console = IntPtr.Zero, attributes = IntPtr.Zero, environment = IntPtr.Zero;
        SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null;
        bool attributesInitialized = false;
        var pi = new ProcessInformation();
        Process? process = null;
        try
        {
            if (!CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0) || !CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create the internal voice transport.");
            int result = CreatePseudoConsole(new Coord(120, 40), inputRead, outputWrite, 0, out console);
            Marshal.ThrowExceptionForHR(result);
            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot allocate the internal voice console attributes.");
            attributesInitialized = true;
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x00020016, console, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot initialize the internal voice console.");
            var si = new StartupInfoEx { Info = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>() }, Attributes = attributes };
            var arguments = new[] { info.FileName }.Concat(info.ArgumentList).Select(QuoteArgument);
            var command = new StringBuilder(string.Join(" ", arguments));
            string environmentText = string.Join("\0", info.Environment.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + "=" + p.Value)) + "\0\0";
            environment = Marshal.StringToHGlobalUni(environmentText);
            if (!CreateProcess(info.FileName, command, IntPtr.Zero, IntPtr.Zero, false, 0x00080000 | 0x00000400, environment, info.WorkingDirectory, ref si, out pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot start the internal voice runtime.");
            try { process = Process.GetProcessById(checked((int)pi.ProcessId)); }
            catch (ArgumentException ex) { throw new InvalidOperationException("The internal voice runtime exited during startup.", ex); }
            var session = new HiddenConsoleProcess(process, console, inputWrite, outputRead, onOutput);
            process = null; console = IntPtr.Zero; inputWrite = outputRead = null;
            return session;
        }
        catch
        {
            if (pi.Process != IntPtr.Zero) TerminateProcess(pi.Process, 1);
            process?.Dispose();
            throw;
        }
        finally
        {
            if (pi.Thread != IntPtr.Zero) CloseHandle(pi.Thread);
            if (pi.Process != IntPtr.Zero) CloseHandle(pi.Process);
            if (attributes != IntPtr.Zero) { if (attributesInitialized) DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            inputRead?.Dispose(); outputWrite?.Dispose();
            inputWrite?.Dispose(); outputRead?.Dispose();
            if (console != IntPtr.Zero) ClosePseudoConsole(console);
        }
    }

    private void ReadOutput(Action<string> onOutput)
    {
        using var reader = new StreamReader(output, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        var buffer = new char[4096];
        var decoder = new ConsoleTextDecoder(reply => _ = ReplyToQueryAsync(reply));
        try
        {
            int count;
            while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                string text = decoder.Feed(new string(buffer, 0, count));
                if (text.Length > 0) onOutput(text);
            }
        }
        catch (Exception ex) when (Volatile.Read(ref stopping) != 0 && ex is IOException or ObjectDisposedException)
        { Trace.TraceInformation("NIVO voice console reader stopped ({0}).", ex.GetType().Name); }
    }

    private async Task ReplyToQueryAsync(string value)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await SendAsync(value, deadline.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        { Trace.TraceInformation("NIVO voice terminal query ended ({0}).", ex.GetType().Name); }
    }

    // Interpret terminal control sequences only; never send provider output as input or execute it.
    internal sealed class ConsoleTextDecoder(Action<string> reply)
    {
        private int state;
        private readonly StringBuilder sequence = new();
        public string Feed(string value)
        {
            var text = new StringBuilder();
            foreach (char c in value)
            {
                if (state == 0)
                {
                    if (c == '\u001b') { state = 1; sequence.Clear(); }
                    else if (!char.IsControl(c) || c is '\r' or '\n' or '\t') text.Append(c);
                }
                else if (state == 1)
                {
                    state = c == '[' ? 2 : c == ']' ? 3 : 0;
                }
                else if (state == 2)
                {
                    sequence.Append(c);
                    if (c is >= '@' and <= '~')
                    {
                        string query = sequence.ToString();
                        if (query == "6n") reply("\u001b[1;1R");
                        else if (query == "c" || query == "0c") reply("\u001b[?1;2c");
                        else if (query == ">c" || query == ">0c") reply("\u001b[>0;1;0c");
                        else if (c is 'H' or 'J') text.Append('\n');
                        state = 0;
                    }
                    else if (sequence.Length > 128) { state = 0; sequence.Clear(); }
                }
                else if (state == 3)
                {
                    if (c == '\u0007') CompleteOsc();
                    else if (c == '\u001b') state = 4;
                    else if (sequence.Length < 4096) sequence.Append(c);
                }
                else if (state == 4)
                {
                    if (c == '\\') CompleteOsc(); else state = 3;
                }
            }
            return text.ToString();
        }
        private void CompleteOsc()
        {
            if (sequence.ToString() == "11;?") reply("\u001b]11;rgb:0000/0000/0000\u001b\\");
            state = 0; sequence.Clear();
        }
    }

    public async Task SendAsync(string value, CancellationToken ct)
    {
        if (Volatile.Read(ref stopping) != 0) throw new ObjectDisposedException(nameof(HiddenConsoleProcess));
        await inputGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            await input.WriteAsync(bytes, ct).ConfigureAwait(false);
            await input.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { inputGate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (disposeGate) return new ValueTask(disposeTask ??= Task.Run(DisposeCoreAsync));
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref stopping, 1);
        try
        {
            try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { Trace.TraceInformation("NIVO internal voice process stop ended ({0}).", ex.GetType().Name); }
            try { await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { Trace.TraceWarning("NIVO internal voice process did not settle ({0}).", ex.GetType().Name); }
            // Keep draining output while ConPTY closes, otherwise its final output can block ClosePseudoConsole.
            await Task.Run(() => ClosePseudoConsole(pseudoConsole)).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            try { await OutputCompleted.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException) { Trace.TraceInformation("NIVO voice console drain ended ({0}).", ex.GetType().Name); }
        }
        finally { input.Dispose(); output.Dispose(); Process.Dispose(); }
    }

    internal static string QuoteArgument(string argument)
    {
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); result.Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential)] private readonly struct Coord(short x, short y) { public readonly short X = x; public readonly short Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo
    {
        public int Size; public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2Size; public IntPtr Reserved2, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, uint size);
    [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint="CreateProcessW", CharSet=CharSet.Unicode, SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfoEx info, out ProcessInformation processInfo);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
