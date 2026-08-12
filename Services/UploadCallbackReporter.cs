using SpeckleUpload.Models;

namespace SpeckleUpload.Services;

/// <summary>上传过程中向 /api/callback 上报 progress、progress_index（0–100 百分比）。</summary>
public sealed class UploadCallbackReporter
{
  public const int PercentReceived = 1;
  public const int PercentEnqueued = 5;
  public const int PercentExecute = 6;
  public const int PercentOpened = 9;
  public const int PercentSpeckleStart = 10;
  public const int PercentConvertEnd = 50;
  public const int PercentUploadEnd = 90;
  public const int PercentSendComplete = 91;
  public const int PercentComplete = 100;

  private const int ConvertBase = 10;
  private const int ConvertQuota = 40;
  private const int UploadBase = 50;
  private const int UploadQuota = 40;

  private readonly UploadRequest _request;
  private readonly string _callbackUrl;
  private readonly int _heartbeatSeconds;
  private int _convertTotal;
  private int _uploadDenominator;
  private int _lastUploadReport;
  private int _lastReportedPercent;
  private DateTime _lastProgressReportUtc = DateTime.MinValue;

  public UploadCallbackReporter(UploadRequest request)
  {
    _request = request;
    _callbackUrl = string.IsNullOrWhiteSpace(request.CallbackUrl)
      ? PluginSettings.CallbackUrl
      : request.CallbackUrl.Trim();
    _heartbeatSeconds = PluginSettings.ProgressHeartbeatSeconds;
    PluginLog.Step(
      "Callback",
      $"status reporter enabled url={_callbackUrl} requestId={request.RequestId} heartbeatSeconds={_heartbeatSeconds}"
    );
  }

  public static void ReportPercent(UploadRequest request, string progress, int percent)
  {
    var callbackUrl = string.IsNullOrWhiteSpace(request.CallbackUrl)
      ? PluginSettings.CallbackUrl
      : request.CallbackUrl.Trim();
    SendPercent(request, callbackUrl, progress, percent);
  }

  public void ReportReceived() => Report("接收", PercentReceived);

  public void ReportEnqueued() => Report("入队", PercentEnqueued);

  public void ReportExecute() => Report("执行", PercentExecute);

  public void ReportOpened() => Report("打开", PercentOpened);

  public void ReportSpeckleStart() => Report("准备", PercentSpeckleStart);

  public void BeginConvert(int totalPhysicalObjects)
  {
    _convertTotal = totalPhysicalObjects;
  }

  public void ReportConvert(int index)
  {
    var byCount = ShouldReportConvert(index, _convertTotal);
    var byHeartbeat = IsHeartbeatDue();
    if (!byCount && !byHeartbeat)
    {
      return;
    }

    var percent = ComputeConvertPercent(index, _convertTotal);
    if (byHeartbeat && !byCount)
    {
      PluginLog.Step(
        "Speckle",
        $"convert heartbeat {index}/{_convertTotal} progress_index={percent} (every {_heartbeatSeconds}s)"
      );
    }

    Report("解析", percent);
  }

  public void ReportConvertComplete()
  {
    Report("解析", PercentConvertEnd);
  }

  public void BeginUpload(int convertedCount)
  {
    _uploadDenominator = Math.Max(convertedCount * 10, 1);
    _lastUploadReport = 0;
  }

  public void ReportUploadStart()
  {
    Report("上传", UploadBase);
  }

  public void ReportUpload(int uploaded)
  {
    _uploadDenominator = Math.Max(_uploadDenominator, Math.Max(uploaded, 1));

    var byCount = uploaded <= 1 || uploaded - _lastUploadReport >= 500;
    var byHeartbeat = IsHeartbeatDue();
    if (!byCount && !byHeartbeat)
    {
      return;
    }

    _lastUploadReport = uploaded;
    var percent = ComputeUploadPercent(uploaded, _uploadDenominator);
    if (byHeartbeat && !byCount)
    {
      PluginLog.Step(
        "Speckle",
        $"upload heartbeat uploadedTotal={uploaded} progress_index={percent} (every {_heartbeatSeconds}s)"
      );
    }

    Report("上传", percent);
  }

  public void ReportUploadComplete()
  {
    Report("提交", PercentSendComplete);
  }

  public void FinishUpload(int uploaded)
  {
    if (uploaded <= 0)
    {
      return;
    }

    _uploadDenominator = Math.Max(_uploadDenominator, uploaded);
    Report("上传", PercentUploadEnd);
  }

  public void ApplyFinalProgress(UploadCallbackPayload payload)
  {
    payload.Progress = "完成";
    payload.IsFinal = true;
    payload.ProgressIndex = PercentComplete;
  }

  private void Report(string progress, int percent)
  {
    percent = Math.Max(0, Math.Min(100, percent));
    if (percent < _lastReportedPercent)
    {
      percent = _lastReportedPercent;
    }
    else
    {
      _lastReportedPercent = percent;
    }

    _lastProgressReportUtc = DateTime.UtcNow;
    SendPercent(_request, _callbackUrl, progress, percent);
  }

  private bool IsHeartbeatDue()
  {
    if (_heartbeatSeconds <= 0)
    {
      return false;
    }

    if (_lastProgressReportUtc == DateTime.MinValue)
    {
      return false;
    }

    return (DateTime.UtcNow - _lastProgressReportUtc).TotalSeconds >= _heartbeatSeconds;
  }

  private static void SendPercent(UploadRequest request, string callbackUrl, string progress, int percent)
  {
    var payload = new UploadCallbackPayload
    {
      RequestId = request.RequestId,
      FilePath = request.FilePath,
      StreamId = request.StreamId,
      BranchName = string.IsNullOrWhiteSpace(request.BranchName) ? "main" : request.BranchName,
      Progress = progress,
      ProgressIndex = percent,
      IsFinal = false,
    };

    PluginLog.Step(
      "Callback",
      $"status report progress={progress} progress_index={percent} requestId={request.RequestId}"
    );
    CallbackService.SendFireAndForget(payload, callbackUrl);
  }

  internal static int ComputeConvertPercent(int current, int total)
  {
    if (total <= 0)
    {
      return ConvertBase;
    }

    var percent = ConvertBase + (int)(ConvertQuota * (double)current / total);
    return Math.Min(PercentConvertEnd, percent);
  }

  internal static int ComputeUploadPercent(int uploaded, int total)
  {
    if (total <= 0)
    {
      return UploadBase;
    }

    var percent = UploadBase + (int)(UploadQuota * (double)uploaded / total);
    return Math.Min(PercentUploadEnd, percent);
  }

  private static bool ShouldReportConvert(int current, int total) =>
    current == 1 || current == total || current % 500 == 0;
}
