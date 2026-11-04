using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Sokoni.Domain;

namespace Sokoni.Channels;

/// <summary>Anything that can deliver a text reply to a customer. Add Instagram by implementing this.</summary>
public interface IMessagingChannel
{
    ChannelType Channel { get; }
    Task SendTextAsync(string recipientId, string text, CancellationToken ct = default);
}

public sealed class WhatsAppChannel : IMessagingChannel
{
    private readonly HttpClient _http;
    private readonly WhatsAppOptions _o;
    private readonly ILogger<WhatsAppChannel> _log;

    public WhatsAppChannel(HttpClient http, IOptions<WhatsAppOptions> o, ILogger<WhatsAppChannel> log)
    {
        _http = http;
        _o = o.Value;
        _log = log;
    }

    public ChannelType Channel => ChannelType.WhatsApp;

    public async Task SendTextAsync(string recipientId, string text, CancellationToken ct = default)
    {
        if (!_o.IsConfigured)
        {
            _log.LogWarning("WhatsApp not configured; would send to {To}: {Text}", recipientId, text);
            return;
        }

        // WhatsApp caps a text body at 4096 characters.
        foreach (var chunk in Chunk(text, 4000))
        {
            var body = new JsonObject
            {
                ["messaging_product"] = "whatsapp",
                ["recipient_type"] = "individual",
                ["to"] = recipientId,
                ["type"] = "text",
                ["text"] = new JsonObject { ["preview_url"] = true, ["body"] = chunk }
            };
            await PostAsync(body, ct);
        }
    }

    /// <summary>Downloads a voice note or photo: first ask Graph for the media URL, then fetch it with the same token.</summary>
    public async Task<(byte[] Bytes, string? MimeType)?> DownloadMediaAsync(string mediaId, CancellationToken ct = default)
    {
        if (!_o.IsConfigured) return null;

        using var metaReq = new HttpRequestMessage(HttpMethod.Get, $"https://graph.facebook.com/{_o.GraphVersion}/{mediaId}");
        metaReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _o.AccessToken);
        using var metaRes = await _http.SendAsync(metaReq, ct);
        var metaJson = await metaRes.Content.ReadAsStringAsync(ct);
        if (!metaRes.IsSuccessStatusCode)
        {
            _log.LogError("Media lookup failed {Status}: {Body}", (int)metaRes.StatusCode, metaJson);
            return null;
        }

        var meta = JsonNode.Parse(metaJson);
        var url = meta?["url"]?.ToString();
        var mime = meta?["mime_type"]?.ToString();
        if (string.IsNullOrEmpty(url)) return null;

        using var fileReq = new HttpRequestMessage(HttpMethod.Get, url);
        fileReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _o.AccessToken);
        using var fileRes = await _http.SendAsync(fileReq, ct);
        if (!fileRes.IsSuccessStatusCode)
        {
            _log.LogError("Media download failed {Status}", (int)fileRes.StatusCode);
            return null;
        }
        return (await fileRes.Content.ReadAsByteArrayAsync(ct), mime);
    }

    /// <summary>Blue ticks plus a typing indicator while the model thinks.</summary>
    public async Task MarkReadAsync(string messageId, CancellationToken ct = default)
    {
        if (!_o.IsConfigured) return;
        var body = new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["status"] = "read",
            ["message_id"] = messageId,
            ["typing_indicator"] = new JsonObject { ["type"] = "text" }
        };
        try { await PostAsync(body, ct); }
        catch (Exception ex) { _log.LogDebug(ex, "Mark-as-read failed (non-fatal)."); }
    }

    private async Task PostAsync(JsonObject body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://graph.facebook.com/{_o.GraphVersion}/{_o.PhoneNumberId}/messages")
        {
            Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _o.AccessToken);
        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var err = await res.Content.ReadAsStringAsync(ct);
            _log.LogError("WhatsApp send failed {Status}: {Body}", (int)res.StatusCode, err);
            throw new HttpRequestException($"WhatsApp send failed: {(int)res.StatusCode}");
        }
    }

    private static IEnumerable<string> Chunk(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size)
            yield return text.Substring(i, Math.Min(size, text.Length - i));
    }
}

/// <summary>Collects replies in memory so /api/dev/chat can return them. Lets you test without WhatsApp.</summary>
public sealed class DevChannel : IMessagingChannel
{
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<string>> Outbox = new();

    public ChannelType Channel => ChannelType.Dev;

    public Task SendTextAsync(string recipientId, string text, CancellationToken ct = default)
    {
        Outbox.GetOrAdd(recipientId, _ => new ConcurrentQueue<string>()).Enqueue(text);
        return Task.CompletedTask;
    }

    public static List<string> Drain(string recipientId)
    {
        var list = new List<string>();
        if (Outbox.TryGetValue(recipientId, out var q))
            while (q.TryDequeue(out var m)) list.Add(m);
        return list;
    }
}

public sealed class ChannelRouter
{
    private readonly WhatsAppChannel _whatsApp;
    private readonly DevChannel _dev;

    public ChannelRouter(WhatsAppChannel whatsApp, DevChannel dev)
    {
        _whatsApp = whatsApp;
        _dev = dev;
    }

    public IMessagingChannel Get(ChannelType channel) => channel switch
    {
        ChannelType.WhatsApp => _whatsApp,
        ChannelType.Dev => _dev,
        // Instagram adapter lands here (Monday stretch goal).
        _ => throw new NotSupportedException($"Channel {channel} is not wired up yet.")
    };

    /// <summary>Owner alerts always go out on WhatsApp.</summary>
    public WhatsAppChannel Owner => _whatsApp;
}

public static class PhoneNumbers
{
    /// <summary>Normalises Kenyan numbers to 2547XXXXXXXX / 2541XXXXXXXX, or null if it doesn't look valid.</summary>
    public static string? NormaliseKenyan(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var digits = new string(input.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("0") && digits.Length == 10) digits = "254" + digits[1..];
        else if ((digits.StartsWith("7") || digits.StartsWith("1")) && digits.Length == 9) digits = "254" + digits;
        return digits.Length == 12 && digits.StartsWith("254") && (digits[3] == '7' || digits[3] == '1') ? digits : null;
    }

    public static string Mask(string phone) =>
        phone.Length >= 12 ? $"{phone[..6]}***{phone[^3..]}" : phone;
}
