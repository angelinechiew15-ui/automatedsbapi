using AutomatedSb.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Oracle.ManagedDataAccess.Client;

namespace AutomatedSb.Api.Controllers;

[ApiController]
[Route("api/workshop-summary")]
public class WorkshopSummaryController : ControllerBase
{
    private readonly IOracleConnectionFactory _factory;
    private readonly ILogger<WorkshopSummaryController> _logger;

    public WorkshopSummaryController(
        IOracleConnectionFactory factory,
        ILogger<WorkshopSummaryController> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    // GET api/workshop-summary
    [HttpGet]
    public async Task<ActionResult> GetAll()
    {
        // Keep to known-safe columns from v_sb_asb_data.
        const string sql = @"
            SELECT fy, loc, sb, RTU_TS
              FROM v_sb_asb_data
             WHERE sb IS NOT NULL
             ORDER BY fy DESC, sb ASC";

        try
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync();
            await using var cmd = new OracleCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            var seen = new HashSet<string>();
            var result = new List<object>();

            while (await reader.ReadAsync())
            {
                var fy = reader["fy"]?.ToString() ?? "";
                var loc = reader["loc"]?.ToString() ?? "";
                var sb = reader["sb"]?.ToString() ?? "";
                var key = $"{fy}|{loc}|{sb}";
                if (!seen.Add(key))
                {
                    continue;
                }

                var rtutsRaw = reader["RTU_TS"]?.ToString()?.Replace(',', '.') ?? "";
                var rtuts = "";
                if (!string.IsNullOrWhiteSpace(rtutsRaw)
                    && decimal.TryParse(
                        rtutsRaw,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var parsed))
                {
                    rtuts = Math.Round(parsed, 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                result.Add(new
                {
                    fy,
                    loc,
                    sb,
                    rtuts,
                });
            }

            return Ok(result);
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "Workshop summary GET failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // GET api/workshop-summary/sb-status?horizon=26-06
    // Returns one row per SB with Div, Sub-Div, SB, Status, Comment, Demand+Cost drivers
    // plus TSpM / RTU / Cost demand values joined from v_sb_asb_data for the given horizon.
    [HttpGet("sb-status")]
    public async Task<ActionResult> GetSbStatus([FromQuery] string? horizon)
    {
        if (string.IsNullOrWhiteSpace(horizon))
            return BadRequest(new { success = false, message = "horizon is required" });

        // Main query: approval status + comments from cm_matrix_sb_ws_sum
        // Then aggregate demand values from v_sb_asb_data for the same horizon.
        const string sql = @"
            SELECT
                d.cm_matrix_sb_div_name                           AS div_name,
                d.cm_matrix_sb_div_bl                             AS sub_div,
                s.cm_matrix_sb_name                               AS sb_name,
                hh.cm_matrix_sb_approval_sb_status                AS sb_status,
                ss.cm_matrix_sb_ws_sb_comment                     AS sb_comment,
                sss.cm_matrix_sb_ws_sb_comment                    AS div_summary,
                CAST(SUM(NVL(TO_NUMBER(v.TS_DEMAND DEFAULT NULL ON CONVERSION ERROR), 0)) AS BINARY_DOUBLE) AS ts_demand,
                CAST(SUM(NVL(TO_NUMBER(v.TS_DEMAND DEFAULT NULL ON CONVERSION ERROR), 0)
                         * NVL(v.rtu_ts, 0) * 3) AS BINARY_DOUBLE) AS rtu_demand,
                CAST(SUM(NVL(TO_NUMBER(v.TS_DEMAND DEFAULT NULL ON CONVERSION ERROR), 0)
                         * NVL(v.rtu_ts, 0) * 3
                         * NVL(TO_NUMBER(v.""COST/RTU"" DEFAULT NULL ON CONVERSION ERROR), 0) / 1000
                         + NVL(TO_NUMBER(v.DEPRECIATION DEFAULT NULL ON CONVERSION ERROR), 0)) AS BINARY_DOUBLE) AS cost_demand
            FROM
                cm_matrix_sb s
                INNER JOIN cm_matrix_sb_div d
                    ON s.cm_matrix_sb_div_name = d.cm_matrix_sb_div_id
                LEFT JOIN (
                    SELECT cm_matrix_sb_approval_sb_id, cm_matrix_sb_approval_sb_status
                    FROM cm_matrix_sb_approval
                    WHERE cm_matrix_sb_approval_horizon_id = (
                        SELECT MAX(cm_matrix_sb_approval_horizon_id) FROM cm_matrix_sb_approval
                    )
                ) hh ON hh.cm_matrix_sb_approval_sb_id = s.cm_matrix_sb_id
                LEFT JOIN cm_matrix_sb_ws_sum ss
                    ON ss.cm_matrix_sb_ws_sb_id = s.cm_matrix_sb_id
                   AND (ss.cm_matrix_sb_ws_sb_by_div IS NULL OR ss.cm_matrix_sb_ws_sb_by_div != 'Y')
                LEFT JOIN cm_matrix_sb_ws_sum sss
                    ON sss.cm_matrix_sb_ws_sb_div = d.cm_matrix_sb_div_id
                   AND sss.cm_matrix_sb_ws_sb_by_div = 'Y'
                LEFT JOIN v_sb_asb_data v
                    ON v.sb = s.cm_matrix_sb_name
                   AND v.horizon = :horizon
            WHERE
                s.cm_matrix_sb_valid = 'Y'
            GROUP BY
                d.cm_matrix_sb_div_name,
                d.cm_matrix_sb_div_bl,
                s.cm_matrix_sb_name,
                hh.cm_matrix_sb_approval_sb_status,
                ss.cm_matrix_sb_ws_sb_comment,
                sss.cm_matrix_sb_ws_sb_comment
            ORDER BY
                d.cm_matrix_sb_div_name ASC,
                s.cm_matrix_sb_name ASC";

        try
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync();
            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            cmd.Parameters.Add(new OracleParameter("horizon", OracleDbType.Varchar2) { Value = horizon });

            await using var reader = await cmd.ExecuteReaderAsync();

            static double? Dbl(object raw) =>
                raw == DBNull.Value || raw == null ? null : Convert.ToDouble(raw);

            var result = new List<object>();
            while (await reader.ReadAsync())
            {
                result.Add(new
                {
                    divName    = reader["div_name"]?.ToString()    ?? "",
                    subDiv     = reader["sub_div"]?.ToString()     ?? "",
                    sb         = reader["sb_name"]?.ToString()     ?? "",
                    sbStatus   = reader["sb_status"]?.ToString()   ?? "",
                    comment    = reader["sb_comment"]?.ToString()  ?? "",
                    summary    = reader["div_summary"]?.ToString() ?? "",
                    tsDemand   = Dbl(reader["ts_demand"]),
                    rtuDemand  = Dbl(reader["rtu_demand"]),
                    costDemand = Dbl(reader["cost_demand"]),
                });
            }

            return Ok(result);
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "WorkshopSummary.GetSbStatus failed for horizon {Horizon}", horizon);
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }
}
