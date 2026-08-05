using Newtonsoft.Json;

namespace SpeckleUpload.Models;

public sealed class UploadCallbackPayload
{
  [JsonProperty("request_id")]
  public string? RequestId { get; set; }

  [JsonProperty("success")]
  public bool? Success { get; set; }

  /// <summary>是否为最终结果回调。进度上报为 false，仅最终一次为 true。</summary>
  [JsonProperty("is_final")]
  public bool? IsFinal { get; set; }

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

  /// <summary>阶段文本：打开 / 解析 / 上传 / 完成。</summary>
  [JsonProperty("progress")]
  public string? Progress { get; set; }

  [JsonProperty("progress_index")]
  public int? ProgressIndex { get; set; }
}
