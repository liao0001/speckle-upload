using Autodesk.Revit.UI;
using SpeckleUpload.Models;

namespace SpeckleUpload.Services;

public sealed class UploadWorkItem
{
  public UploadWorkItem(UploadRequest request, ExternalEvent externalEvent)
  {
    Request = request;
    ExternalEvent = externalEvent;
  }

  public UploadRequest Request { get; }
  public ExternalEvent ExternalEvent { get; }
  public TaskCompletionSource<UploadCallbackPayload> Completion { get; } = new(
    TaskCreationOptions.RunContinuationsAsynchronously
  );
}

public sealed class UploadEventHandler : IExternalEventHandler
{
  private readonly UIApplication _uiApp;
  private readonly object _sync = new();
  private UploadWorkItem? _pending;

  public UploadEventHandler(UIApplication uiApp)
  {
    _uiApp = uiApp;
  }

  public bool TryEnqueue(UploadWorkItem item)
  {
    lock (_sync)
    {
      if (_pending != null)
      {
        return false;
      }

      _pending = item;
    }

    item.ExternalEvent.Raise();
    return true;
  }

  public void Execute(UIApplication app)
  {
    UploadWorkItem? item;
    lock (_sync)
    {
      item = _pending;
      _pending = null;
    }

    if (item == null)
    {
      return;
    }

    var request = item.Request;
    UploadCallbackPayload payload;

    try
    {
      DocumentService.CloseAllDocuments(_uiApp);
      var document = DocumentService.OpenDocument(_uiApp, request.FilePath);
      payload = SpeckleSendService
        .SendPhysicalObjectsAsync(document, request)
        .GetAwaiter()
        .GetResult();
    }
    catch (Exception ex)
    {
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
      CallbackService.SendAsync(payload).GetAwaiter().GetResult();
    }
    catch (Exception ex)
    {
      payload.Success = false;
      payload.Error = string.IsNullOrWhiteSpace(payload.Error)
        ? $"Callback failed: {ex.Message}"
        : $"{payload.Error}; callback failed: {ex.Message}";
    }
    finally
    {
      try
      {
        DocumentService.CloseActiveDocument(_uiApp);
      }
      catch
      {
        // Ignore close failures after callback.
      }
    }

    item.Completion.TrySetResult(payload);
  }

  public string GetName() => "SpeckleUpload Upload Handler";
}
