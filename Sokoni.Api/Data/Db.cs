using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Sokoni.Data;

/// <summary>Opens SQL Server connections (raw ADO.NET, no ORM) and applies the schema.</summary>
public sealed class Db
{
    private readonly string _connectionString;
    private readonly ILogger<Db> _log;

    public Db(IConfiguration config, ILogger<Db> log)
    {
        _connectionString = config.GetConnectionString("Sokoni")
            ?? throw new InvalidOperationException("ConnectionStrings:Sokoni is not set.");
        _log = log;
    }

    public async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    /// <summary>Creates the database if it doesn't exist yet (local dev). On hosted SQL, create it in the host's panel.</summary>
    private async Task EnsureDatabaseExistsAsync(CancellationToken ct)
    {
        try
        {
            await using var probe = await OpenAsync(ct);
            return;
        }
        catch (SqlException ex) when (ex.Number == 4060) // "Cannot open database ... requested by the login"
        {
            var target = new SqlConnectionStringBuilder(_connectionString);
            var dbName = target.InitialCatalog;
            if (string.IsNullOrWhiteSpace(dbName)) throw;

            var master = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = "master" };
            await using var conn = new SqlConnection(master.ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand($"IF DB_ID(@name) IS NULL CREATE DATABASE [{dbName.Replace("]", "]]")}]", conn);
            cmd.Parameters.AddWithValue("@name", dbName);
            await cmd.ExecuteNonQueryAsync(ct);
            _log.LogInformation("Created database {Db}.", dbName);
            SqlConnection.ClearAllPools();
        }
    }

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await EnsureDatabaseExistsAsync(ct);

        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("schema.sql", StringComparison.OrdinalIgnoreCase));
        await using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        var script = await reader.ReadToEndAsync(ct);

        var batches = Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .Select(b => b.Trim())
            .Where(b => b.Length > 0 && !IsOnlyComments(b));

        await using var conn = await OpenAsync(ct);
        foreach (var batch in batches)
        {
            await using var cmd = new SqlCommand(batch, conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        _log.LogInformation("Database schema is up to date.");
    }

    private static bool IsOnlyComments(string batch) =>
        batch.Split('\n').All(l => string.IsNullOrWhiteSpace(l) || l.TrimStart().StartsWith("--"));
}

internal static class SqlReaderExtensions
{
    public static string? GetNullableString(this SqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    public static double? GetNullableDouble(this SqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetDouble(i);
    }

    public static DateTime? GetNullableDateTime(this SqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetDateTime(i);
    }

    public static object DbValue(this object? value) => value ?? DBNull.Value;
}
