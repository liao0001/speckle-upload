namespace SpeckleUpload;

public static class PluginSettings
{
#if REVIT2024
  public const int DefaultHttpPort = 6688;
#else
  public const int DefaultHttpPort = 6687;
#endif

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

  /// <summary>
  /// 默认 false（弹窗由 AHK 等外部脚本处理）。
  /// 设 SPECKLE_UPLOAD_ENABLE_DIALOG_SUPPRESSION=1 时启用插件内置弹窗处理。
  /// </summary>
  public static bool EnableDialogSuppression =>
    string.Equals(
      Environment.GetEnvironmentVariable("SPECKLE_UPLOAD_ENABLE_DIALOG_SUPPRESSION"),
      "1",
      StringComparison.OrdinalIgnoreCase
    )
    || string.Equals(
      Environment.GetEnvironmentVariable("SPECKLE_UPLOAD_ENABLE_DIALOG_SUPPRESSION"),
      "true",
      StringComparison.OrdinalIgnoreCase
    );

  /// <summary>打开 RVT 后自动关弹窗的持续时间（秒）。环境变量 SPECKLE_UPLOAD_OPEN_DIALOG_SUPPRESS_SECONDS。</summary>
  public static int OpenDialogSuppressSeconds
  {
    get
    {
      var value = Environment.GetEnvironmentVariable("SPECKLE_UPLOAD_OPEN_DIALOG_SUPPRESS_SECONDS");
      return int.TryParse(value, out var seconds) && seconds > 0 ? seconds : 120;
    }
  }

  /// <summary>为 true 时，打开阶段内所有可识别的弹窗均尝试自动关闭。SPECKLE_UPLOAD_AUTO_DISMISS_ALL_OPEN_DIALOGS。</summary>
  public static bool AutoDismissAllOpenDialogs =>
    string.Equals(
      Environment.GetEnvironmentVariable("SPECKLE_UPLOAD_AUTO_DISMISS_ALL_OPEN_DIALOGS"),
      "1",
      StringComparison.OrdinalIgnoreCase
    )
    || string.Equals(
      Environment.GetEnvironmentVariable("SPECKLE_UPLOAD_AUTO_DISMISS_ALL_OPEN_DIALOGS"),
      "true",
      StringComparison.OrdinalIgnoreCase
    );
}
