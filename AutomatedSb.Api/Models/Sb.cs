using System.Text.Json.Serialization;

namespace AutomatedSb.Api.Models;

public class Sb
{
    [JsonPropertyName("sbmappingid")] public string SbMappingId { get; set; } = "";
    [JsonPropertyName("sb")]          public string SbCode      { get; set; } = "";
    [JsonPropertyName("sbname")]      public string SbName      { get; set; } = "";
    [JsonPropertyName("ccid")]        public string CcId        { get; set; } = "";
    [JsonPropertyName("ccname")]      public string CcName      { get; set; } = "";
}

public class SbCreateDto
{
    [JsonPropertyName("sb")]     public string SbCode  { get; set; } = "";
    [JsonPropertyName("sbname")] public string SbName  { get; set; } = "";
    [JsonPropertyName("ccid")]   public string CcId    { get; set; } = "";
    [JsonPropertyName("ccname")] public string CcName  { get; set; } = "";
}
