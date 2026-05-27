using Newtonsoft.Json;

namespace SpeckleUpload.Models;

public sealed class OpenDialogRulesConfig
{
  [JsonProperty("never")]
  public OpenDialogNeverRules Never { get; set; } = new();

  [JsonProperty("rules")]
  public List<OpenDialogRule> Rules { get; set; } = new();

  /// <summary>未命中 rules 时，按顺序尝试的默认按钮（可配置顺序与 click/clickResult）。</summary>
  [JsonProperty("unmatchedFallback")]
  public OpenDialogUnmatchedFallback UnmatchedFallback { get; set; } = new();

  /// <summary>
  /// 仅当 DialogBoxShowing 读不到正文与按钮时的顺序兜底（优先用 rules 与正文/按钮关键词匹配）。
  /// </summary>
  [JsonProperty("docWarnEmptyMessageSequence")]
  public OpenDialogUnmatchedFallback DocWarnEmptyMessageSequence { get; set; } = new();
}

public sealed class OpenDialogUnmatchedFallback
{
  [JsonProperty("enabled")]
  public bool Enabled { get; set; } = true;

  /// <summary>从上到下依次尝试，任一 OverrideResult 成功即停止。</summary>
  [JsonProperty("tryButtons")]
  public List<OpenDialogFallbackButton> TryButtons { get; set; } = new();
}

public sealed class OpenDialogFallbackButton
{
  /// <summary>按钮文案（日志用；也会并入 buttonContains 做可选匹配）。</summary>
  [JsonProperty("label")]
  public string Label { get; set; } = string.Empty;

  [JsonProperty("buttonContains")]
  public List<string> ButtonContains { get; set; } = new();

  [JsonProperty("click")]
  public string Click { get; set; } = "ok";

  [JsonProperty("clickResult")]
  public int? ClickResult { get; set; }
}

public sealed class OpenDialogNeverRules
{
  [JsonProperty("titleContains")]
  public List<string> TitleContains { get; set; } = new();

  [JsonProperty("messageContains")]
  public List<string> MessageContains { get; set; } = new();

  [JsonProperty("dialogIdContains")]
  public List<string> DialogIdContains { get; set; } = new();
}

public sealed class OpenDialogRule
{
  [JsonProperty("name")]
  public string Name { get; set; } = string.Empty;

  /// <summary>标题/摘要区文案，任一命中即可（OR）。可写长句。</summary>
  [JsonProperty("titleContains")]
  public List<string> TitleContains { get; set; } = new();

  [JsonProperty("titleNotContains")]
  public List<string> TitleNotContains { get; set; } = new();

  /// <summary>正文区文案，任一命中即可（OR）。可写长句。</summary>
  [JsonProperty("messageContains")]
  public List<string> MessageContains { get; set; } = new();

  [JsonProperty("messageNotContains")]
  public List<string> MessageNotContains { get; set; } = new();

  [JsonProperty("dialogIdContains")]
  public List<string> DialogIdContains { get; set; } = new();

  /// <summary>期望点击的按钮文案（任一命中即可）。Revit API 常读不到按钮列表时，仅当本规则只有一条 buttonActions 才会代点。</summary>
  [JsonProperty("buttonContains")]
  public List<string> ButtonContains { get; set; } = new();

  /// <summary>按按钮文案映射点击；从上到下取第一个 buttonContains 命中的项。</summary>
  [JsonProperty("buttonActions")]
  public List<OpenDialogButtonAction> ButtonActions { get; set; } = new();

  /// <summary>close | ok | cancel | yes | no | commandLink1..4（无 buttonActions 时使用）</summary>
  [JsonProperty("click")]
  public string Click { get; set; } = "close";

  [JsonProperty("clickResult")]
  public int? ClickResult { get; set; }

  [JsonProperty("dialogTypes")]
  public List<string> DialogTypes { get; set; } = new();
}

public sealed class OpenDialogButtonAction
{
  [JsonProperty("buttonContains")]
  public List<string> ButtonContains { get; set; } = new();

  [JsonProperty("click")]
  public string Click { get; set; } = "ok";

  [JsonProperty("clickResult")]
  public int? ClickResult { get; set; }
}
