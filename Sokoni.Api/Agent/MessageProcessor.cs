using System.Globalization;
using Microsoft.Extensions.Options;
using Sokoni.Channels;
using Sokoni.Data;
using Sokoni.Domain;

namespace Sokoni.Agent;

/// <summary>
/// Channel-agnostic pipeline:
/// dedupe → typing indicator → voice/photo to text → store → apply location → run agent (unless owner took over) → reply.
/// </summary>
public sealed class MessageProcessor
{
    private const int MaxMediaBytes = 16 * 1024 * 1024;

    private readonly ConversationRepository _conversations;
    private readonly OrderRepository _orders;
    private readonly SalesAgent _agent;
    private readonly MediaUnderstanding _media;
    private readonly ChannelRouter _router;
    private readonly BusinessOptions _biz;
    private readonly ILogger<MessageProcessor> _log;

    public MessageProcessor(ConversationRepository conversations, OrderRepository orders, SalesAgent agent,
        MediaUnderstanding media, ChannelRouter router, IOptions<BusinessOptions> biz, ILogger<MessageProcessor> log)
    {
        _conversations = conversations;
        _orders = orders;
        _agent = agent;
        _media = media;
        _router = router;
        _biz = biz.Value;
        _log = log;
    }

    /// <returns>The reply sent, or null if nothing was sent (duplicate, or owner has taken over).</returns>
    public async Task<string?> ProcessAsync(IncomingMessage msg, CancellationToken ct = default)
    {
        if (await _conversations.MessageExistsAsync(msg.ExternalId, ct))
        {
            _log.LogInformation("Ignoring duplicate message {Id}", msg.ExternalId);
            return null;
        }

        var conv = await _conversations.GetOrCreateAsync(msg.Channel, msg.CustomerId, msg.CustomerName, msg.Phone, ct);
        var channel = _router.Get(msg.Channel);

        // Blue ticks + typing indicator straight away; transcription can take a few seconds.
        if (!conv.HumanTakeover && channel is WhatsAppChannel wa) await wa.MarkReadAsync(msg.ExternalId, ct);

        var mediaNote = await UnderstandMediaAsync(msg, ct);

        var stored = await _conversations.AddMessageAsync(conv.Id, "user", Describe(msg, mediaNote), msg.ExternalId, ct);
        if (!stored) return null; // lost a race with a webhook retry

        if (msg.Latitude is double lat && msg.Longitude is double lng)
        {
            var order = await _orders.GetOrCreateDraftAsync(conv.Id, _biz.DeliveryFee, ct);
            await _orders.SetLocationAsync(order.Id, lat, lng, msg.LocationLabel, ct);
        }

        if (conv.HumanTakeover)
        {
            // Transcript is still saved, so the owner can read the voice note in the dashboard.
            _log.LogInformation("Conversation {Id} is with the owner; assistant stays quiet.", conv.Id);
            return null;
        }

        string reply;
        try
        {
            reply = await _agent.RespondAsync(conv, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Agent failed for conversation {Id}", conv.Id);
            reply = "Samahani, kuna hitilafu kidogo upande wetu. Please try again in a minute, or we'll get back to you shortly.";
        }

        await _conversations.AddMessageAsync(conv.Id, "assistant", reply, null, ct);
        await channel.SendTextAsync(msg.CustomerId, reply, ct);
        return reply;
    }

    /// <summary>Turns a voice note or photo into a bracketed note the agent can read. Never throws.</summary>
    private async Task<string?> UnderstandMediaAsync(IncomingMessage msg, CancellationToken ct)
    {
        if (msg.Media is null) return null;
        var kind = msg.Media.Kind;

        if (kind == MediaKind.Audio && !_media.VoiceEnabled)
            return "[Sent a voice note. Voice notes are switched off; ask them kindly to type their message.]";
        if (kind == MediaKind.Image && !_media.VisionEnabled)
            return "[Sent a photo. You can't see photos; ask them to describe what they're looking for.]";

        try
        {
            var bytes = msg.Media.Bytes;
            var mime = msg.Media.MimeType;
            if (bytes is null && msg.Media.MediaId is not null && msg.Channel == ChannelType.WhatsApp)
            {
                var downloaded = await _router.Owner.DownloadMediaAsync(msg.Media.MediaId, ct);
                if (downloaded is { } d) { bytes = d.Bytes; mime ??= d.MimeType; }
            }

            if (bytes is null || bytes.Length == 0 || bytes.Length > MaxMediaBytes)
                return kind == MediaKind.Audio
                    ? "[Sent a voice note that couldn't be downloaded. Ask them to resend it or type the message.]"
                    : "[Sent a photo that couldn't be downloaded. Ask them to resend it or describe the product.]";

            if (kind == MediaKind.Audio)
            {
                var transcript = await _media.TranscribeAsync(bytes, mime, ct);
                return transcript is null
                    ? "[Sent a voice note, but it couldn't be transcribed (unclear audio or unsupported format). Ask them to type it or send it again.]"
                    : $"[Voice note, transcribed automatically. Swahili and Sheng words may be misheard:] {transcript}";
            }

            var description = await _media.DescribeImageAsync(bytes, mime, msg.Text, ct);
            return description is null
                ? "[Sent a photo you couldn't make out. Ask them to describe the product.]"
                : $"[Sent a photo. It shows: {description}]";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Media handling failed for {Id}", msg.ExternalId);
            return kind == MediaKind.Audio
                ? "[Sent a voice note that couldn't be processed. Ask them to type the message.]"
                : "[Sent a photo that couldn't be processed. Ask them to describe the product.]";
        }
    }

    private static string Describe(IncomingMessage msg, string? mediaNote)
    {
        var parts = new List<string>();
        if (msg.Latitude is double lat && msg.Longitude is double lng)
            parts.Add($"[Shared a location pin: {msg.LocationLabel ?? "no label"} ({lat.ToString("0.#####", CultureInfo.InvariantCulture)}, {lng.ToString("0.#####", CultureInfo.InvariantCulture)}). It has been saved as the delivery location.]");
        if (mediaNote is not null)
            parts.Add(mediaNote);
        if (!string.IsNullOrWhiteSpace(msg.Text))
            parts.Add(msg.Media?.Kind == MediaKind.Image ? $"Caption: {msg.Text.Trim()}" : msg.Text.Trim());
        return parts.Count > 0 ? string.Join("\n", parts) : "[Empty message]";
    }
}
