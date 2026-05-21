using Newtonsoft.Json;

namespace SpeckleUpload.Models;

public sealed class UploadCallbackPayload
{
  [JsonProperty("request_id")]
  public string? RequestId { get; set; }

  [JsonProperty("success")]
  public bool Success { get; set; }

  [JsonProperty("file_path")]
  public string? FilePath { get; set; }

  [JsonProperty("stream_id")]
  public string? StreamId { get; set; }

  [JsonProperty("branch_name")]
  public string? BranchName { get; set; }

  [JsonProperty("commit_message")]
  public string? CommitMessage { get; set; }

  [JsonProperty("object_id")]
  public string? ObjectId { get; set; }

  [JsonProperty("commit_id")]
  public string? CommitId { get; set; }

  [JsonProperty("object_count")]
  public int ObjectCount { get; set; }

  [JsonProperty("error")]
  public string? Error { get; set; }
}
