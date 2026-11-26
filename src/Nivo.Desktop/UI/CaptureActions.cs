using Nivo.Core;
using Nivo.Services;
namespace Nivo.UI;

public static class CaptureActions
{
    public static readonly string[] Names = ["Explain", "Translate", "Summarize", "Fact-check", "Abbreviation", "Table", "Note", "Task", "Copy as image"];
    public static string Prompt(string action, string language)
    {
        bool english = language == "English";
        return action switch
        {
            "Explain" => english ? "Explain the selected image." : "اشرح الجزء المحدد في الصورة.",
            "Translate" => english ? "Translate the text in the selected image into English." : "ترجم النص الموجود في الصورة إلى العربية.",
            "Summarize" => english ? "Summarize the selected image." : "لخّص محتوى الصورة المحددة.",
            "Fact-check" => english ? "Fact-check the claims in the selected image using live web search. Cite the sources and distinguish uncertain claims." : "تحقق من المعلومات في الصورة باستخدام البحث على الويب. اذكر المصادر ووضّح ما لا يمكن تأكيده.",
            "Abbreviation" => english ? "Expand and explain the abbreviations in the selected image." : "وضّح المعنى الكامل للاختصارات في الصورة واشرحها.",
            "Table" => english ? "Extract the visible table from the selected image as a Markdown table. Mark unclear cells rather than guessing." : "استخرج الجدول الظاهر في الصورة كجدول Markdown، وعلّم الخلايا غير الواضحة بدل تخمينها.",
            _ => throw new InvalidOperationException("This action does not send a model request.")
        };
    }
    public static void SaveImage(Controller c, string kind, string title)
    {
        if (c.Capture == null || c.Busy) throw new InvalidOperationException("Select an image when no request is running.");
        if (kind is not ("Note" or "Task") || string.IsNullOrWhiteSpace(title)) throw new InvalidDataException("Enter a note or task title.");
        var file = FileService.OutputPath(c.Store, "selection-" + kind.ToLowerInvariant(), ".png"); DesktopService.SavePng(c.Capture, file);
        c.Store.Data.Workspace.Add(new() { Title = title.Trim(), Kind = kind, ReferenceFile = file }); c.Store.Save(); c.SetStatus("Image saved with a local " + kind.ToLowerInvariant());
    }
}
