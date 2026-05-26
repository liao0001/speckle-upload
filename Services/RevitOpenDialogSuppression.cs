using System.Text;
using Autodesk.Revit.UI.Events;

namespace SpeckleUpload.Services;

/// <summary>
/// 自动打开 RVT（含跨版本升级）时，拦截并关闭阻塞式弹窗，避免无人点击导致上传卡住。
/// </summary>
public static class RevitOpenDialogSuppression
{
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

    if (!ShouldAutoDismiss(message))
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

  private static bool ShouldAutoDismiss(string message)
  {
    if (PluginSettings.AutoDismissAllOpenDialogs)
    {
      return true;
    }

    if (string.IsNullOrWhiteSpace(message))
    {
      return false;
    }

    var text = message.ToLowerInvariant();

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
        // 「关闭」多为 Close；部分对话框唯一按钮映射为 Ok
        task.OverrideResult((int)TaskDialogResult.Close);
        return true;

      case MessageBoxShowingEventArgs messageBox:
        messageBox.OverrideResult(1);
        return true;

      default:
        return false;
    }
  }

  private static string CollectMessage(DialogBoxShowingEventArgs args)
  {
    var sb = new StringBuilder();

    if (args is TaskDialogShowingEventArgs task)
    {
      AppendLine(sb, task.MainInstruction);
      AppendLine(sb, task.MainContent);
    }

    if (args is MessageBoxShowingEventArgs messageBox)
    {
      AppendLine(sb, messageBox.Message);
    }

    return sb.ToString();
  }

  private static void AppendLine(StringBuilder sb, string? value)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      return;
    }

    if (sb.Length > 0)
    {
      sb.Append(' ');
    }

    sb.Append(value.Trim());
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
