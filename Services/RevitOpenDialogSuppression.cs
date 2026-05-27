using Autodesk.Revit.UI.Events;
using SpeckleUpload.Models;

namespace SpeckleUpload.Services;

/// <summary>
/// 打开 RVT 期间按 SpeckleUpload.open-dialog-rules.json 规则自动处理弹窗。
/// 「取消升级」等 never 规则永不代点；其余按文案/DialogId 匹配后点击 close/ok 等。
/// </summary>
public static class RevitOpenDialogSuppression
{
  private const int TaskDialogOk = 1;
  private const int TaskDialogCancel = 2;
  private const int TaskDialogYes = 6;
  private const int TaskDialogNo = 7;
  private const int TaskDialogClose = 8;
  private const int TaskDialogCommandLink1 = 1001;
  private const int TaskDialogCommandLink2 = 1002;
  private const int MessageBoxOk = 1;
  private const int MessageBoxCancel = 2;

  private static DateTime _armedUntilUtc = DateTime.MinValue;

  public static void ArmForOpen(TimeSpan? duration = null)
  {
    var seconds = PluginSettings.OpenDialogSuppressSeconds;
    _armedUntilUtc = DateTime.UtcNow.Add(duration ?? TimeSpan.FromSeconds(seconds));
    OpenDialogRulesLoader.Load();
    PluginLog.Step("Doc", $"OpenDialogSuppression: armed for {seconds}s");
  }

  public static void Disarm() => _armedUntilUtc = DateTime.MinValue;

  public static bool IsArmed => DateTime.UtcNow < _armedUntilUtc;

  public static void Handle(DialogBoxShowingEventArgs args)
  {
    if (!IsArmed)
    {
      return;
    }

    var message = CollectMessage(args);
    var dialogId = args.DialogId ?? string.Empty;
    var dialogType = args.GetType().Name;
    var buttonsHint = GetButtonsHint(args);

    PluginLog.Step(
      "Doc",
      $"DialogBoxShowing: id={dialogId} type={dialogType} buttons={buttonsHint ?? "-"} text={Truncate(message, 500)}"
    );

    var config = OpenDialogRulesLoader.Load();
    if (IsNeverTouch(config, message, dialogId))
    {
      PluginLog.Step("Doc", "DialogBoxShowing: never-touch (wait for Revit/user, e.g. upgrade in progress)");
      return;
    }

    if (PluginSettings.AutoDismissAllOpenDialogs)
    {
      if (TryClick(args, new OpenDialogRule { Click = "close" }, "auto-dismiss-all"))
      {
        return;
      }
    }

    var rule = MatchRule(config, message, dialogId, dialogType);
    if (rule == null)
    {
      PluginLog.Step("Doc", "DialogBoxShowing: no rule matched, left to user");
      return;
    }

    TryClick(args, rule, rule.Name);
  }

  private static bool IsNeverTouch(OpenDialogRulesConfig config, string message, string dialogId)
  {
    var text = $"{message} {dialogId}".ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(text))
    {
      return false;
    }

    return config.Never.MessageContains.Any(keyword => text.Contains(keyword, StringComparison.Ordinal))
      || config.Never.DialogIdContains.Any(keyword => text.Contains(keyword, StringComparison.Ordinal));
  }

  private static OpenDialogRule? MatchRule(
    OpenDialogRulesConfig config,
    string message,
    string dialogId,
    string dialogType
  )
  {
    var text = $"{message} {dialogId}".ToLowerInvariant();
    var typeKey = dialogType.Contains("MessageBox", StringComparison.Ordinal) ? "messagebox" : "task";

    foreach (var rule in config.Rules)
    {
      if (rule.DialogTypes.Count > 0
        && !rule.DialogTypes.Any(t => typeKey.Contains(t, StringComparison.OrdinalIgnoreCase)))
      {
        continue;
      }

      if (rule.MessageContains.Count > 0
        && !rule.MessageContains.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)))
      {
        continue;
      }

      if (rule.MessageNotContains.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)))
      {
        continue;
      }

      if (rule.DialogIdContains.Count > 0
        && !rule.DialogIdContains.Any(k => dialogId.Contains(k, StringComparison.OrdinalIgnoreCase)))
      {
        continue;
      }

      return rule;
    }

    return null;
  }

  private static bool TryClick(DialogBoxShowingEventArgs args, OpenDialogRule rule, string reason)
  {
    var resultCode = rule.ClickResult ?? MapClick(rule.Click);
    if (resultCode == null)
    {
      PluginLog.Step("Doc", $"DialogBoxShowing: unknown click \"{rule.Click}\" for rule {reason}");
      return false;
    }

    switch (args)
    {
      case TaskDialogShowingEventArgs task:
        task.OverrideResult(resultCode.Value);
        PluginLog.Step("Doc", $"DialogBoxShowing: rule={reason} click={rule.Click} code={resultCode.Value}");
        return true;

      case MessageBoxShowingEventArgs messageBox:
        messageBox.OverrideResult(resultCode.Value);
        PluginLog.Step("Doc", $"DialogBoxShowing: rule={reason} click={rule.Click} code={resultCode.Value}");
        return true;

      default:
        PluginLog.Step("Doc", $"DialogBoxShowing: rule={reason} unsupported dialog type");
        return false;
    }
  }

  private static int? MapClick(string click)
  {
    return click.Trim().ToLowerInvariant() switch
    {
      "close" => TaskDialogClose,
      "ok" => TaskDialogOk,
      "cancel" => TaskDialogCancel,
      "yes" => TaskDialogYes,
      "no" => TaskDialogNo,
      "commandlink1" => TaskDialogCommandLink1,
      "commandlink2" => TaskDialogCommandLink2,
      _ => null,
    };
  }

  private static string? GetButtonsHint(DialogBoxShowingEventArgs args)
  {
    if (args is not TaskDialogShowingEventArgs task)
    {
      return null;
    }

    try
    {
      var prop = task.GetType().GetProperty("CommonButtons");
      return prop?.GetValue(task)?.ToString();
    }
    catch
    {
      return null;
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
