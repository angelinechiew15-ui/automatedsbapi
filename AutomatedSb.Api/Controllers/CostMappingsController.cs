using AutomatedSb.Api.Data;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using Oracle.ManagedDataAccess.Client;
using System.Globalization;
using System.Text.Json.Serialization;

namespace AutomatedSb.Api.Controllers;

[ApiController]
[Route("api/cost-mappings")]
public class CostMappingsController : ControllerBase
{
    private readonly IOracleConnectionFactory _factory;
    private readonly ILogger<CostMappingsController> _logger;

    public CostMappingsController(
        IOracleConnectionFactory factory,
        ILogger<CostMappingsController> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    // GET api/cost-mappings
    [HttpGet]
    public async Task<ActionResult> GetAll()
    {
        const string sql = @"
            SELECT cm_matrix_cost_mapping_id AS costmappingid,
                   cm_matrix_cost_mapping_cost_center AS costcenter,
                   cm_matrix_cost_mapping_lab AS rptlab,
                   cm_matrix_cost_mapping_receiver_wbs AS receiverwbs,
                   cm_matrix_cost_mapping_sb_affect AS sbaffected,
                   cm_matrix_cost_mapping_percentage AS percentage,
                   cm_matrix_cost_mapping_cc AS ccaffected,
                   cm_matrix_cost_mapping_cc_percent AS ccpercentage
            FROM cm_matrix_sb_cost_mapping
            ORDER BY cm_matrix_cost_mapping_cost_center, cm_matrix_cost_mapping_sb_affect ASC";

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
                    costmappingid = reader["costmappingid"]?.ToString() ?? "",
                    costcenter = reader["costcenter"]?.ToString() ?? "",
                    rptlab = reader["rptlab"]?.ToString() ?? "",
                    receiverwbs = reader["receiverwbs"]?.ToString() ?? "",
                    sbaffected = reader["sbaffected"]?.ToString() ?? "",
                    percentage = ParseDecimal(reader["percentage"]?.ToString()),
                    ccaffected = reader["ccaffected"]?.ToString() ?? "",
                    ccpercentage = ParseDecimal(reader["ccpercentage"]?.ToString())
                });
            }

            return Ok(result);
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "GetAll cost mappings failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // POST api/cost-mappings
    [HttpPost]
    public async Task<ActionResult> Add([FromBody] CostMappingDto dto)
    {
        const string sql = @"
            INSERT INTO cm_matrix_sb_cost_mapping
            (cm_matrix_cost_mapping_cost_center, cm_matrix_cost_mapping_lab_id, cm_matrix_cost_mapping_lab,
             cm_matrix_cost_mapping_receiver_wbs, cm_matrix_cost_mapping_sb_affect,
             cm_matrix_cost_mapping_percentage, cm_matrix_cost_mapping_cc,
             cm_matrix_cost_mapping_cc_percent)
            VALUES (:costcenter, :rptlabid, :rptlab, :receiverwbs, :sbaffected, :percentage, :ccaffected, :ccpercentage)";

        try
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync();
            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            cmd.Parameters.Add("costcenter", OracleDbType.Varchar2).Value = dto.Costcenter ?? "";
            cmd.Parameters.Add("rptlabid", OracleDbType.Varchar2).Value = dto.Rptlabid ?? "";
            cmd.Parameters.Add("rptlab", OracleDbType.Varchar2).Value = dto.Rptlab ?? "";
            cmd.Parameters.Add("receiverwbs", OracleDbType.Varchar2).Value = dto.Receiverwbs ?? "";
            cmd.Parameters.Add("sbaffected", OracleDbType.Varchar2).Value = dto.Sbaffected ?? "";
            cmd.Parameters.Add("percentage", OracleDbType.Varchar2).Value = dto.Percentage.ToString("0.00", CultureInfo.InvariantCulture);
            cmd.Parameters.Add("ccaffected", OracleDbType.Varchar2).Value = dto.Ccaffected ?? "";
            cmd.Parameters.Add("ccpercentage", OracleDbType.Varchar2).Value = dto.Ccpercentage.HasValue
                ? dto.Ccpercentage.Value.ToString("0.00", CultureInfo.InvariantCulture)
                : (object)DBNull.Value;

            await cmd.ExecuteNonQueryAsync();
            return Ok(new { success = true, message = "Cost mapping added successfully" });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "Add cost mapping failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // PUT api/cost-mappings/{id}
    [HttpPut("{id}")]
    public async Task<ActionResult> Update(string id, [FromBody] CostMappingDto dto)
    {
        if (!int.TryParse(id, out int costMappingId))
            return BadRequest(new { success = false, message = "Invalid ID" });

        const string sql = @"
            UPDATE cm_matrix_sb_cost_mapping
            SET cm_matrix_cost_mapping_cost_center = :costcenter,
                cm_matrix_cost_mapping_lab = :rptlab,
                cm_matrix_cost_mapping_receiver_wbs = :receiverwbs,
                cm_matrix_cost_mapping_sb_affect = :sbaffected,
                cm_matrix_cost_mapping_percentage = :percentage,
                cm_matrix_cost_mapping_cc = :ccaffected,
                cm_matrix_cost_mapping_cc_percent = :ccpercentage
            WHERE cm_matrix_cost_mapping_id = :id";

        try
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync();
            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            cmd.Parameters.Add("costcenter", OracleDbType.Varchar2).Value = dto.Costcenter ?? "";
            cmd.Parameters.Add("rptlab", OracleDbType.Varchar2).Value = dto.Rptlab ?? "";
            cmd.Parameters.Add("receiverwbs", OracleDbType.Varchar2).Value = dto.Receiverwbs ?? "";
            cmd.Parameters.Add("sbaffected", OracleDbType.Varchar2).Value = dto.Sbaffected ?? "";
            cmd.Parameters.Add("percentage", OracleDbType.Varchar2).Value = dto.Percentage.ToString("0.00", CultureInfo.InvariantCulture);
            cmd.Parameters.Add("ccaffected", OracleDbType.Varchar2).Value = dto.Ccaffected ?? "";
            cmd.Parameters.Add("ccpercentage", OracleDbType.Varchar2).Value = dto.Ccpercentage.HasValue
                ? dto.Ccpercentage.Value.ToString("0.00", CultureInfo.InvariantCulture)
                : (object)DBNull.Value;
            cmd.Parameters.Add("id", OracleDbType.Int32).Value = costMappingId;

            await cmd.ExecuteNonQueryAsync();
            return Ok(new { success = true, message = "Cost mapping updated successfully" });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "Update cost mapping failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // DELETE api/cost-mappings/{id}
    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(string id)
    {
        if (!int.TryParse(id, out int costMappingId))
            return BadRequest(new { success = false, message = "Invalid ID" });

        const string sql = "DELETE FROM cm_matrix_sb_cost_mapping WHERE cm_matrix_cost_mapping_id = :id";

        try
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync();
            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            cmd.Parameters.Add("id", OracleDbType.Int32).Value = costMappingId;

            await cmd.ExecuteNonQueryAsync();
            return Ok(new { success = true, message = "Cost mapping deleted successfully" });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "Delete cost mapping failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // POST api/cost-mappings/export
    [HttpPost("export")]
    public IActionResult Export([FromBody] CostMappingExportRequest request)
    {
        var locationFilter = request.LocationFilter;
        var sbNameFilter = request.SbNameFilter;
        var sbCostMappings = request.SbCostMappings;

        if (sbCostMappings != null)
        {
            if (!string.IsNullOrEmpty(locationFilter))
            {
                sbCostMappings = sbCostMappings.Where(m => m.Rptlab == locationFilter).ToList();
            }
            if (!string.IsNullOrEmpty(sbNameFilter))
            {
                sbCostMappings = sbCostMappings.Where(m => m.Sbaffected == sbNameFilter).ToList();
            }
        }

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("CostMappings");
        worksheet.Cell(1, 1).Value = "Cost Center";
        worksheet.Cell(1, 2).Value = "RPT Lab";
        worksheet.Cell(1, 3).Value = "Receiver WBS";
        worksheet.Cell(1, 4).Value = "SB Affected";
        worksheet.Cell(1, 5).Value = "SB Cost %";
        worksheet.Cell(1, 6).Value = "Client Corridor";
        worksheet.Cell(1, 7).Value = "Cost Split %";
        int row = 2;

        if (sbCostMappings != null)
        {
            foreach (var costMapping in sbCostMappings)
            {
                worksheet.Cell(row, 1).Value = costMapping.Costcenter;
                worksheet.Cell(row, 2).Value = costMapping.Rptlab;
                worksheet.Cell(row, 3).Value = costMapping.Receiverwbs;
                worksheet.Cell(row, 4).Value = costMapping.Sbaffected;
                worksheet.Cell(row, 5).Value = costMapping.Percentage;
                worksheet.Cell(row, 6).Value = costMapping.Ccaffected;
                worksheet.Cell(row, 7).Value = costMapping.Ccpercentage;
                row++;
            }
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        var bytes = stream.ToArray();
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "CostMappings.xlsx");
    }

    private static decimal? ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : null;
    }
}

public class CostMappingDto
{
    [JsonPropertyName("costcenter")]
    public string? Costcenter { get; set; }

    [JsonPropertyName("rptlabid")]
    public string? Rptlabid { get; set; }

    [JsonPropertyName("rptlab")]
    public string? Rptlab { get; set; }

    [JsonPropertyName("receiverwbs")]
    public string? Receiverwbs { get; set; }

    [JsonPropertyName("sbaffected")]
    public string? Sbaffected { get; set; }

    [JsonPropertyName("percentage")]
    public decimal Percentage { get; set; }

    [JsonPropertyName("ccaffected")]
    public string? Ccaffected { get; set; }

    [JsonPropertyName("ccpercentage")]
    public decimal? Ccpercentage { get; set; }
}

public class CostMappingExportRequest
{
    [JsonPropertyName("locationFilter")]
    public string? LocationFilter { get; set; }

    [JsonPropertyName("sbNameFilter")]
    public string? SbNameFilter { get; set; }

    [JsonPropertyName("sbCostMappings")]
    public List<CostMappingExportItem>? SbCostMappings { get; set; }
}

public class CostMappingExportItem
{
    [JsonPropertyName("costcenter")]
    public string? Costcenter { get; set; }

    [JsonPropertyName("rptlab")]
    public string? Rptlab { get; set; }

    [JsonPropertyName("receiverwbs")]
    public string? Receiverwbs { get; set; }

    [JsonPropertyName("sbaffected")]
    public string? Sbaffected { get; set; }

    [JsonPropertyName("percentage")]
    public decimal Percentage { get; set; }

    [JsonPropertyName("ccaffected")]
    public string? Ccaffected { get; set; }

    [JsonPropertyName("ccpercentage")]
    public decimal? Ccpercentage { get; set; }
}
