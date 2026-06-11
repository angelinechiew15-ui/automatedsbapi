using AutomatedSb.Api.Data;
using AutomatedSb.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Oracle.ManagedDataAccess.Client;

namespace AutomatedSb.Api.Controllers;

[ApiController]
[Route("api/sb-owners")]
public class SbOwnersController : ControllerBase
{
    private readonly IOracleConnectionFactory _factory;
    private readonly ILogger<SbOwnersController> _logger;

    public SbOwnersController(
        IOracleConnectionFactory factory,
        ILogger<SbOwnersController> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    // GET api/sb-owners - Service Bundle Owner to SB mappings
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Sb2sbowner>>> GetAll()
    {
        try
        {
            const string sql = @"
                SELECT p.cm_matrix_person_id AS sb,
                       p.cm_matrix_person_lastname || ' ' || p.cm_matrix_person_firstname AS persname,
                       s.cm_matrix_sb_id AS sbid,
                       s.cm_matrix_sb_name AS sbname,
                       p.cm_matrix_person_id AS persid
                  FROM cm_matrix_person_to_sb t
                  JOIN cm_matrix_person p ON p.cm_matrix_person_id = t.cm_matrix_person_to_sb_person_id
                  JOIN cm_matrix_sb s ON s.cm_matrix_sb_id = t.cm_matrix_person_to_sb_sb_id
                 WHERE s.cm_matrix_sb_valid = 'Y'
                 ORDER BY persname ASC, sbname ASC";

            var result = new List<Sb2sbowner>();
            await using var conn = _factory.Create();
            await conn.OpenAsync();
            await using var cmd = new OracleCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                result.Add(new Sb2sbowner
                {
                    sb = reader["sbid"]?.ToString() ?? "",
                    sbname = reader["sbname"]?.ToString() ?? "",
                    persid = reader["persid"]?.ToString() ?? "",
                    persname = reader["persname"]?.ToString() ?? ""
                });
            }

            return Ok(result);
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "SbOwners GET failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // POST api/sb-owners/add - Add new service bundle with owner
    [HttpPost("add")]
    public async Task<ActionResult> AddNewServiceBundle(
        [FromQuery] string newServiceBundleName,
        [FromQuery] string newServiceBundleOwner)
    {
        try
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync();

            // Check if service bundle already exists
            const string checkSql = "SELECT COUNT(*) FROM cm_matrix_sb WHERE cm_matrix_sb_name = :sbname";
            await using var checkCmd = new OracleCommand(checkSql, conn);
            checkCmd.Parameters.Add(new OracleParameter("sbname", newServiceBundleName));
            var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());

            if (count > 0)
            {
                return Ok(new { message = "Service bundle already exists!" });
            }

            // Insert new service bundle
            const string insertSbSql = "INSERT INTO cm_matrix_sb (cm_matrix_sb_name, cm_matrix_sb_valid) VALUES (:sbname, 'Y')";
            await using var insertSbCmd = new OracleCommand(insertSbSql, conn);
            insertSbCmd.Parameters.Add(new OracleParameter("sbname", newServiceBundleName));
            await insertSbCmd.ExecuteNonQueryAsync();

            // Get the new SB ID
            const string getIdSql = "SELECT MAX(cm_matrix_sb_id) FROM cm_matrix_sb";
            await using var getIdCmd = new OracleCommand(getIdSql, conn);
            var sbId = Convert.ToInt32(await getIdCmd.ExecuteScalarAsync());

            // Insert person to SB mapping
            const string insertMappingSql = @"
                INSERT INTO cm_matrix_person_to_sb
                (cm_matrix_person_to_sb_sb_id, cm_matrix_person_to_sb_person_id, cm_matrix_person_to_sb_owner)
                VALUES (:sbid, :personid, 'Y')";
            await using var insertMappingCmd = new OracleCommand(insertMappingSql, conn);
            insertMappingCmd.Parameters.Add(new OracleParameter("sbid", sbId));
            insertMappingCmd.Parameters.Add(new OracleParameter("personid", newServiceBundleOwner));
            await insertMappingCmd.ExecuteNonQueryAsync();

            return Ok(new { message = "Service bundle added successfully" });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "AddNewServiceBundle failed");
            return StatusCode(500, new { message = "Error adding service bundle: " + ex.Message });
        }
    }

    // PUT api/sb-owners/owner - Update owner for selected service bundles
    [HttpPut("owner")]
    public async Task<ActionResult> UpdateOwner([FromBody] UpdateOwnerRequest request)
    {
        try
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync();

            foreach (var sbId in request.SbIds)
            {
                const string updateSql = @"
                    UPDATE cm_matrix_person_to_sb
                    SET cm_matrix_person_to_sb_person_id = :personid
                    WHERE cm_matrix_person_to_sb_sb_id = :sbid
                    AND cm_matrix_person_to_sb_owner = 'Y'";
                await using var cmd = new OracleCommand(updateSql, conn);
                cmd.Parameters.Add(new OracleParameter("personid", request.NewPersonId));
                cmd.Parameters.Add(new OracleParameter("sbid", sbId));
                await cmd.ExecuteNonQueryAsync();
            }

            return Ok(new { message = "Owner updated successfully" });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "UpdateOwner failed");
            return StatusCode(500, new { message = "Error updating owner: " + ex.Message });
        }
    }
}

public class UpdateOwnerRequest
{
    public string[] SbIds { get; set; } = Array.Empty<string>();
    public string NewPersonId { get; set; } = "";
    public string? NewPersonName { get; set; }
}
