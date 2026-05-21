using Newtonsoft.Json;

namespace SpeckleUpload.Models;

/// <summary>speckle_sync 等 lwhale 服务的标准 JSON 响应（ret / msg / error）。</summary>
public sealed class LwhaleResponse
{
  [JsonProperty("ret")]
  public int Ret { get; set; }

  [JsonProperty("error")]
  public string? Error { get; set; }

  [JsonProperty("msg")]
  public object? Msg { get; set; }

  public bool IsSuccess => Ret == 0;
}
