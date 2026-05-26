using Autodesk.Revit.UI.Events;

namespace SpeckleUpload.Services;

/// <summary>
/// 自动打开 RVT（含跨版本升级）时，拦截并关闭阻塞式弹窗，避免无人点击导致上传卡住。
/// </summary>
public static class RevitOpenDialogSuppression
{
  // Revit TaskDialogResult 在部分 API 包中为 internal，使用与官方枚举一致的整型值
  private const int TaskDialogClose = 8;
  private const int MessageBoxOk = 1;

  private static DateTime _armedUntilUtc = DateTime.MinValue;

  public static void ArmForOpen(TimeSpan? duration = null)
  {
    var seconds = PluginSettings.OpenDialogSuppressSeconds;
    _armedUntilUtc = DateTime.UtcNow.Add(duration ?? TimeSpan.FromSeconds(seconds));
    PluginLog.Step("Doc", $"OpenDialogSuppression: armed for {seconds}s");
  }

  public static void Disarm()
  {
    _armedUntilUtc = DateTime.MinValue;
  }

  public static bool IsArmed => DateTime.UtcNow < _armedUntilUtc;

  public static void Handle(DialogBoxShowingEventArgs args)
  {
    if (!IsArmed)
    {
      return;
    }

    var message = CollectMessage(args);
    PluginLog.Step(
      "Doc",
      $"DialogBoxShowing: id={args.DialogId} type={args.GetType().Name} text={Truncate(message, 500)}"
    );

    if (!ShouldAutoDismiss(args, message))
    {
      return;
    }

    if (TryDismiss(args))
    {
      PluginLog.Step("Doc", "DialogBoxShowing: auto-dismissed");
    }
    else
    {
      PluginLog.Step("Doc", "DialogBoxShowing: matched but could not override (unknown dialog type)");
    }
  }

  private static bool ShouldAutoDismiss(DialogBoxShowingEventArgs args, string message)
  {
    if (PluginSettings.AutoDismissAllOpenDialogs)
    {
      return true;
    }

    var text = $"{message} {args.DialogId}".ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(text))
    {
      return false;
    }

    // 跨版本升级后常见的「图元不兼容 / 无法复制」类提示
    string[] keywords =
    [
      "不兼容",
      "不兼容的",
      "无法复制",
      "不能复制",
      "未能复制",
      "丢失",
      "incompatib",
      "could not be copied",
      "cannot copy",
      "not copied",
      "lost element",
      "upgrade",
      "升级",
    ];

    return keywords.Any(keyword => text.Contains(keyword, StringComparison.Ordinal));
  }

  private static bool TryDismiss(DialogBoxShowingEventArgs args)
  {
    switch (args)
    {
      case TaskDialogShowingEventArgs task:
        // 「关闭」多为 Close(8)；部分对话框唯一按钮映射为 Ok(1)
        task.OverrideResult(TaskDialogClose);
        return true;

      case MessageBoxShowingEventArgs messageBox:
        messageBox.OverrideResult(MessageBoxOk);
        return true;

      default:
        return false;
    }
  }

  private static string CollectMessage(DialogBoxShowingEventArgs args)
  {
    switch (args)
    {
      case TaskDialogShowingEventArgs task:
        return task.Message ?? string.Empty;
      case MessageBoxShowingEventArgs messageBox:
        return messageBox.Message ?? string.Empty;
      default:
        return string.Empty;
    }
  }

  private static string Truncate(string value, int maxLen)
  {
    if (string.IsNullOrEmpty(value) || value.Length <= maxLen)
    {
      return value;
    }

    return value.Substring(0, maxLen) + "...";
  }
}
