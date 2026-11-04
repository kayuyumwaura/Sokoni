using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Sokoni.Channels;
using Sokoni.Domain;

namespace Sokoni.Controllers;

[ApiController]
[Route("api/webhooks/whatsapp")]
public sealed class WhatsAppWebhookController : ControllerBase
{
    private readonly WhatsAppOptions _o;
    private readonly InboundQueue _queue;
    private readonly ILogger<WhatsAppWebhookController> _log;

    public WhatsAppWebhookController(IOptions<WhatsAppOptions> o, InboundQueue queue, ILogger<WhatsAppWebhookController> log)
    {
        _o = o.Value;
        _queue = queue;
        _log = log;
    }

    /// <summary>Meta calls this once when you save the webhook URL in the app dashboard.</summary>
    [HttpGet]
    public IActionResult Verify(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? token,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        if (mode == "subscribe" && !string.IsNullOrEmpty(_o.VerifyToken) && token == _o.VerifyToken)
            return Content(challenge ?? "", "text/plain");
        return StatusCode(StatusCodes.Status403Forbidden);
    }

    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        Request.EnableBuffering();
        string raw;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true))
            raw = await reader.ReadToEndAsync(ct);

        if (!string.IsNullOrEmpty(_o.AppSecret) && !SignatureIsValid(raw, Request.Headers["X-Hub-Signature-256"]))
        {
            _log.LogWarning("Rejected WhatsApp webhook with a bad signature.");
            return Unauthorized();
        }

        try
        {
            foreach (var msg in Parse(raw))
                await _queue.EnqueueAsync(msg, ct);
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Unreadable webhook payload.");
        }

        // Always 200 quickly, otherwise Meta retries and eventually disables the webhook.
        return Ok();
    }

    private bool SignatureIsValid(string body, string? header)
    {
        if (string.IsNullOrEmpty(header) || !header.StartsWith("sha256=")) return false;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_o.AppSecret));
        var expected = "sha256=" + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(header));
    }

    internal static IEnumerable<IncomingMessage> Parse(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var results = new List<IncomingMessage>();
        if (!doc.RootElement.TryGetProperty("entry", out var entries)) return results;

        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("changes", out var changes)) continue;
            foreach (var change in changes.EnumerateArray())
            {
                if (!change.TryGetProperty("value", out var value)) continue;
                if (!value.TryGetProperty("messages", out var messages)) continue; // delivery/read statuses

                var names = new Dictionary<string, string>();
                if (value.TryGetProperty("contacts", out var contacts))
                    foreach (var c in contacts.EnumerateArray())
                        if (c.TryGetProperty("wa_id", out var wa) && c.TryGetProperty("profile", out var p) && p.TryGetProperty("name", out var n))
                            names[wa.GetString()!] = n.GetString()!;

                foreach (var m in messages.EnumerateArray())
                {
                    var from = m.GetProperty("from").GetString()!;
                    var id = m.GetProperty("id").GetString()!;
                    var type = m.TryGetProperty("type", out var t) ? t.GetString() : "unknown";
                    names.TryGetValue(from, out var name);

                    string? text = null;
                    double? lat = null, lng = null;
                    string? label = null;
                    MediaAttachment? media = null;

                    switch (type)
                    {
                        case "text":
                            text = m.GetProperty("text").GetProperty("body").GetString();
                            break;
                        case "location":
                            var loc = m.GetProperty("location");
                            lat = loc.GetProperty("latitude").GetDouble();
                            lng = loc.GetProperty("longitude").GetDouble();
                            label = string.Join(", ", new[] { Str(loc, "name"), Str(loc, "address") }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (label.Length == 0) label = null;
                            break;
                        case "image":
                            var img = m.GetProperty("image");
                            media = new MediaAttachment(MediaKind.Image, Str(img, "id"), Str(img, "mime_type"));
                            text = Str(img, "caption");
                            break;
                        case "audio": // voice notes arrive as audio with "voice": true
                            var aud = m.GetProperty("audio");
                            media = new MediaAttachment(MediaKind.Audio, Str(aud, "id"), Str(aud, "mime_type"));
                            break;
                        case "interactive":
                            var inter = m.GetProperty("interactive");
                            text = inter.TryGetProperty("button_reply", out var br) ? Str(br, "title")
                                 : inter.TryGetProperty("list_reply", out var lr) ? Str(lr, "title") : null;
                            break;
                        case "button":
                            text = Str(m.GetProperty("button"), "text");
                            break;
                        default:
                            text = $"[Customer sent a {type} message, which this assistant can't read]";
                            break;
                    }

                    results.Add(new IncomingMessage(id, ChannelType.WhatsApp, from, name, from, text, lat, lng, label, media));
                }
            }
        }
        return results;
    }

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
