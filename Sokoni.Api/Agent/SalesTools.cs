using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Sokoni.Channels;
using Sokoni.Data;
using Sokoni.Domain;
using Sokoni.Payments;

namespace Sokoni.Agent;

/// <summary>
/// The actions Muse Spark can take. The model decides *when* to call them; the code decides *what's allowed*
/// (real prices, stock limits, payment rules), so the model can't invent a price or skip payment.
/// </summary>
public sealed class SalesTools
{
    private readonly ProductRepository _products;
    private readonly OrderRepository _orders;
    private readonly ConversationRepository _conversations;
    private readonly MpesaClient _mpesa;
    private readonly ChannelRouter _router;
    private readonly BusinessOptions _biz;
    private readonly ILogger<SalesTools> _log;

    public SalesTools(ProductRepository products, OrderRepository orders, ConversationRepository conversations,
        MpesaClient mpesa, ChannelRouter router, IOptions<BusinessOptions> biz, ILogger<SalesTools> log)
    {
        _products = products;
        _orders = orders;
        _conversations = conversations;
        _mpesa = mpesa;
        _router = router;
        _biz = biz.Value;
        _log = log;
    }

    public static JsonArray Definitions() => new()
    {
        Fn("search_products",
            "Search the shop catalogue. ALWAYS call this before mentioning any product, price or availability. " +
            "Translate Swahili/Sheng words into simple English product keywords (e.g. 'rangi ya mdomo' -> 'lipstick'). " +
            "If nothing is found, retry with fewer or broader words, or with an empty query to list everything.",
            Props(
                ("query", "string", "1-3 English keywords, or empty string to browse"),
                ("category", "string", "Optional exact category name from the shop's category list")),
            "query"),

        Fn("update_cart",
            "Add a product to the cart, change its quantity, or remove it (quantity 0). Use the product_id from search_products.",
            Props(
                ("product_id", "integer", "Product id from search_products"),
                ("quantity", "integer", "New total quantity for this product; 0 removes it")),
            "product_id", "quantity"),

        Fn("view_cart", "Show the current cart: items, subtotal, delivery fee, total and delivery details.", Props()),

        Fn("set_delivery",
            "Save where to deliver. Call when the customer names an area/estate/landmark or gives directions. " +
            "A WhatsApp location pin is saved automatically, so don't ask for it again if the cart shows one.",
            Props(
                ("area", "string", "Area, estate or landmark, e.g. 'Rongai, near Tuskys Maasai Mall'"),
                ("notes", "string", "Optional extra directions, gate, house number, preferred time")),
            "area"),

        Fn("request_payment",
            "Send an M-Pesa STK push so the customer can pay the cart total by entering their PIN. " +
            "Only call after the customer has seen the order summary with the total AND confirmed they want to pay, " +
            "and a delivery location is saved. Leave phone empty to use the number they're chatting from.",
            Props(("phone", "string", "Optional M-Pesa number, e.g. 0712345678, if different from their WhatsApp number"))),

        Fn("handoff_to_owner",
            "Bring in the human shop owner. Use for complaints, refunds, returns, custom/bulk orders, " +
            "questions you can't answer from the catalogue, or when the customer asks for a person.",
            Props(("reason", "string", "One short sentence for the owner explaining what the customer needs")),
            "reason")
    };

    public async Task<string> ExecuteAsync(string name, JsonObject args, Conversation conv, CancellationToken ct)
    {
        try
        {
            return name switch
            {
                "search_products" => await SearchAsync(args, ct),
                "update_cart" => await UpdateCartAsync(args, conv, ct),
                "view_cart" => await ViewCartAsync(conv, ct),
                "set_delivery" => await SetDeliveryAsync(args, conv, ct),
                "request_payment" => await RequestPaymentAsync(args, conv, ct),
                "handoff_to_owner" => await HandoffAsync(args, conv, ct),
                _ => Error($"Unknown tool '{name}'.")
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Tool {Tool} failed", name);
            return Error("That action failed on our side. Apologise briefly and offer to try again or bring in the owner.");
        }
    }

    private async Task<string> SearchAsync(JsonObject args, CancellationToken ct)
    {
        var query = args["query"]?.GetValue<string>();
        var category = args["category"]?.GetValue<string>();
        var items = await _products.SearchAsync(query, string.IsNullOrWhiteSpace(category) ? null : category, 10, ct);
        var result = new JsonObject
        {
            ["count"] = items.Count,
            ["products"] = new JsonArray(items.Select(p => (JsonNode)new JsonObject
            {
                ["product_id"] = p.Id,
                ["name"] = p.Name,
                ["description"] = p.Description,
                ["category"] = p.Category,
                ["price_kes"] = p.Price,
                ["in_stock"] = p.Stock > 0,
                ["stock"] = p.Stock
            }).ToArray())
        };
        if (items.Count == 0) result["hint"] = "No matches. Retry with fewer/broader English keywords or an empty query.";
        return result.ToJsonString();
    }

    private async Task<string> UpdateCartAsync(JsonObject args, Conversation conv, CancellationToken ct)
    {
        var productId = ReadInt(args, "product_id");
        var qty = ReadInt(args, "quantity");
        if (productId is null || qty is null) return Error("product_id and quantity are required integers.");
        if (qty < 0 || qty > 100) return Error("Quantity must be between 0 and 100. For bulk orders, hand off to the owner.");

        var product = await _products.GetAsync(productId.Value, ct);
        if (product is null || !product.Active) return Error("That product doesn't exist. Search again.");
        if (qty > product.Stock)
            return Error($"Only {product.Stock} of '{product.Name}' in stock. Tell the customer and offer that many or an alternative.");

        var order = await _orders.GetOrCreateDraftAsync(conv.Id, _biz.DeliveryFee, ct);
        await _orders.SetLineAsync(order.Id, product, qty.Value, ct);
        return CartJson(await _orders.GetAsync(order.Id, ct));
    }

    private async Task<string> ViewCartAsync(Conversation conv, CancellationToken ct) =>
        CartJson(await _orders.GetActiveAsync(conv.Id, ct));

    private async Task<string> SetDeliveryAsync(JsonObject args, Conversation conv, CancellationToken ct)
    {
        var area = args["area"]?.GetValue<string>()?.Trim();
        var notes = args["notes"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(area)) return Error("area is required.");

        var order = await _orders.GetOrCreateDraftAsync(conv.Id, _biz.DeliveryFee, ct);
        await _orders.SetDeliveryAsync(order.Id, area, string.IsNullOrWhiteSpace(notes) ? null : notes, ct);
        return CartJson(await _orders.GetAsync(order.Id, ct));
    }

    private async Task<string> RequestPaymentAsync(JsonObject args, Conversation conv, CancellationToken ct)
    {
        var order = await _orders.GetActiveAsync(conv.Id, ct);
        if (order is null || order.Lines.Count == 0) return Error("The cart is empty.");
        if (!order.HasDeliveryLocation) return Error("No delivery location yet. Ask for their area or a WhatsApp location pin first.");

        var requested = args["phone"]?.GetValue<string>();
        var phone = PhoneNumbers.NormaliseKenyan(string.IsNullOrWhiteSpace(requested) ? conv.Phone : requested);
        if (phone is null)
            return Error("No valid Kenyan M-Pesa number. Ask the customer for the number to send the payment prompt to (e.g. 0712345678).");

        var amount = (int)Math.Ceiling(order.Total);

        if (!_mpesa.IsConfigured)
        {
            // Demo mode: lets the full flow run before Daraja keys are in. Confirm via /api/dev/simulate-payment.
            var fakeId = $"SIM-{order.Id}-{DateTime.UtcNow:HHmmss}";
            await _orders.MarkAwaitingPaymentAsync(order.Id, phone, fakeId, ct);
            return new JsonObject
            {
                ["status"] = "payment_prompt_sent",
                ["simulated"] = true,
                ["order_id"] = order.Id,
                ["amount_kes"] = amount,
                ["phone"] = PhoneNumbers.Mask(phone),
                ["next"] = "Tell the customer an M-Pesa prompt is on its way to that number and to enter their PIN. Payment confirmation arrives automatically."
            }.ToJsonString();
        }

        var result = await _mpesa.StkPushAsync(phone, amount, $"ORDER{order.Id}", $"Order {order.Id}", ct);
        if (!result.Accepted || result.CheckoutRequestId is null)
            return Error($"M-Pesa didn't accept the request ({result.Message}). Ask them to check the number, or try again shortly.");

        await _orders.MarkAwaitingPaymentAsync(order.Id, phone, result.CheckoutRequestId, ct);
        return new JsonObject
        {
            ["status"] = "payment_prompt_sent",
            ["order_id"] = order.Id,
            ["amount_kes"] = amount,
            ["phone"] = PhoneNumbers.Mask(phone),
            ["next"] = "Tell the customer to check their phone and enter their M-Pesa PIN. Confirmation arrives automatically; don't claim it's paid."
        }.ToJsonString();
    }

    private async Task<string> HandoffAsync(JsonObject args, Conversation conv, CancellationToken ct)
    {
        var reason = args["reason"]?.GetValue<string>() ?? "Customer needs help";
        await _conversations.SetTakeoverAsync(conv.Id, true, ct);

        var who = conv.CustomerName ?? "A customer";
        var contact = conv.Phone is not null ? $"\nChat with them: https://wa.me/{conv.Phone}" : "";
        await TryNotifyOwnerAsync($"🙋 {who} needs you ({conv.Channel}).\nReason: {reason}{contact}\n\nThe assistant has paused for this chat. Reopen it from the dashboard when done.", ct);

        return new JsonObject
        {
            ["status"] = "owner_notified",
            ["next"] = "Tell the customer the owner has been alerted and will reply here shortly. Don't promise a specific time."
        }.ToJsonString();
    }

    private async Task TryNotifyOwnerAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_biz.OwnerPhone)) { _log.LogWarning("OwnerPhone not set: {Text}", text); return; }
        try { await _router.Owner.SendTextAsync(_biz.OwnerPhone, text, ct); }
        catch (Exception ex) { _log.LogError(ex, "Owner alert failed (owner may be outside the 24h window)."); }
    }

    internal static string CartJson(Order? order)
    {
        if (order is null || order.Lines.Count == 0)
            return new JsonObject
            {
                ["empty"] = true,
                ["delivery_area"] = order?.DeliveryArea,
                ["has_location_pin"] = order?.Latitude is not null
            }.ToJsonString();

        return new JsonObject
        {
            ["order_id"] = order.Id,
            ["status"] = order.Status,
            ["items"] = new JsonArray(order.Lines.Select(l => (JsonNode)new JsonObject
            {
                ["product_id"] = l.ProductId,
                ["name"] = l.ProductName,
                ["quantity"] = l.Quantity,
                ["unit_price_kes"] = l.UnitPrice,
                ["line_total_kes"] = l.LineTotal
            }).ToArray()),
            ["subtotal_kes"] = order.Subtotal,
            ["delivery_fee_kes"] = order.DeliveryFee,
            ["total_kes"] = order.Total,
            ["delivery_area"] = order.DeliveryArea,
            ["delivery_notes"] = order.DeliveryNotes,
            ["has_location_pin"] = order.Latitude is not null,
            ["last_payment_error"] = order.LastPaymentError
        }.ToJsonString();
    }

    private static int? ReadInt(JsonObject args, string key)
    {
        var node = args[key];
        if (node is null) return null;
        try { return node.GetValue<int>(); } catch { /* model sometimes sends "2" */ }
        return int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static string Error(string message) => new JsonObject { ["error"] = message }.ToJsonString();

    private static JsonObject Fn(string name, string description, JsonObject properties, params string[] required) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray())
            }
        }
    };

    private static JsonObject Props(params (string Name, string Type, string Description)[] props)
    {
        var o = new JsonObject();
        foreach (var (n, t, d) in props) o[n] = new JsonObject { ["type"] = t, ["description"] = d };
        return o;
    }
}
