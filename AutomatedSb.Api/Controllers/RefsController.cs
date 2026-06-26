using AutomatedSb.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Oracle.ManagedDataAccess.Client;

namespace AutomatedSb.Api.Controllers;

[ApiController]
[Route("api/refs")]
public class RefsController : ControllerBase
{
    private readonly IOracleConnectionFactory _factory;
    private readonly IOracleRealisConnectionFactory _realisFactory;
    private readonly ILogger<RefsController> _logger;

    public RefsController(
        IOracleConnectionFactory factory,
        IOracleRealisConnectionFactory realisFactory,
        ILogger<RefsController> logger)
    {
        _factory = factory;
        _realisFactory = realisFactory;
        _logger = logger;
    }

    // GET api/refs/horizons - Get horizon list from rfc_horizon table
    [HttpGet("horizons")]
    public async Task<ActionResult> GetHorizons()
    {
        const string sql = @"
            SELECT rhz_id AS value,
                   rhz_name AS text
            FROM rfc_horizon
            ORDER BY rhz_id DESC";

        try
        {
            await using var conn = _realisFactory.Create();
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
            _logger.LogError(ex, "GetHorizons failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
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
