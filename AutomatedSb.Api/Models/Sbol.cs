using System.Text.Json.Serialization;

namespace AutomatedSb.Api.Models;

public class Sbol
{
    [JsonPropertyName("mapid")]   public string MapId { get; set; } = "";
    [JsonPropertyName("ethloc")]  public string EthLoc { get; set; } = "";
    [JsonPropertyName("rptloc")]  public string RptLoc { get; set; } = "";
    [JsonPropertyName("tstrue")]  public string TsTrue { get; set; } = "";
    [JsonPropertyName("rtutrue")] public string RtuTrue { get; set; } = "";
}

public class SbolCreateDto
{
    [JsonPropertyName("ethloc")]  public string EthLoc { get; set; } = "";
    [JsonPropertyName("rptloc")]  public string RptLoc { get; set; } = "";
    [JsonPropertyName("tstrue")]  public string? TsTrue { get; set; }
    [JsonPropertyName("rtutrue")] public string? RtuTrue { get; set; }
}

public class DeleteIdsDto
{
    [JsonPropertyName("ids")]
    public List<string> Ids { get; set; } = new();
}
