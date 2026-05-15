using Newtonsoft.Json;

namespace SpeckleUpload.Models;

public sealed class UploadRequest
{
  [JsonProperty("filePath")]
  public string FilePath { get; set; } = string.Empty;

  [JsonProperty("streamId")]
  public string StreamId { get; set; } = string.Empty;

  [JsonProperty("serverUrl")]
  public string ServerUrl { get; set; } = "https://app.speckle.systems";

  [JsonProperty("token")]
  public string Token { get; set; } = string.Empty;

  [JsonProperty("branchName")]
  public string BranchName { get; set; } = "main";

  [JsonProperty("commitMessage")]
  public string? CommitMessage { get; set; }

  [JsonProperty("requestId")]
  public string? RequestId { get; set; }
}
