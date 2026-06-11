using AutomatedSb.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Oracle.ManagedDataAccess.Client;

namespace AutomatedSb.Api.Controllers;

[ApiController]
[Route("api/sballmap")]
public class SballmapController : ControllerBase
{
    private readonly IOracleConnectionFactory _factory;
    private readonly ILogger<SballmapController> _logger;

    public SballmapController(
        IOracleConnectionFactory factory,
        ILogger<SballmapController> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    // GET api/sballmap
    [HttpGet]
    public async Task<ActionResult> GetAll()
    {
        // RTU/TS and COST/RTU columns cause ORA-01722 in view - using only working columns
        const string sql = "SELECT fy, loc, sb, RTU_TS FROM v_sb_asb_data WHERE sb IS NOT NULL ORDER BY fy DESC, sb ASC";

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
                if (seen.Contains(key)) continue;
                seen.Add(key);

                var rtutsRaw = reader["RTU_TS"]?.ToString()?.Replace(',', '.') ?? "";
                string rtuts = "";

                if (!string.IsNullOrEmpty(rtutsRaw) && decimal.TryParse(rtutsRaw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v1))
                    rtuts = Math.Round(v1, 2).ToString(System.Globalization.CultureInfo.InvariantCulture);

                result.Add(new
                {
                    fy,
                    loc,
                    sb,
                    rtuts_old = "",
                    rtuts,
                    costrtu_old = ""
                });
            }

            return Ok(result);
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "GetAll sballmap failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }
}
