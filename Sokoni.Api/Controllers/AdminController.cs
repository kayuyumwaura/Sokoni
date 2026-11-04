using Microsoft.AspNetCore.Mvc;
using Sokoni.Channels;
using Sokoni.Data;
using Sokoni.Domain;
using Sokoni.Payments;

namespace Sokoni.Controllers;

/// <summary>Backend for the owner dashboard (Sunday): orders, chats, take over / hand back, reply, catalogue.</summary>
[ApiController]
[Route("api/admin")]
[AdminKey]
public sealed class AdminController : ControllerBase
{
    private readonly OrderRepository _orders;
    private readonly ConversationRepository _conversations;
    private readonly ProductRepository _products;
    private readonly ChannelRouter _router;
    private readonly OrderNotifier _notifier;

    public AdminController(OrderRepository orders, ConversationRepository conversations, ProductRepository products,
        ChannelRouter router, OrderNotifier notifier)
    {
        _orders = orders;
        _conversations = conversations;
        _products = products;
        _router = router;
        _notifier = notifier;
    }

    [HttpGet("orders")]
    public async Task<IActionResult> Orders([FromQuery] string? status, CancellationToken ct) =>
        Ok(await _orders.ListAsync(string.IsNullOrWhiteSpace(status) ? null : status, 100, ct));

    public sealed record StatusRequest(string Status);

    [HttpPost("orders/{id:int}/status")]
    public async Task<IActionResult> SetStatus(int id, [FromBody] StatusRequest req, CancellationToken ct)
    {
        if (!OrderStatus.All.Contains(req.Status)) return BadRequest($"Status must be one of: {string.Join(", ", OrderStatus.All)}");
        var order = await _orders.GetAsync(id, ct);
        if (order is null) return NotFound();
        await _orders.SetStatusAsync(id, req.Status, ct);
        await _notifier.StatusChangedAsync(order, req.Status, ct);
        return NoContent();
    }

    [HttpGet("conversations")]
    public async Task<IActionResult> Conversations(CancellationToken ct) =>
        Ok(await _conversations.ListRecentAsync(50, ct));

    [HttpGet("conversations/{id:int}/messages")]
    public async Task<IActionResult> Messages(int id, CancellationToken ct) =>
        Ok(await _conversations.GetRecentMessagesAsync(id, 200, ct));

    public sealed record TakeoverRequest(bool On);

    /// <summary>On = owner handles the chat and the assistant stays quiet. Off = hand back to the assistant.</summary>
    [HttpPost("conversations/{id:int}/takeover")]
    public async Task<IActionResult> Takeover(int id, [FromBody] TakeoverRequest req, CancellationToken ct)
    {
        if (await _conversations.GetAsync(id, ct) is null) return NotFound();
        await _conversations.SetTakeoverAsync(id, req.On, ct);
        return NoContent();
    }

    public sealed record ReplyRequest(string Text);

    /// <summary>Owner replies from the dashboard. Sending a reply also takes over the chat.</summary>
    [HttpPost("conversations/{id:int}/reply")]
    public async Task<IActionResult> Reply(int id, [FromBody] ReplyRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Text)) return BadRequest("Text is required.");
        var conv = await _conversations.GetAsync(id, ct);
        if (conv is null) return NotFound();
        if (conv.LastInboundAt is DateTime last && DateTime.UtcNow - last > TimeSpan.FromHours(24))
            return Conflict("More than 24 hours since the customer's last message. WhatsApp only allows template messages now.");

        await _conversations.SetTakeoverAsync(id, true, ct);
        await _conversations.AddMessageAsync(id, "owner", req.Text.Trim(), null, ct);
        await _router.Get(conv.Channel).SendTextAsync(conv.CustomerId, req.Text.Trim(), ct);
        return NoContent();
    }

    [HttpGet("products")]
    public async Task<IActionResult> Products(CancellationToken ct) => Ok(await _products.ListAllAsync(ct));

    [HttpPost("products")]
    public async Task<IActionResult> SaveProduct([FromBody] Product product, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(product.Name) || product.Price <= 0) return BadRequest("Name and a positive price are required.");
        var id = await _products.UpsertAsync(product, ct);
        return Ok(new { id });
    }
}
