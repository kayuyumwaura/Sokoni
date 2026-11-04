using Microsoft.AspNetCore.Mvc;
using Sokoni.Agent;
using Sokoni.Channels;
using Sokoni.Data;
using Sokoni.Domain;
using Sokoni.Payments;

namespace Sokoni.Controllers;

/// <summary>
/// Test the whole flow from Postman/Bruno without WhatsApp or Daraja.
/// Off unless Security:DevEnabled = true.
/// </summary>
[ApiController]
[Route("api/dev")]
[DevOnly]
public sealed class DevController : ControllerBase
{
    private readonly MessageProcessor _processor;
    private readonly OrderRepository _orders;
    private readonly OrderNotifier _notifier;

    public DevController(MessageProcessor processor, OrderRepository orders, OrderNotifier notifier)
    {
        _processor = processor;
        _orders = orders;
        _notifier = notifier;
    }

    public sealed record DevChatRequest(
        string CustomerId,
        string? Text,
        string? Name = null,
        string? Phone = null,
        double? Latitude = null,
        double? Longitude = null,
        string? LocationLabel = null);

    /// <summary>Send a message as a fake customer and get the assistant's replies back.</summary>
    [HttpPost("chat")]
    public async Task<IActionResult> Chat([FromBody] DevChatRequest req, CancellationToken ct)
    {
        var msg = new IncomingMessage(
            ExternalId: "dev-" + Guid.NewGuid().ToString("N"),
            Channel: ChannelType.Dev,
            CustomerId: req.CustomerId,
            CustomerName: req.Name,
            Phone: req.Phone ?? "254700000000",
            Text: req.Text,
            Latitude: req.Latitude,
            Longitude: req.Longitude,
            LocationLabel: req.LocationLabel);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _processor.ProcessAsync(msg, ct);
        return Ok(new { replies = DevChannel.Drain(req.CustomerId), ms = sw.ElapsedMilliseconds });
    }

    /// <summary>
    /// Upload a voice note (.ogg/.opus/.wav) or photo as a fake customer. Send it from WhatsApp to yourself,
    /// download it, then: curl -F customerId=test1 -F file=@note.ogg .../api/dev/media
    /// </summary>
    [HttpPost("media")]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> Media([FromForm] string customerId, IFormFile file, [FromForm] string? text, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest("file is required.");
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);

        var mime = file.ContentType;
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(mime) || mime == "application/octet-stream")
            mime = ext switch
            {
                ".ogg" or ".opus" => "audio/ogg",
                ".wav" => "audio/wav",
                ".png" => "image/png",
                ".webp" => "image/webp",
                _ => "image/jpeg"
            };
        var kind = mime.StartsWith("audio", StringComparison.OrdinalIgnoreCase) ? MediaKind.Audio : MediaKind.Image;

        var msg = new IncomingMessage(
            ExternalId: "dev-" + Guid.NewGuid().ToString("N"),
            Channel: ChannelType.Dev,
            CustomerId: customerId,
            CustomerName: null,
            Phone: "254700000000",
            Text: text,
            Media: new MediaAttachment(kind, null, mime, ms.ToArray()));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _processor.ProcessAsync(msg, ct);
        var conv = await HttpContext.RequestServices.GetRequiredService<ConversationRepository>()
            .GetOrCreateAsync(ChannelType.Dev, customerId, null, null, ct);
        var history = await HttpContext.RequestServices.GetRequiredService<ConversationRepository>()
            .GetRecentMessagesAsync(conv.Id, 2, ct);
        return Ok(new
        {
            understood = history.FirstOrDefault(m => m.Role == "user")?.Content,
            replies = DevChannel.Drain(customerId),
            ms = sw.ElapsedMilliseconds
        });
    }

    public sealed record SimulatePaymentRequest(bool Success = true, string? Receipt = null, string? FailureReason = null);

    /// <summary>Pretend Safaricom called back. Works for real and simulated STK pushes.</summary>
    [HttpPost("simulate-payment/{orderId:int}")]
    public async Task<IActionResult> SimulatePayment(int orderId, [FromBody] SimulatePaymentRequest req, CancellationToken ct)
    {
        var order = await _orders.GetAsync(orderId, ct);
        if (order is null) return NotFound();
        if (order.Status != OrderStatus.AwaitingPayment) return Conflict($"Order is {order.Status}, not AwaitingPayment.");

        if (req.Success)
            await _notifier.PaymentSucceededAsync(order, req.Receipt ?? $"SIM{Random.Shared.Next(100000, 999999)}", ct);
        else
            await _notifier.PaymentFailedAsync(order, req.FailureReason ?? "Request cancelled by user", ct);

        var conv = await HttpContext.RequestServices.GetRequiredService<ConversationRepository>().GetAsync(order.ConversationId, ct);
        return Ok(new { replies = conv is null ? new List<string>() : DevChannel.Drain(conv.CustomerId) });
    }
}
