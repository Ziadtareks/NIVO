using System.Diagnostics;
using Nivo.Core;

namespace Nivo.Services;

public static class AccountConnections
{
    public static bool IsAccountModel(string? model) => model != null &&
        (model.StartsWith("codex/", StringComparison.Ordinal) || model.StartsWith("antigravity/", StringComparison.Ordinal));

    public static (string Provider, string Model) SplitModel(string model)
    {
        if (!IsAccountModel(model) || !OpenCodeService.IsValidModelId(model))
            throw new InvalidDataException("Invalid connected-account model ID.");
        int slash = model.IndexOf('/');
        return (model[..slash], model[(slash + 1)..]);
    }

    public static async Task<IReadOnlyList<AccountModel>> ModelsAsync(Store store, string provider, CancellationToken ct)
    {
        IReadOnlyList<AccountModel> models = provider switch
        {
            "codex" => await new CodexAccountService(store).ModelsAsync(ct),
            "antigravity" => await new AntigravityAccountService(store).ModelsAsync(ct),
            _ => throw new InvalidDataException("Unknown account provider.")
        };
        return models.Select(m => m with { Id = provider + "/" + m.Id })
            .Where(m => OpenCodeService.IsValidModelId(m.Id)).DistinctBy(m => m.Id).ToArray();
    }

    public static async Task LoginAsync(Store store, string provider, CancellationToken ct)
    {
        switch (provider)
        {
            case "codex": await new CodexAccountService(store).LoginAsync(OpenBrowser, ct); break;
            case "antigravity": await new AntigravityAccountService(store).LoginAsync(OpenBrowser, ct); break;
            default: throw new InvalidDataException("Unknown account provider.");
        }
        store.Data.Settings.LastConnectedAccountProvider = provider;
        store.Save();
    }

    public static Task LogoutAsync(Store store, string provider, CancellationToken ct) => provider switch
    {
        "codex" => new CodexAccountService(store).LogoutAsync(ct),
        "antigravity" => new AntigravityAccountService(store).LogoutAsync(ct),
        _ => throw new InvalidDataException("Unknown account provider.")
    };

    public static Task<AccountModel> ResolveTalkModelAsync(Store store, CancellationToken ct, Action<string>? onStatus = null) =>
        ResolveTalkModelAsync(store.Data.Settings,
            (provider, token) => ModelsAsync(store, provider, token), ct, onStatus);

    internal static async Task<AccountModel> ResolveTalkModelAsync(Settings settings,
        Func<string, CancellationToken, Task<IReadOnlyList<AccountModel>>> readModels,
        CancellationToken ct, Action<string>? onStatus = null)
    {
        if (settings.FreeModelsOnly || !settings.UseUserOpenCodeConfig)
            throw new InvalidOperationException(Loc.T("Connected-account Talk is excluded by strict free isolation. Enable account providers in AI settings first.", "عزل الموديلات المجانية يستبعد صوت الحسابات. فعّل مزوّدات الحسابات في إعدادات الذكاء أولًا."));
        string selected = settings.Model.Trim();
        string explicitProvider = settings.VoiceAccountProvider.Trim().ToLowerInvariant();
        string latest = settings.LastConnectedAccountProvider;
        string preferred = latest is "codex" or "antigravity" ? latest : IsAccountModel(selected) ? SplitModel(selected).Provider : "antigravity";
        string[] providers = explicitProvider is "codex" or "antigravity" ? [explicitProvider]
            : preferred == "codex" ? ["codex", "antigravity"] : ["antigravity", "codex"];
        var errors = new List<string>();
        foreach (string provider in providers)
        {
            ct.ThrowIfCancellationRequested();
            onStatus?.Invoke(Loc.T($"Checking {provider} voice account…", $"فحص حساب صوت {provider}…"));
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var models = await readModels(provider, probe.Token);
                ct.ThrowIfCancellationRequested();
                if (models.Count == 0) { errors.Add(provider + ": " + Loc.T("no available models", "لا توجد موديلات متاحة")); continue; }
                return models.FirstOrDefault(m => string.Equals(m.Id, selected, StringComparison.Ordinal))
                    ?? models.FirstOrDefault(m => m.IsDefault) ?? models[0];
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { errors.Add(provider + ": " + Loc.T("account check timed out", "انتهت مهلة فحص الحساب")); }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or TimeoutException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            {
                string detail = ErrorAwarenessService.RedactSensitiveTokens(ex.Message).Replace('\r', ' ').Replace('\n', ' ');
                errors.Add(provider + ": " + detail[..Math.Min(detail.Length, 220)]);
            }
        }
        throw new InvalidOperationException(Loc.T("No connected account is available for Talk. Connect either account in AI settings.\n", "لا يوجد حساب متصل متاح لـ Talk. اربط أحد الحسابات من إعدادات الذكاء.\n") + string.Join("\n", errors));
    }

    public static Task<string> AskAsync(Store store, string model, string prompt, string system,
        string? image, Action<string>? onText, CancellationToken ct)
    {
        var target = SplitModel(model);
        var context = SynchronizationContext.Current;
        Action<string>? deliver = onText == null ? null : text =>
        {
            if (context == null || ReferenceEquals(SynchronizationContext.Current, context)) onText(text);
            else context.Send(_ => onText(text), null);
        };
        return target.Provider == "codex"
            ? new CodexAccountService(store).AskAsync(target.Model, prompt, system, image, deliver, ct)
            : new AntigravityAccountService(store).AskAsync(target.Model, prompt, system, image, deliver, ct);
    }

    private static void OpenBrowser(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || !new[] { "auth.openai.com", "chatgpt.com", "accounts.google.com" }.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("The provider returned an unrecognized sign-in URL.");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
