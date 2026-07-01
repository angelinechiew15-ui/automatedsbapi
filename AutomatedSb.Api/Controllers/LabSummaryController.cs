using AutomatedSb.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Oracle.ManagedDataAccess.Client;

namespace AutomatedSb.Api.Controllers;

/// <summary>
/// Provides lab summary data from rpt.asb_ts_actual for the Lab Summary tab.
/// One row per (fy_quarter, location, horizon, sb) with all TS/RTU/Cost measures.
/// </summary>
[ApiController]
[Route("api/lab-summary")]
public class LabSummaryController : ControllerBase
{
    private readonly IOracleConnectionFactory _factory;
    private readonly ILogger<LabSummaryController> _logger;

    public LabSummaryController(
        IOracleConnectionFactory factory,
        ILogger<LabSummaryController> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    private static string NumExpr(string expr) =>
        $"TO_NUMBER({expr} DEFAULT 0 ON CONVERSION ERROR)";

    // GET api/lab-summary?horizon=26-06
    // Returns one row per (fy_quarter, location, horizon, sb) with all TS/RTU/Cost columns.
    [HttpGet]
    public async Task<ActionResult> Get([FromQuery] string? horizon)
    {
        if (string.IsNullOrWhiteSpace(horizon))
            return BadRequest(new { success = false, message = "horizon is required" });

        // Adder JOIN helper — reused 5 times for TS Adder, TS Change, RTU Adder, RTU Change, COST Adder
        static string AdderJoin(string baseAlias, string alias, string type, string forMeasure) => $@"
              LEFT JOIN rpt.cm_matrix_sb_adder {alias}
            ON {baseAlias}.loc     = {alias}.cm_matrix_adder_location
               AND {baseAlias}.sb      = {alias}.cm_matrix_adder_sb_name
               AND {baseAlias}.fy || '-' || {baseAlias}.quarter = {alias}.cm_matrix_adder_fy || '-' || {alias}.cm_matrix_adder_quarter
               AND {baseAlias}.horizon = {alias}.cm_matrix_adder_horizon
               AND {alias}.cm_matrix_adder_type = '{type}'
               AND {alias}.cm_matrix_adder_for  = '{forMeasure}'";

        var sql = $@"
                        WITH base AS (
                                SELECT CASE WHEN t.quarter IS NULL THEN t.fy
                                                        ELSE t.fy || ' ' || t.quarter END AS fy_quarter,
                                             t.loc AS location,
                                             t.horizon,
                                             t.sb,
                                             CAST(CASE
                                                                 WHEN cm_change.cm_matrix_change_value IS NOT NULL
                                                                     THEN TO_NUMBER(cm_change.cm_matrix_change_value DEFAULT 0 ON CONVERSION ERROR)
                                                       ELSE {NumExpr(@"t.""RTU/TS""")}
                                                        END AS BINARY_DOUBLE) AS rtu_ts_value,
                                             t.ts_demand,
                                             t.ts_actual,
                                             t.rtu_act,
                                             t.depreciation,
                                             t.cost_act,
                                                 t.""COST/RTU"" AS cost_rtu
                                    FROM rpt.asb_ts_actual t
                                    LEFT JOIN rpt.cm_matrix_sb_change_mappedvalue cm_change
                                        ON t.sb = cm_change.cm_matrix_change_sb_name
                                     AND t.loc = cm_change.cm_matrix_change_location
                                     AND t.horizon = cm_change.cm_matrix_change_horizon
                                     AND t.fy = cm_change.cm_matrix_change_fy
                                    WHERE t.horizon = :horizon
                                        AND t.loc IS NOT NULL
                                        AND t.sb  IS NOT NULL
                        )
                        SELECT base.fy_quarter,
                                     base.location,
                                     base.horizon,
                                     base.sb,
                             CAST(SUM({NumExpr("base.ts_demand")})                                                                         AS BINARY_DOUBLE) AS ts_demand,
                             CAST(SUM(NVL({NumExpr("a_ts.cm_matrix_adder_value")}, 0))                                                     AS BINARY_DOUBLE) AS adder_ts,
                             CAST(SUM({NumExpr("base.ts_actual")})                                                                         AS BINARY_DOUBLE) AS ts_actual,
                             CAST(SUM(NVL({NumExpr("c_ts.cm_matrix_adder_value")}, 0))                                                     AS BINARY_DOUBLE) AS change_ts,
                             CAST(((SUM({NumExpr("base.ts_demand")})
                                     + SUM(NVL({NumExpr("a_ts.cm_matrix_adder_value")}, 0)))
                                    * SUM(base.rtu_ts_value) * 3)                                                                               AS BINARY_DOUBLE) AS rtu_rfc_demand,
                             CAST(SUM(NVL({NumExpr("a_rtu.cm_matrix_adder_value")}, 0))                                                    AS BINARY_DOUBLE) AS adder_rtu,
                             CAST(SUM(base.rtu_ts_value)                                                                                    AS BINARY_DOUBLE) AS rtu_ts,
                             CAST(SUM({NumExpr("base.rtu_act")})                                                                           AS BINARY_DOUBLE) AS rtu_actual,
                             CAST(SUM(NVL({NumExpr("c_rtu.cm_matrix_adder_value")}, 0))                                                    AS BINARY_DOUBLE) AS change_rtu,
                             CAST(((SUM({NumExpr("base.ts_demand")})
                                     + SUM(NVL({NumExpr("a_ts.cm_matrix_adder_value")}, 0)))
                                    * SUM(base.rtu_ts_value) * 3)
                                 * SUM({NumExpr("base.cost_rtu")}) / 1000                                                                    AS BINARY_DOUBLE) AS cost_rfc_wo_depr,
                             CAST(SUM({NumExpr("base.depreciation")})                                                                      AS BINARY_DOUBLE) AS depreciation,
                             CAST(((SUM({NumExpr("base.ts_demand")})
                                     + SUM(NVL({NumExpr("a_ts.cm_matrix_adder_value")}, 0)))
                                    * SUM(base.rtu_ts_value) * 3)
                                 * SUM({NumExpr("base.cost_rtu")}) / 1000
                                 + SUM({NumExpr("base.depreciation")})                                                                       AS BINARY_DOUBLE) AS cost_rfc_demand,
                             CAST(SUM(NVL({NumExpr("a_cost.cm_matrix_adder_value")}, 0))                                                   AS BINARY_DOUBLE) AS adder_cost,
                             CAST(SUM({NumExpr("base.cost_rtu")})                                                                          AS BINARY_DOUBLE) AS cost_rtu,
                             CAST(SUM({NumExpr("base.cost_act")})                                                                          AS BINARY_DOUBLE) AS cost_actual
                              FROM base{AdderJoin("base", "a_ts",   "Adder",  "TS")}{AdderJoin("base", "c_ts",   "Change", "TS")}{AdderJoin("base", "a_rtu",  "Adder",  "RTU")}{AdderJoin("base", "c_rtu",  "Change", "RTU")}{AdderJoin("base", "a_cost", "Adder",  "COST")}
                         GROUP BY base.fy_quarter,
                                            base.location,
                                            base.horizon,
                                            base.sb
                         ORDER BY base.fy_quarter ASC,
                                            base.location ASC,
                                            base.sb ASC";

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
                    fyQuarter      = reader["fy_quarter"]?.ToString() ?? "",
                    location       = reader["location"]?.ToString()   ?? "",
                    horizon        = reader["horizon"]?.ToString()     ?? "",
                    sb             = reader["sb"]?.ToString()          ?? "",
                    tsDemand       = Dbl(reader["ts_demand"]),
                    adderTs        = Dbl(reader["adder_ts"]),
                    tsActual       = Dbl(reader["ts_actual"]),
                    changeTs       = Dbl(reader["change_ts"]),
                    rtuRfcDemand   = Dbl(reader["rtu_rfc_demand"]),
                    adderRtu       = Dbl(reader["adder_rtu"]),
                    rtuTs          = Dbl(reader["rtu_ts"]),
                    rtuActual      = Dbl(reader["rtu_actual"]),
                    changeRtu      = Dbl(reader["change_rtu"]),
                    costRfcWoDepr  = Dbl(reader["cost_rfc_wo_depr"]),
                    depreciation   = Dbl(reader["depreciation"]),
                    costRfcDemand  = Dbl(reader["cost_rfc_demand"]),
                    adderCost      = Dbl(reader["adder_cost"]),
                    costRtu        = Dbl(reader["cost_rtu"]),
                    costActual     = Dbl(reader["cost_actual"]),
                });
            }

            return Ok(result);
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "LabSummary.Get failed for horizon {Horizon}", horizon);
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // GET api/lab-summary/filter-options
    // Returns all distinct FY Quarter, Location, and SB values (across all horizons).
    [HttpGet("filter-options")]
    public async Task<ActionResult> GetFilterOptions()
    {
        const string sql = @"
            SELECT DISTINCT
                   CASE WHEN quarter IS NULL THEN fy ELSE fy || ' ' || quarter END AS fy_quarter,
                   loc AS location,
                   sb
              FROM rpt.asb_ts_actual
             WHERE loc IS NOT NULL
               AND sb  IS NOT NULL
             ORDER BY 1, 2, 3";

        try
        {
            await using var conn = _factory.Create();
            await conn.OpenWithNlsAsync();
            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };

            await using var reader = await cmd.ExecuteReaderAsync();

            var fyQuarters = new SortedSet<string>();
            var locations  = new SortedSet<string>();
            var sbs        = new SortedSet<string>();

            while (await reader.ReadAsync())
            {
                var q = reader["fy_quarter"]?.ToString();
                var l = reader["location"]?.ToString();
                var s = reader["sb"]?.ToString();
                if (!string.IsNullOrEmpty(q)) fyQuarters.Add(q);
                if (!string.IsNullOrEmpty(l)) locations.Add(l);
                if (!string.IsNullOrEmpty(s)) sbs.Add(s);
            }

            return Ok(new
            {
                fyQuarters = fyQuarters.ToList(),
                locations  = locations.ToList(),
                sbs        = sbs.ToList(),
            });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "LabSummary.GetFilterOptions failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }
}
