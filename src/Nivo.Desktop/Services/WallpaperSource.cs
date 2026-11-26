using System.Windows.Media.Imaging;
namespace Nivo.Services;

public sealed class WallpaperSource
{
    private string fingerprint = "";
    public BitmapSource? Image { get; private set; }
    public bool RefreshDesktop()
    {
        try { return Refresh(Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper", null) as string); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { System.Diagnostics.Trace.TraceWarning("NIVO wallpaper registry unavailable ({0}).", ex.GetType().Name); return false; }
    }
    public bool Refresh(string? path)
    {
        try
        {
            string next = "";
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) { var file = new FileInfo(path); next = file.FullName + "|" + file.LastWriteTimeUtc.Ticks + "|" + file.Length; }
            if (fingerprint == next) return false;
            BitmapSource? image = null;
            if (next.Length > 0)
            {
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.UriSource = new Uri(Path.GetFullPath(path!)); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache; bitmap.DecodePixelWidth = 1024; bitmap.EndInit(); bitmap.Freeze(); image = bitmap;
            }
            Image = image; fingerprint = next; return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or UriFormatException) { System.Diagnostics.Trace.TraceWarning("NIVO wallpaper refresh failed ({0}).", ex.GetType().Name); return false; } // Retain the prior material during replacement.
    }
}
