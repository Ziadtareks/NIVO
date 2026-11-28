namespace Nivo.Services;

// Capabilities describe the connected transport, not the provider's entire product catalog.
public sealed record AccountModel(string Id, string DisplayName, bool ImageInput, bool IsDefault = false)
{
    public string Capabilities => Id.StartsWith("codex/", StringComparison.Ordinal)
        ? (ImageInput ? Core.Loc.T("Text · Image input · Account Talk experimental", "نص · فهم الصور · Talk الحساب تجريبي") : Core.Loc.T("Text · Image support unverified · Account Talk experimental", "نص · دعم الصور غير متحقق · Talk الحساب تجريبي"))
        : ImageInput
        ? Core.Loc.T("Text · Image input · Native Talk · inside NIVO · availability unverified", "نص · فهم الصور · Talk أصلي · داخل نيڤو · الإتاحة غير متحققة")
        : Core.Loc.T("Text · Image support unverified · Native Talk · inside NIVO · availability unverified", "نص · دعم الصور غير متحقق · Talk أصلي · داخل نيڤو · الإتاحة غير متحققة");
    public override string ToString() => DisplayName + " — " + Id + "\n" + Capabilities;
}
