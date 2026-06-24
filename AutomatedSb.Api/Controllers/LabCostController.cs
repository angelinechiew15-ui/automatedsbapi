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

    // GET api/lab-cost/qtr-avg?horizon=26-06
    // Returns one row per (location, sb, fy) with the quarterly average cost value.
    [HttpGet("qtr-avg")]
    public async Task<ActionResult> GetQtrAvg([FromQuery] string? horizon)
    {
        if (string.IsNullOrWhiteSpace(horizon))
            return BadRequest(new { success = false, message = "horizon is required" });

        // Cost RFC w/o Depreciation = SUM(RTU_PLAN) * SUM(COST/RTU) / 1000
        //   (mirrors Tableau FIXED LOD: Group RTU RFC Demand * Group COST/RTU / 1000)
        // Total cost per quarter = Cost RFC w/o Dep + DEPRECIATION + ADDER_COST
        // Final value = AVG of the quarterly cost across all quarters in the FY
        const string sql = @"
            SELECT location, sb, fy, AVG(cost_value) AS value
            FROM (
                SELECT
                    loc AS location,
                    sb,
                    fy,
                    quarter,
                    CASE
                        WHEN SUM(rtu_plan) IS NULL OR SUM(""COST/RTU"") IS NULL THEN 0
                        ELSE SUM(rtu_plan) * SUM(""COST/RTU"") / 1000
                    END
                    + NVL(SUM(depreciation), 0)
                    + NVL(SUM(adder_cost),   0)  AS cost_value
                FROM v_sb_asb_data
                WHERE horizon = :horizon
                  AND loc     IS NOT NULL
                  AND sb      IS NOT NULL
                  AND fy      IS NOT NULL
                GROUP BY loc, sb, fy, quarter
            ) q
            GROUP BY location, sb, fy
            ORDER BY location ASC, sb ASC, fy ASC";

        try
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync();
            await using var cmd = new OracleCommand(sql, conn);
            cmd.Parameters.Add(new OracleParameter("horizon", OracleDbType.Varchar2) { Value = horizon });

            await using var reader = await cmd.ExecuteReaderAsync();

            var result = new List<object>();
            while (await reader.ReadAsync())
            {
                var rawValue = reader["value"];
                double? value = rawValue == DBNull.Value || rawValue == null
                    ? null
                    : Convert.ToDouble(rawValue);

                result.Add(new
                {
                    location = reader["location"]?.ToString() ?? "",
                    sb       = reader["sb"]?.ToString()       ?? "",
                    fy       = reader["fy"]?.ToString()       ?? "",
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
