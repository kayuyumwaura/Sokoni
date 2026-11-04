namespace Sokoni.Domain;

public enum ChannelType
{
    WhatsApp = 1,
    Instagram = 2,
    Dev = 9
}

public enum MediaKind
{
    Audio = 1,
    Image = 2
}

/// <summary>A voice note or photo. Either a channel media id to download, or the bytes themselves (dev uploads).</summary>
public sealed record MediaAttachment(MediaKind Kind, string? MediaId, string? MimeType, byte[]? Bytes = null);

/// <summary>A customer message, normalised so the agent doesn't care which channel it came from.</summary>
public sealed record IncomingMessage(
    string ExternalId,
    ChannelType Channel,
    string CustomerId,
    string? CustomerName,
    string? Phone,
    string? Text,
    double? Latitude = null,
    double? Longitude = null,
    string? LocationLabel = null,
    MediaAttachment? Media = null);

public sealed class Conversation
{
    public int Id { get; set; }
    public ChannelType Channel { get; set; }
    public string CustomerId { get; set; } = "";
    public string? CustomerName { get; set; }
    public string? Phone { get; set; }
    public bool HumanTakeover { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastInboundAt { get; set; }
}

public sealed record ChatMessage(long Id, string Role, string Content, DateTime CreatedAt);

public sealed class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Category { get; set; }
    public decimal Price { get; set; }
    public int Stock { get; set; }
    public string? ImageUrl { get; set; }
    public bool Active { get; set; } = true;
}

public static class OrderStatus
{
    public const string Draft = "Draft";
    public const string AwaitingPayment = "AwaitingPayment";
    public const string Paid = "Paid";
    public const string Dispatched = "Dispatched";
    public const string Delivered = "Delivered";
    public const string Cancelled = "Cancelled";

    public static readonly string[] All = { Draft, AwaitingPayment, Paid, Dispatched, Delivered, Cancelled };
}

public sealed class Order
{
    public int Id { get; set; }
    public int ConversationId { get; set; }
    public string Status { get; set; } = OrderStatus.Draft;
    public decimal Subtotal { get; set; }
    public decimal DeliveryFee { get; set; }
    public decimal Total { get; set; }
    public string? DeliveryArea { get; set; }
    public string? DeliveryNotes { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? PaymentPhone { get; set; }
    public string? MpesaCheckoutRequestId { get; set; }
    public string? MpesaReceipt { get; set; }
    public string? LastPaymentError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public List<OrderLine> Lines { get; set; } = new();

    public bool HasDeliveryLocation => !string.IsNullOrWhiteSpace(DeliveryArea) || (Latitude.HasValue && Longitude.HasValue);
    public string? MapsLink => Latitude.HasValue && Longitude.HasValue
        ? $"https://maps.google.com/?q={Latitude.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)},{Longitude.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
        : null;
}

public sealed class OrderLine
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = "";
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
    public decimal LineTotal => UnitPrice * Quantity;
}
