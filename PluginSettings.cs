namespace SpeckleUpload;

public static class PluginSettings
{
  public const int DefaultHttpPort = 6688;
  public const string DefaultCallbackUrl = "http://127.0.0.1:6689/api/callback";

  public static int HttpPort
  {
    get
    {
      var value = Environment.GetEnvironmentVariable("SPECKLE_UPLOAD_HTTP_PORT");
      return int.TryParse(value, out var port) && port > 0 ? port : DefaultHttpPort;
    }
  }

  public static string CallbackUrl =>
    Environment.GetEnvironmentVariable("SPECKLE_UPLOAD_CALLBACK_URL") ?? DefaultCallbackUrl;
}
