using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Sokoni.Channels;
using Sokoni.Data;
using Sokoni.Domain;

namespace Sokoni.Payments;

/// <summary>Turns payment results and status changes into messages for the customer and the owner.</summary>
public sealed class OrderNotifier
{
    private readonly OrderRepository _orders;
    private readonly ConversationRepository _conversations;
    private readonly ChannelRouter _router;
    private readonly BusinessOptions _biz;
    private readonly ILogger<OrderNotifier> _log;

    public OrderNotifier(OrderRepository orders, ConversationRepository conversations, ChannelRouter router,
        IOptions<BusinessOptions> biz, ILogger<OrderNotifier> log)
    {
        _orders = orders;
        _conversations = conversations;
        _router = router;
        _biz = biz.Value;
        _log = log;
    }

    public async Task PaymentSucceededAsync(Order order, string receipt, CancellationToken ct = default)
    {
        if (!await _orders.MarkPaidAsync(order.Id, receipt, ct)) return; // already handled
        var conv = await _conversations.GetAsync(order.ConversationId, ct);
        if (conv is null) return;

        await SendToCustomerAsync(conv,
            $"✅ Payment received, asante! M-Pesa ref *{receipt}*.\n" +
            $"Order #{order.Id} (KES {Kes(order.Total)}) is confirmed and we're getting it ready for delivery to {order.DeliveryArea ?? "your pinned location"}. " +
            "We'll let you know when it's on the way.", ct);

        var sb = new StringBuilder();
        sb.AppendLine($"🛍️ *New paid order #{order.Id}*  KES {Kes(order.Total)}");
        foreach (var l in order.Lines) sb.AppendLine($"- {l.Quantity} x {l.ProductName}");
        sb.AppendLine($"Customer: {conv.CustomerName ?? "-"} {(conv.Phone is not null ? "+" + conv.Phone : "")}");
        sb.AppendLine($"Deliver to: {order.DeliveryArea ?? "-"}");
        if (!string.IsNullOrWhiteSpace(order.DeliveryNotes)) sb.AppendLine($"Notes: {order.DeliveryNotes}");
        if (order.MapsLink is not null) sb.AppendLine($"Map: {order.MapsLink}");
        sb.Append($"M-Pesa: {receipt}");
        await SendToOwnerAsync(sb.ToString(), ct);
    }

    public async Task PaymentFailedAsync(Order order, string reason, CancellationToken ct = default)
    {
        await _orders.MarkPaymentFailedAsync(order.Id, reason, ct);
        var conv = await _conversations.GetAsync(order.ConversationId, ct);
        if (conv is null) return;
        await SendToCustomerAsync(conv,
            $"The M-Pesa payment didn't go through ({Friendly(reason)}). Your cart is saved. Reply *pay* to get a new prompt, or send a different M-Pesa number.", ct);
    }

    public async Task StatusChangedAsync(Order order, string newStatus, CancellationToken ct = default)
    {
        var conv = await _conversations.GetAsync(order.ConversationId, ct);
        if (conv is null) return;
        var text = newStatus switch
        {
            OrderStatus.Dispatched => $"🛵 Your order #{order.Id} is on the way! The rider will call you on arrival.",
            OrderStatus.Delivered => $"📦 Order #{order.Id} delivered. Asante for shopping with {_biz.Name}! 💛",
            OrderStatus.Cancelled => $"Order #{order.Id} has been cancelled. Reply here if you have any questions.",
            _ => null
        };
        if (text is not null) await SendToCustomerAsync(conv, text, ct);
    }

    private async Task SendToCustomerAsync(Conversation conv, string text, CancellationToken ct)
    {
        await _conversations.AddMessageAsync(conv.Id, "assistant", text, null, ct);
        try { await _router.Get(conv.Channel).SendTextAsync(conv.CustomerId, text, ct); }
        catch (Exception ex) { _log.LogError(ex, "Couldn't message customer on conversation {Id}", conv.Id); }
    }

    private async Task SendToOwnerAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_biz.OwnerPhone)) { _log.LogWarning("OwnerPhone not set: {Text}", text); return; }
        try { await _router.Owner.SendTextAsync(_biz.OwnerPhone, text, ct); }
        catch (Exception ex) { _log.LogError(ex, "Owner alert failed (owner may be outside the 24h window)."); }
    }

    private static string Kes(decimal v) => v.ToString("#,0", CultureInfo.InvariantCulture);

    private static string Friendly(string reason)
    {
        var r = reason.ToLowerInvariant();
        if (r.Contains("cancel")) return "it was cancelled";
        if (r.Contains("insufficient")) return "insufficient balance";
        if (r.Contains("timeout") || r.Contains("unreachable") || r.Contains("not reachable")) return "the prompt timed out";
        if (r.Contains("pin") || r.Contains("initiator")) return "the PIN wasn't accepted";
        return "M-Pesa declined it";
    }
}
