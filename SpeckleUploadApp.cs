using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using SpeckleUpload.Http;
using SpeckleUpload.Services;

namespace SpeckleUpload;

public class SpeckleUploadApp : IExternalApplication
{
  private HttpUploadServer? _server;
  private UploadEventHandler? _handler;
  private ExternalEvent? _externalEvent;

  public Result OnStartup(UIControlledApplication application)
  {
    application.ControlledApplication.ApplicationInitialized += OnApplicationInitialized;
    return Result.Succeeded;
  }

  public Result OnShutdown(UIControlledApplication application)
  {
    application.ControlledApplication.ApplicationInitialized -= OnApplicationInitialized;
    _server?.Dispose();
    _server = null;
    return Result.Succeeded;
  }

  private void OnApplicationInitialized(object sender, ApplicationInitializedEventArgs e)
  {
    var app = (Autodesk.Revit.ApplicationServices.Application)sender;
    var uiApp = new UIApplication(app);

    _handler = new UploadEventHandler(uiApp);
    _externalEvent = ExternalEvent.Create(_handler);
    _server = new HttpUploadServer(_handler, _externalEvent);
    _server.Start();
  }
}
