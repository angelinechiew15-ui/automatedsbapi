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

    private static string NumExpr(string expr) =>
        $"TO_NUMBER({expr} DEFAULT 0 ON CONVERSION ERROR)";

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
            await conn.OpenWithNlsAsync();
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
        var sql = $@"
            SELECT
                d.cm_matrix_sb_div_name                           AS div_name,
                d.cm_matrix_sb_div_bl                             AS sub_div,
                s.cm_matrix_sb_name                               AS sb_name,
                hh.cm_matrix_sb_approval_sb_status                AS sb_status,
                ss.cm_matrix_sb_ws_sb_comment                     AS sb_comment,
                sss.cm_matrix_sb_ws_sb_comment                    AS div_summary,
                m.fy                                               AS fy,
                m.ts_demand                                        AS ts_demand,
                m.rtu_demand                                       AS rtu_demand,
                m.cost_demand                                      AS cost_demand
            FROM
                cm_matrix_sb s
                INNER JOIN cm_matrix_sb_div d
                    ON s.cm_matrix_sb_div_name = d.cm_matrix_sb_div_id
                LEFT JOIN (
                    SELECT cm_matrix_sb_approval_sb_id, cm_matrix_sb_approval_sb_status
                    FROM cm_matrix_sb_approval
                ) hh ON hh.cm_matrix_sb_approval_sb_id = s.cm_matrix_sb_id
                LEFT JOIN cm_matrix_sb_ws_sum ss
                    ON ss.cm_matrix_sb_ws_sb_id = s.cm_matrix_sb_id
                   AND (ss.cm_matrix_sb_ws_sb_by_div IS NULL OR ss.cm_matrix_sb_ws_sb_by_div != 'Y')
                LEFT JOIN cm_matrix_sb_ws_sum sss
                    ON sss.cm_matrix_sb_ws_sb_div = d.cm_matrix_sb_div_id
                   AND sss.cm_matrix_sb_ws_sb_by_div = 'Y'
                LEFT JOIN (
                    SELECT
                        t.sb,
                        t.fy,
                        CAST(SUM({NumExpr("t.ts_demand")}) AS BINARY_DOUBLE) AS ts_demand,
                        CAST(SUM((( {NumExpr("t.ts_demand")} + NVL({NumExpr("a_ts.cm_matrix_adder_value")}, 0))
                                  * 3 * {NumExpr(@"t.""RTU/TS""")})
                                 + NVL({NumExpr("a_rtu.cm_matrix_adder_value")}, 0)) AS BINARY_DOUBLE) AS rtu_demand,
                        CAST(SUM((((( {NumExpr("t.ts_demand")} + NVL({NumExpr("a_ts.cm_matrix_adder_value")}, 0))
                                   * 3 * {NumExpr(@"t.""RTU/TS""")})
                                  + NVL({NumExpr("a_rtu.cm_matrix_adder_value")}, 0))
                                 * {NumExpr(@"t.""COST/RTU""")} / 1000)
                                 + {NumExpr("t.depreciation")}
                                 + NVL({NumExpr("a_cost.cm_matrix_adder_value")}, 0)) AS BINARY_DOUBLE) AS cost_demand
                    FROM rpt.asb_ts_actual t
                    LEFT JOIN rpt.cm_matrix_sb_adder a_ts
                      ON t.loc = a_ts.cm_matrix_adder_location
                     AND t.sb  = a_ts.cm_matrix_adder_sb_name
                     AND t.fy || '-' || t.quarter = a_ts.cm_matrix_adder_fy || '-' || a_ts.cm_matrix_adder_quarter
                     AND t.horizon = a_ts.cm_matrix_adder_horizon
                     AND a_ts.cm_matrix_adder_type = 'Adder'
                     AND a_ts.cm_matrix_adder_for  = 'TS'
                    LEFT JOIN rpt.cm_matrix_sb_adder a_rtu
                      ON t.loc = a_rtu.cm_matrix_adder_location
                     AND t.sb  = a_rtu.cm_matrix_adder_sb_name
                     AND t.fy || '-' || t.quarter = a_rtu.cm_matrix_adder_fy || '-' || a_rtu.cm_matrix_adder_quarter
                     AND t.horizon = a_rtu.cm_matrix_adder_horizon
                     AND a_rtu.cm_matrix_adder_type = 'Adder'
                     AND a_rtu.cm_matrix_adder_for  = 'RTU'
                    LEFT JOIN rpt.cm_matrix_sb_adder a_cost
                      ON t.loc = a_cost.cm_matrix_adder_location
                     AND t.sb  = a_cost.cm_matrix_adder_sb_name
                     AND t.fy || '-' || t.quarter = a_cost.cm_matrix_adder_fy || '-' || a_cost.cm_matrix_adder_quarter
                     AND t.horizon = a_cost.cm_matrix_adder_horizon
                     AND a_cost.cm_matrix_adder_type = 'Adder'
                     AND a_cost.cm_matrix_adder_for  = 'COST'
                    WHERE t.horizon = :horizon
                    GROUP BY t.sb, t.fy
                ) m
                    ON m.sb = s.cm_matrix_sb_name
            WHERE
                s.cm_matrix_sb_valid = 'Y'
            GROUP BY
                d.cm_matrix_sb_div_name,
                d.cm_matrix_sb_div_bl,
                s.cm_matrix_sb_name,
                hh.cm_matrix_sb_approval_sb_status,
                ss.cm_matrix_sb_ws_sb_comment,
                sss.cm_matrix_sb_ws_sb_comment,
                m.fy,
                m.ts_demand,
                m.rtu_demand,
                m.cost_demand
            ORDER BY
                d.cm_matrix_sb_div_name ASC,
                s.cm_matrix_sb_name ASC,
                m.fy ASC";

        try
        {
            await using var conn = _factory.Create();
            await conn.OpenWithNlsAsync();
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
                    fy         = reader["fy"]?.ToString()          ?? "",
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

    // GET api/workshop-summary/filter-options — all (divName, sb) pairs for preloading filters
    [HttpGet("filter-options")]
    public async Task<ActionResult> GetFilterOptions()
    {
        const string sql = @"
            SELECT d.cm_matrix_sb_div_name AS div_name,
                   s.cm_matrix_sb_name     AS sb_name
              FROM cm_matrix_sb s
              INNER JOIN cm_matrix_sb_div d
                  ON s.cm_matrix_sb_div_name = d.cm_matrix_sb_div_id
             WHERE s.cm_matrix_sb_valid = 'Y'
             ORDER BY d.cm_matrix_sb_div_name ASC, s.cm_matrix_sb_name ASC";
        try
        {
            await using var conn = _factory.Create();
            await conn.OpenWithNlsAsync();
            await using var cmd = new OracleCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            var result = new List<object>();
            while (await reader.ReadAsync())
                result.Add(new { div = reader["div_name"]?.ToString() ?? "", sb = reader["sb_name"]?.ToString() ?? "" });
            return Ok(result);
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "Workshop GetFilterOptions failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // GET api/workshop-summary/sb-options
    [HttpGet("sb-options")]
    public async Task<ActionResult> GetSbOptions()
    {
        const string sql = @"
            SELECT cm_matrix_sb_id   AS id,
                   cm_matrix_sb_name AS name
              FROM cm_matrix_sb
             WHERE cm_matrix_sb_valid = 'Y'
             ORDER BY cm_matrix_sb_name ASC";
        try
        {
            await using var conn = _factory.Create();
            await conn.OpenWithNlsAsync();
            await using var cmd = new OracleCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            var result = new List<object>();
            while (await reader.ReadAsync())
                result.Add(new { id = reader["id"]?.ToString() ?? "", name = reader["name"]?.ToString() ?? "" });
            return Ok(result);
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "Workshop GetSbOptions failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // GET api/workshop-summary/div-options
    [HttpGet("div-options")]
    public async Task<ActionResult> GetDivOptions()
    {
        const string sql = @"
            SELECT cm_matrix_sb_div_id AS id,
                   cm_matrix_sb_div_name || ' - ' || cm_matrix_sb_div_bl AS name
              FROM cm_matrix_sb_div
             ORDER BY cm_matrix_sb_div_name ASC";
        try
        {
            await using var conn = _factory.Create();
            await conn.OpenWithNlsAsync();
            await using var cmd = new OracleCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            var result = new List<object>();
            while (await reader.ReadAsync())
                result.Add(new { id = reader["id"]?.ToString() ?? "", name = reader["name"]?.ToString() ?? "" });
            return Ok(result);
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "Workshop GetDivOptions failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // GET api/workshop-summary/comment?id=...&type=N|Y
    [HttpGet("comment")]
    public async Task<ActionResult> GetComment([FromQuery] string? id, [FromQuery] string? type)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(type))
            return BadRequest(new { success = false, message = "id and type are required" });

        bool isDiv = type.ToUpper() == "Y";
        string sql = isDiv
            ? @"SELECT cm_matrix_sb_ws_sb_comment FROM cm_matrix_sb_ws_sum
                WHERE TO_NUMBER(cm_matrix_sb_ws_sb_div) = TO_NUMBER(:id)
                  AND cm_matrix_sb_ws_sb_by_div = 'Y' AND ROWNUM = 1"
            : @"SELECT cm_matrix_sb_ws_sb_comment FROM cm_matrix_sb_ws_sum
                WHERE TO_NUMBER(cm_matrix_sb_ws_sb_id) = TO_NUMBER(:id)
                  AND (cm_matrix_sb_ws_sb_by_div IS NULL OR cm_matrix_sb_ws_sb_by_div != 'Y')
                  AND ROWNUM = 1";
        try
        {
            await using var conn = _factory.Create();
            await conn.OpenWithNlsAsync();
            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            cmd.Parameters.Add(new OracleParameter("id", OracleDbType.Varchar2) { Value = id });
            var result = await cmd.ExecuteScalarAsync();
            return Ok(new { comment = result == DBNull.Value || result == null ? "" : result.ToString() });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "Workshop GetComment failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // POST api/workshop-summary/comment
    [HttpPost("comment")]
    public async Task<ActionResult> SaveComment([FromBody] WorkshopCommentRequest req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.Id))
            return BadRequest(new { success = false, message = "id is required" });

        bool isDiv = (req.Type ?? "").ToUpper() == "Y";
        string checkSql = isDiv
            ? @"SELECT COUNT(*) FROM cm_matrix_sb_ws_sum
                WHERE TO_NUMBER(cm_matrix_sb_ws_sb_div) = TO_NUMBER(:id)
                  AND cm_matrix_sb_ws_sb_by_div = 'Y'"
            : @"SELECT COUNT(*) FROM cm_matrix_sb_ws_sum
                WHERE TO_NUMBER(cm_matrix_sb_ws_sb_id) = TO_NUMBER(:id)
                  AND (cm_matrix_sb_ws_sb_by_div IS NULL OR cm_matrix_sb_ws_sb_by_div != 'Y')";
        try
        {
            await using var conn = _factory.Create();
            await conn.OpenWithNlsAsync();

            await using var checkCmd = new OracleCommand(checkSql, conn) { BindByName = true };
            checkCmd.Parameters.Add(new OracleParameter("id", OracleDbType.Varchar2) { Value = req.Id });
            var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());

            if (count > 0)
            {
                string updateSql = isDiv
                    ? @"UPDATE cm_matrix_sb_ws_sum
                           SET cm_matrix_sb_ws_sb_comment = :p_comment, cm_matrix_sb_ws_last_update = SYSDATE
                         WHERE TO_NUMBER(cm_matrix_sb_ws_sb_div) = TO_NUMBER(:id)
                           AND cm_matrix_sb_ws_sb_by_div = 'Y'"
                    : @"UPDATE cm_matrix_sb_ws_sum
                           SET cm_matrix_sb_ws_sb_comment = :p_comment, cm_matrix_sb_ws_last_update = SYSDATE
                         WHERE TO_NUMBER(cm_matrix_sb_ws_sb_id) = TO_NUMBER(:id)
                           AND (cm_matrix_sb_ws_sb_by_div IS NULL OR cm_matrix_sb_ws_sb_by_div != 'Y')";
                await using var upd = new OracleCommand(updateSql, conn) { BindByName = true };
                upd.Parameters.Add(new OracleParameter("p_comment", OracleDbType.Varchar2) { Value = req.Comment ?? "" });
                upd.Parameters.Add(new OracleParameter("id", OracleDbType.Varchar2) { Value = req.Id });
                await upd.ExecuteNonQueryAsync();
            }
            else
            {
                const string horizonSql = "SELECT MAX(cm_matrix_sb_approval_horizon_id) FROM cm_matrix_sb_approval";
                await using var hCmd = new OracleCommand(horizonSql, conn);
                var hVal = await hCmd.ExecuteScalarAsync();
                string horizon = hVal == DBNull.Value || hVal == null ? "" : hVal.ToString()!;

                string insertSql = isDiv
                    ? @"INSERT INTO cm_matrix_sb_ws_sum
                            (cm_matrix_sb_ws_sb_comment, cm_matrix_sb_ws_sb_horizon,
                             cm_matrix_sb_ws_sb_div, cm_matrix_sb_ws_sb_by_div, cm_matrix_sb_ws_last_update)
                        VALUES (:p_comment, :horizon, TO_NUMBER(:id), 'Y', SYSDATE)"
                    : @"INSERT INTO cm_matrix_sb_ws_sum
                            (cm_matrix_sb_ws_sb_id, cm_matrix_sb_ws_sb_comment,
                             cm_matrix_sb_ws_sb_horizon, cm_matrix_sb_ws_sb_by_div, cm_matrix_sb_ws_last_update)
                        VALUES (TO_NUMBER(:id), :p_comment, :horizon, NULL, SYSDATE)";
                await using var ins = new OracleCommand(insertSql, conn) { BindByName = true };
                ins.Parameters.Add(new OracleParameter("p_comment", OracleDbType.Varchar2) { Value = req.Comment ?? "" });
                ins.Parameters.Add(new OracleParameter("id", OracleDbType.Varchar2) { Value = req.Id });
                ins.Parameters.Add(new OracleParameter("horizon", OracleDbType.Varchar2) { Value = horizon });
                await ins.ExecuteNonQueryAsync();
            }

            return Ok(new { success = true });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "Workshop SaveComment failed id={Id} type={Type}", req.Id, req.Type);
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }
}

public record WorkshopCommentRequest(string Id, string Type, string? Comment);
