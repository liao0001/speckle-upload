using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using SpeckleUpload.Http;
using SpeckleUpload.Services;

namespace SpeckleUpload;

public class SpeckleUploadApp : IExternalApplication
{
  private HttpUploadServer? _server;
  private UploadEventHandler? _handler;
  private ExternalEvent? _externalEvent;
  private UIApplication? _uiApp;

  public Result OnStartup(UIControlledApplication application)
  {
    PluginLog.EnsureInitialized();
    PluginLog.Step("App", "OnStartup: register ApplicationInitialized");
    application.ControlledApplication.ApplicationInitialized += OnApplicationInitialized;
    return Result.Succeeded;
  }

  public Result OnShutdown(UIControlledApplication application)
  {
    PluginLog.Step("App", "OnShutdown: begin");
    application.ControlledApplication.ApplicationInitialized -= OnApplicationInitialized;

    if (_uiApp != null)
    {
      PluginLog.Step("App", "OnShutdown: unregister Idling / DialogBoxShowing");
      _uiApp.Idling -= OnIdling;
      _uiApp.DialogBoxShowing -= OnDialogBoxShowing;
    }

    PluginLog.Step("App", "OnShutdown: dispose HTTP server");
    _server?.Dispose();
    _server = null;
    PluginLog.Step("App", "OnShutdown: end");
    return Result.Succeeded;
  }

  private void OnApplicationInitialized(object sender, ApplicationInitializedEventArgs e)
  {
    PluginLog.Step("App", "OnApplicationInitialized: begin");
    var app = (Autodesk.Revit.ApplicationServices.Application)sender;
    _uiApp = new UIApplication(app);

    _handler = new UploadEventHandler();
    _externalEvent = ExternalEvent.Create(_handler);
    _handler.Initialize(_uiApp, _externalEvent);

    PluginLog.Step("App", "OnApplicationInitialized: Idling subscribed");
    _uiApp.Idling += OnIdling;
    _uiApp.DialogBoxShowing += OnDialogBoxShowing;

    _server = new HttpUploadServer(_handler);
    _server.Start();

    var asm = Assembly.GetExecutingAssembly();
    var asmVersion = asm.GetName().Version?.ToString() ?? "unknown";
    var asmTime = File.GetLastWriteTime(asm.Location).ToString("yyyy-MM-dd HH:mm:ss");
    PluginLog.Step("App", $"SpeckleUpload assembly version={asmVersion} fileTime={asmTime}");
    PluginLog.Step(
      "App",
      $"OnApplicationInitialized: HTTP started port={PluginSettings.HttpPort} log={PluginLog.LogFilePath}"
    );
  }

  private void OnIdling(object? sender, IdlingEventArgs e)
  {
    _handler?.OnIdling();
  }

  private static void OnDialogBoxShowing(object sender, DialogBoxShowingEventArgs e)
  {
    RevitOpenDialogSuppression.Handle(e);
  }
}
