using AutomatedSb.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Oracle.ManagedDataAccess.Client;

namespace AutomatedSb.Api.Controllers;

[ApiController]
[Route("api/sb-approval")]
public class SbApprovalController : ControllerBase
{
    private readonly IOracleConnectionFactory _factory;
    private readonly ILogger<SbApprovalController> _logger;

    public SbApprovalController(
        IOracleConnectionFactory factory,
        ILogger<SbApprovalController> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    // GET api/sb-approval/overview?horizon=123&ownerId=...&sbId=...&status=...
    [HttpGet("overview")]
    public async Task<ActionResult> GetOverview(
        [FromQuery] string? horizon,
        [FromQuery] string? ownerId,
        [FromQuery] string? sbId,
        [FromQuery] string? status)
    {
        if (string.IsNullOrWhiteSpace(horizon))
            return BadRequest(new { success = false, message = "horizon is required" });

        const string sql = @"
            SELECT
                :horizonDisplay                                    AS horizon,
                gg.cm_matrix_sb_name                               AS sb_name,
                i.cm_matrix_sb_approval_last_update                AS publish_date,
                i.cm_matrix_sb_approval_customer_group             AS customer_group,
                i.cm_matrix_sb_approval_customer_name              AS customer_name,
                i.cm_matrix_sb_approval_status                     AS approval_status,
                i.cm_matrix_sb_approval_reason                     AS reason,
                i.cm_matrix_sb_approval_approval_date              AS approval_date,
                i.cm_matrix_sb_approval_sb_status                  AS sb_status,
                i.cm_matrix_sb_approval_release_date               AS release_date,
                i.cm_matrix_sb_approval_cr_remark                  AS conditional_release
            FROM cm_matrix_sb_approval i
            JOIN cm_matrix_sb gg
              ON gg.cm_matrix_sb_id = i.cm_matrix_sb_approval_sb_id
            JOIN cm_matrix_person_to_sb k
              ON k.cm_matrix_person_to_sb_sb_id = gg.cm_matrix_sb_id
            WHERE (:horizonId IS NULL OR i.cm_matrix_sb_approval_horizon_id = :horizonId)
              AND (:ownerId IS NULL OR k.cm_matrix_person_to_sb_person_id = :ownerId)
              AND (:sbId   IS NULL OR gg.cm_matrix_sb_id = :sbId)
              AND (:status IS NULL OR i.cm_matrix_sb_approval_status = :status)
            ORDER BY i.cm_matrix_sb_approval_sb_id,
                     i.cm_matrix_sb_approval_customer_group,
                     i.cm_matrix_sb_approval_customer_name,
                     i.cm_matrix_sb_approval_id ASC";

        try
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync();
            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            
            // horizon parameter is now the horizon ID (numeric)
            int horizonId = int.TryParse(horizon, out int hid) ? hid : 0;
            cmd.Parameters.Add(new OracleParameter("horizonId", OracleDbType.Int32)
                { Value = horizonId > 0 ? (object)horizonId : DBNull.Value });
            cmd.Parameters.Add(new OracleParameter("horizonDisplay", OracleDbType.Varchar2)
                { Value = horizon ?? "" });
            cmd.Parameters.Add(new OracleParameter("ownerId", OracleDbType.Varchar2)
                { Value = string.IsNullOrWhiteSpace(ownerId) ? DBNull.Value : (object)ownerId });
            cmd.Parameters.Add(new OracleParameter("sbId", OracleDbType.Varchar2)
                { Value = string.IsNullOrWhiteSpace(sbId)    ? DBNull.Value : (object)sbId });
            cmd.Parameters.Add(new OracleParameter("status", OracleDbType.Varchar2)
                { Value = string.IsNullOrWhiteSpace(status)  ? DBNull.Value : (object)status });

            await using var reader = await cmd.ExecuteReaderAsync();
            var rows = new List<object>();
            while (await reader.ReadAsync())
            {
                rows.Add(new
                {
                    horizon            = reader["horizon"]?.ToString()            ?? "N/A",
                    sbName             = reader["sb_name"]?.ToString()             ?? "",
                    publishDate        = reader["publish_date"] is DBNull ? null : ((DateTime)reader["publish_date"]).ToString("yyyy-MM-dd"),
                    customerGroup      = reader["customer_group"]?.ToString()      ?? "",
                    customerName       = reader["customer_name"]?.ToString()       ?? "",
                    approvalStatus     = reader["approval_status"]?.ToString()     ?? "NO_STATUS",
                    reason             = reader["reason"]?.ToString()             ?? "",
                    approvalDate       = reader["approval_date"] is DBNull ? null : ((DateTime)reader["approval_date"]).ToString("yyyy-MM-dd"),
                    sbStatus           = reader["sb_status"]?.ToString()           ?? "",
                    releaseDate        = reader["release_date"] is DBNull ? null : ((DateTime)reader["release_date"]).ToString("yyyy-MM-dd"),
                    conditionalRelease = reader["conditional_release"]?.ToString() ?? "",
                });
            }

            return Ok(rows);
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "SbApproval overview failed for horizon={Horizon}", horizon);
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }
}
