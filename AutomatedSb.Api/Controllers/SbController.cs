using AutomatedSb.Api.Data;
using AutomatedSb.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Oracle.ManagedDataAccess.Client;

namespace AutomatedSb.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SbController : ControllerBase
{
    // Tables: cm_matrix_sb_mapping (main), cm_matrix_sb (SB names)
    private const string SelectSql = @"
        SELECT k.cm_matrix_sb_mapping_id    AS SBMAPPINGID,
               k.cm_matrix_sb_mapping_sb_id AS SB,
               s.cm_matrix_sb_name          AS SBNAME,
               k.cm_matrix_sb_mapping_gf_id AS CCID
          FROM cm_matrix_sb_mapping k
          JOIN cm_matrix_sb s ON k.cm_matrix_sb_mapping_sb_id = s.cm_matrix_sb_id
         ORDER BY s.cm_matrix_sb_name ASC";

    private readonly IOracleConnectionFactory _factory;
    private readonly IOracleRealisConnectionFactory _realisFactory;
    private readonly ILogger<SbController> _logger;

    public SbController(
        IOracleConnectionFactory factory,
        IOracleRealisConnectionFactory realisFactory,
        ILogger<SbController> logger)
    {
        _factory = factory;
        _realisFactory = realisFactory;
        _logger = logger;
    }

    // GET api/sb
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Sb>>> GetAll()
    {
        try
        {
            // Load all CC names once from Realis to avoid per-row lookups
            var ccNames = await LoadCcNameDictionaryAsync();

            await using var conn = _factory.Create();
            await conn.OpenAsync();
            await using var cmd = new OracleCommand(SelectSql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            var rows = new List<Sb>();
            while (await reader.ReadAsync())
            {
                var ccId = reader["CCID"]?.ToString() ?? "";
                ccNames.TryGetValue(ccId, out var ccName);

                rows.Add(new Sb
                {
                    SbMappingId = reader["SBMAPPINGID"]?.ToString() ?? "",
                    SbCode      = reader["SB"]?.ToString()          ?? "",
                    SbName      = reader["SBNAME"]?.ToString()      ?? "",
                    CcId        = ccId,
                    CcName      = ccName ?? "",
                });
            }
            return Ok(rows);
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "Sb GET failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // POST api/sb
    [HttpPost]
    public async Task<ActionResult> Create([FromBody] SbCreateDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.SbCode) || string.IsNullOrWhiteSpace(dto.CcId))
            return BadRequest(new { success = false, message = "sb and ccid are required." });

        if (!int.TryParse(dto.SbCode, out int sbId) || !int.TryParse(dto.CcId, out int ccId))
            return BadRequest(new { success = false, message = "sb and ccid must be numeric IDs." });

        const string sql = @"
            INSERT INTO cm_matrix_sb_mapping (
                cm_matrix_sb_mapping_sb_id,
                cm_matrix_sb_mapping_gf_id,
                cm_matrix_sb_mapping_valid
            ) VALUES (:sb_id, :cc_id, 'Y')";

        try
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync();
            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            cmd.Parameters.Add("sb_id", OracleDbType.Int32).Value = sbId;
            cmd.Parameters.Add("cc_id", OracleDbType.Int32).Value = ccId;
            await cmd.ExecuteNonQueryAsync();
            return Ok(new { success = true });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "Sb POST failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // POST api/sb/delete
    [HttpPost("delete")]
    public async Task<ActionResult> Delete([FromBody] DeleteIdsDto dto)
    {
        if (dto.Ids.Count == 0)
            return Ok(new { success = true, deleted = 0 });

        const string sql =
            "DELETE FROM cm_matrix_sb_mapping WHERE cm_matrix_sb_mapping_id = :id";

        try
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync();
            int deleted = 0;
            foreach (var id in dto.Ids)
            {
                if (!int.TryParse(id, out int idInt))
                {
                    _logger.LogWarning("Invalid ID format: {Id}", id);
                    continue;
                }
                await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
                cmd.Parameters.Add("id", OracleDbType.Int32).Value = idInt;
                deleted += await cmd.ExecuteNonQueryAsync();
            }
            return Ok(new { success = true, deleted });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "Sb DELETE failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // GET api/sb/ref/newsb - Service Bundle reference list
    [HttpGet("ref/newsb")]
    public async Task<ActionResult<IEnumerable<object>>> GetSbRefList()
    {
        try
        {
            const string sql = @"
                SELECT cm_matrix_sb_id AS value,
                       cm_matrix_sb_name AS text
                  FROM cm_matrix_sb
                 ORDER BY cm_matrix_sb_name ASC";

            var result = new List<object>();
            await using var conn = _factory.Create();
            await conn.OpenAsync();
            await using var cmd = new OracleCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

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
            _logger.LogError(ex, "Failed to load SB reference list");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // GET api/sb/ref/newcc - Client Corridor reference list
    [HttpGet("ref/newcc")]
    public async Task<ActionResult<IEnumerable<object>>> GetCcRefList()
    {
        try
        {
            const string sql = @"
                SELECT gf_id AS value,
                       gf_gf AS text
                  FROM ctlg_pl
                 WHERE gf_valid = 'Y'
                 ORDER BY gf_gf ASC";

            var result = new List<object>();
            await using var conn = _realisFactory.Create();
            await conn.OpenAsync();
            await using var cmd = new OracleCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                int ordValue = reader.GetOrdinal("value");
                int ordText = reader.GetOrdinal("text");
                result.Add(new
                {
                    value = reader.IsDBNull(ordValue) ? "" : reader.GetDecimal(ordValue).ToString(),
                    text = reader.IsDBNull(ordText) ? "" : reader.GetString(ordText)
                });
            }

            return Ok(result);
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "Failed to load CC reference list");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // Loads all valid client corridor names from Realis DB into a dictionary
    // keyed by gf_id — same source as getcclist() in the legacy code.
    private async Task<Dictionary<string, string>> LoadCcNameDictionaryAsync()
    {
        const string sql = @"
            SELECT gf_id, gf_gf AS clientcorridor
              FROM ctlg_pl
             WHERE gf_valid = 'Y'
             ORDER BY gf_gf ASC";

        var dict = new Dictionary<string, string>();
        try
        {
            await using var conn = _realisFactory.Create();
            await conn.OpenAsync();
            await using var cmd = new OracleCommand(sql, conn);
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                int ord = rdr.GetOrdinal("gf_id");
                if (rdr.IsDBNull(ord)) continue;
                string key = rdr.GetDecimal(ord).ToString();
                ord = rdr.GetOrdinal("clientcorridor");
                dict[key] = rdr.IsDBNull(ord) ? "" : rdr.GetString(ord);
            }
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "Failed to load CC name dictionary");
        }
        return dict;
    }
}
