using AutomatedSb.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Oracle.ManagedDataAccess.Client;
using System.Text.Json.Serialization;

namespace AutomatedSb.Api.Controllers;

[ApiController]
[Route("api/orm-summary")]
public class OrmSummaryController : ControllerBase
{
    private readonly IOracleConnectionFactory _factory;
    private readonly IOracleRealisConnectionFactory _realisFactory;
    private readonly ILogger<OrmSummaryController> _logger;

    public OrmSummaryController(
        IOracleConnectionFactory factory,
        IOracleRealisConnectionFactory realisFactory,
        ILogger<OrmSummaryController> logger)
    {
        _factory = factory;
        _realisFactory = realisFactory;
        _logger = logger;
    }

    // GET api/orm-summary/max-horizon
    [HttpGet("max-horizon")]
    public async Task<ActionResult> GetMaxHorizon()
    {
        try
        {
            await using var conn = _realisFactory.Create();
            await conn.OpenAsync();

            const string sql = "SELECT MAX(rhz_name) AS current_horizon FROM rfc_horizon";
            await using var cmd = new OracleCommand(sql, conn);
            var result = await cmd.ExecuteScalarAsync();
            var maxHorizon = result == null || result == DBNull.Value ? "" : result.ToString() ?? "";

            return Ok(new { success = true, horizon = maxHorizon });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "Error getting max horizon");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // GET api/orm-summary/{horizon}
    [HttpGet("{horizon}")]
    public async Task<ActionResult> GetOrmSummary(string horizon)
    {
        try
        {
            var comment = await GetCommentORM(horizon);
            return Ok(new { success = true, comment });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "Error getting ORM summary for horizon: {Horizon}", horizon);
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // GET api/orm-summary/{horizon}/previous
    [HttpGet("{horizon}/previous")]
    public async Task<ActionResult> GetPreviousOrmSummary(string horizon)
    {
        try
        {
            var previousHorizon = await GetPreviousHorizon(horizon);
            if (string.IsNullOrEmpty(previousHorizon))
            {
                return Ok(new { success = true, comment = "", previousHorizon = (string?)null });
            }

            var comment = await GetCommentORM(previousHorizon);
            return Ok(new { success = true, comment, previousHorizon });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "Error getting previous ORM summary for horizon: {Horizon}", horizon);
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // POST api/orm-summary
    [HttpPost]
    public async Task<ActionResult> SaveOrmSummary([FromBody] OrmSummaryDto dto)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dto.Statement))
            {
                return BadRequest(new { success = false, message = "Statement cannot be empty" });
            }

            if (string.IsNullOrWhiteSpace(dto.Horizon))
            {
                return BadRequest(new { success = false, message = "Horizon is required" });
            }

            await using var conn = _factory.Create();
            await conn.OpenAsync();

            // Check if record exists
            var exists = await RecordExists(conn, dto.Horizon);

            if (exists)
            {
                await UpdateCommentORM(conn, dto.Statement, dto.Horizon);
            }
            else
            {
                await InsertCommentORM(conn, dto.Statement, dto.Horizon);
            }

            _logger.LogInformation("ORM comment saved successfully for horizon: {Horizon}", dto.Horizon);
            return Ok(new { success = true, message = "ORM Summary saved successfully" });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "Error saving ORM comment for horizon: {Horizon}", dto.Horizon);
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // DELETE api/orm-summary/{horizon}
    [HttpDelete("{horizon}")]
    public async Task<ActionResult> DeleteOrmSummary(string horizon)
    {
        try
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync();

            const string sql = "DELETE FROM cm_matrix_sb_email WHERE CM_MATRIX_SB_EMAIL_TYPE = 'ORM' AND cm_matrix_sb_email_horizon = :horizon";
            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            cmd.Parameters.Add("horizon", OracleDbType.Varchar2).Value = horizon;
            await cmd.ExecuteNonQueryAsync();

            return Ok(new { success = true, message = "ORM Summary deleted successfully" });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "Error deleting ORM summary for horizon: {Horizon}", horizon);
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    private async Task<bool> RecordExists(OracleConnection conn, string horizon)
    {
        const string sql = "SELECT COUNT(*) FROM cm_matrix_sb_email WHERE CM_MATRIX_SB_EMAIL_TYPE = 'ORM' AND cm_matrix_sb_email_horizon = :horizon";
        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add("horizon", OracleDbType.Varchar2).Value = horizon;
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        return count > 0;
    }

    private async Task<string> GetCommentORM(string horizon)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync();

        const string sql = "SELECT CM_MATRIX_SB_EMAIL_BODY FROM cm_matrix_sb_email WHERE CM_MATRIX_SB_EMAIL_TYPE = 'ORM' AND cm_matrix_sb_email_horizon = :horizon";
        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add("horizon", OracleDbType.Varchar2).Value = horizon;

        var result = await cmd.ExecuteScalarAsync();
        return result == null || result == DBNull.Value ? "" : result.ToString() ?? "";
    }

    private async Task<string?> GetPreviousHorizon(string currentHorizon)
    {
        try
        {
            await using var conn = _realisFactory.Create();
            await conn.OpenAsync();

            // Get all horizons ordered
            const string sql = "SELECT rhz_name FROM rfc_horizon ORDER BY rhz_name ASC";
            await using var cmd = new OracleCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            var allHorizons = new List<string>();
            while (await reader.ReadAsync())
            {
                if (!reader.IsDBNull(0))
                {
                    allHorizons.Add(reader.GetString(0));
                }
            }

            // Find the current horizon index
            int currentIndex = allHorizons.IndexOf(currentHorizon);

            // Return the previous horizon if it exists
            if (currentIndex > 0)
            {
                return allHorizons[currentIndex - 1];
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting previous horizon for: {CurrentHorizon}", currentHorizon);
            return null;
        }
    }

    private async Task UpdateCommentORM(OracleConnection conn, string statement, string horizon)
    {
        const string sql = @"
            UPDATE cm_matrix_sb_email
            SET CM_MATRIX_SB_EMAIL_BODY = :statement,
                CM_MATRIX_SB_EMAIL_LASTUPDATE = :lastUpdate
            WHERE CM_MATRIX_SB_EMAIL_TYPE = 'ORM'
              AND cm_matrix_sb_email_horizon = :horizon";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add("statement", OracleDbType.Clob).Value = statement;
        cmd.Parameters.Add("lastUpdate", OracleDbType.Date).Value = DateTime.Now;
        cmd.Parameters.Add("horizon", OracleDbType.Varchar2).Value = horizon;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertCommentORM(OracleConnection conn, string statement, string horizon)
    {
        const string sql = @"
            INSERT INTO cm_matrix_sb_email
            (CM_MATRIX_SB_EMAIL_BODY, CM_MATRIX_SB_EMAIL_TYPE, CM_MATRIX_SB_EMAIL_LASTUPDATE, cm_matrix_sb_email_horizon)
            VALUES (:statement, 'ORM', :lastUpdate, :horizon)";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add("statement", OracleDbType.Clob).Value = statement;
        cmd.Parameters.Add("lastUpdate", OracleDbType.Date).Value = DateTime.Now;
        cmd.Parameters.Add("horizon", OracleDbType.Varchar2).Value = horizon;
        await cmd.ExecuteNonQueryAsync();
    }
}

public class OrmSummaryDto
{
    [JsonPropertyName("horizon")]
    public string? Horizon { get; set; }

    [JsonPropertyName("statement")]
    public string? Statement { get; set; }
}
