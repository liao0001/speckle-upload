using Newtonsoft.Json;

namespace SpeckleUpload.Models;

public sealed class UploadCallbackPayload
{
  [JsonProperty("requestId")]
  public string? RequestId { get; set; }

  [JsonProperty("success")]
  public bool Success { get; set; }

  [JsonProperty("filePath")]
  public string? FilePath { get; set; }

  [JsonProperty("streamId")]
  public string? StreamId { get; set; }

  [JsonProperty("objectId")]
  public string? ObjectId { get; set; }

  [JsonProperty("commitId")]
  public string? CommitId { get; set; }

  [JsonProperty("objectCount")]
  public int ObjectCount { get; set; }

  [JsonProperty("error")]
  public string? Error { get; set; }
}
