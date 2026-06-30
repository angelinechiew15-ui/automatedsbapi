using Oracle.ManagedDataAccess.Client;

namespace AutomatedSb.Api.Data;

/// <summary>
/// Extension helpers for <see cref="OracleConnection"/> that apply
/// session-level settings required for safe numeric conversions.
/// </summary>
public static class OracleConnectionExtensions
{
    /// <summary>
    /// Opens the connection and immediately sets
    /// <c>NLS_NUMERIC_CHARACTERS = '.,'</c> so that all subsequent
    /// <c>TO_NUMBER</c> calls treat <c>.</c> as the decimal separator,
    /// regardless of the server's German locale default.
    /// </summary>
    public static async Task OpenWithNlsAsync(this OracleConnection conn)
    {
        await conn.OpenAsync();
        await using var cmd = new OracleCommand(
            "ALTER SESSION SET NLS_NUMERIC_CHARACTERS = '.,'", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
