using AutomatedSb.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Oracle.ManagedDataAccess.Client;

namespace AutomatedSb.Api.Controllers;

[ApiController]
[Route("api/refs")]
public class RefsController : ControllerBase
{
    private readonly IOracleConnectionFactory _factory;
    private readonly ILogger<RefsController> _logger;

    public RefsController(
        IOracleConnectionFactory factory,
        ILogger<RefsController> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    // GET api/refs/horizons - Generate horizon list (current year + previous year quarters)
    [HttpGet("horizons")]
    public ActionResult GetHorizons()
    {
        var horizons = new List<object>();
        string currentYear = DateTime.Now.Year.ToString();
        string curyr = currentYear.Substring(currentYear.Length - 2);
        string prevyr = (int.Parse(curyr) - 1).ToString("D2");
        string[] months = { "03", "06", "09", "12" };

        // Add current year items
        foreach (string month in months)
        {
            string value = $"{curyr}-{month}";
            horizons.Add(new { value, text = value });
        }

        // Add previous year items
        foreach (string month in months)
        {
            string value = $"{prevyr}-{month}";
            horizons.Add(new { value, text = value });
        }

        return Ok(horizons);
    }

    // GET api/refs/sbOwners - Get list of SB owners (persons)
    [HttpGet("sbOwners")]
    public async Task<ActionResult> GetSbOwners()
    {
        const string sql = @"
            SELECT cm_matrix_person_id AS value,
                   cm_matrix_person_lastname || ' ' || cm_matrix_person_firstname AS text
            FROM cm_matrix_person
            WHERE cm_matrix_person_valid = 'Y'
            ORDER BY text ASC";

        try
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync();
            await using var cmd = new OracleCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            var result = new List<object>();
            while (await reader.ReadAsync())
            {
                result.Add(new
                {
                    value = reader["value"]?.ToString() ?? "",
                    text = reader["text"]?.ToString() ?? ""
                });
            }

            return Ok(result);
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "GetSbOwners failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // GET api/refs/sb-names - Get list of service bundle names
    [HttpGet("sb-names")]
    public async Task<ActionResult> GetSbNames()
    {
        const string sql = @"
            SELECT cm_matrix_sb_id AS value,
                   cm_matrix_sb_name AS text
            FROM cm_matrix_sb
            WHERE cm_matrix_sb_valid = 'Y'
            ORDER BY text ASC";

        try
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync();
            await using var cmd = new OracleCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            var result = new List<object>();
            while (await reader.ReadAsync())
            {
                result.Add(new
                {
                    value = reader["value"]?.ToString() ?? "",
                    text = reader["text"]?.ToString() ?? ""
                });
            }

            return Ok(result);
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "GetSbNames failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }
}
