using System.Security.Cryptography;
using Nivo.Core;

namespace Nivo.Services;

public sealed record ReviewedEditResult(
    string TargetPath,
    string BackupPath,
    string PreviousSha256,
    string NewSha256,
    int BytesWritten);

public static class ReviewedEditService
{
    public const long MaxSourceBytes = 256 * 1024;
    public const int MaxReplacementChars = 100_000;

    // Strictly bounded plain text/code/config extensions.
    // Explicitly excludes binaries, executables, shell scripts (.bat/.cmd/.ps1/.vbs/.sh/.reg/.lnk), and Office/archive containers.
    public static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".json", ".cs", ".js", ".jsx", ".ts", ".tsx", ".py",
        ".html", ".htm", ".css", ".scss", ".xml", ".xaml", ".yml", ".yaml", ".toml",
        ".sql", ".c", ".h", ".cpp", ".hpp", ".java", ".kt", ".go", ".rs", ".rb", ".php", ".log"
    };

    public static bool IsAllowedExtension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && AllowedExtensions.Contains(ext);
    }

    public static bool IsValidSha256(string? hash)
    {
        if (string.IsNullOrEmpty(hash) || hash.Length != 64) return false;
        foreach (char c in hash)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    public static bool CanEditSourcePath(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string full = NormalizeExistingTarget(path);
            return IsAllowedExtension(full) && IsUtf8Text(ReadSourceBytes(full));
        }
        catch (Exception ex) when (ExpectedSourceFailure(ex)) { return false; }
    }

    public static Dictionary<string, string> SnapshotEditableSources(IEnumerable<string>? attachedFiles)
    {
        var snapshots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (attachedFiles == null) return snapshots;
        foreach (var file in attachedFiles)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(file)) continue;
                string full = NormalizeExistingTarget(file);
                if (!IsAllowedExtension(full)) continue;
                byte[] bytes = ReadSourceBytes(full);
                if (!IsUtf8Text(bytes)) continue;
                snapshots[full] = HashBytes(bytes);
            }
            catch (Exception ex) when (ExpectedSourceFailure(ex)) { System.Diagnostics.Trace.TraceInformation("NIVO skipped a non-editable source ({0}).", ex.GetType().Name); }
        }
        return snapshots;
    }

    public static string ValidateTarget(string? targetPath, IReadOnlyCollection<string> attachedFiles)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            throw new InvalidDataException("Edit step is missing a target source file path.");
        if (attachedFiles == null || attachedFiles.Count == 0)
            throw new InvalidDataException("Code and text edits require an explicitly attached source file. Attach the file and try again.");

        string full = NormalizeExistingTarget(targetPath);

        bool explicitlyAttached = false;
        foreach (var f in attachedFiles)
        {
            if (string.IsNullOrWhiteSpace(f)) continue;
            try
            {
                if (string.Equals(Path.GetFullPath(f), full, StringComparison.OrdinalIgnoreCase))
                {
                    explicitlyAttached = true;
                    break;
                }
            }
            catch (Exception ex) when (ExpectedSourceFailure(ex)) { System.Diagnostics.Trace.TraceInformation("NIVO skipped a non-editable source ({0}).", ex.GetType().Name); }
        }
        if (!explicitlyAttached)
            throw new InvalidDataException("Edits can only modify files you explicitly attached. Attach the source file and try again.");

        if (!IsAllowedExtension(full))
            throw new InvalidDataException("This file extension is not allowed for reviewed text/code edits.");

        byte[] bytes = ReadSourceBytes(full);
        if (!IsUtf8Text(bytes))
            throw new InvalidDataException("Reviewed edits require UTF-8 text, not binary or unsupported text encodings.");

        return full;
    }

    public static void ValidateReplacement(string? replacementText)
    {
        if (replacementText == null)
            throw new InvalidDataException("Edit step is missing its replacement text.");
        if (replacementText.Length > MaxReplacementChars)
            throw new InvalidDataException($"Replacement content exceeds the {MaxReplacementChars} character limit.");
        if (replacementText.Contains('\0'))
            throw new InvalidDataException("Replacement text contains invalid NUL bytes.");
    }

    public static string ComputeSha256(string filePath)
    {
        string full = NormalizeExistingTarget(filePath);
        return HashBytes(ReadSourceBytes(full));
    }

    public static async Task<ReviewedEditResult> ApplyAsync(
        Store store,
        string targetPath,
        string replacementText,
        string expectedSha256,
        IReadOnlyCollection<string> allowedSources,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsValidSha256(expectedSha256))
            throw new InvalidDataException("Edit step is missing a valid optimistic content-hash precondition.");

        string full = ValidateTarget(targetPath, allowedSources);
        ValidateReplacement(replacementText);

        byte[] originalBytes = await Task.Run(() => ReadSourceBytes(full), ct).ConfigureAwait(false);
        if (originalBytes.Length > MaxSourceBytes)
            throw new InvalidDataException($"Source file exceeds the {MaxSourceBytes / 1024} KB limit for reviewed edits.");
        if (!IsUtf8Text(originalBytes))
            throw new InvalidDataException("Binary files cannot be modified with reviewed text/code edits.");

        string currentSha = HashBytes(originalBytes);
        if (!string.Equals(currentSha, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The source file changed after the edit plan was prepared. Aborted without modifying the original file.");

        // Create and verify backup inside Store/Backups before touching the target file.
        string backupPath = CreateVerifiedBackup(store, full, originalBytes, currentSha);

        bool hasUtf8Bom = originalBytes.Length >= 3 && originalBytes[0] == 0xEF && originalBytes[1] == 0xBB && originalBytes[2] == 0xBF;
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasUtf8Bom);
        byte[] replacementBytes = hasUtf8Bom ? encoding.GetPreamble().Concat(encoding.GetBytes(replacementText)).ToArray() : encoding.GetBytes(replacementText);
        if (replacementBytes.Length > MaxSourceBytes)
            throw new InvalidDataException($"Replacement file exceeds the {MaxSourceBytes / 1024} KB limit.");

        string dir = Path.GetDirectoryName(full) ?? throw new InvalidDataException("Target file has no parent directory.");
        string tempPath = Path.Combine(dir, "." + Path.GetFileName(full) + ".nivo-tmp-" + Guid.NewGuid().ToString("N"));
        bool replaced = false;

        try
        {
            ct.ThrowIfCancellationRequested();
            await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await fs.WriteAsync(replacementBytes, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();

            // Re-verify optimistic precondition immediately prior to atomic swap to narrow TOCTOU window.
            string preSwapSha = HashBytes(await Task.Run(() => ReadSourceBytes(full), ct).ConfigureAwait(false));
            if (!string.Equals(preSwapSha, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The source file changed concurrently before write. Aborted without modifying the original file.");

            File.Replace(tempPath, full, destinationBackupFileName: null, ignoreMetadataErrors: true);
            replaced = true;

            string newSha = HashBytes(replacementBytes);
            return new ReviewedEditResult(full, backupPath, currentSha, newSha, replacementBytes.Length);
        }
        finally
        {
            if (!replaced && File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Trace.TraceWarning("NIVO edit temp cleanup failed ({0}).", ex.GetType().Name); }
            }
        }
    }

    private static string CreateVerifiedBackup(Store store, string fullTargetPath, byte[] originalBytes, string expectedSha)
    {
        string backupDir = Path.Combine(store.Root, "Backups");
        Directory.CreateDirectory(backupDir);

        string baseName = string.Concat(Path.GetFileNameWithoutExtension(fullTargetPath)
            .Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "source";
        if (baseName.Length > 60) baseName = baseName[..60];
        string ext = Path.GetExtension(fullTargetPath);

        string stamp = $"{baseName}-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..8]}";
        string backupPath = Path.Combine(backupDir, stamp + ext + ".bak");

        File.WriteAllBytes(backupPath, originalBytes);

        string verifiedSha = HashBytes(ReadSourceBytes(backupPath));
        if (!string.Equals(verifiedSha, expectedSha, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Backup verification failed. Aborted without modifying the original file.");

        return backupPath;
    }

    private static string NormalizeExistingTarget(string path)
    {
        if (path.Contains('\0')) throw new InvalidDataException("Invalid file path.");
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new InvalidDataException("Device paths are not allowed for reviewed edits.");

        string full = Path.GetFullPath(path);
        if (Directory.Exists(full))
            throw new InvalidDataException("Reviewed edits target files only, not directories.");
        if (!File.Exists(full))
            throw new FileNotFoundException("Attached source file was not found.", full);

        for (var directory = new DirectoryInfo(Path.GetDirectoryName(full)!); directory != null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Source paths through symbolic-link directories are not allowed for reviewed edits.");
        var info = new FileInfo(full);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Symbolic links and reparse points cannot be modified by reviewed edits.");
        if (info.Length > MaxSourceBytes)
            throw new InvalidDataException($"Source file exceeds the {MaxSourceBytes / 1024} KB limit for reviewed edits.");

        return full;
    }

    private static byte[] ReadSourceBytes(string path)
    {
        // Hold a read-only share while checking and reading: a concurrent writer cannot grow
        // the file between FileInfo.Length and an unbounded ReadAllBytes allocation.
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaxSourceBytes) throw new InvalidDataException("Source file exceeds the reviewed edit size limit.");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        int bomCharacters = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 1 : 0;
        if (Encoding.UTF8.GetCharCount(bytes) - bomCharacters > FileService.MaxContextCharacters)
            throw new InvalidDataException($"Reviewed source edits require the complete file within the {FileService.MaxContextCharacters} character context limit. Split the source before requesting an in-place edit.");
        return bytes;
    }

    private static bool ExpectedSourceFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;
    private static bool IsUtf8Text(byte[] bytes)
    {
        if (ContainsBinaryNullByte(bytes)) return false;
        try { new UTF8Encoding(false, true).GetCharCount(bytes); return true; }
        catch (DecoderFallbackException) { return false; }
    }

    private static bool ContainsBinaryNullByte(byte[] bytes) => Array.IndexOf(bytes, (byte)0) >= 0;

    private static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
