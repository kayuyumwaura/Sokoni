using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Sokoni.Data;
using Sokoni.Payments;

namespace Sokoni.Controllers;

[ApiController]
[Route("api/mpesa")]
public sealed class MpesaCallbackController : ControllerBase
{
    private readonly MpesaOptions _o;
    private readonly OrderRepository _orders;
    private readonly OrderNotifier _notifier;
    private readonly ILogger<MpesaCallbackController> _log;

    public MpesaCallbackController(IOptions<MpesaOptions> o, OrderRepository orders, OrderNotifier notifier,
        ILogger<MpesaCallbackController> log)
    {
        _o = o.Value;
        _orders = orders;
        _notifier = notifier;
        _log = log;
    }

    /// <summary>Safaricom posts the STK result here. The secret key in the path stops forged callbacks.</summary>
    [HttpPost("callback/{key}")]
    public async Task<IActionResult> Callback(string key, [FromBody] JsonElement payload, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_o.CallbackKey) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(_o.CallbackKey)))
            return NotFound();

        // Always acknowledge, so Safaricom doesn't keep retrying.
        var ack = Ok(new { ResultCode = 0, ResultDesc = "Accepted" });

        if (!payload.TryGetProperty("Body", out var body) || !body.TryGetProperty("stkCallback", out var cb))
        {
            _log.LogWarning("Unexpected M-Pesa callback: {Payload}", payload.ToString());
            return ack;
        }

        var checkoutId = cb.GetProperty("CheckoutRequestID").GetString() ?? "";
        var resultCode = cb.GetProperty("ResultCode").ToString();
        var resultDesc = cb.TryGetProperty("ResultDesc", out var d) ? d.GetString() ?? "" : "";

        var order = await _orders.GetByCheckoutIdAsync(checkoutId, ct);
        if (order is null)
        {
            _log.LogWarning("M-Pesa callback for unknown checkout {Id}", checkoutId);
            return ack;
        }

        if (resultCode == "0")
        {
            string receipt = "UNKNOWN";
            if (cb.TryGetProperty("CallbackMetadata", out var meta) && meta.TryGetProperty("Item", out var items))
                foreach (var item in items.EnumerateArray())
                    if (item.GetProperty("Name").GetString() == "MpesaReceiptNumber" && item.TryGetProperty("Value", out var v))
                        receipt = v.ToString();

            _log.LogInformation("Order {Order} paid, receipt {Receipt}", order.Id, receipt);
            await _notifier.PaymentSucceededAsync(order, receipt, ct);
        }
        else
        {
            _log.LogInformation("Order {Order} payment failed: {Code} {Desc}", order.Id, resultCode, resultDesc);
            await _notifier.PaymentFailedAsync(order, resultDesc, ct);
        }

        return ack;
    }
}
