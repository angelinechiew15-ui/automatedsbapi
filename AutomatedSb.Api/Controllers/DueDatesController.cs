using AutomatedSb.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Oracle.ManagedDataAccess.Client;

namespace AutomatedSb.Api.Controllers;

[ApiController]
[Route("api/due-dates")]
public class DueDatesController : ControllerBase
{
    private readonly IOracleConnectionFactory _factory;
    private readonly ILogger<DueDatesController> _logger;

    public DueDatesController(IOracleConnectionFactory factory, ILogger<DueDatesController> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    // GET api/due-dates/{horizon}
    [HttpGet("{horizon}")]
    public async Task<ActionResult> GetDueDate([FromRoute] string horizon)
    {
        if (string.IsNullOrWhiteSpace(horizon)) return BadRequest(new { success = false, message = "horizon is required" });

        try
        {
            await using var conn = _factory.Create();
            await conn.OpenWithNlsAsync();
            const string sql = @"SELECT cm_matrix_sb_dd_duedate FROM cm_matrix_sb_duedate WHERE CM_MATRIX_SB_DD_HORIZON = :horizon";
            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            cmd.Parameters.Add(new OracleParameter("horizon", OracleDbType.Varchar2) { Value = horizon });
            var scalar = await cmd.ExecuteScalarAsync();
            if (scalar == null || scalar == DBNull.Value)
            {
                return Ok(new { dueDate = (string?)null });
            }
            // Return ISO 8601 string
            if (scalar is DateTime dt)
            {
                return Ok(new { dueDate = dt.ToString("yyyy-MM-dd") });
            }
            return Ok(new { dueDate = scalar.ToString() });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "GetDueDate failed for horizon={Horizon}", horizon);
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // POST api/due-dates
    // Body: { horizon: string, dueDate: string }
    [HttpPost]
    public async Task<ActionResult> SaveDueDate([FromBody] DueDateSaveRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Horizon)) return BadRequest(new { success = false, message = "horizon is required" });
        try
        {
            DateTime? dt = null;
            if (!string.IsNullOrWhiteSpace(request.DueDate))
            {
                if (!DateTime.TryParse(request.DueDate, out var parsed))
                {
                    return BadRequest(new { success = false, message = "Invalid dueDate format" });
                }
                dt = parsed.Date;
            }

            await using var conn = _factory.Create();
            await conn.OpenWithNlsAsync();

            const string existsSql = @"SELECT 1 FROM cm_matrix_sb_duedate WHERE CM_MATRIX_SB_DD_HORIZON = :horizon";
            await using var existsCmd = new OracleCommand(existsSql, conn) { BindByName = true };
            existsCmd.Parameters.Add(new OracleParameter("horizon", OracleDbType.Varchar2) { Value = request.Horizon });
            var exists = await existsCmd.ExecuteScalarAsync();

            if (exists is null || exists == DBNull.Value)
            {
                const string insertSql = @"
                    INSERT INTO cm_matrix_sb_duedate (CM_MATRIX_SB_DD_HORIZON, cm_matrix_sb_dd_duedate, cm_matrix_sb_duedate_lastupdate)
                    VALUES (:horizon, :duedate, :lastupdate)";
                await using var ins = new OracleCommand(insertSql, conn) { BindByName = true };
                ins.Parameters.Add(new OracleParameter("horizon", OracleDbType.Varchar2) { Value = request.Horizon });
                ins.Parameters.Add(new OracleParameter("duedate", OracleDbType.TimeStamp) { Value = (object?)dt ?? DBNull.Value });
                ins.Parameters.Add(new OracleParameter("lastupdate", OracleDbType.TimeStamp) { Value = DateTime.Now });
                await ins.ExecuteNonQueryAsync();
            }
            else
            {
                const string updateSql = @"
                    UPDATE cm_matrix_sb_duedate SET cm_matrix_sb_dd_duedate = :duedate, cm_matrix_sb_duedate_lastupdate = :lastupdate
                     WHERE CM_MATRIX_SB_DD_HORIZON = :horizon";
                await using var upd = new OracleCommand(updateSql, conn) { BindByName = true };
                upd.Parameters.Add(new OracleParameter("duedate", OracleDbType.TimeStamp) { Value = (object?)dt ?? DBNull.Value });
                upd.Parameters.Add(new OracleParameter("lastupdate", OracleDbType.TimeStamp) { Value = DateTime.Now });
                upd.Parameters.Add(new OracleParameter("horizon", OracleDbType.Varchar2) { Value = request.Horizon });
                await upd.ExecuteNonQueryAsync();
            }

            return Ok(new { success = true });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "SaveDueDate failed for horizon={Horizon}", request.Horizon);
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // DELETE api/due-dates/{horizon}
    [HttpDelete("{horizon}")]
    public async Task<ActionResult> DeleteDueDate([FromRoute] string horizon)
    {
        if (string.IsNullOrWhiteSpace(horizon)) return BadRequest(new { success = false, message = "horizon is required" });
        try
        {
            await using var conn = _factory.Create();
            await conn.OpenWithNlsAsync();
            const string deleteSql = @"DELETE FROM cm_matrix_sb_duedate WHERE CM_MATRIX_SB_DD_HORIZON = :horizon";
            await using var cmd = new OracleCommand(deleteSql, conn) { BindByName = true };
            cmd.Parameters.Add(new OracleParameter("horizon", OracleDbType.Varchar2) { Value = horizon });
            await cmd.ExecuteNonQueryAsync();
            return Ok(new { success = true });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "DeleteDueDate failed for horizon={Horizon}", horizon);
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    public sealed record DueDateSaveRequest(string Horizon, string? DueDate);
}
