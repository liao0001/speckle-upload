using Autodesk.Revit.UI;
using SpeckleUpload;
using SpeckleUpload.Models;

namespace SpeckleUpload.Services;

public sealed class UploadEventHandler : IExternalEventHandler
{
  private readonly object _sync = new();
  private UIApplication? _revitApp;
  private ExternalEvent? _externalEvent;
  private UploadWorkItem? _pending;
  private volatile bool _deferredCloseActive;

  public void Initialize(UIApplication uiApp, ExternalEvent externalEvent)
  {
    _revitApp = uiApp;
    _externalEvent = externalEvent;
    PluginLog.Step("UploadHandler", "Initialize: ExternalEvent bound");
  }

  public UploadEnqueueResult TryEnqueue(UploadWorkItem item)
  {
    var externalEvent = _externalEvent;
    if (externalEvent == null)
    {
      PluginLog.Step("UploadHandler", "TryEnqueue: denied ExternalEvent is null");
      return new UploadEnqueueResult(UploadEnqueueStatus.Denied, "ExternalEvent is not initialized.");
    }

    lock (_sync)
    {
      if (_pending != null)
      {
        PluginLog.Step("UploadHandler", "TryEnqueue: busy another item in queue");
        return new UploadEnqueueResult(UploadEnqueueStatus.Busy);
      }

      _pending = item;
    }

    PluginLog.Step("UploadHandler", $"TryEnqueue: Raise begin requestId={item.Request.RequestId}");
    var raiseStatus = externalEvent.Raise();
    PluginLog.Step("UploadHandler", $"TryEnqueue: ExternalEvent.Raise() => {raiseStatus}, requestId={item.Request.RequestId}");

    if (raiseStatus == ExternalEventRequest.Denied)
    {
      lock (_sync)
      {
        if (_pending == item)
        {
          _pending = null;
        }
      }

      PluginLog.Step("UploadHandler", "TryEnqueue: Denied by Revit");
      return new UploadEnqueueResult(
        UploadEnqueueStatus.Denied,
        "Revit rejected the request (modal dialog or command may be active). Click the Revit window and retry."
      );
    }

    if (raiseStatus == ExternalEventRequest.Pending)
    {
      PluginLog.Step("UploadHandler", "TryEnqueue: Pending (wait for idle / Idling re-raise)");
      return new UploadEnqueueResult(
        UploadEnqueueStatus.Pending,
        "Queued until Revit is idle. Click the Revit window to continue."
      );
    }

    PluginLog.Step("UploadHandler", "TryEnqueue: Accepted");
    return new UploadEnqueueResult(UploadEnqueueStatus.Accepted);
  }

  public void OnIdling()
  {
    if (_deferredCloseActive && _revitApp != null)
    {
      _deferredCloseActive = false;
      PluginLog.Step("UploadHandler", "OnIdling: deferred CloseActiveDocument");
      try
      {
        DocumentService.CloseActiveDocument(_revitApp);
      }
      catch (Exception ex)
      {
        PluginLog.Step("UploadHandler", $"OnIdling: deferred close exception {ex.Message}");
      }
    }

    var externalEvent = _externalEvent;
    if (externalEvent == null || !externalEvent.IsPending)
    {
      return;
    }

    UploadWorkItem? item;
    lock (_sync)
    {
      item = _pending;
    }

    if (item == null)
    {
      PluginLog.Step("UploadHandler", "OnIdling: IsPending but no _pending item (skip)");
      return;
    }

    PluginLog.Step("UploadHandler", $"OnIdling: re-raise for requestId={item.Request.RequestId}");
    var raiseStatus = externalEvent.Raise();
    PluginLog.Step("UploadHandler", $"OnIdling: re-raise => {raiseStatus}");
  }

  public void Execute(UIApplication app)
  {
    _revitApp = app;
    PluginLog.Step("UploadHandler", "Execute: invoked by Revit");
    UploadWorkItem? item;
    lock (_sync)
    {
      item = _pending;
      _pending = null;
    }

    if (item == null)
    {
      PluginLog.Step("UploadHandler", "Execute: no pending work, exit");
      return;
    }

    var request = item.Request;
    PluginLog.Step("UploadHandler", $"Execute: start requestId={request.RequestId} file={request.FilePath}");

    UploadCallbackPayload payload;
    var reporter = new UploadCallbackReporter(request);

    try
    {
      PluginLog.Step("UploadHandler", "Execute: step PrepareDocumentForUpload (open target then close others)");
      var document = DocumentService.PrepareDocumentForUpload(app, request.FilePath);

      RevitOpenDialogSuppression.CompleteOpenPhase();
      reporter.ReportOpened();
      PluginLog.Step("UploadHandler", "Execute: step SpeckleSend (dialog suppression must be off)");
      payload = SpeckleSendService.SendPhysicalObjects(document, request, reporter);

      PluginLog.Step("UploadHandler", $"Execute: SpeckleSend OK objectId={payload.ObjectId}");
    }
    catch (Exception ex)
    {
      PluginLog.Step("UploadHandler", $"Execute: failed {ex}");
      payload = new UploadCallbackPayload
      {
        RequestId = request.RequestId,
        Success = false,
        FilePath = request.FilePath,
        StreamId = request.StreamId,
        BranchName = string.IsNullOrWhiteSpace(request.BranchName) ? "main" : request.BranchName,
        CommitMessage = request.CommitMessage,
        Error = ex.Message,
      };
    }

    try
    {
      reporter.ApplyFinalProgress(payload);
      PluginLog.Step("UploadHandler", "Execute: step Callback");
      CallbackService.SendAsync(payload, request.CallbackUrl).GetAwaiter().GetResult();
      PluginLog.Step("UploadHandler", $"Execute: callback OK success={payload.Success}");
    }
    catch (Exception ex)
    {
      PluginLog.Step("UploadHandler", $"Execute: callback failed {ex}");
      payload.Success = false;
      payload.Error = string.IsNullOrWhiteSpace(payload.Error)
        ? $"Callback failed: {ex.Message}"
        : $"{payload.Error}; callback failed: {ex.Message}";
    }
    finally
    {
      _deferredCloseActive = true;
      PluginLog.Step("UploadHandler", "Execute: scheduled deferred CloseActiveDocument on next Idling");
    }

    PluginLog.Step(
      "UploadHandler",
      $"Execute: result requestId={request.RequestId} success={payload.Success} error={payload.Error ?? "(none)"} objectId={payload.ObjectId ?? "-"} commitId={payload.CommitId ?? "-"}"
    );

    item.Completion.TrySetResult(payload);
    PluginLog.Step("UploadHandler", $"Execute: finished requestId={request.RequestId}");
  }

  public string GetName() => "SpeckleUpload Upload Handler";
}
