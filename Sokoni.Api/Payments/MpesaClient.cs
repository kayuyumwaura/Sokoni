using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Sokoni.Payments;

public sealed record StkPushResult(bool Accepted, string? CheckoutRequestId, string Message);

/// <summary>Safaricom Daraja: OAuth token + M-Pesa Express (STK push).</summary>
public sealed class MpesaClient
{
    private static readonly SemaphoreSlim TokenLock = new(1, 1);
    private static string? _token;
    private static DateTime _tokenExpiresUtc;

    private readonly HttpClient _http;
    private readonly MpesaOptions _o;
    private readonly ILogger<MpesaClient> _log;

    public MpesaClient(HttpClient http, IOptions<MpesaOptions> o, ILogger<MpesaClient> log)
    {
        _http = http;
        _o = o.Value;
        _log = log;
    }

    public bool IsConfigured => _o.IsConfigured;

    private string BaseUrl => _o.Environment.Equals("production", StringComparison.OrdinalIgnoreCase)
        ? "https://api.safaricom.co.ke"
        : "https://sandbox.safaricom.co.ke";

    public string CallbackUrl => $"{_o.CallbackBaseUrl.TrimEnd('/')}/api/mpesa/callback/{_o.CallbackKey}";

    public async Task<StkPushResult> StkPushAsync(string phone, int amount, string accountReference, string description, CancellationToken ct)
    {
        var token = await GetTokenAsync(ct);
        // Daraja expects Nairobi local time.
        var timestamp = DateTime.UtcNow.AddHours(3).ToString("yyyyMMddHHmmss");
        var password = Convert.ToBase64String(Encoding.UTF8.GetBytes(_o.ShortCode + _o.Passkey + timestamp));

        var body = new JsonObject
        {
            ["BusinessShortCode"] = _o.ShortCode,
            ["Password"] = password,
            ["Timestamp"] = timestamp,
            ["TransactionType"] = _o.TransactionType,
            ["Amount"] = Math.Max(1, amount),
            ["PartyA"] = phone,
            ["PartyB"] = _o.ShortCode,
            ["PhoneNumber"] = phone,
            ["CallBackURL"] = CallbackUrl,
            ["AccountReference"] = accountReference.Length > 12 ? accountReference[..12] : accountReference,
            ["TransactionDesc"] = description.Length > 13 ? description[..13] : description
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/mpesa/stkpush/v1/processrequest")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var res = await _http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        JsonNode? json = null;
        try { json = JsonNode.Parse(text); } catch { /* non-JSON error page */ }

        var responseCode = json?["ResponseCode"]?.ToString();
        if (res.IsSuccessStatusCode && responseCode == "0")
        {
            var checkoutId = json?["CheckoutRequestID"]?.ToString();
            _log.LogInformation("STK push accepted: {CheckoutId}", checkoutId);
            return new StkPushResult(true, checkoutId, json?["CustomerMessage"]?.ToString() ?? "Accepted");
        }

        var error = json?["errorMessage"]?.ToString() ?? json?["ResponseDescription"]?.ToString() ?? $"HTTP {(int)res.StatusCode}";
        _log.LogWarning("STK push rejected: {Body}", text);
        return new StkPushResult(false, null, error);
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTime.UtcNow < _tokenExpiresUtc) return _token;

        await TokenLock.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTime.UtcNow < _tokenExpiresUtc) return _token;

            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/oauth/v1/generate?grant_type=client_credentials");
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_o.ConsumerKey}:{_o.ConsumerSecret}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

            using var res = await _http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                throw new HttpRequestException($"Daraja auth failed {(int)res.StatusCode}: {text}");

            var json = JsonNode.Parse(text)!;
            _token = json["access_token"]!.ToString();
            var seconds = int.TryParse(json["expires_in"]?.ToString(), out var s) ? s : 3599;
            _tokenExpiresUtc = DateTime.UtcNow.AddSeconds(seconds - 60);
            return _token;
        }
        finally
        {
            TokenLock.Release();
        }
    }
}
