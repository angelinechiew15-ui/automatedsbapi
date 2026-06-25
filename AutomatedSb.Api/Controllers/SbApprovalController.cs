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
                NVL(h.cm_matrix_sb_approval_horizon, 'N/A') AS horizon,
                s.cm_matrix_sb_name                         AS sb_name,
                TRUNC(s.cm_matrix_sb_date_created)          AS publish_date,
                s.cm_matrix_sb_customer_group               AS customer_group,
                s.cm_matrix_sb_customer_name                AS customer_name,
                NVL(a.cm_matrix_sb_approval_sb_status, 'NO_STATUS') AS approval_status,
                a.cm_matrix_sb_approval_reason              AS reason,
                a.cm_matrix_sb_approval_date                AS approval_date,
                s.cm_matrix_sb_status                       AS sb_status,
                s.cm_matrix_sb_date_release                 AS release_date,
                s.cm_matrix_sb_conditional_release          AS conditional_release
            FROM cm_matrix_sb s
            JOIN cm_matrix_person_to_sb ps
              ON ps.cm_matrix_person_to_sb_sb_id = s.cm_matrix_sb_id
             AND ps.cm_matrix_person_to_sb_owner = 'Y'
            JOIN cm_matrix_person p
              ON p.cm_matrix_person_id = ps.cm_matrix_person_to_sb_person_id
            LEFT JOIN (
                SELECT z.sb_id,
                       z.cm_matrix_sb_approval_sb_status,
                       z.cm_matrix_sb_approval_reason,
                       z.cm_matrix_sb_approval_date,
                       z.cm_matrix_sb_approval_horizon_id
                FROM (
                    SELECT
                        a.cm_matrix_sb_approval_sb_id     AS sb_id,
                        a.cm_matrix_sb_approval_sb_status,
                        a.cm_matrix_sb_approval_reason,
                        a.cm_matrix_sb_approval_date,
                        a.cm_matrix_sb_approval_horizon_id,
                        ROW_NUMBER() OVER (
                            PARTITION BY a.cm_matrix_sb_approval_sb_id
                            ORDER BY a.cm_matrix_sb_approval_id DESC
                        ) AS rn
                    FROM cm_matrix_sb_approval a
                    WHERE a.cm_matrix_sb_approval_horizon_id = (
                        SELECT MAX(cm_matrix_sb_approval_horizon_id)
                        FROM cm_matrix_sb_approval
                    )
                ) z
                WHERE z.rn = 1
            ) a ON a.sb_id = s.cm_matrix_sb_id
            LEFT JOIN cm_matrix_sb_approval_horizon h
              ON h.cm_matrix_sb_approval_horizon_id = (
                  SELECT MAX(cm_matrix_sb_approval_horizon_id) FROM cm_matrix_sb_approval
              )
            WHERE s.cm_matrix_sb_valid = 'Y'
              AND (:ownerId IS NULL OR p.cm_matrix_person_id = :ownerId)
              AND (:sbId   IS NULL OR s.cm_matrix_sb_id      = :sbId)
              AND (:status IS NULL OR NVL(a.cm_matrix_sb_approval_sb_status, 'NO_STATUS') = :status)
            ORDER BY p.cm_matrix_person_lastname  ASC,
                     p.cm_matrix_person_firstname ASC,
                     s.cm_matrix_sb_name          ASC";

        try
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync();
            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
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
                    horizon            = reader["horizon"]?.ToString()    ?? "N/A",
                    sbName             = reader["sb_name"]?.ToString()    ?? "",
                    publishDate        = reader["publish_date"]  is DBNull ? null : ((DateTime)reader["publish_date"]).ToString("yyyy-MM-dd"),
                    customerGroup      = reader["customer_group"]?.ToString() ?? "",
                    customerName       = reader["customer_name"]?.ToString()  ?? "",
                    approvalStatus     = reader["approval_status"]?.ToString() ?? "NO_STATUS",
                    reason             = reader["reason"]?.ToString()       ?? "",
                    approvalDate       = reader["approval_date"] is DBNull ? null : ((DateTime)reader["approval_date"]).ToString("yyyy-MM-dd"),
                    sbStatus           = reader["sb_status"]?.ToString()    ?? "",
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
