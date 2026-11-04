using Microsoft.Data.SqlClient;
using Sokoni.Domain;

namespace Sokoni.Data;

/// <summary>
/// The cart is simply the customer's latest order in Draft or AwaitingPayment status.
/// </summary>
public sealed class OrderRepository
{
    private readonly Db _db;
    public OrderRepository(Db db) => _db = db;

    public async Task<Order?> GetActiveAsync(int conversationId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            @"SELECT TOP 1 * FROM dbo.Orders WHERE ConversationId = @cid AND Status IN ('Draft','AwaitingPayment')
              ORDER BY Id DESC", conn);
        cmd.Parameters.AddWithValue("@cid", conversationId);
        var order = await ReadSingleAsync(cmd, ct);
        if (order is not null) order.Lines = await GetLinesAsync(conn, order.Id, ct);
        return order;
    }

    public async Task<Order> GetOrCreateDraftAsync(int conversationId, decimal deliveryFee, CancellationToken ct = default)
    {
        var active = await GetActiveAsync(conversationId, ct);
        if (active is not null) return active;

        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            @"INSERT INTO dbo.Orders (ConversationId, Status, DeliveryFee, Total)
              OUTPUT INSERTED.Id VALUES (@cid, 'Draft', @fee, @fee)", conn);
        cmd.Parameters.AddWithValue("@cid", conversationId);
        cmd.Parameters.AddWithValue("@fee", deliveryFee);
        var id = (int)(await cmd.ExecuteScalarAsync(ct))!;
        return (await GetAsync(id, ct))!;
    }

    public async Task<Order?> GetAsync(int orderId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT * FROM dbo.Orders WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", orderId);
        var order = await ReadSingleAsync(cmd, ct);
        if (order is not null) order.Lines = await GetLinesAsync(conn, order.Id, ct);
        return order;
    }

    public async Task<Order?> GetByCheckoutIdAsync(string checkoutRequestId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT * FROM dbo.Orders WHERE MpesaCheckoutRequestId = @c", conn);
        cmd.Parameters.AddWithValue("@c", checkoutRequestId);
        var order = await ReadSingleAsync(cmd, ct);
        if (order is not null) order.Lines = await GetLinesAsync(conn, order.Id, ct);
        return order;
    }

    public async Task<List<Order>> ListAsync(string? status, int max = 100, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            @"SELECT TOP (@max) * FROM dbo.Orders
              WHERE (@status IS NULL AND Status <> 'Draft') OR Status = @status
              ORDER BY Id DESC", conn);
        cmd.Parameters.AddWithValue("@max", max);
        cmd.Parameters.AddWithValue("@status", status.DbValue());
        var orders = new List<Order>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) orders.Add(Map(r));
        foreach (var o in orders) o.Lines = await GetLinesAsync(conn, o.Id, ct);
        return orders;
    }

    /// <summary>Sets quantity for a product (0 removes it). Changing the cart cancels any pending payment request.</summary>
    public async Task SetLineAsync(int orderId, Product product, int quantity, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        var sql = quantity <= 0
            ? "DELETE FROM dbo.OrderLines WHERE OrderId = @oid AND ProductId = @pid"
            : @"MERGE dbo.OrderLines AS t
                USING (SELECT @oid AS OrderId, @pid AS ProductId) AS s
                ON t.OrderId = s.OrderId AND t.ProductId = s.ProductId
                WHEN MATCHED THEN UPDATE SET Quantity = @qty, UnitPrice = @price, ProductName = @name
                WHEN NOT MATCHED THEN INSERT (OrderId, ProductId, ProductName, UnitPrice, Quantity)
                     VALUES (@oid, @pid, @name, @price, @qty);";
        await using (var cmd = new SqlCommand(sql, conn, tx))
        {
            cmd.Parameters.AddWithValue("@oid", orderId);
            cmd.Parameters.AddWithValue("@pid", product.Id);
            cmd.Parameters.AddWithValue("@qty", quantity);
            cmd.Parameters.AddWithValue("@price", product.Price);
            cmd.Parameters.AddWithValue("@name", product.Name);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var reset = new SqlCommand(
            @"UPDATE dbo.Orders SET Status = 'Draft', MpesaCheckoutRequestId = NULL WHERE Id = @oid", conn, tx))
        {
            reset.Parameters.AddWithValue("@oid", orderId);
            await reset.ExecuteNonQueryAsync(ct);
        }

        await RecalculateAsync(conn, tx, orderId, ct);
        await tx.CommitAsync(ct);
    }

    public async Task SetDeliveryAsync(int orderId, string? area, string? notes, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            @"UPDATE dbo.Orders SET DeliveryArea = COALESCE(@area, DeliveryArea),
                DeliveryNotes = COALESCE(@notes, DeliveryNotes) WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", orderId);
        cmd.Parameters.AddWithValue("@area", area.DbValue());
        cmd.Parameters.AddWithValue("@notes", notes.DbValue());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetLocationAsync(int orderId, double lat, double lng, string? label, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            @"UPDATE dbo.Orders SET Latitude = @lat, Longitude = @lng,
                DeliveryArea = COALESCE(DeliveryArea, @label) WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", orderId);
        cmd.Parameters.AddWithValue("@lat", lat);
        cmd.Parameters.AddWithValue("@lng", lng);
        cmd.Parameters.AddWithValue("@label", label.DbValue());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkAwaitingPaymentAsync(int orderId, string phone, string checkoutRequestId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            @"UPDATE dbo.Orders SET Status = 'AwaitingPayment', PaymentPhone = @phone,
                MpesaCheckoutRequestId = @chk, LastPaymentError = NULL WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", orderId);
        cmd.Parameters.AddWithValue("@phone", phone);
        cmd.Parameters.AddWithValue("@chk", checkoutRequestId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Marks paid only if still awaiting payment, so a repeated callback can't double-notify.</summary>
    public async Task<bool> MarkPaidAsync(int orderId, string receipt, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            @"UPDATE dbo.Orders SET Status = 'Paid', MpesaReceipt = @rcpt, PaidAt = SYSUTCDATETIME()
              WHERE Id = @id AND Status = 'AwaitingPayment'", conn);
        cmd.Parameters.AddWithValue("@id", orderId);
        cmd.Parameters.AddWithValue("@rcpt", receipt);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task MarkPaymentFailedAsync(int orderId, string reason, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            @"UPDATE dbo.Orders SET Status = 'Draft', MpesaCheckoutRequestId = NULL, LastPaymentError = @why
              WHERE Id = @id AND Status = 'AwaitingPayment'", conn);
        cmd.Parameters.AddWithValue("@id", orderId);
        cmd.Parameters.AddWithValue("@why", reason.Length > 300 ? reason[..300] : reason);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetStatusAsync(int orderId, string status, CancellationToken ct = default)
    {
        if (!OrderStatus.All.Contains(status)) throw new ArgumentException($"Unknown status '{status}'.");
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new SqlCommand("UPDATE dbo.Orders SET Status = @s WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", orderId);
        cmd.Parameters.AddWithValue("@s", status);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task RecalculateAsync(SqlConnection conn, SqlTransaction tx, int orderId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            @"UPDATE o SET
                Subtotal = COALESCE(l.Sub, 0),
                Total = COALESCE(l.Sub, 0) + o.DeliveryFee
              FROM dbo.Orders o
              OUTER APPLY (SELECT SUM(UnitPrice * Quantity) AS Sub FROM dbo.OrderLines WHERE OrderId = o.Id) l
              WHERE o.Id = @id", conn, tx);
        cmd.Parameters.AddWithValue("@id", orderId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<OrderLine>> GetLinesAsync(SqlConnection conn, int orderId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT ProductId, ProductName, UnitPrice, Quantity FROM dbo.OrderLines WHERE OrderId = @id ORDER BY Id", conn);
        cmd.Parameters.AddWithValue("@id", orderId);
        var lines = new List<OrderLine>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            lines.Add(new OrderLine
            {
                ProductId = r.GetInt32(0),
                ProductName = r.GetString(1),
                UnitPrice = r.GetDecimal(2),
                Quantity = r.GetInt32(3)
            });
        return lines;
    }

    private static async Task<Order?> ReadSingleAsync(SqlCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    private static Order Map(SqlDataReader r) => new()
    {
        Id = r.GetInt32(r.GetOrdinal("Id")),
        ConversationId = r.GetInt32(r.GetOrdinal("ConversationId")),
        Status = r.GetString(r.GetOrdinal("Status")),
        Subtotal = r.GetDecimal(r.GetOrdinal("Subtotal")),
        DeliveryFee = r.GetDecimal(r.GetOrdinal("DeliveryFee")),
        Total = r.GetDecimal(r.GetOrdinal("Total")),
        DeliveryArea = r.GetNullableString("DeliveryArea"),
        DeliveryNotes = r.GetNullableString("DeliveryNotes"),
        Latitude = r.GetNullableDouble("Latitude"),
        Longitude = r.GetNullableDouble("Longitude"),
        PaymentPhone = r.GetNullableString("PaymentPhone"),
        MpesaCheckoutRequestId = r.GetNullableString("MpesaCheckoutRequestId"),
        MpesaReceipt = r.GetNullableString("MpesaReceipt"),
        LastPaymentError = r.GetNullableString("LastPaymentError"),
        CreatedAt = r.GetDateTime(r.GetOrdinal("CreatedAt")),
        PaidAt = r.GetNullableDateTime("PaidAt")
    };
}
