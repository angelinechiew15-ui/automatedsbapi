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

    // GET api/sb-approval/overview?horizon=26-06&ownerId=...&sbId=...&status=...
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
                :horizon                                                          AS horizon,
                s.cm_matrix_sb_name                                               AS sb_name,
                p.cm_matrix_person_lastname || ' ' || p.cm_matrix_person_firstname AS owner_name,
                a.cm_matrix_sb_approval_customer_group_id                         AS customer_group,
                a.cm_matrix_sb_approval_customer_name                             AS customer_name,
                NVL(a.cm_matrix_sb_approval_sb_status, 'NO_STATUS')              AS approval_status
            FROM cm_matrix_sb s
            JOIN cm_matrix_person_to_sb ps
              ON ps.cm_matrix_person_to_sb_sb_id = s.cm_matrix_sb_id
             AND ps.cm_matrix_person_to_sb_owner = 'Y'
            JOIN cm_matrix_person p
              ON p.cm_matrix_person_id = ps.cm_matrix_person_to_sb_person_id
            LEFT JOIN cm_matrix_sb_approval a
              ON a.cm_matrix_sb_approval_sb_id = s.cm_matrix_sb_id
             AND a.cm_matrix_sb_approval_horizon_id = (
                 SELECT MAX(cm_matrix_sb_approval_horizon_id) FROM cm_matrix_sb_approval
             )
            WHERE s.cm_matrix_sb_valid = 'Y'
              AND (:ownerId IS NULL OR p.cm_matrix_person_id = :ownerId)
              AND (:sbId   IS NULL OR s.cm_matrix_sb_id      = :sbId)
              AND (:status IS NULL OR NVL(a.cm_matrix_sb_approval_sb_status, 'NO_STATUS') = :status)
            ORDER BY p.cm_matrix_person_lastname              ASC,
                     p.cm_matrix_person_firstname             ASC,
                     s.cm_matrix_sb_name                      ASC,
                     a.cm_matrix_sb_approval_customer_group_id ASC,
                     a.cm_matrix_sb_approval_customer_name    ASC";

        try
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync();
            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            cmd.Parameters.Add(new OracleParameter("horizon", OracleDbType.Varchar2)
                { Value = horizon });
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
                    horizon        = reader["horizon"]?.ToString()        ?? "",
                    sbName         = reader["sb_name"]?.ToString()        ?? "",
                    ownerName      = reader["owner_name"]?.ToString()     ?? "",
                    customerGroup  = reader["customer_group"]?.ToString() ?? "",
                    customerName   = reader["customer_name"]?.ToString()  ?? "",
                    approvalStatus = reader["approval_status"]?.ToString() ?? "NO_STATUS",
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
