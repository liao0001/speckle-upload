using Autodesk.Revit.UI;
using SpeckleUpload.Models;

namespace SpeckleUpload.Services;

public sealed class UploadEventHandler : IExternalEventHandler
{
  private readonly object _sync = new();
  private ExternalEvent? _externalEvent;
  private UploadWorkItem? _pending;

  public void Initialize(UIApplication uiApp, ExternalEvent externalEvent)
  {
    _ = uiApp;
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

    try
    {
      PluginLog.Step("UploadHandler", "Execute: step CloseAllDocuments");
      DocumentService.CloseAllDocuments(app);

      PluginLog.Step("UploadHandler", "Execute: step OpenDocument");
      var document = DocumentService.OpenDocument(app, request.FilePath);

      PluginLog.Step("UploadHandler", "Execute: step SpeckleSend");
      payload = SpeckleSendService
        .SendPhysicalObjectsAsync(document, request)
        .GetAwaiter()
        .GetResult();

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
        Error = ex.Message,
      };
    }

    try
    {
      PluginLog.Step("UploadHandler", "Execute: step Callback");
      CallbackService.SendAsync(payload).GetAwaiter().GetResult();
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
      try
      {
        PluginLog.Step("UploadHandler", "Execute: step CloseActiveDocument");
        DocumentService.CloseActiveDocument(app);
        PluginLog.Step("UploadHandler", "Execute: CloseActiveDocument done");
      }
      catch (Exception ex)
      {
        PluginLog.Step("UploadHandler", $"Execute: CloseActiveDocument error {ex.Message}");
      }
    }

    item.Completion.TrySetResult(payload);
    PluginLog.Step("UploadHandler", $"Execute: finished requestId={request.RequestId}");
  }

  public string GetName() => "SpeckleUpload Upload Handler";
}
