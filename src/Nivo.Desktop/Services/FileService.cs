using System.IO.Compression;
using System.Xml.Linq;
using System.Diagnostics;
using Nivo.Core;

namespace Nivo.Services;

public static class FileService
{
    public static System.Windows.Media.Imaging.BitmapSource LoadImage(string file)
    {
        if (new FileInfo(file).Length > 15 * 1024 * 1024) throw new InvalidDataException("Image attachments must be smaller than 15 MB.");
        int width, height;
        using (var stream = File.OpenRead(file))
        {
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.OnDemand);
            width = decoder.Frames[0].PixelWidth; height = decoder.Frames[0].PixelHeight;
        }
        if (width < 1 || height < 1 || (long)width * height > 100_000_000) throw new InvalidDataException("This image exceeds the supported dimensions.");
        double scale = Math.Min(1, 1600.0 / Math.Max(width, height));
        var image = new System.Windows.Media.Imaging.BitmapImage(); image.BeginInit(); image.UriSource = new Uri(Path.GetFullPath(file)); image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; image.DecodePixelWidth = Math.Max(1, (int)(width * scale)); image.DecodePixelHeight = Math.Max(1, (int)(height * scale)); image.EndInit(); image.Freeze(); return image;
    }
    public const int MaxContextCharacters = 60_000;
    internal static async Task<bool> CleanupRuntimeFileAsync(string? path)
    {
        if (path == null) return true;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { File.Delete(path); return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 2) { System.Diagnostics.Trace.TraceWarning("NIVO temporary-file cleanup deferred ({0}).", ex.GetType().Name); return false; }
                await Task.Delay(50 * (attempt + 1));
            }
        }
        return false;
    }
    public static string ReadText(string file, CancellationToken ct = default, bool allowPdfMetadata = false)
    {
        ct.ThrowIfCancellationRequested();
        var ext = Path.GetExtension(file).ToLowerInvariant();
        long length = new FileInfo(file).Length;
        if (length > 15 * 1024 * 1024)
        {
            if (allowPdfMetadata && ext == ".pdf" && length <= PdfWordService.MaxInputBytes) return "[PDF source: text preview omitted due to size. Its contents are unknown; use the exact attached path for conversion only, or ask for a smaller extract to analyze.]";
            throw new InvalidDataException("Files must be smaller than 15 MB for text extraction.");
        }
        if (ReviewedEditService.IsAllowedExtension(file)) return Limit(File.ReadAllText(file));
        if (new[] { ".docx", ".pptx", ".xlsx" }.Contains(ext))
        {
            using var zip = ZipFile.OpenRead(file); var text = new StringBuilder(); long expanded = 0;
            foreach (var entry in zip.Entries.Where(e => e.FullName == "word/document.xml" || (e.FullName.StartsWith("ppt/slides/slide") && e.FullName.EndsWith(".xml")) || e.FullName == "xl/sharedStrings.xml" || (e.FullName.StartsWith("xl/worksheets/") && e.FullName.EndsWith(".xml"))))
            {
                ct.ThrowIfCancellationRequested(); expanded += entry.Length; if (expanded > 20 * 1024 * 1024) throw new InvalidDataException("Document expands beyond the 20 MB safety limit.");
                using var stream = entry.Open(); using var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, MaxCharactersInDocument = 2_000_000 }); var doc = XDocument.Load(reader);
                foreach (var node in doc.Descendants().Where(n => n.Name.LocalName is "t" or "v")) { text.AppendLine(node.Value); if (text.Length > 60_000) return Limit(text.ToString()); }
            }
            return Limit(text.ToString());
        }
        if (ext == ".pdf")
        {
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(file); var text = new StringBuilder();
            foreach (var page in pdf.GetPages()) { ct.ThrowIfCancellationRequested(); text.AppendLine(page.Text); if (text.Length > 60_000) break; }
            if (text.Length < 5)
            {
                if (allowPdfMetadata) return "[PDF source without selectable text: contents are unknown. Use its exact attached path for convert_word; scanned pages become images without OCR. Ask for a capture if content analysis is needed.]";
                throw new InvalidDataException("This PDF contains no selectable text. Use Screen or Crop to ask about scanned pages.");
            }
            return Limit(text.ToString());
        }
        throw new InvalidDataException("Attach text, PDF, DOCX, PPTX, or XLSX. Image attachments use Screen/Crop.");
    }
    private static string Limit(string text) => text.Length > MaxContextCharacters ? text[..MaxContextCharacters] + "\n[truncated at 60000 characters]" : text;
    public static IEnumerable<string> CompletedOutputs(Store store) => Directory.EnumerateFiles(store.Output).Where(path => !IsStagedOutput(path));
    private static bool IsStagedOutput(string path)
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        return new[] { ".nivo-video-", ".nivo-pdf2docx-" }.Any(prefix => stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(stem[prefix.Length..], "N", out _));
    }
    public static string OutputPath(Store store, string name, string ext)
    {
        var clean = string.Concat(Path.GetFileNameWithoutExtension(name).Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        if (string.IsNullOrWhiteSpace(clean)) clean = "NIVO-output";
        clean = clean[..Math.Min(clean.Length, 80)];
        return Path.Combine(store.Output, clean + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..6] + ext);
    }
    public static string FindTool(string name)
    {
        foreach (var rawPath in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string dir = rawPath.Trim().Trim('"').Trim();
            if (string.IsNullOrWhiteSpace(dir) || dir.IndexOfAny(Path.GetInvalidPathChars()) >= 0) continue;
            var file = Path.Combine(dir, name + ".exe");
            if (File.Exists(file)) return name == "soffice" && File.Exists(Path.ChangeExtension(file, ".com")) ? Path.ChangeExtension(file, ".com") : file;
        }
        if (name == "soffice")
        {
            foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NIVO.Local", "Tools", "LibreOffice") })
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                foreach (string relative in new[] { "LibreOffice/program/soffice.exe", "program/soffice.exe", "ProgramFiles64/LibreOffice/program/soffice.exe" })
                { string file = Path.Combine(root, relative); if (File.Exists(file)) return name == "soffice" && File.Exists(Path.ChangeExtension(file, ".com")) ? Path.ChangeExtension(file, ".com") : file; }
            }
        }
        throw new FileNotFoundException(name + " was not found. Install it locally and add it to PATH.");
    }
    public static async Task CompressVideo(string input, string output, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(output)) throw new IOException("Choose a new output filename; existing files are never overwritten.");
        if (!File.Exists(input) || !new[] { ".mp4", ".mov", ".mkv", ".webm", ".avi" }.Contains(Path.GetExtension(input).ToLowerInvariant())) throw new InvalidDataException("Choose a video file.");
        string stagedOutput = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, ".nivo-video-" + Guid.NewGuid().ToString("N") + Path.GetExtension(output));
        var info = new ProcessStartInfo(FindTool("ffmpeg")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-nostdin", "-n", "-i", Path.GetFullPath(input), "-map_metadata", "-1", "-vf", "scale=ceil(iw/2)*2:ceil(ih/2)*2", "-pix_fmt", "yuv420p", "-c:v", "libx264", "-crf", "28", "-preset", "fast", "-c:a", "aac", "-b:a", "96k", "-movflags", "+faststart", stagedOutput }) info.ArgumentList.Add(a);
        using var p = Process.Start(info) ?? throw new InvalidOperationException("Cannot start FFmpeg.");
        using var reg = ct.Register(() => StopConversionProcess(p));
        var err = p.StandardError.ReadToEndAsync(ct); var stdout = p.StandardOutput.ReadToEndAsync(ct);
        try
        {
            await p.WaitForExitAsync(ct); await stdout; var error = await err;
            if (p.ExitCode != 0) throw new InvalidOperationException("Video conversion failed: " + error[^Math.Min(error.Length, 500)..]);
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(stagedOutput) || new FileInfo(stagedOutput).Length == 0) throw new InvalidDataException("Video conversion returned an empty output.");
            if (new FileInfo(stagedOutput).Length >= new FileInfo(input).Length) throw new InvalidDataException("This video is already compact; recompression did not make it smaller. The source is preserved.");
            File.Move(stagedOutput, output);
        }
        finally
        {
            StopConversionProcess(p);
            await p.WaitForExitAsync();
            DeleteConversionFile(stagedOutput);
        }
    }
    public static async Task<string> OfficeConvert(Store store, string input, string extension, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(input)) throw new FileNotFoundException("Source file not found.", input);
        if (!new[] { "pdf", "docx" }.Contains(extension)) throw new InvalidDataException("Unsupported conversion.");
        string inputExt = Path.GetExtension(input).ToLowerInvariant();
        if (extension == "docx" && inputExt == ".pdf")
        {
            ct.ThrowIfCancellationRequested(); var output = OutputPath(store, Path.GetFileNameWithoutExtension(input), ".docx");
            bool created = false;
            try
            {
                await PdfWordService.ConvertAsync(input, output, ct); created = true;
                ct.ThrowIfCancellationRequested(); return output;
            }
            catch { if (created) DeleteConversionFile(output); throw; }
        }
        if (extension == "docx" && !new[] { ".doc", ".odt", ".rtf", ".txt" }.Contains(inputExt)) throw new InvalidDataException("Choose a PDF or supported document (.doc, .odt, .rtf, .txt) for Word conversion.");
        if (extension == "pdf" && !new[] { ".docx", ".pptx", ".xlsx", ".odt", ".ods", ".odp" }.Contains(inputExt)) throw new InvalidDataException("Choose an Office document or spreadsheet for PDF conversion.");
        var tool = Path.GetFullPath(FindTool("soffice")); var stagingRoot = Path.Combine(Path.GetTempPath(), "NivoOffice");
        var folder = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var info = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-env:UserInstallation=" + new Uri(Path.Combine(folder, "profile")).AbsoluteUri, "--headless", "--convert-to", extension, "--outdir", folder, Path.GetFullPath(input) }) info.ArgumentList.Add(a);
        using var p = Process.Start(info) ?? throw new InvalidOperationException("Cannot start LibreOffice."); p.StandardInput.Close(); using var reg = ct.Register(() => StopConversionProcess(p));
        var result = Path.Combine(folder, Path.GetFileNameWithoutExtension(input) + "." + extension);
        var stdout = p.StandardOutput.ReadToEndAsync(ct); var stderr = p.StandardError.ReadToEndAsync(ct);
        try
        {
            await p.WaitForExitAsync(ct); string log = await stdout; string error = await stderr; ct.ThrowIfCancellationRequested();
            if (p.ExitCode != 0 || !File.Exists(result) || new FileInfo(result).Length == 0) throw new InvalidOperationException($"LibreOffice exited {p.ExitCode} without a valid output ({tool}). " + (error.Length > 0 ? error : log)[..Math.Min(500, error.Length > 0 ? error.Length : log.Length)]);
            string output = OutputPath(store, Path.GetFileNameWithoutExtension(input), "." + extension); File.Move(result, output); return output;
        }
        finally
        {
            StopConversionProcess(p);
            await p.WaitForExitAsync();
            string resolved = Path.GetFullPath(folder), runtime = Path.GetFullPath(stagingRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(runtime, StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
            {
                try { Directory.Delete(resolved, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Trace.TraceWarning("NIVO conversion directory cleanup failed ({0}).", ex.GetType().Name); }
            }
        }
    }

    private static void StopConversionProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        { Trace.TraceWarning("NIVO conversion process stop failed ({0}).", ex.GetType().Name); }
    }

    private static void DeleteConversionFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Trace.TraceWarning("NIVO partial conversion cleanup failed ({0}).", ex.GetType().Name); }
    }
}
