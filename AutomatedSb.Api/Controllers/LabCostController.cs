using AutomatedSb.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Oracle.ManagedDataAccess.Client;

namespace AutomatedSb.Api.Controllers;

/// <summary>
/// Provides lab-cost quarterly-average data for the Lab Cost tab.
/// Source view: v_sb_asb_data
/// Cost value = COALESCE(rfcwodemand,0) + COALESCE(depreciation,0) + COALESCE(adderdemand,0)
/// (mirrors Tableau: ZN([Cost RFC w/o Depreciation]) + ZN([Depreciation]) + ZN([Adder Value Cost Demand]))
/// </summary>
[ApiController]
[Route("api/lab-cost")]
public class LabCostController : ControllerBase
{
    private readonly IOracleConnectionFactory _factory;
    private readonly ILogger<LabCostController> _logger;

    public LabCostController(
        IOracleConnectionFactory factory,
        ILogger<LabCostController> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    private static string NumExpr(string expr) =>
        $"TO_NUMBER({expr} DEFAULT 0 ON CONVERSION ERROR)";

    // GET api/lab-cost/filter-options
    // Returns distinct locations and SBs for preloading filter dropdowns.
    [HttpGet("filter-options")]
    public async Task<ActionResult> GetFilterOptions()
    {
           const string sql = @"
              SELECT DISTINCT t.loc AS location,
                    t.sb,
                    NVL(s.cm_matrix_sb_name, t.sb) AS sbname
                FROM rpt.asb_ts_actual t
                LEFT JOIN cm_matrix_sb s ON s.cm_matrix_sb_name = t.sb
               WHERE t.loc IS NOT NULL AND t.sb IS NOT NULL
               ORDER BY t.loc ASC, t.sb ASC";
        try
        {
            await using var conn = _factory.Create();
            await conn.OpenWithNlsAsync();
            await using var cmd = new OracleCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            var locations = new SortedSet<string>();
            var sbMap = new SortedDictionary<string, string>(); // sb -> sbname
            while (await reader.ReadAsync())
            {
                var loc    = reader["location"]?.ToString() ?? "";
                var sb     = reader["sb"]?.ToString()       ?? "";
                var sbname = reader["sbname"]?.ToString()   ?? sb;
                if (!string.IsNullOrWhiteSpace(loc))   locations.Add(loc);
                if (!string.IsNullOrWhiteSpace(sb))    sbMap.TryAdd(sb, sbname);
            }
            return Ok(new
            {
                locations = locations.ToList(),
                sbs = sbMap.Select(kv => new { value = kv.Key, text = kv.Value }).ToList(),
            });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "LabCost GetFilterOptions failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // GET api/lab-cost/qtr-avg?horizon=26-06
    // Returns one row per (location, sb, fy) with the quarterly average cost value.
    [HttpGet("qtr-avg")]
    public async Task<ActionResult> GetQtrAvg([FromQuery] string? horizon)
    {
        if (string.IsNullOrWhiteSpace(horizon))
            return BadRequest(new { success = false, message = "horizon is required" });

                // Cost RFC w/o Depreciation = RTU RFC demand * COST/RTU / 1000
                // RTU RFC demand = ((TS demand + TS adder) * 3 * RTU/TS) + RTU adder
                // Cost value per quarter = cost RFC w/o depr + depreciation + cost adder
                // FY-only rows (no quarter) are annualized by factor 4.
                var sql = $@"
                        SELECT q.location, q.sb,
                                     NVL(s.cm_matrix_sb_name, q.sb) AS sbname,
                                     q.fy,
                                     CAST(SUM(q.cost_value) AS BINARY_DOUBLE) AS value
                            FROM (
                                        SELECT
                                                t.loc AS location,
                                                t.sb,
                                                t.fy,
                                                CASE WHEN t.quarter IS NULL THEN t.fy ELSE t.fy || ' ' || t.quarter END AS fy_quarter,
                                                CAST(CASE
                                                        WHEN t.quarter IS NULL THEN
                                                                (((SUM({NumExpr("t.ts_demand")}) + SUM(NVL({NumExpr("a_ts.cm_matrix_adder_value")}, 0)))
                                                                    * 3
                                                                    * SUM({NumExpr(@"t.""RTU/TS""")})
                                                                    + SUM(NVL({NumExpr("a_rtu.cm_matrix_adder_value")}, 0)))
                                                                 * SUM({NumExpr(@"t.""COST/RTU""")}) / 1000
                                                                 + SUM({NumExpr("t.depreciation")})
                                                                 + SUM(NVL({NumExpr("a_cost.cm_matrix_adder_value")}, 0))) * 4
                                                        ELSE
                                                                (((SUM({NumExpr("t.ts_demand")}) + SUM(NVL({NumExpr("a_ts.cm_matrix_adder_value")}, 0)))
                                                                    * 3
                                                                    * SUM({NumExpr(@"t.""RTU/TS""")})
                                                                    + SUM(NVL({NumExpr("a_rtu.cm_matrix_adder_value")}, 0)))
                                                                 * SUM({NumExpr(@"t.""COST/RTU""")}) / 1000
                                                                 + SUM({NumExpr("t.depreciation")})
                                                                 + SUM(NVL({NumExpr("a_cost.cm_matrix_adder_value")}, 0)))
                                                END AS BINARY_DOUBLE) AS cost_value
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
                                            AND t.loc IS NOT NULL
                                            AND t.sb IS NOT NULL
                                            AND t.fy IS NOT NULL
                                        GROUP BY t.loc, t.sb, t.fy, CASE WHEN t.quarter IS NULL THEN t.fy ELSE t.fy || ' ' || t.quarter END, t.quarter
                            ) q
                            LEFT JOIN cm_matrix_sb s ON s.cm_matrix_sb_name = q.sb
                         GROUP BY q.location, q.sb, s.cm_matrix_sb_name, q.fy
                         ORDER BY q.location ASC, q.sb ASC, q.fy ASC";

        try
        {
            await using var conn = _factory.Create();
            await conn.OpenWithNlsAsync();
            await using var cmd = new OracleCommand(sql, conn);
            cmd.Parameters.Add(new OracleParameter("horizon", OracleDbType.Varchar2) { Value = horizon });

            await using var reader = await cmd.ExecuteReaderAsync();

            var valueOrd    = reader.GetOrdinal("value");
            var locationOrd = reader.GetOrdinal("location");
            var sbOrd       = reader.GetOrdinal("sb");
            var sbnameOrd   = reader.GetOrdinal("sbname");
            var fyOrd       = reader.GetOrdinal("fy");

            var result = new List<object>();
            while (await reader.ReadAsync())
            {
                double? value = reader.IsDBNull(valueOrd)
                    ? null
                    : reader.GetDouble(valueOrd);

                result.Add(new
                {
                    location = reader.IsDBNull(locationOrd) ? "" : reader.GetString(locationOrd),
                    sb       = reader.IsDBNull(sbOrd)       ? "" : reader.GetString(sbOrd),
                    sbname   = reader.IsDBNull(sbnameOrd)   ? "" : reader.GetString(sbnameOrd),
                    fy       = reader.IsDBNull(fyOrd)       ? "" : reader.GetString(fyOrd),
                    value
                });
            }

            return Ok(result);
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "GetQtrAvg failed for horizon {Horizon}", horizon);
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }
}
