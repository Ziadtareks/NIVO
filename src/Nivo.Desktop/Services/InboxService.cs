using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using Nivo.Core;

namespace Nivo.Services;

public sealed class InboxMessage
{
    public string Id { get; set; } = "";
    public string Sender { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Preview { get; set; } = "";
    public bool Important { get; set; }
}
public sealed class InboxService(Store store)
{
    private sealed class Tokens
    {
        public string Access { get; set; } = "";
        public string Refresh { get; set; } = "";
        public DateTime Expires { get; set; }
    }
    public bool Connected => !string.IsNullOrEmpty(store.GetSecret("gmail-tokens"));
    private static string Base64(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static HttpClient Client() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    public async Task ConnectAsync(string clientId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId) || !clientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal)) throw new InvalidDataException("Enter a Google Desktop OAuth client ID first.");
        var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); int port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        var redirect = $"http://127.0.0.1:{port}/"; using var listener = new HttpListener(); listener.Prefixes.Add(redirect); listener.Start();
        string state = Base64(RandomNumberGenerator.GetBytes(24)), verifier = Base64(RandomNumberGenerator.GetBytes(48));
        string challenge = Base64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var parameters = new Dictionary<string, string> { ["client_id"] = clientId, ["redirect_uri"] = redirect, ["response_type"] = "code", ["scope"] = "https://www.googleapis.com/auth/gmail.metadata", ["access_type"] = "offline", ["prompt"] = "consent", ["state"] = state, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256" };
        DesktopService.OpenTarget("https://accounts.google.com/o/oauth2/v2/auth?" + string.Join("&", parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))));
        using var reg = ct.Register(listener.Stop); HttpListenerContext context;
        try { context = await listener.GetContextAsync().WaitAsync(ct); } catch (HttpListenerException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        var received = context.Request.QueryString; bool valid = received["state"] == state && !string.IsNullOrWhiteSpace(received["code"]);
        var reply = Encoding.UTF8.GetBytes(valid ? "NIVO received authorization. You can close this tab." : "Authorization rejected. Return to NIVO and try again.");
        context.Response.ContentType = "text/plain; charset=utf-8"; context.Response.StatusCode = valid ? 200 : 400;
        await context.Response.OutputStream.WriteAsync(reply, ct); context.Response.Close();
        if (!valid) throw new InvalidOperationException("Google authorization was cancelled or the state check failed.");
        var form = new Dictionary<string, string> { ["client_id"] = clientId, ["code"] = received["code"]!, ["code_verifier"] = verifier, ["redirect_uri"] = redirect, ["grant_type"] = "authorization_code" };
        var secret = store.GetSecret("gmail-client"); if (secret.Length > 0) form["client_secret"] = secret;
        await Exchange(form, null, ct);
    }
    private async Task<Tokens> Exchange(Dictionary<string, string> form, Tokens? existing, CancellationToken ct)
    {
        using var http = Client(); using var response = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form), ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Google token exchange returned HTTP " + (int)response.StatusCode + ". Check your Desktop OAuth client and Gmail API configuration.");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); var root = doc.RootElement;
        var tokens = new Tokens { Access = root.GetProperty("access_token").GetString()!, Refresh = root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString()! : existing?.Refresh ?? "", Expires = DateTime.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32()) };
        store.SetSecret("gmail-tokens", JsonSerializer.Serialize(tokens)); return tokens;
    }
    public async Task<List<InboxMessage>> ReadAsync(CancellationToken ct)
    {
        var tokens = JsonSerializer.Deserialize<Tokens>(store.GetSecret("gmail-tokens")) ?? throw new InvalidOperationException("Connect Gmail first.");
        if (tokens.Expires < DateTime.UtcNow.AddSeconds(60))
        {
            var form = new Dictionary<string, string> { ["client_id"] = store.Data.Settings.GmailClientId, ["refresh_token"] = tokens.Refresh, ["grant_type"] = "refresh_token" };
            var secret = store.GetSecret("gmail-client"); if (secret.Length > 0) form["client_secret"] = secret;
            tokens = await Exchange(form, tokens, ct);
        }
        using var http = Client(); http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Access);
        using var list = await http.GetAsync("https://gmail.googleapis.com/gmail/v1/users/me/messages?labelIds=INBOX&maxResults=12", ct);
        if (!list.IsSuccessStatusCode) throw new InvalidOperationException("Gmail returned HTTP " + (int)list.StatusCode);
        using var json = JsonDocument.Parse(await list.Content.ReadAsStringAsync(ct)); var result = new List<InboxMessage>();
        if (!json.RootElement.TryGetProperty("messages", out var messages)) return result;
        foreach (var item in messages.EnumerateArray())
        {
            string id = item.GetProperty("id").GetString()!;
            using var response = await http.GetAsync("https://gmail.googleapis.com/gmail/v1/users/me/messages/" + Uri.EscapeDataString(id) + "?format=metadata&metadataHeaders=From&metadataHeaders=Subject", ct);
            if (!response.IsSuccessStatusCode) continue;
            using var message = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); var m = message.RootElement; var headers = m.GetProperty("payload").GetProperty("headers").EnumerateArray().ToArray();
            string Header(string key) { var h = headers.FirstOrDefault(h => h.GetProperty("name").GetString()!.Equals(key, StringComparison.OrdinalIgnoreCase)); return h.ValueKind == JsonValueKind.Object && h.TryGetProperty("value", out var value) ? value.GetString() ?? "" : ""; }
            result.Add(new() { Id = id, Sender = Header("From"), Subject = Header("Subject"), Preview = m.TryGetProperty("snippet", out var snippet) ? WebUtility.HtmlDecode(snippet.GetString() ?? "") : "", Important = m.TryGetProperty("labelIds", out var labels) && labels.EnumerateArray().Any(l => l.GetString() == "IMPORTANT") });
        }
        return result;
    }
    public async Task Disconnect(CancellationToken ct)
    {
        try
        {
            if (Connected)
            {
                var tokens = JsonSerializer.Deserialize<Tokens>(store.GetSecret("gmail-tokens"))!;
                using var http = Client(); using var response = await http.PostAsync("https://oauth2.googleapis.com/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = tokens.Refresh.Length > 0 ? tokens.Refresh : tokens.Access }), ct);
            }
        }
        finally { store.SetSecret("gmail-tokens", ""); }
    }
}
