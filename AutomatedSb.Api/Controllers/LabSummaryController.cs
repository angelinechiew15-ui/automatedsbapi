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

    // GET api/lab-summary?horizon=26-06
    // Returns one row per (fy_quarter, location, horizon, sb) with all TS/RTU/Cost columns.
    [HttpGet]
    public async Task<ActionResult> Get([FromQuery] string? horizon)
    {
        if (string.IsNullOrWhiteSpace(horizon))
            return BadRequest(new { success = false, message = "horizon is required" });

        // Adder JOIN helper — reused 5 times for TS Adder, TS Change, RTU Adder, RTU Change, COST Adder
        static string AdderJoin(string alias, string type, string forMeasure) => $@"
              LEFT JOIN rpt.cm_matrix_sb_adder {alias}
                ON t.loc     = {alias}.cm_matrix_adder_location
               AND t.sb      = {alias}.cm_matrix_adder_sb_name
               AND t.fy || '-' || t.quarter = {alias}.cm_matrix_adder_fy || '-' || {alias}.cm_matrix_adder_quarter
               AND t.horizon = {alias}.cm_matrix_adder_horizon
               AND {alias}.cm_matrix_adder_type = '{type}'
               AND {alias}.cm_matrix_adder_for  = '{forMeasure}'";

        var sql = $@"
            SELECT CASE WHEN t.quarter IS NULL THEN t.fy
                        ELSE t.fy || ' ' || t.quarter END AS fy_quarter,
                   t.loc       AS location,
                   t.horizon,
                   t.sb,
                   CAST(SUM(TO_NUMBER(t.ts_demand     DEFAULT 0 ON CONVERSION ERROR))                                                                         AS BINARY_DOUBLE) AS ts_demand,
                   CAST(SUM(NVL(TO_NUMBER(a_ts.cm_matrix_adder_value  DEFAULT 0 ON CONVERSION ERROR), 0))                                                     AS BINARY_DOUBLE) AS adder_ts,
                   CAST(SUM(TO_NUMBER(t.ts_actual     DEFAULT 0 ON CONVERSION ERROR))                                                                         AS BINARY_DOUBLE) AS ts_actual,
                   CAST(SUM(NVL(TO_NUMBER(c_ts.cm_matrix_adder_value  DEFAULT 0 ON CONVERSION ERROR), 0))                                                     AS BINARY_DOUBLE) AS change_ts,
                   CAST(((SUM(TO_NUMBER(t.ts_demand   DEFAULT 0 ON CONVERSION ERROR))
                           + SUM(NVL(TO_NUMBER(a_ts.cm_matrix_adder_value DEFAULT 0 ON CONVERSION ERROR), 0)))
                          * SUM(TO_NUMBER(t.""RTU/TS"" DEFAULT 0 ON CONVERSION ERROR)) * 3)                                                                    AS BINARY_DOUBLE) AS rtu_rfc_demand,
                   CAST(SUM(NVL(TO_NUMBER(a_rtu.cm_matrix_adder_value DEFAULT 0 ON CONVERSION ERROR), 0))                                                     AS BINARY_DOUBLE) AS adder_rtu,
                   CAST(SUM(TO_NUMBER(t.""RTU/TS""     DEFAULT 0 ON CONVERSION ERROR))                                                                        AS BINARY_DOUBLE) AS rtu_ts,
                   CAST(SUM(TO_NUMBER(t.rtu_act       DEFAULT 0 ON CONVERSION ERROR))                                                                         AS BINARY_DOUBLE) AS rtu_actual,
                   CAST(SUM(NVL(TO_NUMBER(c_rtu.cm_matrix_adder_value DEFAULT 0 ON CONVERSION ERROR), 0))                                                     AS BINARY_DOUBLE) AS change_rtu,
                   CAST(((SUM(TO_NUMBER(t.ts_demand   DEFAULT 0 ON CONVERSION ERROR))
                           + SUM(NVL(TO_NUMBER(a_ts.cm_matrix_adder_value DEFAULT 0 ON CONVERSION ERROR), 0)))
                          * SUM(TO_NUMBER(t.""RTU/TS"" DEFAULT 0 ON CONVERSION ERROR)) * 3)
                         * SUM(TO_NUMBER(t.""COST/RTU"" DEFAULT 0 ON CONVERSION ERROR)) / 1000                                                                 AS BINARY_DOUBLE) AS cost_rfc_wo_depr,
                   CAST(SUM(TO_NUMBER(t.depreciation  DEFAULT 0 ON CONVERSION ERROR))                                                                         AS BINARY_DOUBLE) AS depreciation,
                   CAST(((SUM(TO_NUMBER(t.ts_demand   DEFAULT 0 ON CONVERSION ERROR))
                           + SUM(NVL(TO_NUMBER(a_ts.cm_matrix_adder_value DEFAULT 0 ON CONVERSION ERROR), 0)))
                          * SUM(TO_NUMBER(t.""RTU/TS"" DEFAULT 0 ON CONVERSION ERROR)) * 3)
                         * SUM(TO_NUMBER(t.""COST/RTU"" DEFAULT 0 ON CONVERSION ERROR)) / 1000
                         + SUM(TO_NUMBER(t.depreciation DEFAULT 0 ON CONVERSION ERROR))                                                                        AS BINARY_DOUBLE) AS cost_rfc_demand,
                   CAST(SUM(NVL(TO_NUMBER(a_cost.cm_matrix_adder_value DEFAULT 0 ON CONVERSION ERROR), 0))                                                    AS BINARY_DOUBLE) AS adder_cost,
                   CAST(SUM(TO_NUMBER(t.""COST/RTU""   DEFAULT 0 ON CONVERSION ERROR))                                                                        AS BINARY_DOUBLE) AS cost_rtu,
                   CAST(SUM(TO_NUMBER(t.cost_act      DEFAULT 0 ON CONVERSION ERROR))                                                                         AS BINARY_DOUBLE) AS cost_actual
              FROM rpt.asb_ts_actual t{AdderJoin("a_ts",   "Adder",  "TS")}{AdderJoin("c_ts",   "Change", "TS")}{AdderJoin("a_rtu",  "Adder",  "RTU")}{AdderJoin("c_rtu",  "Change", "RTU")}{AdderJoin("a_cost", "Adder",  "COST")}
             WHERE t.horizon = :horizon
               AND t.loc IS NOT NULL
               AND t.sb  IS NOT NULL
             GROUP BY CASE WHEN t.quarter IS NULL THEN t.fy ELSE t.fy || ' ' || t.quarter END,
                      t.loc, t.horizon, t.sb
             ORDER BY CASE WHEN t.quarter IS NULL THEN t.fy ELSE t.fy || ' ' || t.quarter END ASC,
                      t.loc ASC, t.sb ASC";

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
}
