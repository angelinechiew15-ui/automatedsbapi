using AutomatedSb.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Oracle.ManagedDataAccess.Client;
using System.Globalization;
using System.Text.Json.Serialization;

namespace AutomatedSb.Api.Controllers;

[ApiController]
[Route("api/engovh")]
public class EngovhController : ControllerBase
{
    private readonly IOracleConnectionFactory _factory;
    private readonly ILogger<EngovhController> _logger;

    public EngovhController(
        IOracleConnectionFactory factory,
        ILogger<EngovhController> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    // GET api/engovh
    [HttpGet]
    public async Task<ActionResult> GetAll()
    {
        const string sql = @"
            SELECT cm_matrix_sb_engovh_id AS engovhid,
                   cm_matrix_sb_engovh_lab AS loc,
                   cm_matrix_sb_engovh_cc AS cc,
                   cm_matrix_sb_engovh_value AS val,
                   cm_matrix_sb_engovh_fy AS fy
            FROM cm_matrix_sb_eng_ovh
            ORDER BY cm_matrix_sb_engovh_lab, cm_matrix_sb_engovh_cc, cm_matrix_sb_engovh_fy ASC";

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
                    engovhid = reader["engovhid"]?.ToString() ?? "",
                    loc = reader["loc"]?.ToString() ?? "",
                    cc = reader["cc"]?.ToString() ?? "",
                    val = reader["val"]?.ToString() ?? "",
                    fy = reader["fy"]?.ToString() ?? ""
                });
            }

            return Ok(result);
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "GetAll eng ovh failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // POST api/engovh
    [HttpPost]
    public async Task<ActionResult> Add([FromBody] EngovhDto dto)
    {
        const string sql = @"
            INSERT INTO cm_matrix_sb_eng_ovh
            (cm_matrix_sb_engovh_lab, cm_matrix_sb_engovh_cc, cm_matrix_sb_engovh_value, cm_matrix_sb_engovh_fy)
            VALUES (:loc, :cc, :val, :fy)";

        try
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync();
            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            cmd.Parameters.Add("loc", OracleDbType.Varchar2).Value = dto.Loc ?? "";
            cmd.Parameters.Add("cc", OracleDbType.Varchar2).Value = dto.Cc ?? "";
            cmd.Parameters.Add("val", OracleDbType.Varchar2).Value = dto.Val?.ToString(CultureInfo.InvariantCulture) ?? "";
            cmd.Parameters.Add("fy", OracleDbType.Varchar2).Value = dto.Fy ?? "";

            await cmd.ExecuteNonQueryAsync();
            return Ok(new { success = true, message = "Eng OVH added successfully" });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "Add eng ovh failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // PUT api/engovh/{id}
    [HttpPut("{id}")]
    public async Task<ActionResult> Update(string id, [FromBody] EngovhDto dto)
    {
        if (!int.TryParse(id, out int engovhId))
            return BadRequest(new { success = false, message = "Invalid ID" });

        const string sql = @"
            UPDATE cm_matrix_sb_eng_ovh
            SET cm_matrix_sb_engovh_lab = :loc,
                cm_matrix_sb_engovh_cc = :cc,
                cm_matrix_sb_engovh_value = :val,
                cm_matrix_sb_engovh_fy = :fy
            WHERE cm_matrix_sb_engovh_id = :id";

        try
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync();
            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            cmd.Parameters.Add("loc", OracleDbType.Varchar2).Value = dto.Loc ?? "";
            cmd.Parameters.Add("cc", OracleDbType.Varchar2).Value = dto.Cc ?? "";
            cmd.Parameters.Add("val", OracleDbType.Varchar2).Value = dto.Val?.ToString(CultureInfo.InvariantCulture) ?? "";
            cmd.Parameters.Add("fy", OracleDbType.Varchar2).Value = dto.Fy ?? "";
            cmd.Parameters.Add("id", OracleDbType.Int32).Value = engovhId;

            await cmd.ExecuteNonQueryAsync();
            return Ok(new { success = true, message = "Eng OVH updated successfully" });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "Update eng ovh failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // POST api/engovh/delete
    [HttpPost("delete")]
    public async Task<ActionResult> Delete([FromBody] EngovhDeleteDto dto)
    {
        if (dto.Ids == null || dto.Ids.Length == 0)
            return BadRequest(new { success = false, message = "No IDs provided" });

        try
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync();

            foreach (var idStr in dto.Ids)
            {
                if (!int.TryParse(idStr, out int id)) continue;

                const string sql = "DELETE FROM cm_matrix_sb_eng_ovh WHERE cm_matrix_sb_engovh_id = :id";
                await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
                cmd.Parameters.Add("id", OracleDbType.Int32).Value = id;
                await cmd.ExecuteNonQueryAsync();
            }

            return Ok(new { success = true, message = "Eng OVH deleted successfully" });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "Delete eng ovh failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }
}

public class EngovhDto
{
    [JsonPropertyName("loc")]
    public string? Loc { get; set; }

    [JsonPropertyName("cc")]
    public string? Cc { get; set; }

    [JsonPropertyName("val")]
    public decimal? Val { get; set; }

    [JsonPropertyName("fy")]
    public string? Fy { get; set; }
}

public class EngovhDeleteDto
{
    [JsonPropertyName("ids")]
    public string[]? Ids { get; set; }
}
