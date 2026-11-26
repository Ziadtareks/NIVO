namespace Nivo.Services;

// Capabilities describe the connected transport, not the provider's entire product catalog.
public sealed record AccountModel(string Id, string DisplayName, bool ImageInput, bool IsDefault = false)
{
    public string Capabilities => Id.StartsWith("codex/", StringComparison.Ordinal)
        ? (ImageInput ? Core.Loc.T("Text · Image input · Account Talk experimental", "نص · فهم الصور · Talk الحساب تجريبي") : Core.Loc.T("Text · Image support unverified · Account Talk experimental", "نص · دعم الصور غير متحقق · Talk الحساب تجريبي"))
        : ImageInput
        ? Core.Loc.T("Text · Image input · Native Talk · external CLI (F6)", "نص · فهم الصور · Talk أصلي · نافذة CLI خارجية (F6)")
        : Core.Loc.T("Text · Image support unverified · Native Talk · external CLI (F6)", "نص · دعم الصور غير متحقق · Talk أصلي · نافذة CLI خارجية (F6)");
    public override string ToString() => DisplayName + " — " + Id + "\n" + Capabilities;
}
