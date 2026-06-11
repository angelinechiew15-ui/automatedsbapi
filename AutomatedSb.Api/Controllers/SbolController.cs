using AutomatedSb.Api.Data;
using AutomatedSb.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Oracle.ManagedDataAccess.Client;

namespace AutomatedSb.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SbolController : ControllerBase
{
    // Table: CM_MATRIX_SB_EXT_MAPPING
    // ROWID is used as the row identifier (MapId) until a real PK is confirmed.
    private const string SelectSql = @"
        SELECT CM_MATRIX_SB_EXT_MAPPING_ID AS MAPID,
               cm_matrix_sb_ext_mapping_ext_id AS ETHLOC,
               cm_matrix_sb_ext_mapping_rpt_id AS RPTLOC,
               CM_MATRIX_SB_EXT_FOR_TS         AS TSTRUE,
               CM_MATRIX_SB_EXT_FOR_RTU        AS RTUTRUE
          FROM CM_MATRIX_SB_EXT_MAPPING";

    private readonly IOracleConnectionFactory _factory;
    private readonly IOracleRealisConnectionFactory _realisFactory;
    private readonly ILogger<SbolController> _logger;

    public SbolController(IOracleConnectionFactory factory, IOracleRealisConnectionFactory realisFactory, ILogger<SbolController> logger)
    {
        _factory = factory;
        _logger = logger;
        _realisFactory = realisFactory;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Sbol>>> GetAll()
    {
        try
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync();
            await using var cmd = new OracleCommand(SelectSql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            var rows = new List<Sbol>();
            while (await reader.ReadAsync())
            {
                var ethLoc = reader["ETHLOC"]?.ToString() ?? "";
                var eth = await GetLocationNameByLocAsync(ethLoc);
                var rptLoc = reader["RPTLOC"]?.ToString() ?? "";
                var rpt = await GetLocationNameByLocAsync(rptLoc);

                rows.Add(new Sbol
                {
                    MapId = reader["MAPID"]?.ToString() ?? "",
                    EthLoc = eth,
                    RptLoc = rpt,
                    TsTrue = reader["TSTRUE"]?.ToString() ?? "",
                    RtuTrue = reader["RTUTRUE"]?.ToString() ?? "",
                });
            }
            return Ok(rows);
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "Sbol GET failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<ActionResult> Create([FromBody] SbolCreateDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.EthLoc) || string.IsNullOrWhiteSpace(dto.RptLoc))
            return BadRequest(new { success = false, message = "ethloc and rptloc are required" });

        // Get location names for ext_id and rpt_id
        var extLocName = await GetLocationNameByLocAsync(dto.EthLoc);
        var rptLocName = await GetLocationNameByLocAsync(dto.RptLoc);

        const string sql = @"
        INSERT INTO CM_MATRIX_SB_EXT_MAPPING (
          cm_matrix_sb_ext_mapping_ext_id,
          cm_matrix_sb_ext_mapping_rpt_id,
          cm_matrix_sb_ext_mapping_lastupdate,
          CM_MATRIX_SB_EXT_FOR_TS,
          CM_MATRIX_SB_EXT_FOR_RTU,
          CM_MATRIX_SB_EXT_MAPPING_ext_loc,
          CM_MATRIX_SB_EXT_MAPPING_rpt_loc
        ) VALUES (:ext_id, :rpt_id, SYSDATE, :ts, :rtu, :ext_loc, :rpt_loc)";

        try
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync();
            await using var cmd = new OracleCommand(sql, conn)
            {
                BindByName = true
            };
            cmd.Parameters.Add("ext_id", dto.EthLoc);
            cmd.Parameters.Add("rpt_id", dto.RptLoc);
            cmd.Parameters.Add("ts", dto.TsTrue ?? "");
            cmd.Parameters.Add("rtu", dto.RtuTrue ?? "");
            cmd.Parameters.Add("ext_loc", extLocName ?? "");
            cmd.Parameters.Add("rpt_loc", rptLocName ?? "");
            await cmd.ExecuteNonQueryAsync();
            return Ok(new { success = true });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "Sbol POST failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    [HttpPut("{id}")]
    public async Task<ActionResult> Update(string id, [FromBody] SbolCreateDto dto)
    {
        _logger.LogInformation("DTO received: {@Dto}", dto);

        // Ensure only "Y" or "N" is saved, never "true"/"false"
        string tsValue = (dto.TsTrue?.ToUpper() == "Y" || dto.TsTrue?.ToLower() == "true") ? "Y" : "N";
        string rtuValue = (dto.RtuTrue?.ToUpper() == "Y" || dto.RtuTrue?.ToLower() == "true") ? "Y" : "N";

        const string sql = @"
        UPDATE CM_MATRIX_SB_EXT_MAPPING
           SET 
               CM_MATRIX_SB_EXT_FOR_TS         = :ts,
               CM_MATRIX_SB_EXT_FOR_RTU        = :rtu,
               cm_matrix_sb_ext_mapping_lastupdate = SYSDATE
         WHERE CM_MATRIX_SB_EXT_MAPPING_ID = :id";

        try
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync();
            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            cmd.Parameters.Add("ts", tsValue);
            cmd.Parameters.Add("rtu", rtuValue);
            cmd.Parameters.Add("id", OracleDbType.Int32).Value = int.Parse(id);
            var affected = await cmd.ExecuteNonQueryAsync();
            if (affected == 0)
                return NotFound(new { success = false, message = "Not found" });
            return Ok(new { success = true });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "Sbol PUT failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    [HttpPost("delete")]
    public async Task<ActionResult> Delete([FromBody] DeleteIdsDto dto)
    {
        if (dto.Ids.Count == 0)
            return Ok(new { success = true });

        const string sql =
            "DELETE FROM CM_MATRIX_SB_EXT_MAPPING WHERE CM_MATRIX_SB_EXT_MAPPING_ID = :id";

        try
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync();
            foreach (var id in dto.Ids)
            {
                await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
                cmd.Parameters.Add("id", OracleDbType.Int32).Value = id;
                await cmd.ExecuteNonQueryAsync();
            }
            return Ok(new { success = true });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "Sbol DELETE failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    [HttpGet("location-name")]
    public async Task<ActionResult<string>> GetLocationName([FromQuery] int? ext_id, [FromQuery] int? rpt_id)
    {
        if (ext_id == null && rpt_id == null)
            return BadRequest("Either ext_id or rpt_id must be provided.");

        string query = "SELECT cloc_location_name FROM ctlg_location_type WHERE cloc_id = :id";
        int id = ext_id ?? rpt_id.Value;

        try
        {
            await using var conn = _realisFactory.Create();
            await conn.OpenAsync();
            await using var cmd = new OracleCommand(query, conn);
            cmd.Parameters.Add(new OracleParameter("id", id));

            var result = await cmd.ExecuteScalarAsync();
            if (result == null)
                return NotFound("Location not found.");

            return Ok(result.ToString());
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "Failed to get location name");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // Endpoint for EXT locations
    [HttpGet("extlocation")]
    public async Task<ActionResult<List<SelectListItem>>> GetExtLocationsAsync()
    {
        var items = await GetLocationsByFilterAsync("EXT%");
        return Ok(items);
    }

    // Endpoint for RPT locations
    [HttpGet("rptlocation")]
    public async Task<ActionResult<List<SelectListItem>>> GetRptLocationsAsync()
    {
        var items = await GetLocationsByFilterAsync("RPT%");
        return Ok(items);
    }

    // Dynamic helper for location filtering
    private async Task<List<SelectListItem>> GetLocationsByFilterAsync(string clusterOrNamePattern)
    {
        var items = new List<SelectListItem>();
        string qry;
        if (clusterOrNamePattern.StartsWith("EXT"))
            qry = @"SELECT cloc_id, cloc_location_name 
                    FROM ctlg_location_type 
                    WHERE cloc_cluster LIKE :pattern AND cloc_valid = 'Y' 
                    ORDER BY cloc_location_name ASC";
        else
            qry = @"SELECT cloc_id, cloc_location_name 
                    FROM ctlg_location_type 
                    WHERE cloc_location_name LIKE :pattern AND cloc_valid = 'Y' 
                    ORDER BY cloc_location_name ASC";

        await using var conn = _realisFactory.Create();
        await conn.OpenAsync();
        await using var cmd = new OracleCommand(qry, conn);
        cmd.Parameters.Add(new OracleParameter("pattern", clusterOrNamePattern));
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            var item = new SelectListItem();
            int ordinal = rdr.GetOrdinal("cloc_id");
            if (!rdr.IsDBNull(ordinal))
                item.Value = rdr.GetDecimal(ordinal).ToString();
            ordinal = rdr.GetOrdinal("cloc_location_name");
            if (!rdr.IsDBNull(ordinal))
                item.Text = rdr.GetString(ordinal);
            items.Add(item);
        }
        return items;
    }

    private async Task<string> GetLocationNameByLocAsync(string loc)
    {
        if (string.IsNullOrWhiteSpace(loc))
            return string.Empty;

        const string query = "SELECT cloc_location_name FROM ctlg_location_type WHERE cloc_id = :id";
        try
        {
            await using var conn = _realisFactory.Create();
            await conn.OpenAsync();
            await using var cmd = new OracleCommand(query, conn);
            cmd.Parameters.Add(new OracleParameter("id", loc));
            var result = await cmd.ExecuteScalarAsync();
            return result?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
