using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SpeckleUpload;
using SpeckleUpload.Models;
using System.Diagnostics;

namespace SpeckleUpload.Services;

public sealed class UploadEventHandler : IExternalEventHandler
{
  private enum UploadPhase
  {
    None,
    WaitingPostOpenIdle,
  }

  private readonly object _sync = new();
  private UIApplication? _revitApp;
  private ExternalEvent? _externalEvent;
  private UploadWorkItem? _pending;
  private volatile bool _deferredCloseActive;
  private string? _deferredCloseDocumentPath;

  private UploadPhase _phase = UploadPhase.None;
  private UploadWorkItem? _activeItem;
  private UploadCallbackReporter? _activeReporter;
  private Document? _activeDocument;
  private int _remainingPostOpenIdleTicks;

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
      if (_pending != null || _phase != UploadPhase.None)
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
      var pathToClose = _deferredCloseDocumentPath;
      _deferredCloseDocumentPath = null;
      var closeWatch = Stopwatch.StartNew();
      PluginLog.Step(
        "UploadHandler",
        $"OnIdling: deferred close uploaded document begin path=\"{pathToClose ?? "(active)"}\""
      );
      try
      {
        DocumentService.CloseUploadedDocument(_revitApp, pathToClose);
        closeWatch.Stop();
        PluginLog.StepElapsed("UploadHandler", "OnIdling: deferred close uploaded document end", closeWatch.ElapsedMilliseconds);
      }
      catch (Exception ex)
      {
        closeWatch.Stop();
        PluginLog.StepElapsed(
          "UploadHandler",
          $"OnIdling: deferred close exception {ex.GetType().Name} {ex.Message}",
          closeWatch.ElapsedMilliseconds
        );
      }
    }

    if (_phase == UploadPhase.WaitingPostOpenIdle && _revitApp != null)
    {
      _remainingPostOpenIdleTicks--;
      PluginLog.Step(
        "UploadHandler",
        $"OnIdling: post-open idle wait remaining={_remainingPostOpenIdleTicks} requestId={_activeItem?.Request.RequestId ?? "-"}"
      );

      if (_remainingPostOpenIdleTicks > 0)
      {
        return;
      }

      _phase = UploadPhase.None;
      RunSpecklePhase(_revitApp);
      return;
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
    var executeWatch = Stopwatch.StartNew();
    PluginLog.Step("UploadHandler", $"Execute: start requestId={request.RequestId} file={request.FilePath}");

    var reporter = new UploadCallbackReporter(request);
    reporter.ReportExecute();

    try
    {
      var prepareWatch = Stopwatch.StartNew();
      PluginLog.Step("UploadHandler", "Execute: step PrepareDocumentForUpload (open target then close others)");
      var document = DocumentService.PrepareDocumentForUpload(app, request.FilePath);
      prepareWatch.Stop();
      PluginLog.StepElapsed("UploadHandler", "Execute: PrepareDocumentForUpload done", prepareWatch.ElapsedMilliseconds);

      RevitOpenDialogSuppression.CompleteOpenPhase();
      reporter.ReportOpened();

      if (PluginSettings.ImmediateConvertAfterOpen)
      {
        PluginLog.Step("UploadHandler", "Execute: immediate convert after open (legacy mode)");
        RunSpecklePhase(app, item, reporter, document, skipDocumentReady: true);
        executeWatch.Stop();
        PluginLog.StepElapsed(
          "UploadHandler",
          $"Execute: finished requestId={request.RequestId}",
          executeWatch.ElapsedMilliseconds
        );
        return;
      }

      var idleTicks = PluginSettings.PostOpenIdleTicks;
      if (idleTicks > 0)
      {
        _activeItem = item;
        _activeReporter = reporter;
        _activeDocument = document;
        _remainingPostOpenIdleTicks = idleTicks;
        _phase = UploadPhase.WaitingPostOpenIdle;
        PluginLog.Step(
          "UploadHandler",
          $"Execute: document opened; defer Speckle convert until {idleTicks} Idling tick(s) (Revit finishes load/regen)"
        );
        executeWatch.Stop();
        PluginLog.StepElapsed(
          "UploadHandler",
          $"Execute: finished open phase requestId={request.RequestId}",
          executeWatch.ElapsedMilliseconds
        );
        return;
      }

      DocumentService.EnsureDocumentReadyForConversion(app, document);
      RunSpecklePhase(app, item, reporter, document, skipDocumentReady: false);
    }
    catch (Exception ex)
    {
      PluginLog.Step("UploadHandler", $"Execute: failed {ex}");
      var payload = CreateFailurePayload(request, ex.Message);
      FinishUpload(item, reporter, payload, request);
    }
  }

  private void RunSpecklePhase(UIApplication app)
  {
    var item = _activeItem;
    var reporter = _activeReporter;
    var document = _activeDocument;
    _activeItem = null;
    _activeReporter = null;
    _activeDocument = null;

    if (item == null || reporter == null || document == null)
    {
      PluginLog.Step("UploadHandler", "RunSpecklePhase: missing active state, skip");
      return;
    }

    RunSpecklePhase(app, item, reporter, document, skipDocumentReady: false);
  }

  private void RunSpecklePhase(
    UIApplication app,
    UploadWorkItem item,
    UploadCallbackReporter reporter,
    Document document,
    bool skipDocumentReady
  )
  {
    var request = item.Request;
    var phaseWatch = Stopwatch.StartNew();
    PluginLog.Step("UploadHandler", $"RunSpecklePhase: begin requestId={request.RequestId}");

    UploadCallbackPayload payload;
    try
    {
      if (!skipDocumentReady)
      {
        DocumentService.EnsureDocumentReadyForConversion(app, document);
      }
      else
      {
        PluginLog.Step("UploadHandler", "RunSpecklePhase: skip EnsureDocumentReadyForConversion (legacy immediate)");
      }

      var speckleWatch = Stopwatch.StartNew();
      PluginLog.Step("UploadHandler", "RunSpecklePhase: step SpeckleSend");
      payload = SpeckleSendService.SendPhysicalObjects(document, request, reporter);
      speckleWatch.Stop();
      PluginLog.StepElapsed(
        "UploadHandler",
        $"RunSpecklePhase: SpeckleSend OK objectId={payload.ObjectId}",
        speckleWatch.ElapsedMilliseconds
      );
    }
    catch (Exception ex)
    {
      PluginLog.Step("UploadHandler", $"RunSpecklePhase: failed {ex}");
      payload = CreateFailurePayload(request, ex.Message);
    }

    FinishUpload(item, reporter, payload, request);
    phaseWatch.Stop();
    PluginLog.StepElapsed(
      "UploadHandler",
      $"RunSpecklePhase: finished requestId={request.RequestId}",
      phaseWatch.ElapsedMilliseconds
    );
  }

  private static UploadCallbackPayload CreateFailurePayload(UploadRequest request, string error)
  {
    return new UploadCallbackPayload
    {
      RequestId = request.RequestId,
      Success = false,
      FilePath = request.FilePath,
      StreamId = request.StreamId,
      BranchName = string.IsNullOrWhiteSpace(request.BranchName) ? "main" : request.BranchName,
      CommitMessage = request.CommitMessage,
      Error = error,
    };
  }

  private void FinishUpload(
    UploadWorkItem item,
    UploadCallbackReporter reporter,
    UploadCallbackPayload payload,
    UploadRequest request
  )
  {
    try
    {
      reporter.ApplyFinalProgress(payload);
      var callbackWatch = Stopwatch.StartNew();
      PluginLog.Step(
        "UploadHandler",
        $"FinishUpload: FinalCallback (sync is_final=true, before close document, timeout={PluginSettings.CallbackTimeoutSeconds}s)"
      );
      CallbackService.SendAsync(payload, request.CallbackUrl).GetAwaiter().GetResult();
      callbackWatch.Stop();
      PluginLog.StepElapsed(
        "UploadHandler",
        $"FinishUpload: callback OK success={payload.Success}",
        callbackWatch.ElapsedMilliseconds
      );
    }
    catch (Exception ex)
    {
      PluginLog.Step(
        "UploadHandler",
        $"FinishUpload: callback failed ex={ex.GetType().Name} msg={ex.Message}"
      );
      payload.Success = false;
      payload.Error = string.IsNullOrWhiteSpace(payload.Error)
        ? $"Callback failed: {ex.Message}"
        : $"{payload.Error}; callback failed: {ex.Message}";
    }
    finally
    {
      _deferredCloseDocumentPath = request.FilePath;
      _deferredCloseActive = true;
      PluginLog.Step(
        "UploadHandler",
        "FinishUpload: final callback done; scheduled async close uploaded document on next Idling"
      );
    }

    PluginLog.Step(
      "UploadHandler",
      $"FinishUpload: result requestId={request.RequestId} success={payload.Success} error={payload.Error ?? "(none)"} objectId={payload.ObjectId ?? "-"} commitId={payload.CommitId ?? "-"}"
    );

    item.Completion.TrySetResult(payload);
  }

  public string GetName() => "SpeckleUpload Upload Handler";
}
