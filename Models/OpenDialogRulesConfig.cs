using Newtonsoft.Json;

namespace SpeckleUpload.Models;

public sealed class OpenDialogRulesConfig
{
  [JsonProperty("never")]
  public OpenDialogNeverRules Never { get; set; } = new();

  [JsonProperty("rules")]
  public List<OpenDialogRule> Rules { get; set; } = new();
}

public sealed class OpenDialogNeverRules
{
  [JsonProperty("messageContains")]
  public List<string> MessageContains { get; set; } = new();

  [JsonProperty("dialogIdContains")]
  public List<string> DialogIdContains { get; set; } = new();
}

public sealed class OpenDialogRule
{
  [JsonProperty("name")]
  public string Name { get; set; } = string.Empty;

  [JsonProperty("messageContains")]
  public List<string> MessageContains { get; set; } = new();

  [JsonProperty("messageNotContains")]
  public List<string> MessageNotContains { get; set; } = new();

  [JsonProperty("dialogIdContains")]
  public List<string> DialogIdContains { get; set; } = new();

  /// <summary>close | ok | cancel | yes | no | commandLink1..4；或配合 clickResult 整型</summary>
  [JsonProperty("click")]
  public string Click { get; set; } = "close";

  /// <summary>直接指定 OverrideResult 整型（优先于 click）</summary>
  [JsonProperty("clickResult")]
  public int? ClickResult { get; set; }

  [JsonProperty("dialogTypes")]
  public List<string> DialogTypes { get; set; } = new();
}
