using Nivo.Core;
namespace Nivo.Services;

public static class ConversionService
{
    public static async Task<string> Convert(Store store, string file, string kind, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(file)) throw new FileNotFoundException("The selected source file no longer exists.", file);
        string extension = Path.GetExtension(file).ToLowerInvariant();
        if (kind == "pdf_to_word" && extension != ".pdf") throw new InvalidDataException("PDF to Word requires a PDF source.");
        if (kind == "pdf_to_word") return await FileService.OfficeConvert(store, file, "docx", ct);
        if (kind == "to_pdf") return await FileService.OfficeConvert(store, file, "pdf", ct);
        if (kind != "compress_video") throw new InvalidDataException("Unsupported conversion.");
        string output = FileService.OutputPath(store, Path.GetFileNameWithoutExtension(file) + "-smaller", ".mp4");
        await FileService.CompressVideo(file, output, ct); return output;
    }
}
