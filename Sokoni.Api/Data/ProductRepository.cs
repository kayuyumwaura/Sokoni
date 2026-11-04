using Microsoft.Data.SqlClient;
using Sokoni.Domain;

namespace Sokoni.Data;

public sealed class ProductRepository
{
    private readonly Db _db;
    public ProductRepository(Db db) => _db = db;

    /// <summary>
    /// Keyword search across name, description and category. Each word must match somewhere,
    /// so "red lipstick" finds "Matte Liquid Lipstick - Ruby" only if both words appear;
    /// the agent is told to retry with fewer/English words when nothing comes back.
    /// </summary>
    public async Task<List<Product>> SearchAsync(string? query, string? category, int max = 10, CancellationToken ct = default)
    {
        var words = (query ?? "")
            .Split(new[] { ' ', ',', '-', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 1)
            .Take(5)
            .ToList();

        var sql = new System.Text.StringBuilder(
            "SELECT TOP (@max) Id, Name, Description, Category, Price, Stock, ImageUrl, Active FROM dbo.Products WHERE Active = 1");
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new SqlCommand { Connection = conn };
        cmd.Parameters.AddWithValue("@max", max);

        for (var i = 0; i < words.Count; i++)
        {
            sql.Append($" AND (Name LIKE @w{i} OR Description LIKE @w{i} OR Category LIKE @w{i})");
            cmd.Parameters.AddWithValue($"@w{i}", $"%{words[i]}%");
        }
        if (!string.IsNullOrWhiteSpace(category))
        {
            sql.Append(" AND Category = @cat");
            cmd.Parameters.AddWithValue("@cat", category.Trim());
        }
        sql.Append(" ORDER BY Category, Name");
        cmd.CommandText = sql.ToString();

        return await ReadProductsAsync(cmd, ct);
    }

    public async Task<Product?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT Id, Name, Description, Category, Price, Stock, ImageUrl, Active FROM dbo.Products WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        return (await ReadProductsAsync(cmd, ct)).FirstOrDefault();
    }

    public async Task<List<string>> GetCategoriesAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT DISTINCT Category FROM dbo.Products WHERE Active = 1 AND Category IS NOT NULL ORDER BY Category", conn);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    public async Task<List<Product>> ListAllAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT Id, Name, Description, Category, Price, Stock, ImageUrl, Active FROM dbo.Products ORDER BY Category, Name", conn);
        return await ReadProductsAsync(cmd, ct);
    }

    public async Task<int> UpsertAsync(Product p, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var sql = p.Id == 0
            ? @"INSERT INTO dbo.Products (Name, Description, Category, Price, Stock, ImageUrl, Active)
                OUTPUT INSERTED.Id VALUES (@name, @desc, @cat, @price, @stock, @img, @active)"
            : @"UPDATE dbo.Products SET Name=@name, Description=@desc, Category=@cat, Price=@price,
                Stock=@stock, ImageUrl=@img, Active=@active OUTPUT INSERTED.Id WHERE Id=@id";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", p.Id);
        cmd.Parameters.AddWithValue("@name", p.Name);
        cmd.Parameters.AddWithValue("@desc", p.Description.DbValue());
        cmd.Parameters.AddWithValue("@cat", p.Category.DbValue());
        cmd.Parameters.AddWithValue("@price", p.Price);
        cmd.Parameters.AddWithValue("@stock", p.Stock);
        cmd.Parameters.AddWithValue("@img", p.ImageUrl.DbValue());
        cmd.Parameters.AddWithValue("@active", p.Active);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<List<Product>> ReadProductsAsync(SqlCommand cmd, CancellationToken ct)
    {
        var list = new List<Product>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new Product
            {
                Id = r.GetInt32(r.GetOrdinal("Id")),
                Name = r.GetString(r.GetOrdinal("Name")),
                Description = r.GetNullableString("Description"),
                Category = r.GetNullableString("Category"),
                Price = r.GetDecimal(r.GetOrdinal("Price")),
                Stock = r.GetInt32(r.GetOrdinal("Stock")),
                ImageUrl = r.GetNullableString("ImageUrl"),
                Active = r.GetBoolean(r.GetOrdinal("Active"))
            });
        }
        return list;
    }
}
