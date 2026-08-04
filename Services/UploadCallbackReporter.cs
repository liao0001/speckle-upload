using SpeckleUpload.Models;

namespace SpeckleUpload.Services;

/// <summary>上传过程中向 /api/callback 上报 progress、progress_index。</summary>
public sealed class UploadCallbackReporter
{
  private readonly UploadRequest _request;
  private readonly string _callbackUrl;
  private int _convertTotal;

  public UploadCallbackReporter(UploadRequest request)
  {
    _request = request;
    _callbackUrl = string.IsNullOrWhiteSpace(request.CallbackUrl)
      ? PluginSettings.CallbackUrl
      : request.CallbackUrl.Trim();
    PluginLog.Step("Callback", $"status reporter enabled url={_callbackUrl} requestId={request.RequestId}");
  }

  public void ReportOpened()
  {
    Report("打开", 0);
  }

  public void BeginConvert(int totalPhysicalObjects)
  {
    _convertTotal = totalPhysicalObjects;
  }

  public void ReportConvert(int index)
  {
    if (!ShouldReportConvert(index, _convertTotal))
    {
      return;
    }

    Report("解析", index);
  }

  public void ReportUploadStart()
  {
    Report("上传", 1);
  }

  public void ReportUpload(int progressIndex)
  {
    Report("上传", progressIndex);
  }

  public void ApplyFinalProgress(UploadCallbackPayload payload)
  {
    payload.Progress = "完成";
    if (payload.ObjectCount > 0)
    {
      payload.ProgressIndex = payload.ObjectCount;
    }
    else if (!payload.ProgressIndex.HasValue)
    {
      payload.ProgressIndex = 0;
    }
  }

  private void Report(string progress, int progressIndex)
  {
    var payload = new UploadCallbackPayload
    {
      RequestId = _request.RequestId,
      FilePath = _request.FilePath,
      StreamId = _request.StreamId,
      BranchName = string.IsNullOrWhiteSpace(_request.BranchName) ? "main" : _request.BranchName,
      Progress = progress,
      ProgressIndex = progressIndex,
      Success = false,
    };

    PluginLog.Step(
      "Callback",
      $"status report progress={progress} progress_index={progressIndex} requestId={_request.RequestId}"
    );
    CallbackService.SendFireAndForget(payload, _callbackUrl);
  }

  private static bool ShouldReportConvert(int current, int total) =>
    current == 1 || current == total || current % 500 == 0;
}
