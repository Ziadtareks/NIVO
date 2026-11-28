using System.Net.Http;
using System.Net.Http.Headers;
using NAudio.Wave;
using Nivo.Core;

namespace Nivo.Services;

public sealed class VoiceService(Store store) : IDisposable
{
    private readonly object writerGate = new();
    private WaveInEvent? recorder;
    private WaveFileWriter? writer;
    private string? recordingPath;
    private TaskCompletionSource<Exception?>? captureStopped;
    private CancellationTokenSource? transcription;
    public bool Recording => recorder != null;
    public static Uri ValidateEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
            throw new InvalidDataException("Voice endpoints require HTTPS, or HTTP on localhost. Credentials must not be embedded in URLs.");
        return uri;
    }
    public void StartRecording()
    {
        if (!store.Data.Settings.VoiceEnabled || string.IsNullOrEmpty(store.Data.Settings.SttEndpoint)) throw new InvalidOperationException("Voice is off. Configure your speech API in Settings first.");
        if (Recording) return;
        transcription?.Cancel();
        recordingPath = Path.Combine(store.Runtime, Guid.NewGuid().ToString("N") + ".wav");
        recorder = new WaveInEvent { WaveFormat = new WaveFormat(16000, 1), BufferMilliseconds = 100 };
        var device = recorder;
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        captureStopped = stopped;
        try
        {
            writer = new WaveFileWriter(recordingPath, device.WaveFormat);
            device.DataAvailable += (_, e) =>
            {
                lock (writerGate)
                {
                    if (ReferenceEquals(recorder, device)) writer?.Write(e.Buffer, 0, e.BytesRecorded);
                }
            };
            device.RecordingStopped += (_, e) => stopped.TrySetResult(e.Exception);
            device.StartRecording();
        }
        catch { CancelRecording(); throw; }
    }
    public async Task<string> StopAndTranscribe(CancellationToken ct)
    {
        var device = recorder ?? throw new InvalidOperationException("No recording is active.");
        if (transcription is { IsCancellationRequested: false }) throw new InvalidOperationException(Loc.T("Transcription is already starting.", "جاري بدء تفريغ التسجيل بالفعل."));
        var stopped = captureStopped!.Task;
        string path = recordingPath!;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
        transcription = request;
        ct = request.Token;
        try
        {
            if (!stopped.IsCompleted) device.StopRecording();
            var failure = await stopped.WaitAsync(TimeSpan.FromSeconds(5), ct);
            if (!ReferenceEquals(device, recorder)) throw new OperationCanceledException();
            if (failure != null) throw new InvalidOperationException(Loc.T("The microphone stopped unexpectedly.", "توقف الميكروفون بشكل غير متوقع."), failure);
            ReleaseRecording();
            var s = store.Data.Settings; using var http = Client(); using var form = new MultipartFormDataContent();
            var bytes = new ByteArrayContent(await File.ReadAllBytesAsync(path, ct)); bytes.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(bytes, "file", "speech.wav"); form.Add(new StringContent(s.SttModel), "model");
            using var req = new HttpRequestMessage(HttpMethod.Post, ValidateEndpoint(s.SttEndpoint)) { Content = form }; SetAuth(req, store.GetSecret("stt"));
            using var response = await http.SendAsync(req, ct); if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Speech recognition returned HTTP " + (int)response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return json.RootElement.GetProperty("text").GetString() ?? "";
        }
        finally
        {
            if (ReferenceEquals(request, transcription)) transcription = null;
            if (ReferenceEquals(device, recorder)) ReleaseRecording();
            if (File.Exists(path)) File.Delete(path);
        }
    }
    public async Task<string> Speak(string text, CancellationToken ct)
    {
        var s = store.Data.Settings;
        if (!s.VoiceEnabled || string.IsNullOrWhiteSpace(s.TtsEndpoint)) throw new InvalidOperationException("Configure and enable a TTS API in Settings first.");
        if (text.Length > 4000) text = text[..4000];
        using var http = Client(); using var req = new HttpRequestMessage(HttpMethod.Post, ValidateEndpoint(s.TtsEndpoint));
        SetAuth(req, store.GetSecret("tts"));
        req.Content = new StringContent(JsonSerializer.Serialize(new { model = s.TtsModel, input = text, voice = s.Voice, response_format = "wav" }), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Speech synthesis returned HTTP " + (int)response.StatusCode);
        var path = Path.Combine(store.Runtime, Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(ct); await using var dest = File.Create(path);
            var buffer = new byte[8192]; int read, total = 0;
            while ((read = await source.ReadAsync(buffer, ct)) > 0) { total += read; if (total > 20 * 1024 * 1024) throw new InvalidDataException("Audio response exceeds 20 MB."); await dest.WriteAsync(buffer.AsMemory(0, read), ct); }
            return path;
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }
    private static HttpClient Client() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(90) };
    private static void SetAuth(HttpRequestMessage req, string key) { if (key.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key); }
    private void ReleaseRecording()
    {
        var device = recorder;
        var stopped = captureStopped;
        lock (writerGate)
        {
            recorder = null;
            writer?.Dispose();
            writer = null;
        }
        recordingPath = null;
        captureStopped = null;
        stopped?.TrySetCanceled();
        device?.Dispose();
    }
    public void CancelRecording()
    {
        transcription?.Cancel();
        string? path = recordingPath;
        try { ReleaseRecording(); }
        finally { if (path != null && File.Exists(path)) File.Delete(path); }
    }
    public void Dispose() => CancelRecording();
}
