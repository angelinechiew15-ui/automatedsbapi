using AutomatedSb.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Oracle.ManagedDataAccess.Client;
using System.Text.Json.Serialization;

namespace AutomatedSb.Api.Controllers;

[ApiController]
[Route("api/sb-paths")]
public class SbPathsController : ControllerBase
{
    private readonly IOracleConnectionFactory _factory;
    private readonly ILogger<SbPathsController> _logger;

    public SbPathsController(
        IOracleConnectionFactory factory,
        ILogger<SbPathsController> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    // GET api/sb-paths - Get all paths
    [HttpGet]
    public async Task<ActionResult> GetAllPaths()
    {
        const string sql = @"
            SELECT cm_matrix_sb_att_sb_id AS sbId,
                   cm_matrix_sb_att_local_path AS localPath,
                   cm_matrix_sb_att_ishare_path AS iSharePath
              FROM cm_matrix_sb_attachment";

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
                    sbId = reader["sbId"]?.ToString() ?? "",
                    localPath = reader["localPath"]?.ToString() ?? "",
                    iSharePath = reader["iSharePath"]?.ToString() ?? ""
                });
            }

            return Ok(result);
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "GetAllPaths failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // GET api/sb-paths/{sbId}
    [HttpGet("{sbId}")]
    public async Task<ActionResult> GetPath(string sbId)
    {
        if (!int.TryParse(sbId, out int serviceBundleId))
            return BadRequest(new { message = "Invalid service bundle ID" });

        const string sql = @"
            SELECT cm_matrix_sb_att_local_path AS localPath,
                   cm_matrix_sb_att_ishare_path AS iSharePath
              FROM cm_matrix_sb_attachment
             WHERE cm_matrix_sb_att_sb_id = :sbId";

        try
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync();
            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            cmd.Parameters.Add("sbId", OracleDbType.Int32).Value = serviceBundleId;
            await using var reader = await cmd.ExecuteReaderAsync();

            string localPath = "";
            string iSharePath = "";

            if (await reader.ReadAsync())
            {
                localPath = reader["localPath"]?.ToString() ?? "";
                iSharePath = reader["iSharePath"]?.ToString() ?? "";
            }

            return Ok(new { localPath, iSharePath });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "GetPath failed for sbId {SbId}", sbId);
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // PUT api/sb-paths
    [HttpPut]
    public async Task<ActionResult> UpdatePath([FromBody] SbPathUpdateDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Selectednewsb))
            return BadRequest(new { success = false, message = "Service bundle ID is required" });

        if (!int.TryParse(dto.Selectednewsb, out int serviceBundleId))
            return BadRequest(new { success = false, message = "Invalid service bundle ID" });

        try
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync();

            // Check if record exists
            const string checkSql = "SELECT COUNT(*) FROM cm_matrix_sb_attachment WHERE cm_matrix_sb_att_sb_id = :sbId";
            await using var checkCmd = new OracleCommand(checkSql, conn) { BindByName = true };
            checkCmd.Parameters.Add("sbId", OracleDbType.Int32).Value = serviceBundleId;
            var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());

            if (count > 0)
            {
                // Update existing record
                const string updateSql = @"
                    UPDATE cm_matrix_sb_attachment
                    SET cm_matrix_sb_att_local_path = :localPath,
                        cm_matrix_sb_att_ishare_path = :iSharePath,
                        cm_matrix_sb_att_lastupdate = :lastUpdate
                    WHERE cm_matrix_sb_att_sb_id = :sbId";

                await using var updateCmd = new OracleCommand(updateSql, conn) { BindByName = true };
                updateCmd.Parameters.Add("localPath", OracleDbType.Varchar2).Value = dto.LocalPath ?? "";
                updateCmd.Parameters.Add("iSharePath", OracleDbType.Varchar2).Value = dto.ISharePath ?? "";
                updateCmd.Parameters.Add("lastUpdate", OracleDbType.Date).Value = DateTime.Now;
                updateCmd.Parameters.Add("sbId", OracleDbType.Int32).Value = serviceBundleId;
                await updateCmd.ExecuteNonQueryAsync();
            }
            else
            {
                // Insert new record
                const string insertSql = @"
                    INSERT INTO cm_matrix_sb_attachment
                    (cm_matrix_sb_att_sb_id, cm_matrix_sb_att_local_path, cm_matrix_sb_att_ishare_path, cm_matrix_sb_att_lastupdate)
                    VALUES (:sbId, :localPath, :iSharePath, :lastUpdate)";

                await using var insertCmd = new OracleCommand(insertSql, conn) { BindByName = true };
                insertCmd.Parameters.Add("sbId", OracleDbType.Int32).Value = serviceBundleId;
                insertCmd.Parameters.Add("localPath", OracleDbType.Varchar2).Value = dto.LocalPath ?? "";
                insertCmd.Parameters.Add("iSharePath", OracleDbType.Varchar2).Value = dto.ISharePath ?? "";
                insertCmd.Parameters.Add("lastUpdate", OracleDbType.Date).Value = DateTime.Now;
                await insertCmd.ExecuteNonQueryAsync();
            }

            return Ok(new { success = true });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "UpdatePath failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }
}

public class SbPathUpdateDto
{
    [JsonPropertyName("selectednewsb")]
    public string Selectednewsb { get; set; } = "";

    [JsonPropertyName("localPath")]
    public string? LocalPath { get; set; }

    [JsonPropertyName("iSharePath")]
    public string? ISharePath { get; set; }
}
