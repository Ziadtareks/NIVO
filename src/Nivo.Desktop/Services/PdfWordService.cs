using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;
using Windows.Data.Pdf;
using Windows.Storage.Streams;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using PdfPigPage = UglyToad.PdfPig.Content.Page;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Nivo.Services;

public static class PdfWordService
{
    public const long MaxInputBytes = 25L * 1024 * 1024;
    public const int MaxPages = 200;
    public const int MaxExtractedCharacters = 500_000;
    public const int MaxEmbeddedImagesTotal = 250;
    public const int MaxEmbeddedImageBytes = 8 * 1024 * 1024;
    public const long MaxTotalImageBytes = 64L * 1024 * 1024;
    public const long MaxImagePixels = 25_000_000L;
    public const int MaxScannedRenderedPages = 50;
    public const double ScannedRenderDpi = 150.0;
    public const int MaxRenderPixelDimension = 2400;

    private const uint MinPageTwips = 1440;   // 1 inch
    private const uint MaxPageTwips = 31680;  // 22 inches (Word maximum)
    private const long EmuPerPoint = 12700L;
    private const long EmuPerPixelAt96Dpi = 9525L;

    public static Task ConvertAsync(string inputPath, string outputPath, CancellationToken ct = default)
    {
        return Task.Run(() => ConvertCoreAsync(inputPath, outputPath, ct), ct);
    }

    private static async Task ConvertCoreAsync(string inputPath, string outputPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("Source PDF file not found.", inputPath);
        if (File.Exists(outputPath))
            throw new IOException("Choose a new output filename; existing files are never overwritten.");

        var fileInfo = new FileInfo(inputPath);
        if (fileInfo.Length == 0)
            throw new InvalidDataException("The selected PDF file is empty.");
        if (fileInfo.Length > MaxInputBytes)
            throw new InvalidDataException($"PDF files must be smaller than {MaxInputBytes / (1024 * 1024)} MB for local Word conversion.");

        string? outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        string stagedPath = Path.Combine(outputDir!, ".nivo-pdf2docx-" + Guid.NewGuid().ToString("N") + ".docx");
        NativePdfRenderer? nativeRenderer = null;
        bool nativeRendererAttempted = false;

        try
        {
            UglyToad.PdfPig.PdfDocument pdf;
            try
            {
                pdf = UglyToad.PdfPig.PdfDocument.Open(inputPath, new ParsingOptions { SkipMissingFonts = true });
            }
            catch (PdfDocumentEncryptedException ex)
            {
                throw new InvalidDataException("Password-protected PDFs cannot be converted locally.", ex);
            }
            catch (Exception ex) when (ex is PdfDocumentFormatException or EndOfStreamException)
            {
                throw new InvalidDataException("This PDF is corrupted or uses an unsupported PDF structure.", ex);
            }

            using (pdf)
            {
                ct.ThrowIfCancellationRequested();
                if (pdf.NumberOfPages < 1)
                    throw new InvalidDataException("This PDF contains no pages.");
                if (pdf.NumberOfPages > MaxPages)
                    throw new InvalidDataException($"PDFs with more than {MaxPages} pages exceed the local conversion limit.");

                var pages = new List<PageModel>(pdf.NumberOfPages);
                int totalCharacters = 0;
                int totalImages = 0;
                long totalImageBytes = 0;
                int renderedScannedPages = 0;
                int failedVisualPages = 0;

                for (int pageNumber = 1; pageNumber <= pdf.NumberOfPages; pageNumber++)
                {
                    ct.ThrowIfCancellationRequested();
                    PdfPigPage page;
                    try
                    {
                        page = pdf.GetPage(pageNumber);
                    }
                    catch (Exception ex) when (ex is PdfDocumentFormatException or EndOfStreamException)
                    {
                        throw new InvalidDataException($"Page {pageNumber} of this PDF could not be parsed.", ex);
                    }

                    double rawPageWidthPt = page.Width > 1 ? page.Width : 612.0;
                    double rawPageHeightPt = page.Height > 1 ? page.Height : 792.0;

                    var extractedLetters = ExtractSanitizedLetters(
                        page,
                        rawPageWidthPt,
                        rawPageHeightPt,
                        MaxExtractedCharacters - totalCharacters,
                        ct,
                        out int pageNormalizeAngle,
                        out double pageWidthPt,
                        out double pageHeightPt);
                    bool hasSelectableText = extractedLetters.Any(l => !string.IsNullOrWhiteSpace(l.Text));

                    if (hasSelectableText)
                    {
                        var pageImages = ExtractEmbeddedImages(page, rawPageWidthPt, rawPageHeightPt, pageNormalizeAngle, rotateToVisualPage: false, ref totalImages, ref totalImageBytes, ct);
                        var contentBounds = ComputeContentBounds(extractedLetters, pageImages, pageWidthPt, pageHeightPt);
                        var section = BuildSectionSpec(pageWidthPt, pageHeightPt, contentBounds, isFullPageImage: false, isRtlPage: false);

                        var textBlocks = BuildTextBlocks(extractedLetters, pageWidthPt, pageHeightPt, section, ct);
                        int pageChars = textBlocks.Sum(b => b.CharacterCount);
                        totalCharacters += pageChars;
                        if (totalCharacters > MaxExtractedCharacters)
                            throw new InvalidDataException($"This PDF exceeds the {MaxExtractedCharacters:N0}-character local conversion limit.");

                        bool isRtlPage = IsPredominantlyRtl(textBlocks);
                        section = section with { IsRtl = isRtlPage };

                        var elements = InterleavePageElements(textBlocks, pageImages);
                        pages.Add(new PageModel(section, elements, HasContent: elements.Count > 0));
                    }
                    else
                    {
                        bool hasVisualOperations = page.NumberOfImages > 0 || page.Paths.Count > 0 || page.Operations.Any(op => op is UglyToad.PdfPig.Graphics.Operations.PaintShading or UglyToad.PdfPig.Graphics.Operations.InvokeNamedXObject);
                        if (!hasVisualOperations)
                        {
                            var emptySection = BuildSectionSpec(rawPageWidthPt, rawPageHeightPt, null, isFullPageImage: false, isRtlPage: false);
                            pages.Add(new PageModel(emptySection, [], HasContent: false));
                            continue;
                        }

                        if (renderedScannedPages >= MaxScannedRenderedPages)
                            throw new InvalidDataException($"Scanned or image-only PDFs with more than {MaxScannedRenderedPages} visual pages exceed the local conversion limit.");

                        var imageSection = BuildSectionSpec(rawPageWidthPt, rawPageHeightPt, null, isFullPageImage: true, isRtlPage: false);
                        ExtractedImage? renderedPageImage = null;

                        if (!nativeRendererAttempted) { nativeRendererAttempted = true; nativeRenderer = await NativePdfRenderer.TryOpenAsync(inputPath, ct); }
                        if (nativeRenderer != null)
                        {
                            renderedPageImage = await nativeRenderer.TryRenderPageAsync((uint)(pageNumber - 1), rawPageWidthPt, rawPageHeightPt, ct);
                        }

                        if (renderedPageImage != null)
                        {
                            if (totalImages >= MaxEmbeddedImagesTotal || totalImageBytes + renderedPageImage.Bytes.Length > MaxTotalImageBytes)
                                throw new InvalidDataException($"This PDF exceeds the {MaxTotalImageBytes / (1024 * 1024)} MB local image budget.");

                            var renderedSection = BuildSectionSpec(renderedPageImage.WidthPt, renderedPageImage.HeightPt, null, isFullPageImage: true, isRtlPage: false);
                            totalImages++;
                            totalImageBytes += renderedPageImage.Bytes.Length;
                            renderedScannedPages++;
                            pages.Add(new PageModel(renderedSection, [new PageElement(renderedPageImage.HeightPt, null, renderedPageImage, IsFullPageScan: true)], HasContent: true));
                        }
                        else
                        {
                            var fallbackImages = ExtractEmbeddedImages(page, rawPageWidthPt, rawPageHeightPt, normalizeAngle: 0, rotateToVisualPage: true, ref totalImages, ref totalImageBytes, ct);
                            // A recovered logo must never stand in for an unrenderable scan or vector page.
                            if (page.NumberOfImages == 1 && page.Paths.Count == 0 && fallbackImages.Count == 1 && fallbackImages[0].WidthPt >= rawPageWidthPt * .9 && fallbackImages[0].HeightPt >= rawPageHeightPt * .9)
                            {
                                renderedScannedPages++;
                                var items = fallbackImages
                                    .OrderByDescending(i => i.TopPt)
                                    .Select(i => new PageElement(i.TopPt, null, i, IsFullPageScan: fallbackImages.Count == 1))
                                    .ToList();
                                pages.Add(new PageModel(imageSection, items, HasContent: true));
                            }
                            else
                            {
                                failedVisualPages++;
                                pages.Add(new PageModel(imageSection, [], HasContent: false));
                            }
                        }
                    }
                }

                if (failedVisualPages > 0)
                    throw new InvalidDataException($"{failedVisualPages} PDF page(s) could not be rendered locally. No incomplete Word document was created.");
                bool anyContent = pages.Any(p => p.HasContent);
                if (!anyContent)
                {
                    throw new InvalidDataException("This PDF contains no extractable text or visual content.");
                }

                ct.ThrowIfCancellationRequested();
                WriteDocx(stagedPath, pages, ct);
            }

            ct.ThrowIfCancellationRequested();
            if (!File.Exists(stagedPath) || new FileInfo(stagedPath).Length == 0)
                throw new InvalidDataException("PDF-to-Word conversion produced an empty output.");

            File.Move(stagedPath, outputPath);
        }
        finally
        {
            nativeRenderer?.Dispose();
            TryDeleteFile(stagedPath);
        }
    }

    private static (double X, double Y) TransformPoint(double x, double y, int normalizeAngle, double spaceWidth, double spaceHeight) =>
        normalizeAngle switch
        {
            // PdfPig applies /Rotate clockwise to page coordinates; these inverse transforms restore upright horizontal space.
            90 => (spaceHeight - y, x),
            180 => (spaceWidth - x, spaceHeight - y),
            270 => (y, spaceWidth - x),
            _ => (x, y)
        };

    private static (double Left, double Right, double Bottom, double Top) TransformRect(
        double rawLeft,
        double rawRight,
        double rawBottom,
        double rawTop,
        int normalizeAngle,
        double spaceWidth,
        double spaceHeight)
    {
        var p1 = TransformPoint(rawLeft, rawBottom, normalizeAngle, spaceWidth, spaceHeight);
        var p2 = TransformPoint(rawLeft, rawTop, normalizeAngle, spaceWidth, spaceHeight);
        var p3 = TransformPoint(rawRight, rawBottom, normalizeAngle, spaceWidth, spaceHeight);
        var p4 = TransformPoint(rawRight, rawTop, normalizeAngle, spaceWidth, spaceHeight);
        double left = Math.Min(Math.Min(p1.X, p2.X), Math.Min(p3.X, p4.X));
        double right = Math.Max(Math.Max(p1.X, p2.X), Math.Max(p3.X, p4.X));
        double bottom = Math.Min(Math.Min(p1.Y, p2.Y), Math.Min(p3.Y, p4.Y));
        double top = Math.Max(Math.Max(p1.Y, p2.Y), Math.Max(p3.Y, p4.Y));
        return (left, right, bottom, top);
    }

    private static (double OriginX, double OriginY) ComputeCropOrigin(PdfPigPage page, double rawPageWidthPt, double rawPageHeightPt)
    {
        double cropLeft = Math.Min(page.CropBox.Bounds.Left, page.CropBox.Bounds.Right);
        double cropRight = Math.Max(page.CropBox.Bounds.Left, page.CropBox.Bounds.Right);
        double cropBottom = Math.Min(page.CropBox.Bounds.Bottom, page.CropBox.Bounds.Top);
        double cropTop = Math.Max(page.CropBox.Bounds.Bottom, page.CropBox.Bounds.Top);
        if (Math.Abs((cropRight - cropLeft) - rawPageWidthPt) < 1.0 && Math.Abs((cropTop - cropBottom) - rawPageHeightPt) < 1.0)
            return (cropLeft, cropBottom);
        return (0, 0);
    }

    private static List<GlyphInfo> ExtractSanitizedLetters(
        PdfPigPage page,
        double rawPageWidthPt,
        double rawPageHeightPt,
        int remainingCharacters,
        CancellationToken ct,
        out int pageNormalizeAngle,
        out double normalizedPageWidthPt,
        out double normalizedPageHeightPt)
    {
        var rawLetters = page.Letters;
        if (rawLetters.Count > MaxExtractedCharacters) throw new InvalidDataException("This PDF page exceeds the local glyph extraction limit.");

        int horizCount = 0, rot90Count = 0, rot180Count = 0, rot270Count = 0;
        for (int i = 0; i < rawLetters.Count; i++)
        {
            if ((i & 511) == 0) ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(rawLetters[i].Value)) continue;
            switch (rawLetters[i].TextOrientation)
            {
                case TextOrientation.Horizontal: horizCount++; break;
                case TextOrientation.Rotate90: rot90Count++; break;
                case TextOrientation.Rotate180: rot180Count++; break;
                case TextOrientation.Rotate270: rot270Count++; break;
            }
        }

        pageNormalizeAngle = 0;
        if (rot90Count > horizCount && rot90Count >= rot180Count && rot90Count >= rot270Count) pageNormalizeAngle = 90;
        else if (rot270Count > horizCount && rot270Count >= rot90Count && rot270Count >= rot180Count) pageNormalizeAngle = 270;
        else if (rot180Count > horizCount && rot180Count >= rot90Count && rot180Count >= rot270Count) pageNormalizeAngle = 180;

        normalizedPageWidthPt = pageNormalizeAngle is 90 or 270 ? rawPageHeightPt : rawPageWidthPt;
        normalizedPageHeightPt = pageNormalizeAngle is 90 or 270 ? rawPageWidthPt : rawPageHeightPt;

        var result = new List<GlyphInfo>(rawLetters.Count);
        int extractedCharacters = 0;
        var (originX, originY) = ComputeCropOrigin(page, rawPageWidthPt, rawPageHeightPt);

        for (int i = 0; i < rawLetters.Count; i++)
        {
            if ((i & 511) == 0) ct.ThrowIfCancellationRequested();
            var letter = rawLetters[i];
            if (letter.Value.Length > remainingCharacters - extractedCharacters) throw new InvalidDataException("This PDF exceeds the local character extraction limit.");
            extractedCharacters += letter.Value.Length;
            string sanitized = SanitizeXmlText(letter.Value);
            if (sanitized.Length == 0) continue;

            var rect = letter.GlyphRectangle;
            var (left, right, bottom, top) = TransformRect(
                rect.Left - originX,
                rect.Right - originX,
                rect.Bottom - originY,
                rect.Top - originY,
                pageNormalizeAngle,
                rawPageWidthPt,
                rawPageHeightPt);
            var (startX, startY) = TransformPoint(letter.StartBaseLine.X - originX, letter.StartBaseLine.Y - originY, pageNormalizeAngle, rawPageWidthPt, rawPageHeightPt);
            var (endX, endY) = TransformPoint(letter.EndBaseLine.X - originX, letter.EndBaseLine.Y - originY, pageNormalizeAngle, rawPageWidthPt, rawPageHeightPt);
            double baselineY = (startY + endY) * 0.5;

            double advanceLeft = Math.Min(left, Math.Min(startX, endX));
            double advanceRight = Math.Max(right, Math.Max(startX, endX));

            int letterAngle = letter.TextOrientation switch
            {
                TextOrientation.Horizontal => 0,
                TextOrientation.Rotate90 => 90,
                TextOrientation.Rotate180 => 180,
                TextOrientation.Rotate270 => 270,
                _ => -1
            };
            int residualAngle = letterAngle < 0 ? -1 : ((letterAngle - pageNormalizeAngle) % 360 + 360) % 360;

            double fallbackSpan = residualAngle is 90 or 270 ? (right - left) : (top - bottom);
            double fontSize = letter.PointSize > 0.5 ? letter.PointSize
                : letter.FontSize > 0.5 ? letter.FontSize
                : Math.Max(6.0, fallbackSpan);
            fontSize = Math.Clamp(fontSize, 5.0, 96.0);

            string rawFontName = letter.FontName ?? letter.Font?.Name ?? "";
            bool isBold = (letter.Font?.IsBold ?? false) || (letter.Font?.Weight >= 600) || FontNameIndicatesBold(rawFontName);
            bool isItalic = (letter.Font?.IsItalic ?? false) || FontNameIndicatesItalic(rawFontName);
            string? colorHex = ExtractReadableColorHex(letter.FillColor ?? letter.Color);
            string normalizedFont = MapFontFamily(rawFontName);

            // Detect faux-bold duplicate glyphs drawn at nearly identical coordinates.
            if (result.Count > 0 && !string.IsNullOrWhiteSpace(sanitized))
            {
                var prev = result[^1];
                double dupTol = Math.Max(fontSize * 0.08, 0.8);
                if (prev.Text == sanitized &&
                    prev.ResidualAngle == residualAngle &&
                    Math.Abs(prev.Left - left) <= dupTol &&
                    Math.Abs(prev.BaselineY - baselineY) <= dupTol)
                {
                    if (!prev.IsBold)
                        result[^1] = prev with { IsBold = true };
                    continue;
                }
            }

            result.Add(new GlyphInfo(
                StreamIndex: i,
                Text: sanitized,
                Left: left,
                Right: right,
                AdvanceLeft: advanceLeft,
                AdvanceRight: advanceRight,
                Bottom: bottom,
                Top: top,
                BaselineY: baselineY,
                StartBaselineX: startX,
                StartBaselineY: startY,
                EndBaselineX: endX,
                EndBaselineY: endY,
                FontSize: fontSize,
                FontFamily: normalizedFont,
                IsBold: isBold,
                IsItalic: isItalic,
                ColorHex: colorHex,
                ResidualAngle: residualAngle));
        }

        return result;
    }

    private static List<ExtractedImage> ExtractEmbeddedImages(
        PdfPigPage page,
        double rawPageWidthPt,
        double rawPageHeightPt,
        int normalizeAngle,
        bool rotateToVisualPage,
        ref int totalImages,
        ref long totalImageBytes,
        CancellationToken ct)
    {
        var extracted = new List<ExtractedImage>();
        if (page.NumberOfImages <= 0) return extracted;
        if (totalImages >= MaxEmbeddedImagesTotal || totalImageBytes >= MaxTotalImageBytes) throw new InvalidDataException("This PDF exceeds the local image budget.");

        var (originX, originY) = ComputeCropOrigin(page, rawPageWidthPt, rawPageHeightPt);
        int pageRotation = ((page.Rotation.Value % 360) + 360) % 360;

        foreach (var image in page.GetImages())
        {
            ct.ThrowIfCancellationRequested();
            if (totalImages >= MaxEmbeddedImagesTotal || totalImageBytes >= MaxTotalImageBytes)
                throw new InvalidDataException("This PDF exceeds the local image budget.");

            if (image.IsImageMask) continue;
            if (image.WidthInSamples < 2 || image.HeightInSamples < 2) continue;
            if ((long)image.WidthInSamples * image.HeightInSamples > MaxImagePixels) throw new InvalidDataException("A PDF image exceeds the local decoded-pixel limit.");

            var bounds = image.Bounds;
            var (leftPt, rightPt, bottomPt, topPt) = TransformRect(
                bounds.Left - originX,
                bounds.Right - originX,
                bounds.Bottom - originY,
                bounds.Top - originY,
                normalizeAngle,
                rawPageWidthPt,
                rawPageHeightPt);
            double widthPt = rightPt - leftPt;
            double heightPt = topPt - bottomPt;
            if (widthPt <= 0 || heightPt <= 0) continue;

            try
            {
                if (!TryExtractImagePayload(image, out var bytes, out var partType, out int pixelWidth, out int pixelHeight))
                    throw new InvalidDataException("An embedded PDF image could not be decoded within the local limits.");

                if (rotateToVisualPage && pageRotation is 90 or 180 or 270)
                {
                    if (!TryRotateImagePayload(bytes, pageRotation, out bytes, out partType, out pixelWidth, out pixelHeight))
                        throw new InvalidDataException("A rotated PDF page image could not be normalized within the local limits.");
                }

                if (bytes.Length == 0 || bytes.Length > MaxEmbeddedImageBytes) throw new InvalidDataException("An embedded PDF image exceeds the local encoded-size limit.");
                if (totalImageBytes + bytes.Length > MaxTotalImageBytes) throw new InvalidDataException("This PDF exceeds the local image byte budget.");

                extracted.Add(new ExtractedImage(
                    Bytes: bytes,
                    PartType: partType,
                    PixelWidth: pixelWidth > 0 ? pixelWidth : image.WidthInSamples,
                    PixelHeight: pixelHeight > 0 ? pixelHeight : image.HeightInSamples,
                    LeftPt: leftPt,
                    RightPt: rightPt,
                    TopPt: topPt,
                    BottomPt: bottomPt,
                    WidthPt: widthPt,
                    HeightPt: heightPt));

                totalImages++;
                totalImageBytes += bytes.Length;
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or FileFormatException or ArgumentException or OverflowException or IOException or COMException)
            {
                throw new InvalidDataException($"An embedded image on PDF page {page.Number} could not be converted. No incomplete Word document was created.", ex);
            }
        }

        return extracted;
    }

    private static bool TryExtractImagePayload(IPdfImage image, out byte[] bytes, out PartTypeInfo partType, out int pixelWidth, out int pixelHeight)
    {
        bytes = [];
        partType = ImagePartType.Png;
        pixelWidth = 0;
        pixelHeight = 0;

        var rawSpan = image.RawMemory.Span;
        if (HasJpegSignature(rawSpan) && rawSpan.Length > MaxEmbeddedImageBytes) return false;
        if (rawSpan.Length >= 4 && rawSpan.Length <= MaxEmbeddedImageBytes)
        {
            if (HasJpegSignature(rawSpan))
            {
                byte[] jpegBytes = rawSpan.ToArray();
                if (TryValidateBitmap(jpegBytes, out pixelWidth, out pixelHeight, out bool isCmyk))
                {
                    if (isCmyk)
                    {
                        if (TryConvertToRgbPng(jpegBytes, out var convertedPng))
                        {
                            bytes = convertedPng;
                            partType = ImagePartType.Png;
                            return true;
                        }
                        return false;
                    }

                    bytes = jpegBytes;
                    partType = ImagePartType.Jpeg;
                    return true;
                }
            }
            else if (HasPngSignature(rawSpan))
            {
                byte[] directPng = rawSpan.ToArray();
                if (TryValidateBitmap(directPng, out pixelWidth, out pixelHeight, out _))
                {
                    bytes = directPng;
                    partType = ImagePartType.Png;
                    return true;
                }
            }
        }

        // Preserve native JPEG payloads before considering a potentially much larger decoded PNG.
        if (image.TryGetPng(out var pngBytes) && pngBytes is { Length: >= 8 and <= MaxEmbeddedImageBytes } && HasPngSignature(pngBytes) &&
            TryValidateBitmap(pngBytes, out pixelWidth, out pixelHeight, out bool pngCmyk) && !pngCmyk)
        { bytes = pngBytes; partType = ImagePartType.Png; return true; }
        return false;
    }

    private static BitmapDecoder DecodeKnownImage(Stream stream, ReadOnlySpan<byte> bytes, BitmapCreateOptions options, BitmapCacheOption cache)
    {
        if (HasPngSignature(bytes)) return new PngBitmapDecoder(stream, options, cache);
        if (HasJpegSignature(bytes)) return new JpegBitmapDecoder(stream, options, cache);
        throw new InvalidDataException("Only validated PNG and JPEG images are accepted in PDF conversion.");
    }

    private static bool TryValidateBitmap(byte[] payload, out int width, out int height, out bool isCmyk)
    {
        width = 0;
        height = 0;
        isCmyk = false;
        using var ms = new MemoryStream(payload, writable: false);
        var decoder = DecodeKnownImage(ms, payload, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
        if (decoder.Frames.Count == 0) return false;
        var frame = decoder.Frames[0];
        width = frame.PixelWidth;
        height = frame.PixelHeight;
        if (width < 2 || height < 2 || (long)width * height > MaxImagePixels) return false;
        isCmyk = frame.Format == System.Windows.Media.PixelFormats.Cmyk32;
        return true;
    }

    private static bool TryRotateImagePayload(byte[] sourceBytes, int clockwiseDegrees, out byte[] rotatedBytes, out PartTypeInfo partType, out int width, out int height)
    {
        rotatedBytes = [];
        partType = ImagePartType.Png;
        width = 0;
        height = 0;

        using var input = new MemoryStream(sourceBytes, writable: false);
        var decoder = DecodeKnownImage(input, sourceBytes, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        if (decoder.Frames.Count == 0) return false;
        var frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > MaxImagePixels) return false;

        BitmapSource source = frame.Format == System.Windows.Media.PixelFormats.Cmyk32
            ? new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0)
            : frame;
        var transformed = new TransformedBitmap(source, new System.Windows.Media.RotateTransform(clockwiseDegrees));
        transformed.Freeze();
        width = transformed.PixelWidth;
        height = transformed.PixelHeight;
        if (width < 2 || height < 2 || (long)width * height > MaxImagePixels) return false;

        try
        {
            var pngEncoder = new PngBitmapEncoder();
            pngEncoder.Frames.Add(BitmapFrame.Create(transformed));
            using var pngOut = new CappedImageStream();
            pngEncoder.Save(pngOut);
            if (pngOut.Length > 0 && pngOut.Length <= MaxEmbeddedImageBytes)
            {
                rotatedBytes = pngOut.ToArray();
                partType = ImagePartType.Png;
                return true;
            }
        }
        catch (InvalidDataException)
        {
            // Fall through to JPEG if rotated PNG exceeds the per-image byte cap.
        }

        var jpgEncoder = new JpegBitmapEncoder { QualityLevel = 88 };
        jpgEncoder.Frames.Add(BitmapFrame.Create(transformed));
        using var jpgOut = new CappedImageStream();
        jpgEncoder.Save(jpgOut);
        if (jpgOut.Length == 0 || jpgOut.Length > MaxEmbeddedImageBytes) return false;
        rotatedBytes = jpgOut.ToArray();
        partType = ImagePartType.Jpeg;
        return true;
    }

    private static bool TryConvertToRgbPng(byte[] sourceBytes, out byte[] pngBytes)
    {
        pngBytes = [];
        using var input = new MemoryStream(sourceBytes, writable: false);
        var decoder = DecodeKnownImage(input, sourceBytes, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        if (decoder.Frames.Count == 0) return false;
        var frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > MaxImagePixels) return false;
        var converted = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(converted));
        using var output = new CappedImageStream();
        encoder.Save(output);
        if (output.Length == 0 || output.Length > MaxEmbeddedImageBytes) return false;
        pngBytes = output.ToArray();
        return true;
    }

    private static ContentBounds? ComputeContentBounds(List<GlyphInfo> letters, List<ExtractedImage> images, double pageWidthPt, double pageHeightPt)
    {
        double minLeft = double.PositiveInfinity;
        double maxRight = double.NegativeInfinity;
        double minBottom = double.PositiveInfinity;
        double maxTop = double.NegativeInfinity;

        foreach (var l in letters)
        {
            if (string.IsNullOrWhiteSpace(l.Text)) continue;
            if (l.Right < 0 || l.Left > pageWidthPt || l.Top < 0 || l.Bottom > pageHeightPt) continue;
            minLeft = Math.Min(minLeft, Math.Max(0, l.Left));
            maxRight = Math.Max(maxRight, Math.Min(pageWidthPt, l.Right));
            minBottom = Math.Min(minBottom, Math.Max(0, l.Bottom));
            maxTop = Math.Max(maxTop, Math.Min(pageHeightPt, l.Top));
        }

        foreach (var img in images)
        {
            if (img.RightPt < 0 || img.LeftPt > pageWidthPt || img.TopPt < 0 || img.BottomPt > pageHeightPt) continue;
            minLeft = Math.Min(minLeft, Math.Max(0, img.LeftPt));
            maxRight = Math.Max(maxRight, Math.Min(pageWidthPt, img.RightPt));
            minBottom = Math.Min(minBottom, Math.Max(0, img.BottomPt));
            maxTop = Math.Max(maxTop, Math.Min(pageHeightPt, img.TopPt));
        }

        if (!double.IsFinite(minLeft) || !double.IsFinite(maxRight) || maxRight <= minLeft)
            return null;

        return new ContentBounds(minLeft, maxRight, minBottom, maxTop);
    }

    private static SectionSpec BuildSectionSpec(double pageWidthPt, double pageHeightPt, ContentBounds? bounds, bool isFullPageImage, bool isRtlPage)
    {
        uint widthTwips = (uint)Math.Clamp((long)Math.Round(pageWidthPt * 20.0), MinPageTwips, MaxPageTwips);
        uint heightTwips = (uint)Math.Clamp((long)Math.Round(pageHeightPt * 20.0), MinPageTwips, MaxPageTwips);
        bool landscape = widthTwips > heightTwips;

        double marginLeftPt, marginRightPt, marginTopPt, marginBottomPt;
        if (isFullPageImage)
        {
            double m = Math.Clamp(Math.Min(pageWidthPt, pageHeightPt) * 0.03, 9.0, 18.0);
            marginLeftPt = marginRightPt = marginTopPt = marginBottomPt = m;
        }
        else
        {
            double minMarginX = Math.Min(32.0, pageWidthPt * 0.08);
            double maxMarginX = Math.Min(72.0, pageWidthPt * 0.20);
            double minMarginY = Math.Min(32.0, pageHeightPt * 0.08);
            double maxMarginY = Math.Min(72.0, pageHeightPt * 0.20);

            if (bounds != null)
            {
                marginLeftPt = Math.Clamp(bounds.MinLeft, minMarginX, maxMarginX);
                marginRightPt = Math.Clamp(pageWidthPt - bounds.MaxRight, minMarginX, maxMarginX);
                marginTopPt = Math.Clamp(pageHeightPt - bounds.MaxTop, minMarginY, maxMarginY);
                marginBottomPt = Math.Clamp(bounds.MinBottom, minMarginY, maxMarginY);
            }
            else
            {
                marginLeftPt = marginRightPt = Math.Min(54.0, maxMarginX);
                marginTopPt = marginBottomPt = Math.Min(54.0, maxMarginY);
            }
        }

        uint leftTwips = (uint)Math.Clamp((long)Math.Round(marginLeftPt * 20.0), 180, widthTwips / 3);
        uint rightTwips = (uint)Math.Clamp((long)Math.Round(marginRightPt * 20.0), 180, widthTwips / 3);
        int topTwips = (int)Math.Clamp((long)Math.Round(marginTopPt * 20.0), 180, heightTwips / 3);
        int bottomTwips = (int)Math.Clamp((long)Math.Round(marginBottomPt * 20.0), 180, heightTwips / 3);

        return new SectionSpec(widthTwips, heightTwips, landscape, leftTwips, rightTwips, topTwips, bottomTwips, isRtlPage);
    }

    private static List<ParagraphBlock> BuildTextBlocks(List<GlyphInfo> letters, double pageWidthPt, double pageHeightPt, SectionSpec section, CancellationToken ct)
    {
        var horizontalLetters = letters.Where(l => l.ResidualAngle == 0).ToList();
        var rotatedLetters = letters.Where(l => l.ResidualAngle != 0).ToList();

        var horizontalLines = GroupIntoVisualLines(horizontalLetters, ct);
        // Detect clear two-column flow on horizontal lines before appending minority rotated labels.
        var orderedLines = ReorderTwoColumnIfPresent(horizontalLines, pageWidthPt);
        if (rotatedLetters.Count > 0)
            orderedLines.AddRange(GroupRotatedLetters(rotatedLetters, pageWidthPt, pageHeightPt, ct));

        if (orderedLines.Count == 0)
            return [];

        // Compute modal/median body font size and typical line step for paragraph grouping.
        double bodyFontSize = ComputeBodyFontSize(orderedLines);
        double typicalLineStep = ComputeTypicalLineStep(orderedLines, bodyFontSize);
        double contentLeftPt = section.MarginLeftTwips / 20.0;
        double contentRightPt = pageWidthPt - section.MarginRightTwips / 20.0;
        double contentWidthPt = Math.Max(72.0, contentRightPt - contentLeftPt);

        var blocks = new List<ParagraphBlock>();
        var currentLines = new List<VisualLine> { orderedLines[0] };

        for (int i = 1; i < orderedLines.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var prev = orderedLines[i - 1];
            var curr = orderedLines[i];

            if (ShouldStartNewParagraph(prev, curr, bodyFontSize, typicalLineStep, contentLeftPt, contentRightPt, contentWidthPt))
            {
                blocks.Add(MergeLinesToParagraph(currentLines, bodyFontSize, contentLeftPt, contentRightPt, contentWidthPt));
                currentLines = [curr];
            }
            else
            {
                currentLines.Add(curr);
            }
        }

        if (currentLines.Count > 0)
            blocks.Add(MergeLinesToParagraph(currentLines, bodyFontSize, contentLeftPt, contentRightPt, contentWidthPt));

        return blocks;
    }

    private static List<VisualLine> GroupIntoVisualLines(List<GlyphInfo> letters, CancellationToken ct)
    {
        if (letters.Count == 0) return [];

        // Sort top-to-bottom by baseline, then left-to-right.
        var sorted = letters.OrderByDescending(l => l.BaselineY).ThenBy(l => l.Left).ToList();
        var clusters = new List<List<GlyphInfo>>();
        var clusterBaselines = new List<double>();
        var clusterFontSizes = new List<double>();

        for (int i = 0; i < sorted.Count; i++)
        {
            if ((i & 511) == 0) ct.ThrowIfCancellationRequested();
            var g = sorted[i];
            int bestCluster = -1;
            double bestDist = double.MaxValue;

            // Check only recent clusters since sorted descending by baselineY.
            for (int c = clusters.Count - 1; c >= 0; c--)
            {
                double diff = Math.Abs(clusterBaselines[c] - g.BaselineY);
                double tol = Math.Max(Math.Min(clusterFontSizes[c], g.FontSize) * 0.45, 2.5);
                if (diff <= tol && diff < bestDist)
                {
                    bestDist = diff;
                    bestCluster = c;
                }
                if (clusterBaselines[c] - g.BaselineY > Math.Max(clusterFontSizes[c], g.FontSize) * 1.5)
                    break;
            }

            if (bestCluster >= 0)
            {
                clusters[bestCluster].Add(g);
                int n = clusters[bestCluster].Count;
                clusterBaselines[bestCluster] += (g.BaselineY - clusterBaselines[bestCluster]) / n;
                clusterFontSizes[bestCluster] = Math.Max(clusterFontSizes[bestCluster], g.FontSize);
            }
            else
            {
                clusters.Add([g]);
                clusterBaselines.Add(g.BaselineY);
                clusterFontSizes.Add(g.FontSize);
            }
        }

        var lines = new List<VisualLine>(clusters.Count);
        foreach (var cluster in clusters)
        {
            ct.ThrowIfCancellationRequested();
            var line = BuildVisualLine(cluster, columnIndex: 0);
            if (line != null) lines.Add(line);
        }

        lines.Sort((a, b) => b.BaselineY.CompareTo(a.BaselineY));
        return lines;
    }

    private static List<VisualLine> GroupRotatedLetters(List<GlyphInfo> rotated, double pageWidthPt, double pageHeightPt, CancellationToken ct)
    {
        if (rotated.Count == 0) return [];
        var lines = new List<VisualLine>();
        int nextRotatedColumnId = -1;

        foreach (int angle in new[] { 90, 270, 180 })
        {
            var group = rotated.Where(g => g.ResidualAngle == angle).ToList();
            if (group.Count == 0) continue;

            var localGlyphs = new List<GlyphInfo>(group.Count);
            foreach (var g in group)
            {
                var (left, right, bottom, top) = TransformRect(g.Left, g.Right, g.Bottom, g.Top, angle, pageWidthPt, pageHeightPt);
                var (startX, startY) = TransformPoint(g.StartBaselineX, g.StartBaselineY, angle, pageWidthPt, pageHeightPt);
                var (endX, endY) = TransformPoint(g.EndBaselineX, g.EndBaselineY, angle, pageWidthPt, pageHeightPt);
                double baselineY = (startY + endY) * 0.5;
                localGlyphs.Add(g with
                {
                    Left = left,
                    Right = right,
                    AdvanceLeft = Math.Min(left, Math.Min(startX, endX)),
                    AdvanceRight = Math.Max(right, Math.Max(startX, endX)),
                    Bottom = bottom,
                    Top = top,
                    BaselineY = baselineY,
                    StartBaselineX = startX,
                    StartBaselineY = startY,
                    EndBaselineX = endX,
                    EndBaselineY = endY,
                    ResidualAngle = 0
                });
            }

            foreach (var localLine in GroupIntoVisualLines(localGlyphs, ct))
            {
                lines.Add(localLine with { ColumnIndex = nextRotatedColumnId-- });
            }
        }

        var oblique = rotated.Where(g => g.ResidualAngle < 0).OrderBy(g => g.StreamIndex).ToList();
        if (oblique.Count > 0)
        {
            var obliqueLine = BuildVisualLine(oblique, columnIndex: nextRotatedColumnId--, preserveStreamOrder: true);
            if (obliqueLine != null) lines.Add(obliqueLine);
        }

        return lines;
    }

    private static VisualLine? BuildVisualLine(List<GlyphInfo> glyphs, int columnIndex, bool preserveStreamOrder = false)
    {
        if (glyphs.Count == 0) return null;

        var nonWhitespace = glyphs.Where(g => !string.IsNullOrWhiteSpace(g.Text)).ToList();
        if (nonWhitespace.Count == 0) return null;

        double left = nonWhitespace.Min(g => g.Left);
        double right = nonWhitespace.Max(g => g.Right);
        double top = nonWhitespace.Max(g => g.Top);
        double bottom = nonWhitespace.Min(g => g.Bottom);
        double baselineY = nonWhitespace.Average(g => g.BaselineY);
        double primaryFontSize = ComputeWeightedMedianFontSize(nonWhitespace);
        double lineHeight = Math.Max(primaryFontSize, top - bottom);

        // Split horizontal segments if there are wide visual gaps (tabs / columns).
        List<List<GlyphInfo>> segments;
        if (preserveStreamOrder)
        {
            segments = [glyphs];
        }
        else
        {
            var visualSorted = glyphs.OrderBy(g => g.Left).ThenBy(g => g.StreamIndex).ToList();
            double tabGapThreshold = Math.Max(primaryFontSize * 2.4, 24.0);
            segments = SplitHorizontalSegments(visualSorted, tabGapThreshold);
        }

        // Determine dominant script direction across the line.
        int totalRtl = 0, totalLtr = 0;
        foreach (var g in nonWhitespace)
        {
            ClassifyTextDirection(g.Text, ref totalRtl, ref totalLtr);
        }
        bool isRtlLine = totalRtl > 0 && totalRtl >= totalLtr;

        // If RTL line has multiple segments, order segments right-to-left.
        if (isRtlLine && segments.Count > 1 && !preserveStreamOrder)
            segments.Reverse();

        var runs = new List<StyledRun>();
        var segmentBounds = new List<(double Left, double Right)>(segments.Count);

        for (int s = 0; s < segments.Count; s++)
        {
            var seg = segments[s];
            var segNonSpace = seg.Where(g => !string.IsNullOrWhiteSpace(g.Text)).ToList();
            if (segNonSpace.Count == 0) continue;

            segmentBounds.Add((segNonSpace.Min(g => g.Left), segNonSpace.Max(g => g.Right)));

            if (runs.Count > 0)
                runs.Add(StyledRun.Tab(primaryFontSize, nonWhitespace[0].FontFamily, isRtlLine));

            var segRuns = preserveStreamOrder
                ? AssembleRunsFromOrderedGlyphs(seg, isRtlLine)
                : AssembleLogicalRuns(seg, primaryFontSize, isRtlLine);

            runs.AddRange(segRuns);
        }

        if (runs.Count == 0) return null;

        string plainText = string.Concat(runs.Select(r => r.IsTab ? "\t" : r.Text)).Trim();
        if (plainText.Length == 0) return null;

        bool allBold = nonWhitespace.All(g => g.IsBold);
        return new VisualLine(
            Runs: CoalesceRuns(runs),
            PlainText: plainText,
            Left: left,
            Right: right,
            Top: top,
            Bottom: bottom,
            BaselineY: baselineY,
            PrimaryFontSize: primaryFontSize,
            LineHeight: lineHeight,
            IsRtl: isRtlLine,
            AllBold: allBold,
            SegmentBounds: segmentBounds,
            ColumnIndex: columnIndex);
    }

    private static List<List<GlyphInfo>> SplitHorizontalSegments(List<GlyphInfo> visualSorted, double tabGapThreshold)
    {
        var segments = new List<List<GlyphInfo>>();
        var current = new List<GlyphInfo> { visualSorted[0] };
        double currentMaxRight = visualSorted[0].Right;

        for (int i = 1; i < visualSorted.Count; i++)
        {
            var g = visualSorted[i];
            if (!string.IsNullOrWhiteSpace(g.Text) && g.Left - currentMaxRight > tabGapThreshold)
            {
                segments.Add(current);
                current = [g];
                currentMaxRight = g.Right;
            }
            else
            {
                current.Add(g);
                if (!string.IsNullOrWhiteSpace(g.Text))
                    currentMaxRight = Math.Max(currentMaxRight, g.Right);
            }
        }

        segments.Add(current);
        return segments;
    }

    private static List<StyledRun> AssembleLogicalRuns(List<GlyphInfo> visualSortedSegment, double fontSize, bool isRtlLine)
    {
        // Step 1: Split visual-sorted glyphs into visual words by whitespace glyphs or horizontal advance gaps.
        double avgCharWidth = EstimateAverageGlyphWidth(visualSortedSegment, fontSize);
        double spaceThreshold = Math.Clamp(Math.Max(fontSize * 0.20, avgCharWidth * 0.32), 1.4, Math.Max(4.5, fontSize * 0.55));

        var words = new List<VisualWord>();
        var currentWord = new List<GlyphInfo>();
        double currentAdvanceRight = 0;

        foreach (var g in visualSortedSegment)
        {
            if (string.IsNullOrWhiteSpace(g.Text))
            {
                FlushWord(words, currentWord);
                continue;
            }

            if (currentWord.Count > 0 && g.AdvanceLeft - currentAdvanceRight > spaceThreshold)
            {
                FlushWord(words, currentWord);
            }

            currentWord.Add(g);
            currentAdvanceRight = currentWord.Count == 1 ? g.AdvanceRight : Math.Max(currentAdvanceRight, g.AdvanceRight);
        }
        FlushWord(words, currentWord);

        if (words.Count == 0) return [];

        // Step 2: Order directional runs according to base line direction (inverse of UBA visual layout).
        // Group consecutive words into directional runs (LTR vs RTL; neutral words inherit surrounding direction).
        var wordDirections = new int[words.Count]; // +1 = LTR, -1 = RTL, 0 = neutral
        for (int i = 0; i < words.Count; i++)
        {
            int rtl = 0, ltr = 0;
            foreach (var g in words[i].Glyphs) ClassifyTextDirection(g.Text, ref rtl, ref ltr);
            wordDirections[i] = rtl > 0 && rtl >= ltr ? -1 : ltr > 0 ? 1 : 0;
        }

        // Resolve neutral words (digits/punctuation) to line base direction unless sandwiched between two opposite-direction words.
        int baseDir = isRtlLine ? -1 : 1;
        for (int i = 0; i < words.Count; i++)
        {
            if (wordDirections[i] != 0) continue;
            bool isNumeric = words[i].Glyphs.All(g => g.Text.All(c => char.IsDigit(c) || c is '.' or ',' or '%' or '-' or '+' or ':' or '/'));
            if (isNumeric)
            {
                wordDirections[i] = 1; // Numbers always read left-to-right internally.
            }
            else
            {
                int prevDir = i > 0 ? wordDirections[i - 1] : baseDir;
                int nextDir = i + 1 < words.Count && wordDirections[i + 1] != 0 ? wordDirections[i + 1] : baseDir;
                wordDirections[i] = prevDir == nextDir ? prevDir : baseDir;
            }
        }

        // Build contiguous directional runs (in visual left-to-right order).
        var dirRuns = new List<(int Dir, List<VisualWord> Words)>();
        for (int i = 0; i < words.Count; i++)
        {
            int d = wordDirections[i];
            if (dirRuns.Count > 0 && dirRuns[^1].Dir == d)
                dirRuns[^1].Words.Add(words[i]);
            else
                dirRuns.Add((d, [words[i]]));
        }

        // If base line is RTL, reverse the sequence of directional runs (rightmost run comes first logically).
        if (isRtlLine)
            dirRuns.Reverse();

        var resultRuns = new List<StyledRun>();
        bool firstWord = true;

        foreach (var (dir, runWords) in dirRuns)
        {
            // Inside an RTL run, words read right-to-left; inside an LTR run, words stay left-to-right.
            IEnumerable<VisualWord> orderedRunWords = dir < 0 ? runWords.AsEnumerable().Reverse() : runWords;
            foreach (var word in orderedRunWords)
            {
                if (!firstWord && resultRuns.Count > 0)
                {
                    var sample = word.Glyphs[0];
                    resultRuns.Add(new StyledRun(" ", sample.FontSize, sample.FontFamily, sample.IsBold, sample.IsItalic, sample.ColorHex, dir < 0, IsTab: false));
                }
                firstWord = false;

                var orderedWordGlyphs = OrderWordGlyphs(word.Glyphs, isRtlWord: dir < 0, out bool reversedFromVisualLtr);
                foreach (var g in orderedWordGlyphs)
                {
                    string text = NormalizeEditableUnicode(g.Text);
                    if (reversedFromVisualLtr) text = MirrorPairedBrackets(text);
                    if (text.Length == 0) continue;
                    resultRuns.Add(new StyledRun(text, g.FontSize, g.FontFamily, g.IsBold, g.IsItalic, g.ColorHex, dir < 0 || ContainsRtl(text), IsTab: false));
                }
            }
        }

        return resultRuns;
    }

    private static List<GlyphInfo> OrderWordGlyphs(List<GlyphInfo> visualLtrGlyphs, bool isRtlWord, out bool reversedFromVisualLtr)
    {
        reversedFromVisualLtr = false;
        if (!isRtlWord || visualLtrGlyphs.Count <= 1)
            return visualLtrGlyphs;

        // Check if PDF emitted glyphs in logical order positioned right-to-left (stream index increases as X decreases).
        int r2lStreamSteps = 0;
        int l2rStreamSteps = 0;
        var byStream = visualLtrGlyphs.OrderBy(g => g.StreamIndex).ToList();
        for (int i = 1; i < byStream.Count; i++)
        {
            double dx = byStream[i].Left - byStream[i - 1].Left;
            if (dx < -0.25) r2lStreamSteps++;
            else if (dx > 0.25) l2rStreamSteps++;
        }

        if (r2lStreamSteps > l2rStreamSteps)
        {
            // Stream order is already logical right-to-left order.
            return byStream;
        }

        // Visual-LTR emitted Arabic (or shaped presentation forms): read visually right-to-left,
        // keeping combining diacritical marks immediately after their base character.
        reversedFromVisualLtr = true;
        var baseGroups = visualLtrGlyphs.Where(g => !IsCombiningMarkOnly(g.Text)).Select(g => (CenterX: (g.Left + g.Right) * .5, Cluster: new List<GlyphInfo> { g })).ToList();
        if (baseGroups.Count == 0) return visualLtrGlyphs;
        foreach (var g in visualLtrGlyphs.Where(g => IsCombiningMarkOnly(g.Text)))
        {
            double cx = (g.Left + g.Right) * 0.5;
                // Attach combining mark to the closest base glyph in X.
                int closest = 0;
                double minD = double.MaxValue;
                for (int k = 0; k < baseGroups.Count; k++)
                {
                    double d = Math.Abs(baseGroups[k].CenterX - cx);
                    if (d < minD) { minD = d; closest = k; }
                }
                baseGroups[closest].Cluster.Add(g);
        }

        baseGroups.Sort((a, b) => b.CenterX.CompareTo(a.CenterX));
        var ordered = new List<GlyphInfo>(visualLtrGlyphs.Count);
        foreach (var group in baseGroups)
            ordered.AddRange(group.Cluster);
        return ordered;
    }

    private static List<StyledRun> AssembleRunsFromOrderedGlyphs(List<GlyphInfo> glyphs, bool isRtl)
    {
        var runs = new List<StyledRun>(glyphs.Count);
        foreach (var g in glyphs)
        {
            string text = NormalizeEditableUnicode(g.Text);
            if (text.Length == 0) continue;
            runs.Add(new StyledRun(text, g.FontSize, g.FontFamily, g.IsBold, g.IsItalic, g.ColorHex, isRtl || ContainsRtl(text), IsTab: false));
        }
        return runs;
    }

    private static void FlushWord(List<VisualWord> words, List<GlyphInfo> currentWord)
    {
        if (currentWord.Count == 0) return;
        words.Add(new VisualWord([.. currentWord]));
        currentWord.Clear();
    }

    private static List<VisualLine> ReorderTwoColumnIfPresent(List<VisualLine> lines, double pageWidthPt)
    {
        if (lines.Count < 6) return lines;

        // Look for a consistent central vertical gutter splitting at least 4 lines into left and right columns.
        double midMin = pageWidthPt * 0.35;
        double midMax = pageWidthPt * 0.65;
        var splitLines = new List<(int Index, VisualLine Line, double GapLeft, double GapRight)>();

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.SegmentBounds.Count != 2) continue;
            var seg0 = line.SegmentBounds[0];
            var seg1 = line.SegmentBounds[1];
            double leftSegRight = Math.Min(seg0.Right, seg1.Right);
            double rightSegLeft = Math.Max(seg0.Left, seg1.Left);
            if (rightSegLeft - leftSegRight < 14.0) continue;

            double gapCenter = (leftSegRight + rightSegLeft) * 0.5;
            if (gapCenter >= midMin && gapCenter <= midMax)
                splitLines.Add((i, line, leftSegRight, rightSegLeft));
        }

        if (splitLines.Count < 4) return lines;

        double gutterLeft = splitLines.Max(s => s.GapLeft);
        double gutterRight = splitLines.Min(s => s.GapRight);
        if (gutterRight - gutterLeft < 10.0) return lines;

        double gutterX = (gutterLeft + gutterRight) * 0.5;
        double bandTop = splitLines.Max(s => s.Line.BaselineY);
        double bandBottom = splitLines.Min(s => s.Line.BaselineY);

        var header = new List<VisualLine>();
        var leftCol = new List<VisualLine>();
        var rightCol = new List<VisualLine>();
        var footer = new List<VisualLine>();

        foreach (var line in lines)
        {
            if (line.BaselineY > bandTop + line.LineHeight * 0.6)
            {
                header.Add(line);
                continue;
            }
            if (line.BaselineY < bandBottom - line.LineHeight * 0.6)
            {
                footer.Add(line);
                continue;
            }

            if (line.Right <= gutterRight + 4.0)
            {
                leftCol.Add(line with { ColumnIndex = 1 });
            }
            else if (line.Left >= gutterLeft - 4.0)
            {
                rightCol.Add(line with { ColumnIndex = 2 });
            }
            else if (line.SegmentBounds.Count == 2)
            {
                // Split runs at the tab separator into left and right column lines.
                int tabIdx = line.Runs.FindIndex(r => r.IsTab);
                if (tabIdx > 0 && tabIdx + 1 < line.Runs.Count)
                {
                    var firstHalf = line.Runs.Take(tabIdx).ToList();
                    var secondHalf = line.Runs.Skip(tabIdx + 1).ToList();
                    var leftRuns = line.IsRtl ? secondHalf : firstHalf;
                    var rightRuns = line.IsRtl ? firstHalf : secondHalf;
                    var leftBounds = line.SegmentBounds[0].Left < line.SegmentBounds[1].Left ? line.SegmentBounds[0] : line.SegmentBounds[1];
                    var rightBounds = line.SegmentBounds[0].Left < line.SegmentBounds[1].Left ? line.SegmentBounds[1] : line.SegmentBounds[0];

                    string leftText = string.Concat(leftRuns.Select(r => r.Text)).Trim();
                    string rightText = string.Concat(rightRuns.Select(r => r.Text)).Trim();
                    if (leftText.Length > 0)
                        leftCol.Add(line with { Runs = leftRuns, PlainText = leftText, Left = leftBounds.Left, Right = leftBounds.Right, SegmentBounds = [leftBounds], ColumnIndex = 1 });
                    if (rightText.Length > 0)
                        rightCol.Add(line with { Runs = rightRuns, PlainText = rightText, Left = rightBounds.Left, Right = rightBounds.Right, SegmentBounds = [rightBounds], ColumnIndex = 2 });
                }
                else
                {
                    header.Add(line);
                }
            }
            else
            {
                header.Add(line);
            }
        }

        if (leftCol.Count < 3 || rightCol.Count < 3)
            return lines;

        bool pageIsRtl = lines.Count(l => l.IsRtl) > lines.Count / 2;
        var reordered = new List<VisualLine>(lines.Count + splitLines.Count);
        reordered.AddRange(header);
        if (pageIsRtl)
        {
            reordered.AddRange(rightCol);
            reordered.AddRange(leftCol);
        }
        else
        {
            reordered.AddRange(leftCol);
            reordered.AddRange(rightCol);
        }
        reordered.AddRange(footer);
        return reordered;
    }

    private static bool ShouldStartNewParagraph(
        VisualLine prev,
        VisualLine curr,
        double bodyFontSize,
        double typicalLineStep,
        double contentLeftPt,
        double contentRightPt,
        double contentWidthPt)
    {
        // Column boundary always breaks paragraphs.
        if (prev.ColumnIndex != curr.ColumnIndex) return true;

        // Direction change breaks paragraphs.
        if (prev.IsRtl != curr.IsRtl) return true;

        // Tabular / multi-segment rows stay as separate lines.
        if (prev.SegmentBounds.Count > 1 || curr.SegmentBounds.Count > 1) return true;

        // Significant vertical gap (or jumping back up between columns).
        double baselineDelta = prev.BaselineY - curr.BaselineY;
        double maxNormalStep = Math.Max(Math.Max(prev.LineHeight, curr.LineHeight) * 1.48, typicalLineStep * 1.32);
        if (baselineDelta < -2.0 || baselineDelta > maxNormalStep) return true;

        // Heading / font size change.
        if (Math.Abs(curr.PrimaryFontSize - prev.PrimaryFontSize) >= Math.Max(1.8, prev.PrimaryFontSize * 0.16))
            return true;

        // Bold heading followed by regular body text.
        if (prev.AllBold && !curr.AllBold && prev.PlainText.Length < 110)
            return true;

        // Bullet or numbered list item starts a new paragraph.
        if (LooksLikeListItem(curr.PlainText))
            return true;

        // Alignment shift (centered vs non-centered).
        var prevAlign = DetectAlignment(prev, contentLeftPt, contentRightPt, contentWidthPt);
        var currAlign = DetectAlignment(curr, contentLeftPt, contentRightPt, contentWidthPt);
        if (prevAlign == ParagraphAlignment.Center || currAlign == ParagraphAlignment.Center)
        {
            if (prevAlign != currAlign) return true;
        }

        // First-line indentation on curr relative to prev.
        double startIndentDiff = curr.IsRtl ? (prev.Right - curr.Right) : (curr.Left - prev.Left);
        if (startIndentDiff >= 10.0 && prev.PlainText.Length > 20)
            return true;

        // Short previous line (intentional line break / title / end of paragraph):
        // if the previous line ended well short of the margin and the first word of curr would easily have fit.
        double prevWidth = prev.Right - prev.Left;
        double availableWidth = prev.ColumnIndex == 0 ? contentWidthPt : contentWidthPt * 0.46;
        if (availableWidth > 60 && prevWidth < availableWidth * 0.72)
        {
            string firstWord = curr.PlainText.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            double estFirstWordWidth = firstWord.Length * curr.PrimaryFontSize * 0.55;
            double remainingSpace = Math.Max(prev.Left - contentLeftPt, contentRightPt - prev.Right);
            if (prev.ColumnIndex == 0 && remainingSpace > estFirstWordWidth + curr.PrimaryFontSize * 1.5)
                return true;
            if (prev.ColumnIndex != 0 && prevWidth < availableWidth * 0.65)
                return true;
        }

        return false;
    }

    private static ParagraphBlock MergeLinesToParagraph(
        List<VisualLine> lines,
        double bodyFontSize,
        double contentLeftPt,
        double contentRightPt,
        double contentWidthPt)
    {
        var mergedRuns = new List<StyledRun>();
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (i > 0 && mergedRuns.Count > 0)
            {
                var lastRun = mergedRuns[^1];
                var firstCurrRun = line.Runs[0];
                if (ShouldDehyphenate(lastRun.Text, firstCurrRun.Text, line.IsRtl))
                {
                    mergedRuns[^1] = lastRun with { Text = lastRun.Text[..^1] };
                }
                else if (!lastRun.IsTab && !lastRun.Text.EndsWith(' ') && !firstCurrRun.Text.StartsWith(' '))
                {
                    mergedRuns.Add(new StyledRun(" ", lastRun.FontSize, lastRun.FontFamily, lastRun.IsBold, lastRun.IsItalic, lastRun.ColorHex, line.IsRtl, IsTab: false));
                }
            }

            mergedRuns.AddRange(line.Runs);
        }

        var coalesced = CoalesceRuns(mergedRuns);
        var first = lines[0];
        bool isRtl = lines.Count(l => l.IsRtl) >= (lines.Count + 1) / 2;
        double primarySize = lines.Average(l => l.PrimaryFontSize);
        bool allBold = lines.All(l => l.AllBold);
        string plainText = string.Concat(coalesced.Select(r => r.IsTab ? "\t" : r.Text)).Trim();

        int headingLevel = 0;
        if (lines.Count <= 2 && plainText.Length <= 140 && !LooksLikeListItem(plainText))
        {
            if (primarySize >= bodyFontSize * 1.45 || (primarySize >= bodyFontSize * 1.30 && allBold))
                headingLevel = 1;
            else if (primarySize >= bodyFontSize * 1.20 || (allBold && primarySize >= bodyFontSize * 1.10))
                headingLevel = 2;
        }

        var alignment = DetectParagraphAlignment(lines, isRtl, contentLeftPt, contentRightPt, contentWidthPt);

        int indentTwips = 0;
        if (alignment != ParagraphAlignment.Center && lines[0].ColumnIndex == 0)
        {
            double indentPt = isRtl ? (contentRightPt - lines.Max(l => l.Right)) : (lines.Min(l => l.Left) - contentLeftPt);
            if (indentPt >= 10.0 && indentPt <= contentWidthPt * 0.45)
                indentTwips = (int)Math.Round(indentPt * 20.0);
        }

        return new ParagraphBlock(
            Runs: coalesced,
            TopPt: lines.Max(l => l.Top),
            BottomPt: lines.Min(l => l.Bottom),
            LeftPt: lines.Min(l => l.Left),
            RightPt: lines.Max(l => l.Right),
            ColumnIndex: lines[0].ColumnIndex,
            PrimaryFontSize: primarySize,
            IsRtl: isRtl,
            HeadingLevel: headingLevel,
            Alignment: alignment,
            IndentTwips: indentTwips,
            CharacterCount: plainText.Length);
    }

    private static List<PageElement> InterleavePageElements(List<ParagraphBlock> blocks, List<ExtractedImage> images)
    {
        var items = new List<PageElement>(blocks.Count + images.Count);
        foreach (var b in blocks)
            items.Add(new PageElement(b.TopPt, b, null, IsFullPageScan: false));
        foreach (var img in images.OrderByDescending(image => image.TopPt))
        {
            var candidates = blocks.Where(block => block.ColumnIndex > 0 && block.LeftPt < img.RightPt && block.RightPt > img.LeftPt).ToList();
            if (candidates.Count == 0 || candidates.Select(block => block.ColumnIndex).Distinct().Count() > 1) candidates = blocks;
            var below = candidates.FirstOrDefault(block => block.TopPt <= img.TopPt);
            int at = below != null ? items.FindIndex(item => ReferenceEquals(item.TextBlock, below)) : -1;
            if (at < 0 && candidates.Count > 0) at = items.FindLastIndex(item => ReferenceEquals(item.TextBlock, candidates[^1])) + 1;
            if (at < 0) at = items.Count;
            while (at < items.Count && items[at].Image is { } prior && prior.TopPt >= img.TopPt) at++;
            items.Insert(at, new PageElement(img.TopPt, null, img, IsFullPageScan: false));
        }
        return items;
    }

    private static void WriteDocx(string stagedPath, List<PageModel> pages, CancellationToken ct)
    {
        using var doc = WordprocessingDocument.Create(stagedPath, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        AddMinimalStylesPart(main);

        var body = new W.Body();
        var imagePartCache = new Dictionary<string, string>(StringComparer.Ordinal);
        uint nextDrawingId = 1;
        int sectionIndex = 0;

        for (int p = 0; p < pages.Count; p++)
        {
            ct.ThrowIfCancellationRequested();
            var page = pages[p];
            bool isFirstPage = p == 0;
            bool prevSameSection = p > 0 && SectionSpec.AreEquivalent(pages[p - 1].Section, page.Section);
            if (!isFirstPage && !prevSameSection)
                sectionIndex++;
            bool nextDifferentSection = p + 1 < pages.Count && !SectionSpec.AreEquivalent(page.Section, pages[p + 1].Section);
            bool needPageBreakBeforeFirstParagraph = !isFirstPage && prevSameSection;

            var pageParagraphs = new List<W.Paragraph>();
            double printableWidthPt = (page.Section.WidthTwips - page.Section.MarginLeftTwips - page.Section.MarginRightTwips) / 20.0;
            double printableHeightPt = (page.Section.HeightTwips - page.Section.MarginTopTwips - page.Section.MarginBottomTwips) / 20.0;
            long maxContentWidthEmu = Math.Max(EmuPerPoint * 36, (long)Math.Round(printableWidthPt * EmuPerPoint));
            long maxContentHeightEmu = Math.Max(EmuPerPoint * 36, (long)Math.Round(printableHeightPt * EmuPerPoint));

            for (int e = 0; e < page.Elements.Count; e++)
            {
                ct.ThrowIfCancellationRequested();
                var item = page.Elements[e];
                bool breakBefore = needPageBreakBeforeFirstParagraph && pageParagraphs.Count == 0;

                if (item.TextBlock is { } block)
                {
                    int spaceBeforeTwips = 0;
                    if (e > 0 && page.Elements[e - 1].TextBlock is { } prevBlock)
                    {
                        double gapPt = prevBlock.BottomPt - block.TopPt;
                        if (gapPt > 2.0)
                            spaceBeforeTwips = (int)Math.Clamp(Math.Round(gapPt * 20.0), 40, 480);
                    }

                    pageParagraphs.Add(CreateTextParagraph(block, breakBefore, spaceBeforeTwips));
                }
                else if (item.Image is { } img)
                {
                    string relId = GetOrAddImagePart(main, img, imagePartCache);
                    pageParagraphs.Add(CreateImageParagraph(img, relId, ref nextDrawingId, maxContentWidthEmu, maxContentHeightEmu, page.Section.MarginLeftTwips / 20.0, item.IsFullPageScan, breakBefore));
                }
            }

            if (pageParagraphs.Count == 0)
            {
                var emptyProps = new W.ParagraphProperties();
                if (needPageBreakBeforeFirstParagraph)
                    emptyProps.Append(new W.PageBreakBefore());
                pageParagraphs.Add(new W.Paragraph(emptyProps));
            }

            if (nextDifferentSection)
            {
                // Attach section properties for the ending section to its final paragraph.
                var sectPr = CreateSectionProperties(page.Section, includeNextPageType: sectionIndex > 0);
                var lastParagraph = pageParagraphs[^1];
                var pPr = lastParagraph.ParagraphProperties;
                if (pPr == null)
                {
                    pPr = new W.ParagraphProperties();
                    lastParagraph.PrependChild(pPr);
                }
                pPr.Append(sectPr);
            }

            foreach (var paragraph in pageParagraphs)
                body.Append(paragraph);
        }

        // Final section properties must be the last direct child of w:body.
        var lastPage = pages[^1];
        body.Append(CreateSectionProperties(lastPage.Section, includeNextPageType: sectionIndex > 0));

        main.Document = new W.Document(body);
        main.Document.Save();
    }

    private static void AddMinimalStylesPart(MainDocumentPart main)
    {
        var stylesPart = main.AddNewPart<StyleDefinitionsPart>();
        stylesPart.Styles = new W.Styles(
            new W.DocDefaults(
                new W.RunPropertiesDefault(
                    new W.RunPropertiesBaseStyle(
                        new W.RunFonts { Ascii = "Segoe UI", HighAnsi = "Segoe UI", ComplexScript = "Segoe UI" },
                        new W.FontSize { Val = "22" },
                        new W.FontSizeComplexScript { Val = "22" },
                        new W.Languages { Val = "en-US", Bidi = "ar-SA" })),
                new W.ParagraphPropertiesDefault(
                    new W.ParagraphPropertiesBaseStyle(
                        new W.SpacingBetweenLines { After = "80", Line = "264", LineRule = W.LineSpacingRuleValues.Auto }))),
            new W.Style(
                new W.StyleName { Val = "Normal" },
                new W.PrimaryStyle())
            { Type = W.StyleValues.Paragraph, StyleId = "Normal", Default = true },
            new W.Style(
                new W.StyleName { Val = "heading 1" },
                new W.BasedOn { Val = "Normal" },
                new W.NextParagraphStyle { Val = "Normal" },
                new W.PrimaryStyle(),
                new W.StyleParagraphProperties(
                    new W.KeepNext(),
                    new W.SpacingBetweenLines { Before = "200", After = "80" },
                    new W.OutlineLevel { Val = 0 }),
                new W.StyleRunProperties(
                    new W.Bold(),
                    new W.BoldComplexScript()))
            { Type = W.StyleValues.Paragraph, StyleId = "Heading1" },
            new W.Style(
                new W.StyleName { Val = "heading 2" },
                new W.BasedOn { Val = "Normal" },
                new W.NextParagraphStyle { Val = "Normal" },
                new W.PrimaryStyle(),
                new W.StyleParagraphProperties(
                    new W.KeepNext(),
                    new W.SpacingBetweenLines { Before = "160", After = "60" },
                    new W.OutlineLevel { Val = 1 }),
                new W.StyleRunProperties(
                    new W.Bold(),
                    new W.BoldComplexScript()))
            { Type = W.StyleValues.Paragraph, StyleId = "Heading2" });
        stylesPart.Styles.Save();
    }

    private static W.Paragraph CreateTextParagraph(ParagraphBlock block, bool pageBreakBefore, int spaceBeforeTwips)
    {
        var pPr = new W.ParagraphProperties();

        // Strict ECMA-376 CT_PPr child ordering: pStyle, keepNext, pageBreakBefore, bidi, spacing, ind, jc.
        if (block.HeadingLevel == 1)
            pPr.Append(new W.ParagraphStyleId { Val = "Heading1" });
        else if (block.HeadingLevel == 2)
            pPr.Append(new W.ParagraphStyleId { Val = "Heading2" });

        if (block.HeadingLevel > 0)
            pPr.Append(new W.KeepNext());

        if (pageBreakBefore)
            pPr.Append(new W.PageBreakBefore());

        if (block.IsRtl)
            pPr.Append(new W.BiDi());

        int afterTwips = block.HeadingLevel > 0 ? 80 : 100;
        var spacing = new W.SpacingBetweenLines { After = afterTwips.ToString(CultureInfo.InvariantCulture) };
        if (spaceBeforeTwips > 0)
            spacing.Before = spaceBeforeTwips.ToString(CultureInfo.InvariantCulture);
        pPr.Append(spacing);

        if (block.IndentTwips > 0)
        {
            var ind = block.IsRtl
                ? new W.Indentation { Right = block.IndentTwips.ToString(CultureInfo.InvariantCulture) }
                : new W.Indentation { Left = block.IndentTwips.ToString(CultureInfo.InvariantCulture) };
            pPr.Append(ind);
        }

        var jc = block.Alignment switch
        {
            ParagraphAlignment.Center => W.JustificationValues.Center,
            ParagraphAlignment.Right => W.JustificationValues.Right,
            _ => W.JustificationValues.Left
        };
        pPr.Append(new W.Justification { Val = jc });

        var paragraph = new W.Paragraph(pPr);

        foreach (var runSpec in block.Runs)
        {
            var rPr = CreateRunProperties(runSpec);
            if (runSpec.IsTab)
            {
                paragraph.Append(new W.Run(rPr, new W.TabChar()));
            }
            else if (runSpec.Text.Length > 0)
            {
                paragraph.Append(new W.Run(rPr, new W.Text(runSpec.Text) { Space = SpaceProcessingModeValues.Preserve }));
            }
        }

        return paragraph;
    }

    private static W.RunProperties CreateRunProperties(StyledRun runSpec)
    {
        var rPr = new W.RunProperties();

        // Strict ECMA-376 CT_RPr child ordering: rFonts, b, bCs, i, iCs, color, sz, szCs, rtl, lang.
        string csFont = runSpec.IsRtl ? "Segoe UI" : runSpec.FontFamily;
        rPr.Append(new W.RunFonts
        {
            Ascii = runSpec.FontFamily,
            HighAnsi = runSpec.FontFamily,
            ComplexScript = csFont
        });

        if (runSpec.IsBold)
        {
            rPr.Append(new W.Bold());
            rPr.Append(new W.BoldComplexScript());
        }

        if (runSpec.IsItalic)
        {
            rPr.Append(new W.Italic());
            rPr.Append(new W.ItalicComplexScript());
        }

        if (!string.IsNullOrEmpty(runSpec.ColorHex))
            rPr.Append(new W.Color { Val = runSpec.ColorHex });

        string halfPoints = Math.Clamp((int)Math.Round(runSpec.FontSize * 2.0), 12, 144).ToString(CultureInfo.InvariantCulture);
        rPr.Append(new W.FontSize { Val = halfPoints });
        rPr.Append(new W.FontSizeComplexScript { Val = halfPoints });

        if (runSpec.IsRtl)
        {
            rPr.Append(new W.RightToLeftText());
            rPr.Append(new W.Languages { Val = "en-US", Bidi = "ar-SA" });
        }

        return rPr;
    }

    private static W.Paragraph CreateImageParagraph(
        ExtractedImage image,
        string relationshipId,
        ref uint nextDrawingId,
        long maxWidthEmu,
        long maxHeightEmu,
        double leftMarginPt,
        bool isFullPageScan,
        bool pageBreakBefore)
    {
        long targetWidthEmu;
        long targetHeightEmu;

        if (isFullPageScan)
        {
            // Fit full-page rendered image cleanly inside the printable page box with a tiny safety margin to avoid spillover.
            long safeMaxW = (long)(maxWidthEmu * 0.985);
            long safeMaxH = (long)(maxHeightEmu * 0.985);
            double aspect = image.PixelHeight > 0 ? (double)image.PixelWidth / image.PixelHeight : image.WidthPt / Math.Max(1.0, image.HeightPt);
            double boxAspect = (double)safeMaxW / Math.Max(1L, safeMaxH);
            if (aspect >= boxAspect)
            {
                targetWidthEmu = safeMaxW;
                targetHeightEmu = Math.Max(EmuPerPoint, (long)Math.Round(safeMaxW / Math.Max(0.05, aspect)));
            }
            else
            {
                targetHeightEmu = safeMaxH;
                targetWidthEmu = Math.Max(EmuPerPoint, (long)Math.Round(safeMaxH * aspect));
            }
        }
        else
        {
            long desiredW = image.WidthPt > 1 ? (long)Math.Round(image.WidthPt * EmuPerPoint) : image.PixelWidth * EmuPerPixelAt96Dpi;
            long desiredH = image.HeightPt > 1 ? (long)Math.Round(image.HeightPt * EmuPerPoint) : image.PixelHeight * EmuPerPixelAt96Dpi;
            double scale = Math.Min(1.0, Math.Min((double)maxWidthEmu / Math.Max(1L, desiredW), (double)(maxHeightEmu * 0.90) / Math.Max(1L, desiredH)));
            targetWidthEmu = Math.Max(EmuPerPoint * 4, (long)Math.Round(desiredW * scale));
            targetHeightEmu = Math.Max(EmuPerPoint * 4, (long)Math.Round(desiredH * scale));
        }

        uint docPrId = nextDrawingId++;
        uint picId = nextDrawingId++;
        string name = $"PDF Image {docPrId}";

        var drawing = new W.Drawing(
            new DW.Inline(
                new DW.Extent { Cx = targetWidthEmu, Cy = targetHeightEmu },
                new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                new DW.DocProperties { Id = docPrId, Name = name },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(
                    new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties { Id = picId, Name = name },
                                new PIC.NonVisualPictureDrawingProperties()),
                            new PIC.BlipFill(
                                new A.Blip { Embed = relationshipId },
                                new A.Stretch(new A.FillRectangle())),
                            new PIC.ShapeProperties(
                                new A.Transform2D(
                                    new A.Offset { X = 0L, Y = 0L },
                                    new A.Extents { Cx = targetWidthEmu, Cy = targetHeightEmu }),
                                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                    { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
            {
                DistanceFromTop = 0U,
                DistanceFromBottom = 0U,
                DistanceFromLeft = 0U,
                DistanceFromRight = 0U
            });

        var pPr = new W.ParagraphProperties();
        if (pageBreakBefore)
            pPr.Append(new W.PageBreakBefore());
        pPr.Append(new W.SpacingBetweenLines { Before = isFullPageScan ? "0" : "80", After = isFullPageScan ? "0" : "100" });
        if (!isFullPageScan)
        {
            double remainingPt = Math.Max(0, (maxWidthEmu - targetWidthEmu) / (double)EmuPerPoint);
            pPr.Append(new W.Indentation { Left = Math.Round(Math.Clamp(image.LeftPt - leftMarginPt, 0, remainingPt) * 20).ToString(CultureInfo.InvariantCulture) });
        }
        pPr.Append(new W.Justification { Val = isFullPageScan ? W.JustificationValues.Center : W.JustificationValues.Left });

        return new W.Paragraph(pPr, new W.Run(drawing));
    }

    private static W.SectionProperties CreateSectionProperties(SectionSpec spec, bool includeNextPageType)
    {
        var sectPr = new W.SectionProperties();
        // Strict ECMA-376 CT_SectPr child ordering: type, pgSz, pgMar, bidi.
        if (includeNextPageType)
            sectPr.Append(new W.SectionType { Val = W.SectionMarkValues.NextPage });

        sectPr.Append(new W.PageSize
        {
            Width = spec.WidthTwips,
            Height = spec.HeightTwips,
            Orient = spec.IsLandscape ? W.PageOrientationValues.Landscape : W.PageOrientationValues.Portrait
        });

        sectPr.Append(new W.PageMargin
        {
            Top = spec.MarginTopTwips,
            Right = spec.MarginRightTwips,
            Bottom = spec.MarginBottomTwips,
            Left = spec.MarginLeftTwips,
            Header = 360U,
            Footer = 360U,
            Gutter = 0U
        });

        if (spec.IsRtl)
            sectPr.Append(new W.BiDi());

        return sectPr;
    }

    private static string GetOrAddImagePart(MainDocumentPart main, ExtractedImage image, Dictionary<string, string> cache)
    {
        string key = Convert.ToHexString(SHA256.HashData(image.Bytes));
        if (cache.TryGetValue(key, out string? existingId))
            return existingId;

        var part = main.AddImagePart(image.PartType);
        using (var ms = new MemoryStream(image.Bytes, writable: false))
            part.FeedData(ms);

        string id = main.GetIdOfPart(part);
        cache[key] = id;
        return id;
    }

    private static List<StyledRun> CoalesceRuns(List<StyledRun> runs)
    {
        if (runs.Count <= 1) return runs;
        var merged = new List<StyledRun>(runs.Count) { runs[0] };
        for (int i = 1; i < runs.Count; i++)
        {
            var prev = merged[^1];
            var curr = runs[i];
            if (!prev.IsTab && !curr.IsTab &&
                prev.IsBold == curr.IsBold &&
                prev.IsItalic == curr.IsItalic &&
                prev.IsRtl == curr.IsRtl &&
                Math.Abs(prev.FontSize - curr.FontSize) < 0.4 &&
                prev.FontFamily == curr.FontFamily &&
                prev.ColorHex == curr.ColorHex)
            {
                merged[^1] = prev with { Text = prev.Text + curr.Text };
            }
            else
            {
                merged.Add(curr);
            }
        }
        return merged;
    }

    private static double ComputeWeightedMedianFontSize(List<GlyphInfo> glyphs)
    {
        var sizes = glyphs.Select(g => g.FontSize).OrderBy(s => s).ToArray();
        return sizes.Length == 0 ? 11.0 : sizes[sizes.Length / 2];
    }

    private static double ComputeBodyFontSize(List<VisualLine> lines)
    {
        var buckets = new Dictionary<int, int>();
        foreach (var line in lines)
        {
            int key = (int)Math.Round(line.PrimaryFontSize * 2.0);
            buckets[key] = buckets.GetValueOrDefault(key) + Math.Max(1, line.PlainText.Length);
        }
        if (buckets.Count == 0) return 11.0;
        int bestKey = buckets.OrderByDescending(kv => kv.Value).First().Key;
        return bestKey / 2.0;
    }

    private static double ComputeTypicalLineStep(List<VisualLine> lines, double bodyFontSize)
    {
        var deltas = new List<double>();
        for (int i = 1; i < lines.Count; i++)
        {
            if (lines[i].ColumnIndex != lines[i - 1].ColumnIndex) continue;
            double d = lines[i - 1].BaselineY - lines[i].BaselineY;
            if (d > bodyFontSize * 0.7 && d < bodyFontSize * 2.6)
                deltas.Add(d);
        }
        if (deltas.Count == 0) return bodyFontSize * 1.25;
        deltas.Sort();
        return deltas[deltas.Count / 2];
    }

    private static double EstimateAverageGlyphWidth(List<GlyphInfo> glyphs, double fontSize)
    {
        double sum = 0;
        int count = 0;
        foreach (var g in glyphs)
        {
            if (string.IsNullOrWhiteSpace(g.Text)) continue;
            double w = g.Right - g.Left;
            if (w > 0.5 && w < fontSize * 2.5)
            {
                sum += w;
                count++;
            }
        }
        return count > 0 ? sum / count : fontSize * 0.5;
    }

    private static ParagraphAlignment DetectAlignment(VisualLine line, double contentLeftPt, double contentRightPt, double contentWidthPt)
    {
        double lineWidth = line.Right - line.Left;
        double leftSpace = line.Left - contentLeftPt;
        double rightSpace = contentRightPt - line.Right;

        if (line.ColumnIndex == 0 && lineWidth < contentWidthPt * 0.84 && leftSpace > 14.0 && rightSpace > 14.0 && Math.Abs(leftSpace - rightSpace) <= 14.0)
            return ParagraphAlignment.Center;

        if (line.IsRtl)
            return ParagraphAlignment.Right;

        if (line.ColumnIndex == 0 && lineWidth < contentWidthPt * 0.65 && leftSpace > contentWidthPt * 0.30 && rightSpace < 16.0)
            return ParagraphAlignment.Right;

        return ParagraphAlignment.Left;
    }

    private static ParagraphAlignment DetectParagraphAlignment(List<VisualLine> lines, bool isRtl, double contentLeftPt, double contentRightPt, double contentWidthPt)
    {
        if (lines.All(l => DetectAlignment(l, contentLeftPt, contentRightPt, contentWidthPt) == ParagraphAlignment.Center))
            return ParagraphAlignment.Center;
        if (isRtl)
            return ParagraphAlignment.Right;
        if (lines.Count == 1)
            return DetectAlignment(lines[0], contentLeftPt, contentRightPt, contentWidthPt);
        return ParagraphAlignment.Left;
    }

    private static bool IsPredominantlyRtl(List<ParagraphBlock> blocks)
    {
        int rtlChars = 0, ltrChars = 0;
        foreach (var b in blocks)
        {
            if (b.IsRtl) rtlChars += b.CharacterCount;
            else ltrChars += b.CharacterCount;
        }
        return rtlChars > 0 && rtlChars >= ltrChars;
    }

    private static bool LooksLikeListItem(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        char c = text[0];
        if (c is '•' or '●' or '○' or '▪' or '■' or '‣' or '◦' or '–' or '—' or '▪' or '✓' or '✔')
            return true;
        if ((c is '-' or '*') && text.Length > 2 && text[1] == ' ')
            return true;

        int i = 0;
        while (i < text.Length && i < 4 && (char.IsDigit(text[i]) || (text[i] is >= '\u0660' and <= '\u0669'))) i++;
        if (i > 0 && i + 1 < text.Length && (text[i] is '.' or ')' or '-') && text[i + 1] == ' ')
            return true;

        return false;
    }

    private static bool ShouldDehyphenate(string prevText, string nextText, bool isRtl)
    {
        if (isRtl || prevText.Length < 2 || nextText.Length == 0) return false;
        if (prevText[^1] == '\u00AD') return true;
        return prevText[^1] == '-' && char.IsAsciiLetterLower(prevText[^2]) && char.IsAsciiLetterLower(nextText[0]);
    }

    private static string SanitizeXmlText(string? input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        var sb = new StringBuilder(input.Length);
        for (int i = 0; i < input.Length; i++)
        {
            char ch = input[i];
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]))
                {
                    sb.Append(ch);
                    sb.Append(input[++i]);
                }
                continue;
            }
            if (char.IsLowSurrogate(ch)) continue;
            if (ch == '\t' || ch == '\r' || ch == '\n')
            {
                sb.Append(' ');
                continue;
            }
            if (ch >= 0x20 && ch <= 0xD7FF || ch >= 0xE000 && ch <= 0xFFFD)
                sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string NormalizeEditableUnicode(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        bool needsNorm = false;
        foreach (char c in text)
        {
            if ((c is >= '\uFB00' and <= '\uFB06') || (c is >= '\uFB50' and <= '\uFDFF') || (c is >= '\uFE70' and <= '\uFEFF'))
            {
                needsNorm = true;
                break;
            }
        }
        if (!needsNorm) return text;

        var sb = new StringBuilder(text.Length + 4);
        foreach (char c in text)
        {
            if ((c is >= '\uFB00' and <= '\uFB06') || (c is >= '\uFB50' and <= '\uFDFF') || (c is >= '\uFE70' and <= '\uFEFF'))
                sb.Append(c.ToString().Normalize(NormalizationForm.FormKC));
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    private static string MirrorPairedBrackets(string text)
    {
        if (text.Length != 1) return text;
        return text[0] switch
        {
            '(' => ")",
            ')' => "(",
            '[' => "]",
            ']' => "[",
            '{' => "}",
            '}' => "{",
            '«' => "»",
            '»' => "«",
            _ => text
        };
    }

    private static void ClassifyTextDirection(string text, ref int rtl, ref int ltr)
    {
        foreach (char c in text)
        {
            if (IsRtlChar(c)) rtl++;
            else if (char.IsLetter(c)) ltr++;
        }
    }

    private static bool ContainsRtl(string text)
    {
        foreach (char c in text)
            if (IsRtlChar(c)) return true;
        return false;
    }

    private static bool IsRtlChar(char c) => char.IsLetter(c) && (
        (c is >= '\u0590' and <= '\u08FF') ||
        (c is >= '\uFB50' and <= '\uFDFF') ||
        (c is >= '\uFE70' and < '\uFEFF'));

    private static bool IsCombiningMarkOnly(string text)
    {
        if (text.Length == 0) return false;
        foreach (char c in text)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
                return false;
        }
        return true;
    }

    private static bool FontNameIndicatesBold(string fontName) =>
        fontName.Contains("Bold", StringComparison.OrdinalIgnoreCase) ||
        fontName.Contains("Black", StringComparison.OrdinalIgnoreCase) ||
        fontName.Contains("Heavy", StringComparison.OrdinalIgnoreCase) ||
        fontName.Contains("Demi", StringComparison.OrdinalIgnoreCase) ||
        fontName.Contains("SemiBold", StringComparison.OrdinalIgnoreCase);

    private static bool FontNameIndicatesItalic(string fontName) =>
        fontName.Contains("Italic", StringComparison.OrdinalIgnoreCase) ||
        fontName.Contains("Oblique", StringComparison.OrdinalIgnoreCase);

    private static string MapFontFamily(string rawFontName)
    {
        if (string.IsNullOrWhiteSpace(rawFontName)) return "Segoe UI";
        string name = rawFontName;
        int plus = name.IndexOf('+');
        if (plus >= 0 && plus + 1 < name.Length) name = name[(plus + 1)..];
        int sep = name.IndexOfAny(['-', ',']);
        if (sep > 0) name = name[..sep];
        name = name.Trim();

        if (name.Contains("Times", StringComparison.OrdinalIgnoreCase)) return "Times New Roman";
        if (name.Contains("Courier", StringComparison.OrdinalIgnoreCase) || name.Contains("Consolas", StringComparison.OrdinalIgnoreCase) || name.Contains("Mono", StringComparison.OrdinalIgnoreCase)) return "Consolas";
        if (name.Contains("Helvetica", StringComparison.OrdinalIgnoreCase) || name.Contains("Arial", StringComparison.OrdinalIgnoreCase)) return "Arial";
        if (name.Contains("Calibri", StringComparison.OrdinalIgnoreCase)) return "Calibri";
        if (name.Contains("Cambria", StringComparison.OrdinalIgnoreCase)) return "Cambria";
        if (name.Contains("Georgia", StringComparison.OrdinalIgnoreCase)) return "Georgia";
        if (name.Contains("Tahoma", StringComparison.OrdinalIgnoreCase)) return "Tahoma";
        if (name.Contains("Verdana", StringComparison.OrdinalIgnoreCase)) return "Verdana";
        if (name.Contains("Segoe", StringComparison.OrdinalIgnoreCase)) return "Segoe UI";

        return "Segoe UI";
    }

    private static string? ExtractReadableColorHex(UglyToad.PdfPig.Graphics.Colors.IColor? color)
    {
        if (color == null) return null;
        var (r, g, b) = color.ToRGBValues();
        r = Math.Clamp(r, 0.0, 1.0);
        g = Math.Clamp(g, 0.0, 1.0);
        b = Math.Clamp(b, 0.0, 1.0);

        // Omit default near-black so Word uses automatic ink.
        if (r < 0.08 && g < 0.08 && b < 0.08) return null;

        // Omit very light/white text (e.g., exported from dark-background slides) so it remains readable on white Word pages.
        double luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        if (luminance > 0.80) return null;

        byte rb = (byte)Math.Round(r * 255.0);
        byte gb = (byte)Math.Round(g * 255.0);
        byte bb = (byte)Math.Round(b * 255.0);
        return $"{rb:X2}{gb:X2}{bb:X2}";
    }

    private static bool HasPngSignature(ReadOnlySpan<byte> data) =>
        data.Length >= 8 &&
        data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&
        data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A;

    private static bool HasJpegSignature(ReadOnlySpan<byte> data) =>
        data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("NIVO PDF-to-Word temporary file cleanup failed ({0}).", ex.GetType().Name);
        }
    }

    private sealed class CappedImageStream : MemoryStream
    {
        private void CheckEnd(long end)
        {
            if (end < 0 || end > MaxEmbeddedImageBytes) throw new InvalidDataException("Encoded PDF image exceeds the local image byte limit.");
            if (end > Capacity) Capacity = (int)Math.Min(MaxEmbeddedImageBytes, Math.Max(end, Math.Max(65536L, Capacity * 2L)));
        }
        public override void Write(byte[] buffer, int offset, int count) { CheckEnd(Position + count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { CheckEnd(Position + buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { CheckEnd(Position + 1); base.WriteByte(value); }
        public override void SetLength(long value) { CheckEnd(value); base.SetLength(value); }
    }

    private sealed class NativePdfRenderer : IDisposable
    {
        private readonly FileStream _fileStream;
        private readonly IRandomAccessStream _randomAccessStream;
        private readonly Windows.Data.Pdf.PdfDocument _document;

        private NativePdfRenderer(FileStream fileStream, IRandomAccessStream randomAccessStream, Windows.Data.Pdf.PdfDocument document)
        {
            _fileStream = fileStream;
            _randomAccessStream = randomAccessStream;
            _document = document;
        }

        public static async Task<NativePdfRenderer?> TryOpenAsync(string inputPath, CancellationToken ct)
        {
            FileStream? fs = null;
            IRandomAccessStream? ras = null;
            try
            {
                ct.ThrowIfCancellationRequested();
                fs = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                ras = fs.AsRandomAccessStream();
                var doc = await Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(ras).AsTask(ct);
                return new NativePdfRenderer(fs, ras, doc);
            }
            catch (OperationCanceledException)
            {
                ras?.Dispose();
                fs?.Dispose();
                throw;
            }
            catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or TypeLoadException or ArgumentException)
            {
                ras?.Dispose();
                fs?.Dispose();
                Trace.TraceWarning("NIVO Windows.Data.Pdf renderer unavailable ({0}).", ex.GetType().Name);
                return null;
            }
        }

        public async Task<ExtractedImage?> TryRenderPageAsync(uint pageIndex, double pageWidthPt, double pageHeightPt, CancellationToken ct)
        {
            if (pageIndex >= _document.PageCount) return null;

            try
            {
                ct.ThrowIfCancellationRequested();
                using var page = _document.GetPage(pageIndex);

                double effectiveWidthPt = page.Size.Width > 1 ? page.Size.Width * 72.0 / 96.0 : pageWidthPt;
                double effectiveHeightPt = page.Size.Height > 1 ? page.Size.Height * 72.0 / 96.0 : pageHeightPt;

                var (pngImage, pngExceededLimit) = await TryRenderEncodedAsync(
                    page,
                    ScannedRenderDpi,
                    Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,
                    ImagePartType.Png,
                    effectiveWidthPt,
                    effectiveHeightPt,
                    ct);
                if (pngImage != null) return pngImage;
                if (!pngExceededLimit) return null;

                // Dense scanned pages can exceed the 8 MB per-image budget as PNG; fall back to JPEG and lower DPI within budget.
                foreach (double fallbackDpi in new[] { ScannedRenderDpi, 110.0, 85.0, 72.0 })
                {
                    var (jpegImage, jpegExceededLimit) = await TryRenderEncodedAsync(
                        page,
                        fallbackDpi,
                        Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId,
                        ImagePartType.Jpeg,
                        effectiveWidthPt,
                        effectiveHeightPt,
                        ct);
                    if (jpegImage != null) return jpegImage;
                    if (!jpegExceededLimit) return null;
                }

                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException)
            {
                Trace.TraceWarning("NIVO Windows.Data.Pdf page {0} render failed ({1}).", pageIndex + 1, ex.GetType().Name);
                return null;
            }
        }

        private static async Task<(ExtractedImage? Image, bool ExceededSizeLimit)> TryRenderEncodedAsync(
            Windows.Data.Pdf.PdfPage page,
            double dpi,
            Guid encoderId,
            PartTypeInfo partType,
            double effectiveWidthPt,
            double effectiveHeightPt,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            double rawWidth = Math.Max(1.0, page.Size.Width / 96.0 * dpi);
            double rawHeight = Math.Max(1.0, page.Size.Height / 96.0 * dpi);
            double scale = Math.Min(1.0, MaxRenderPixelDimension / Math.Max(rawWidth, rawHeight));
            uint targetWidth = Math.Max(1U, (uint)Math.Round(rawWidth * scale));
            uint targetHeight = Math.Max(1U, (uint)Math.Round(rawHeight * scale));

            if ((long)targetWidth * targetHeight > MaxImagePixels)
            {
                double pixelScale = Math.Sqrt((double)MaxImagePixels / ((long)targetWidth * targetHeight));
                targetWidth = Math.Max(1U, (uint)(targetWidth * pixelScale));
                targetHeight = Math.Max(1U, (uint)(targetHeight * pixelScale));
            }

            using var memStream = new InMemoryRandomAccessStream();
            var options = new PdfPageRenderOptions
            {
                BitmapEncoderId = encoderId,
                DestinationWidth = targetWidth,
                DestinationHeight = targetHeight,
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255)
            };

            await page.RenderToStreamAsync(memStream, options).AsTask(ct);
            ct.ThrowIfCancellationRequested();
            if (memStream.Size == 0) return (null, false);
            if (memStream.Size > MaxEmbeddedImageBytes) return (null, true);

            memStream.Seek(0);
            using var readStream = memStream.AsStreamForRead();
            byte[] encodedBytes = new byte[(int)memStream.Size];
            await readStream.ReadExactlyAsync(encodedBytes, ct);
            bool validSignature = partType == ImagePartType.Png ? HasPngSignature(encodedBytes) : HasJpegSignature(encodedBytes);
            if (encodedBytes.Length == 0 || encodedBytes.Length > MaxEmbeddedImageBytes || !validSignature)
                return (null, false);

            return (new ExtractedImage(
                Bytes: encodedBytes,
                PartType: partType,
                PixelWidth: (int)targetWidth,
                PixelHeight: (int)targetHeight,
                LeftPt: 0,
                RightPt: effectiveWidthPt,
                TopPt: effectiveHeightPt,
                BottomPt: 0,
                WidthPt: effectiveWidthPt,
                HeightPt: effectiveHeightPt), false);
        }

        public void Dispose()
        {
            _randomAccessStream.Dispose();
            _fileStream.Dispose();
        }
    }

    private enum ParagraphAlignment { Left, Center, Right }

    private sealed record ContentBounds(double MinLeft, double MaxRight, double MinBottom, double MaxTop);

    private sealed record SectionSpec(
        uint WidthTwips,
        uint HeightTwips,
        bool IsLandscape,
        uint MarginLeftTwips,
        uint MarginRightTwips,
        int MarginTopTwips,
        int MarginBottomTwips,
        bool IsRtl)
    {
        public static bool AreEquivalent(SectionSpec a, SectionSpec b) =>
            a.IsLandscape == b.IsLandscape &&
            a.IsRtl == b.IsRtl &&
            Math.Abs((long)a.WidthTwips - b.WidthTwips) <= 40 &&
            Math.Abs((long)a.HeightTwips - b.HeightTwips) <= 40 &&
            Math.Abs((long)a.MarginLeftTwips - b.MarginLeftTwips) <= 120 &&
            Math.Abs((long)a.MarginRightTwips - b.MarginRightTwips) <= 120 &&
            Math.Abs(a.MarginTopTwips - b.MarginTopTwips) <= 120 &&
            Math.Abs(a.MarginBottomTwips - b.MarginBottomTwips) <= 120;
    }

    private sealed record GlyphInfo(
        int StreamIndex,
        string Text,
        double Left,
        double Right,
        double AdvanceLeft,
        double AdvanceRight,
        double Bottom,
        double Top,
        double BaselineY,
        double StartBaselineX,
        double StartBaselineY,
        double EndBaselineX,
        double EndBaselineY,
        double FontSize,
        string FontFamily,
        bool IsBold,
        bool IsItalic,
        string? ColorHex,
        int ResidualAngle);

    private sealed record VisualWord(List<GlyphInfo> Glyphs);

    private sealed record StyledRun(
        string Text,
        double FontSize,
        string FontFamily,
        bool IsBold,
        bool IsItalic,
        string? ColorHex,
        bool IsRtl,
        bool IsTab)
    {
        public static StyledRun Tab(double fontSize, string fontFamily, bool isRtl) =>
            new("", fontSize, fontFamily, false, false, null, isRtl, IsTab: true);
    }

    private sealed record VisualLine(
        List<StyledRun> Runs,
        string PlainText,
        double Left,
        double Right,
        double Top,
        double Bottom,
        double BaselineY,
        double PrimaryFontSize,
        double LineHeight,
        bool IsRtl,
        bool AllBold,
        List<(double Left, double Right)> SegmentBounds,
        int ColumnIndex);

    private sealed record ParagraphBlock(
        List<StyledRun> Runs,
        double TopPt,
        double BottomPt,
        double LeftPt,
        double RightPt,
        int ColumnIndex,
        double PrimaryFontSize,
        bool IsRtl,
        int HeadingLevel,
        ParagraphAlignment Alignment,
        int IndentTwips,
        int CharacterCount);

    private sealed record ExtractedImage(
        byte[] Bytes,
        PartTypeInfo PartType,
        int PixelWidth,
        int PixelHeight,
        double LeftPt,
        double RightPt,
        double TopPt,
        double BottomPt,
        double WidthPt,
        double HeightPt);

    private sealed record PageElement(
        double SortTopPt,
        ParagraphBlock? TextBlock,
        ExtractedImage? Image,
        bool IsFullPageScan);

    private sealed record PageModel(
        SectionSpec Section,
        List<PageElement> Elements,
        bool HasContent);
}
