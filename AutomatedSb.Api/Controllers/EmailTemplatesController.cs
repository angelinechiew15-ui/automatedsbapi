using AutomatedSb.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Oracle.ManagedDataAccess.Client;
using System.Text.Json.Serialization;

namespace AutomatedSb.Api.Controllers;

[ApiController]
[Route("api/email-templates")]
public class EmailTemplatesController : ControllerBase
{
    private readonly IOracleConnectionFactory _factory;
    private readonly ILogger<EmailTemplatesController> _logger;

    public EmailTemplatesController(
        IOracleConnectionFactory factory,
        ILogger<EmailTemplatesController> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    // GET api/email-templates/all - Get all email templates
    [HttpGet("all")]
    public async Task<ActionResult> GetAllEmails()
    {
        const string sql = @"
            SELECT cm_matrix_sb_email_horizon AS horizon,
                   cm_matrix_sb_email_type AS emailType,
                   cm_matrix_sb_email_body AS emailBody
            FROM cm_matrix_sb_email";

        try
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync();
            await using var cmd = new OracleCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            var result = new Dictionary<string, Dictionary<string, string>>();
            while (await reader.ReadAsync())
            {
                string horizon = reader["horizon"]?.ToString() ?? "";
                string emailType = reader["emailType"]?.ToString() ?? "";
                string emailBody = reader["emailBody"]?.ToString() ?? "";

                if (!result.ContainsKey(horizon))
                {
                    result[horizon] = new Dictionary<string, string>
                    {
                        { "firstemail", "" },
                        { "reminderemail", "" },
                        { "releaseemail", "" }
                    };
                }

                if (emailType == "FIRST") result[horizon]["firstemail"] = emailBody;
                else if (emailType == "REMINDER") result[horizon]["reminderemail"] = emailBody;
                else if (emailType == "RELEASE") result[horizon]["releaseemail"] = emailBody;
            }

            var list = result.Select(kv => new
            {
                horizon = kv.Key,
                firstemail = kv.Value["firstemail"],
                reminderemail = kv.Value["reminderemail"],
                releaseemail = kv.Value["releaseemail"]
            }).ToList();

            return Ok(list);
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "GetAllEmails failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // GET api/email-templates?horizon=26-06
    [HttpGet]
    public async Task<ActionResult> GetEmails([FromQuery] string horizon)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(horizon))
            return BadRequest(new { message = "Horizon is required" });

        const string sql = @"
            SELECT cm_matrix_sb_email_type AS emailType,
                   cm_matrix_sb_email_body AS emailBody
            FROM cm_matrix_sb_email
            WHERE cm_matrix_sb_email_horizon = :horizon";

        try
        {
            _logger.LogInformation("GetEmails: Creating connection...");
            await using var conn = _factory.Create();

            _logger.LogInformation("GetEmails: Opening connection... ({Elapsed}ms)", sw.ElapsedMilliseconds);
            await conn.OpenAsync();

            _logger.LogInformation("GetEmails: Connection opened ({Elapsed}ms)", sw.ElapsedMilliseconds);
            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            cmd.Parameters.Add("horizon", OracleDbType.Varchar2).Value = horizon;

            _logger.LogInformation("GetEmails: Executing query... ({Elapsed}ms)", sw.ElapsedMilliseconds);
            await using var reader = await cmd.ExecuteReaderAsync();

            string firstemail = "", reminderemail = "", releaseemail = "";
            while (await reader.ReadAsync())
            {
                string emailType = reader["emailType"]?.ToString() ?? "";
                string emailBody = reader["emailBody"]?.ToString() ?? "";

                if (emailType == "FIRST") firstemail = emailBody;
                else if (emailType == "REMINDER") reminderemail = emailBody;
                else if (emailType == "RELEASE") releaseemail = emailBody;
            }

            _logger.LogInformation("GetEmails: Complete ({Elapsed}ms)", sw.ElapsedMilliseconds);
            return Ok(new { firstemail, reminderemail, releaseemail });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "GetEmails failed for horizon {Horizon}", horizon);
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // PUT api/email-templates
    [HttpPut]
    public async Task<ActionResult> UpdateEmailTemplate([FromBody] EmailTemplateDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Horizon))
            return BadRequest(new { success = false, message = "Horizon is required" });

        try
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync();

            await UpdateOrInsertEmail(conn, dto.Horizon, "FIRST", dto.Firstemail);
            await UpdateOrInsertEmail(conn, dto.Horizon, "REMINDER", dto.Reminderemail);
            await UpdateOrInsertEmail(conn, dto.Horizon, "RELEASE", dto.Releaseemail);

            return Ok(new { success = true });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "UpdateEmailTemplate failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    private async Task<string> GetEmailBody(OracleConnection conn, string horizon, string emailType)
    {
        const string sql = @"
            SELECT cm_matrix_sb_email_body
            FROM cm_matrix_sb_email
            WHERE cm_matrix_sb_email_horizon = :horizon
            AND cm_matrix_sb_email_type = :emailType";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add("horizon", OracleDbType.Varchar2).Value = horizon;
        cmd.Parameters.Add("emailType", OracleDbType.Varchar2).Value = emailType;

        var result = await cmd.ExecuteScalarAsync();
        return result?.ToString() ?? "";
    }

    private async Task UpdateOrInsertEmail(OracleConnection conn, string horizon, string mailType, string? emailBody)
    {
        if (string.IsNullOrEmpty(emailBody))
            return;

        // Check if record exists
        const string checkSql = @"
            SELECT COUNT(*) FROM cm_matrix_sb_email
            WHERE cm_matrix_sb_email_horizon = :horizon
            AND cm_matrix_sb_email_type = :mailType";

        await using var checkCmd = new OracleCommand(checkSql, conn) { BindByName = true };
        checkCmd.Parameters.Add("horizon", OracleDbType.Varchar2).Value = horizon;
        checkCmd.Parameters.Add("mailType", OracleDbType.Varchar2).Value = mailType;
        var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());

        if (count > 0)
        {
            // Update existing record
            const string updateSql = @"
                UPDATE cm_matrix_sb_email
                SET cm_matrix_sb_email_body = :emailBody,
                    cm_matrix_sb_email_lastupdate = :lastUpdate
                WHERE cm_matrix_sb_email_horizon = :horizon
                AND cm_matrix_sb_email_type = :mailType";

            await using var updateCmd = new OracleCommand(updateSql, conn) { BindByName = true };
            updateCmd.Parameters.Add("emailBody", OracleDbType.Clob).Value = emailBody;
            updateCmd.Parameters.Add("lastUpdate", OracleDbType.Date).Value = DateTime.Now;
            updateCmd.Parameters.Add("horizon", OracleDbType.Varchar2).Value = horizon;
            updateCmd.Parameters.Add("mailType", OracleDbType.Varchar2).Value = mailType;
            await updateCmd.ExecuteNonQueryAsync();
        }
        else
        {
            // Insert new record
            const string insertSql = @"
                INSERT INTO cm_matrix_sb_email
                (cm_matrix_sb_email_body, cm_matrix_sb_email_type, cm_matrix_sb_email_horizon, cm_matrix_sb_email_lastupdate)
                VALUES (:emailBody, :mailType, :horizon, :lastUpdate)";

            await using var insertCmd = new OracleCommand(insertSql, conn) { BindByName = true };
            insertCmd.Parameters.Add("emailBody", OracleDbType.Clob).Value = emailBody;
            insertCmd.Parameters.Add("mailType", OracleDbType.Varchar2).Value = mailType;
            insertCmd.Parameters.Add("horizon", OracleDbType.Varchar2).Value = horizon;
            insertCmd.Parameters.Add("lastUpdate", OracleDbType.Date).Value = DateTime.Now;
            await insertCmd.ExecuteNonQueryAsync();
        }
    }
}

public class EmailTemplateDto
{
    [JsonPropertyName("horizon")]
    public string Horizon { get; set; } = "";

    [JsonPropertyName("firstemail")]
    public string? Firstemail { get; set; }

    [JsonPropertyName("reminderemail")]
    public string? Reminderemail { get; set; }

    [JsonPropertyName("releaseemail")]
    public string? Releaseemail { get; set; }
}
