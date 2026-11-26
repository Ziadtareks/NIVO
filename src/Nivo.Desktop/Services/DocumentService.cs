using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;
using P = DocumentFormat.OpenXml.Presentation;
using D = DocumentFormat.OpenXml.Drawing;

namespace Nivo.Services;

public static class DocumentService
{
    public static void Word(string path, string title, string content)
    {
        string safeTitle = SanitizeXmlLine(title);
        string safeContent = content ?? "";
        string[] lines = safeContent.Split('\n').Select(l => SanitizeXmlLine(l.TrimEnd('\r'))).ToArray();

        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        AddWordStylesPart(main);

        var body = new W.Body();
        bool titleRtl = IsPredominantlyArabic(safeTitle);
        var titlePPr = new W.ParagraphProperties(new W.ParagraphStyleId { Val = "Title" });
        if (titleRtl)
        {
            titlePPr.Append(new W.BiDi());
            titlePPr.Append(new W.Justification { Val = W.JustificationValues.Right });
        }
        body.Append(new W.Paragraph(
            titlePPr,
            new W.Run(CreateWordRunProperties(fontSizeHalfPoints: "40", bold: true, isRtl: titleRtl), new W.Text(safeTitle) { Space = SpaceProcessingModeValues.Preserve })));

        int docRtl = 0, docLtr = 0;
        CountScriptDirection(safeTitle, ref docRtl, ref docLtr);
        foreach (var line in lines)
        {
            CountScriptDirection(line, ref docRtl, ref docLtr);
            bool lineRtl = IsPredominantlyArabic(line);
            var paragraph = new W.Paragraph();
            if (lineRtl)
                paragraph.Append(new W.ParagraphProperties(new W.BiDi(), new W.Justification { Val = W.JustificationValues.Right }));
            paragraph.Append(new W.Run(CreateWordRunProperties(fontSizeHalfPoints: "22", bold: false, isRtl: lineRtl), new W.Text(line) { Space = SpaceProcessingModeValues.Preserve }));
            body.Append(paragraph);
        }

        var sectPr = new W.SectionProperties(
            new W.PageSize { Width = 11906, Height = 16838 },
            new W.PageMargin { Top = 1134, Bottom = 1134, Left = 1134, Right = 1134 });
        if (docRtl > 0 && docRtl >= docLtr)
            sectPr.Append(new W.BiDi());
        body.Append(sectPr);
        main.Document = new W.Document(body); main.Document.Save();
    }
    private static void AddWordStylesPart(MainDocumentPart main)
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
                        new W.SpacingBetweenLines { After = "120", Line = "264", LineRule = W.LineSpacingRuleValues.Auto }))),
            new W.Style(
                new W.StyleName { Val = "Normal" },
                new W.PrimaryStyle())
            { Type = W.StyleValues.Paragraph, StyleId = "Normal", Default = true },
            new W.Style(
                new W.StyleName { Val = "Title" },
                new W.BasedOn { Val = "Normal" },
                new W.NextParagraphStyle { Val = "Normal" },
                new W.PrimaryStyle(),
                new W.StyleParagraphProperties(
                    new W.KeepNext(),
                    new W.SpacingBetweenLines { After = "200" }),
                new W.StyleRunProperties(
                    new W.Bold(),
                    new W.BoldComplexScript(),
                    new W.FontSize { Val = "40" },
                    new W.FontSizeComplexScript { Val = "40" }))
            { Type = W.StyleValues.Paragraph, StyleId = "Title" });
        stylesPart.Styles.Save();
    }
    private static W.RunProperties CreateWordRunProperties(string fontSizeHalfPoints, bool bold, bool isRtl)
    {
        var rPr = new W.RunProperties(new W.RunFonts { Ascii = "Segoe UI", HighAnsi = "Segoe UI", ComplexScript = "Segoe UI" });
        if (bold)
        {
            rPr.Append(new W.Bold());
            rPr.Append(new W.BoldComplexScript());
        }
        rPr.Append(new W.FontSize { Val = fontSizeHalfPoints });
        rPr.Append(new W.FontSizeComplexScript { Val = fontSizeHalfPoints });
        if (isRtl)
            rPr.Append(new W.RightToLeftText());
        rPr.Append(new W.Languages { Val = "en-US", Bidi = "ar-SA" });
        return rPr;
    }
    public sealed class SlideSpec
    {
        public string Title { get; set; } = "";
        public string[] Bullets { get; set; } = [];
    }
    public static void Presentation(string path, string title, IEnumerable<SlideSpec> slides, bool includeCover = true)
    {
        string safeTitle = SanitizeXmlLine(title);
        using var doc = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
        var part = doc.AddPresentationPart(); var ids = new P.SlideIdList();
        var master = part.AddNewPart<SlideMasterPart>();
        var layout = master.AddNewPart<SlideLayoutPart>();
        layout.SlideLayout = new P.SlideLayout(new P.CommonSlideData(EmptyTree()), new P.ColorMapOverride(new D.MasterColorMapping())) { Type = P.SlideLayoutValues.Blank, Preserve = true };
        layout.AddPart(master);
        master.SlideMaster = new P.SlideMaster(new P.CommonSlideData(EmptyTree()), new P.ColorMap {
            Accent1 = D.ColorSchemeIndexValues.Accent1, Accent2 = D.ColorSchemeIndexValues.Accent2, Accent3 = D.ColorSchemeIndexValues.Accent3, Accent4 = D.ColorSchemeIndexValues.Accent4,
            Accent5 = D.ColorSchemeIndexValues.Accent5, Accent6 = D.ColorSchemeIndexValues.Accent6, Background1 = D.ColorSchemeIndexValues.Light1, Background2 = D.ColorSchemeIndexValues.Light2,
            Text1 = D.ColorSchemeIndexValues.Dark1, Text2 = D.ColorSchemeIndexValues.Dark2, Hyperlink = D.ColorSchemeIndexValues.Hyperlink, FollowedHyperlink = D.ColorSchemeIndexValues.FollowedHyperlink
        }, new P.SlideLayoutIdList(new P.SlideLayoutId { Id = 2147483649U, RelationshipId = master.GetIdOfPart(layout) }));
        var theme = master.AddNewPart<ThemePart>();
        theme.Theme = new D.Theme(new D.ThemeElements(
            new D.ColorScheme(new D.Dark1Color(new D.RgbColorModelHex { Val = "102430" }), new D.Light1Color(new D.RgbColorModelHex { Val = "FFFFFF" }), new D.Dark2Color(new D.RgbColorModelHex { Val = "182D3B" }), new D.Light2Color(new D.RgbColorModelHex { Val = "F1F6FA" }),
                new D.Accent1Color(new D.RgbColorModelHex { Val = "88DBD5" }), new D.Accent2Color(new D.RgbColorModelHex { Val = "8AB6DB" }), new D.Accent3Color(new D.RgbColorModelHex { Val = "C8B6DB" }), new D.Accent4Color(new D.RgbColorModelHex { Val = "D9B794" }), new D.Accent5Color(new D.RgbColorModelHex { Val = "A4D5B8" }), new D.Accent6Color(new D.RgbColorModelHex { Val = "D5CDA4" }), new D.Hyperlink(new D.RgbColorModelHex { Val = "88DBD5" }), new D.FollowedHyperlinkColor(new D.RgbColorModelHex { Val = "C8B6DB" })) { Name = "NIVO" },
            new D.FontScheme(new D.MajorFont(new D.LatinFont { Typeface = "Segoe UI" }, new D.EastAsianFont { Typeface = "" }, new D.ComplexScriptFont { Typeface = "Segoe UI" }), new D.MinorFont(new D.LatinFont { Typeface = "Segoe UI" }, new D.EastAsianFont { Typeface = "" }, new D.ComplexScriptFont { Typeface = "Segoe UI" })) { Name = "NIVO" },
            new D.FormatScheme(
                new D.FillStyleList(new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor }), new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor }), new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor })),
                new D.LineStyleList(new D.Outline(new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor })), new D.Outline(new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor })), new D.Outline(new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor }))),
                new D.EffectStyleList(new D.EffectStyle(new D.EffectList()), new D.EffectStyle(new D.EffectList()), new D.EffectStyle(new D.EffectList())),
                new D.BackgroundFillStyleList(new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor }), new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor }), new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor }))) { Name = "NIVO" })) { Name = "NIVO" };
        part.Presentation = new P.Presentation(new P.SlideMasterIdList(new P.SlideMasterId { Id = 2147483648U, RelationshipId = part.GetIdOfPart(master) }), ids, new P.SlideSize { Cx = 12192000, Cy = 6858000, Type = P.SlideSizeValues.Screen16x9 }, new P.NotesSize { Cx = 6858000, Cy = 9144000 });
        var safeSlides = (slides ?? []).Where(s => s != null);
        var all = includeCover ? new[] { new SlideSpec { Title = safeTitle, Bullets = [IsArabic(safeTitle) ? "أُعدّ بواسطة نيڤو" : "Created with NIVO Local"] } }.Concat(safeSlides.Take(24)) : safeSlides.Take(24);
        uint id = 256;
        foreach (var spec in all)
        {
            var slide = part.AddNewPart<SlidePart>();
            slide.AddPart(layout);
            var tree = new P.ShapeTree(new P.NonVisualGroupShapeProperties(new P.NonVisualDrawingProperties { Id = 1, Name = "Slide" }, new P.NonVisualGroupShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()), new P.GroupShapeProperties(new D.TransformGroup()));
            bool cover = id == 256;
            string slideTitle = SanitizeXmlLine(spec.Title);
            var bullets = (spec.Bullets ?? []).Take(8).Select(s => cover ? SanitizeXmlLine(s) : "•  " + SanitizeXmlLine(s));
            tree.Append(TextShape(2, "Title", slideTitle, 700000, cover ? 1850000 : 650000, 10800000, cover ? 2300000 : 1500000, cover ? 4400 : 3400, "88DBD5", true));
            tree.Append(TextShape(3, "Body", string.Join("\n", bullets), 700000, cover ? 4400000 : 2250000, 10600000, cover ? 1000000 : 3700000, cover ? 1800 : 2200, "F1F6FA", false));
            tree.Append(TextShape(4, "Footer", $"NIVO LOCAL     /     {id - 255:00}", 700000, 6200000, 10600000, 300000, 1000, "8CABB9", false));
            slide.Slide = new P.Slide(new P.CommonSlideData(new P.Background(new P.BackgroundProperties(new D.SolidFill(new D.RgbColorModelHex { Val = "102430" }), new D.EffectList())), tree), new P.ColorMapOverride(new D.MasterColorMapping()));
            slide.Slide.Save(); ids.Append(new P.SlideId { Id = id++, RelationshipId = part.GetIdOfPart(slide) });
        }
        part.Presentation.Save();
    }
    private static P.ShapeTree EmptyTree() => new(new P.NonVisualGroupShapeProperties(new P.NonVisualDrawingProperties { Id = 1, Name = "Slide" }, new P.NonVisualGroupShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()), new P.GroupShapeProperties(new D.TransformGroup()));
    private static bool IsArabicChar(char c) =>
        (c is >= '\u0600' and <= '\u06ff') ||
        (c is >= '\u0750' and <= '\u077f') ||
        (c is >= '\u08a0' and <= '\u08ff') ||
        (c is >= '\ufb50' and <= '\ufdff') ||
        (c is >= '\ufe70' and <= '\ufeff');
    private static bool IsArabic(string text) => text.Any(IsArabicChar);
    private static void CountScriptDirection(string text, ref int rtl, ref int ltr)
    {
        foreach (char c in text)
        {
            if (IsArabicChar(c) && char.IsLetter(c)) rtl++;
            else if (char.IsLetter(c)) ltr++;
        }
    }
    private static bool IsPredominantlyArabic(string text)
    {
        int rtl = 0, ltr = 0;
        CountScriptDirection(text, ref rtl, ref ltr);
        return rtl > 0 && rtl >= ltr;
    }
    private static string SanitizeXmlLine(string? input)
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
            if (ch is '\t' or '\r' or '\n')
            {
                sb.Append(' ');
                continue;
            }
            if ((ch >= 0x20 && ch <= 0xD7FF) || (ch >= 0xE000 && ch <= 0xFFFD))
                sb.Append(ch);
        }
        return sb.ToString();
    }
    private static P.Shape TextShape(uint id, string name, string text, long x, long y, long width, long height, int size, string color, bool bold)
    {
        var body = new P.TextBody(new D.BodyProperties { Wrap = D.TextWrappingValues.Square }, new D.ListStyle());
        foreach (var rawLine in (text ?? "").Split('\n'))
        {
            string line = SanitizeXmlLine(rawLine.TrimEnd('\r'));
            bool rtl = IsArabic(line);
            body.Append(new D.Paragraph(new D.ParagraphProperties { RightToLeft = rtl, Alignment = rtl ? D.TextAlignmentTypeValues.Right : D.TextAlignmentTypeValues.Left }, new D.Run(new D.RunProperties(new D.SolidFill(new D.RgbColorModelHex { Val = color }), new D.LatinFont { Typeface = "Segoe UI" }, new D.ComplexScriptFont { Typeface = "Segoe UI" }) { FontSize = size, Bold = bold, Language = rtl ? "ar-SA" : "en-US" }, new D.Text(line))));
        }
        return new P.Shape(new P.NonVisualShapeProperties(new P.NonVisualDrawingProperties { Id = id, Name = name }, new P.NonVisualShapeDrawingProperties(new D.ShapeLocks { NoGrouping = true }), new P.ApplicationNonVisualDrawingProperties()), new P.ShapeProperties(new D.Transform2D(new D.Offset { X = x, Y = y }, new D.Extents { Cx = width, Cy = height }), new D.PresetGeometry(new D.AdjustValueList()) { Preset = D.ShapeTypeValues.Rectangle }, new D.NoFill(), new D.Outline(new D.NoFill())), body);
    }
}
