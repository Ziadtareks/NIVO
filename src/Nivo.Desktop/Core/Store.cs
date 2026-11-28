using System.Runtime.InteropServices;

namespace Nivo.Core;

public sealed class Store
{
    public string Root { get; }
    public string Runtime => Path.Combine(Root, "runtime");
    public string Output => Path.Combine(Root, "Outputs");
    public AppData Data { get; private set; }
    public bool Incognito { get; set; }
    public event Action? Changed;
    public Store(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NIVO.Local");
        Directory.CreateDirectory(Root); Directory.CreateDirectory(Runtime); Directory.CreateDirectory(Output);
        Data = new();
        var file = Path.Combine(Root, "data.json");
        Data.Settings.PendingModelDiscovery = !File.Exists(file);
        if (File.Exists(file))
        {
            try { Data = JsonSerializer.Deserialize<AppData>(File.ReadAllText(file)) ?? new(); }
            catch (JsonException) { File.Copy(file, file + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); }
        }
    }
    public void Save()
    {
        var file = Path.Combine(Root, "data.json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(file + ".tmp", file, true); Changed?.Invoke();
    }
    public void AddMemory(string text)
    {
        if (Incognito) throw new InvalidOperationException("Memory is paused in Incognito.");
        if (string.IsNullOrWhiteSpace(text)) return;
        Data.Memories.Add(new() { Text = text.Trim() }); Save();
    }
    public void Record(TaskRecord task) { if (!Incognito) { Data.Tasks.Insert(0, task); if (Data.Tasks.Count > 200) Data.Tasks.RemoveAt(200); Save(); } }
    public void SetSecret(string name, string value)
    {
        var path = Path.Combine(Root, name + ".secret");
        if (string.IsNullOrEmpty(value)) { if (File.Exists(path)) File.Delete(path); return; }
        File.WriteAllBytes(path, Protect(Encoding.UTF8.GetBytes(value), true));
    }
    public string GetSecret(string name)
    {
        var path = Path.Combine(Root, name + ".secret");
        if (!File.Exists(path)) return "";
        try { return Encoding.UTF8.GetString(Protect(File.ReadAllBytes(path), false)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { throw new InvalidOperationException("The saved API key could not be decrypted for this Windows user. Re-enter it in Settings.", ex); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptProtectData(ref Blob input, string? desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr data);
    private static byte[] Protect(byte[] bytes, bool encrypt)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool ok = encrypt ? CryptProtectData(ref input, "NIVO API key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output) : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception();
            byte[] result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }
}
