using Microsoft.Data.SqlClient;
using Sokoni.Domain;

namespace Sokoni.Data;

public sealed class ConversationRepository
{
    private readonly Db _db;
    public ConversationRepository(Db db) => _db = db;

    public async Task<Conversation> GetOrCreateAsync(ChannelType channel, string customerId, string? name, string? phone, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);

        var existing = await FindAsync(conn, channel, customerId, ct);
        if (existing is null)
        {
            try
            {
                await using var ins = new SqlCommand(
                    @"INSERT INTO dbo.Conversations (Channel, CustomerId, CustomerName, Phone, LastInboundAt)
                      VALUES (@ch, @cid, @name, @phone, SYSUTCDATETIME())", conn);
                ins.Parameters.AddWithValue("@ch", (byte)channel);
                ins.Parameters.AddWithValue("@cid", customerId);
                ins.Parameters.AddWithValue("@name", name.DbValue());
                ins.Parameters.AddWithValue("@phone", phone.DbValue());
                await ins.ExecuteNonQueryAsync(ct);
            }
            catch (SqlException ex) when (ex.Number is 2627 or 2601)
            {
                // Another message from the same customer created it first. Fine.
            }
        }
        else
        {
            await using var upd = new SqlCommand(
                @"UPDATE dbo.Conversations SET LastInboundAt = SYSUTCDATETIME(),
                    CustomerName = COALESCE(@name, CustomerName), Phone = COALESCE(@phone, Phone)
                  WHERE Id = @id", conn);
            upd.Parameters.AddWithValue("@id", existing.Id);
            upd.Parameters.AddWithValue("@name", name.DbValue());
            upd.Parameters.AddWithValue("@phone", phone.DbValue());
            await upd.ExecuteNonQueryAsync(ct);
        }

        return (await FindAsync(conn, channel, customerId, ct))!;
    }

    public async Task<Conversation?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT * FROM dbo.Conversations WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    public async Task<List<Conversation>> ListRecentAsync(int max = 50, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT TOP (@max) * FROM dbo.Conversations ORDER BY COALESCE(LastInboundAt, CreatedAt) DESC", conn);
        cmd.Parameters.AddWithValue("@max", max);
        var list = new List<Conversation>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Map(r));
        return list;
    }

    /// <summary>Cheap check before paying for transcription on a webhook retry.</summary>
    public async Task<bool> MessageExistsAsync(string? externalId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(externalId)) return false;
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT COUNT(1) FROM dbo.Messages WHERE ExternalId = @ext", conn);
        cmd.Parameters.AddWithValue("@ext", externalId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    /// <summary>Stores a message. Returns false if the channel message id was already stored (webhook retry).</summary>
    public async Task<bool> AddMessageAsync(int conversationId, string role, string content, string? externalId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            @"INSERT INTO dbo.Messages (ConversationId, Role, Content, ExternalId)
              VALUES (@cid, @role, @content, @ext)", conn);
        cmd.Parameters.AddWithValue("@cid", conversationId);
        cmd.Parameters.AddWithValue("@role", role);
        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@ext", externalId.DbValue());
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            return false;
        }
    }

    /// <summary>Most recent messages, returned oldest first.</summary>
    public async Task<List<ChatMessage>> GetRecentMessagesAsync(int conversationId, int max, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            @"SELECT * FROM (
                SELECT TOP (@max) Id, Role, Content, CreatedAt FROM dbo.Messages
                WHERE ConversationId = @cid ORDER BY Id DESC) m
              ORDER BY Id ASC", conn);
        cmd.Parameters.AddWithValue("@cid", conversationId);
        cmd.Parameters.AddWithValue("@max", max);
        var list = new List<ChatMessage>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ChatMessage(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetDateTime(3)));
        return list;
    }

    public async Task SetTakeoverAsync(int conversationId, bool on, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new SqlCommand("UPDATE dbo.Conversations SET HumanTakeover = @on WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", conversationId);
        cmd.Parameters.AddWithValue("@on", on);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<Conversation?> FindAsync(SqlConnection conn, ChannelType channel, string customerId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT * FROM dbo.Conversations WHERE Channel = @ch AND CustomerId = @cid", conn);
        cmd.Parameters.AddWithValue("@ch", (byte)channel);
        cmd.Parameters.AddWithValue("@cid", customerId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    private static Conversation Map(SqlDataReader r) => new()
    {
        Id = r.GetInt32(r.GetOrdinal("Id")),
        Channel = (ChannelType)r.GetByte(r.GetOrdinal("Channel")),
        CustomerId = r.GetString(r.GetOrdinal("CustomerId")),
        CustomerName = r.GetNullableString("CustomerName"),
        Phone = r.GetNullableString("Phone"),
        HumanTakeover = r.GetBoolean(r.GetOrdinal("HumanTakeover")),
        CreatedAt = r.GetDateTime(r.GetOrdinal("CreatedAt")),
        LastInboundAt = r.GetNullableDateTime("LastInboundAt")
    };
}
