using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using Nivo.Core;

namespace Nivo.Services;

public static class SystemAudio
{
    private const int HResultNotFound = unchecked((int)0x80070490);
    private const int HResultAccessDenied = unchecked((int)0x80070005);
    private const int HResultServiceNotActive = unchecked((int)0x80070426);
    private const int HResultDeviceInvalidated = unchecked((int)0x88890004);
    private const int HResultServiceNotRunning = unchecked((int)0x88890010);

    public static bool? Muted() => MutedCore(() =>
    {
        using var endpoints = new MMDeviceEnumerator();
        using var device = endpoints.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return device.AudioEndpointVolume.Mute;
    });

    public static bool ToggleMute() => ToggleCore(() =>
    {
        using var endpoints = new MMDeviceEnumerator();
        using var device = endpoints.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        bool next = !device.AudioEndpointVolume.Mute;
        device.AudioEndpointVolume.Mute = next;
        return next;
    });

    public static bool TryToggleMute(out bool? muted, out string? error)
    {
        try
        {
            muted = ToggleMute();
            error = null;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            muted = null;
            error = ex.Message;
            return false;
        }
    }

    public static string DescribeAudioError(Exception ex)
    {
        if (ex is COMException com)
        {
            return com.HResult switch
            {
                HResultNotFound => Loc.T("No default audio output device is available.", "لا يوجد جهاز إخراج صوت افتراضي متاح حاليًا."),
                HResultDeviceInvalidated => Loc.T("The audio output device changed or was disconnected. Try again.", "تغيّر جهاز إخراج الصوت أو تم فصله. حاول مرة أخرى."),
                HResultServiceNotRunning or HResultServiceNotActive => Loc.T("The Windows Audio service is not running.", "خدمة صوت ويندوز غير قيد التشغيل حاليًا."),
                HResultAccessDenied => Loc.T("Windows denied access to the default audio device.", "رفض ويندوز الوصول إلى جهاز الصوت الافتراضي."),
                _ => Loc.T("Could not change system mute right now. Check your Windows audio device.", "تعذّر تغيير كتم الصوت حاليًا. تحقّق من جهاز الصوت في ويندوز.")
            };
        }
        if (ex is UnauthorizedAccessException)
            return Loc.T("Windows denied access to the default audio device.", "رفض ويندوز الوصول إلى جهاز الصوت الافتراضي.");
        return Loc.T("Could not change system mute right now.", "تعذّر تغيير كتم الصوت حاليًا.");
    }

    internal static bool? MutedCore(Func<bool> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException) { return null; }
    }

    internal static bool ToggleCore(Func<bool> toggle)
    {
        try { return toggle(); }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("NIVO audio mute toggle unavailable ({0} 0x{1:X8}).", ex.GetType().Name, ex.HResult);
            throw new InvalidOperationException(DescribeAudioError(ex), ex);
        }
    }
}
