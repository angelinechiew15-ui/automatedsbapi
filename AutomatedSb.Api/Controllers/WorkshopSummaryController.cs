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
}
