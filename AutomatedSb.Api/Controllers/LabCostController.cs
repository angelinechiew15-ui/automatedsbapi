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

        const string sql = @"
            SELECT
                rptloc                                                          AS location,
                sb                                                              AS sb,
                fy                                                              AS fy,
                AVG(
                    COALESCE(rfcwodemand,  0)
                  + COALESCE(depreciation, 0)
                  + COALESCE(adderdemand,  0)
                )                                                               AS value
            FROM v_sb_asb_data
            WHERE horizon = :horizon
              AND rptloc IS NOT NULL
              AND sb     IS NOT NULL
              AND fy     IS NOT NULL
            GROUP BY rptloc, sb, fy
            ORDER BY rptloc ASC, sb ASC, fy ASC";

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
