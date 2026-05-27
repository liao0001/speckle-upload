using System.Reflection;
using System.Text;
using Autodesk.Revit.UI.Events;
using SpeckleUpload.Models;

namespace SpeckleUpload.Services;

public static class RevitOpenDialogSuppression
{
  private const int TaskDialogOk = 1;
  private const int TaskDialogCancel = 2;
  private const int TaskDialogYes = 6;
  private const int TaskDialogNo = 7;
  private const int TaskDialogClose = 8;
  private const int TaskDialogCommandLink1 = 1001;
  private const int TaskDialogCommandLink2 = 1002;
  private const int MessageBoxOk = 6;
  private const string DocWarnDialogId = "Dialog_Revit_DocWarnDialog";

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

    var dialogId = args.DialogId ?? string.Empty;
    var dialogType = args.GetType().Name;
    var title = CollectTitle(args);
    var body = CollectBody(args);
    var combined = CombineText(title, body, dialogId);
    var buttonsText = CollectAvailableButtonsText(args, buttonsHint: GetButtonsHint(args));

    LogDialogContent(args, dialogId, dialogType, title, body, combined, buttonsText);

    var config = OpenDialogRulesLoader.Load();
    if (IsNeverTouch(config, title, body, dialogId, out var neverKeyword))
    {
      LogMatchResult(false, $"never 规则命中（关键词: {neverKeyword}），不代点");
      return;
    }

    if (PluginSettings.AutoDismissAllOpenDialogs)
    {
      if (TryClick(args, new OpenDialogRule { Click = "close" }, null, "auto-dismiss-all"))
      {
        LogMatchResult(true, "AUTO_DISMISS_ALL -> click close");
        return;
      }

      LogMatchResult(false, "AUTO_DISMISS_ALL 已开启但代点失败");
    }

    var rule = MatchRule(config, title, body, dialogId, dialogType, out var scanLines);
    if (rule == null)
    {
      LogMatchResult(false, "未匹配 JSON 规则");
      LogRuleScanDetails(scanLines);
      return;
    }

    var buttonAction = ResolveButtonAction(rule, buttonsText, combined, out var buttonExplain);
    if (buttonAction == null)
    {
      LogMatchResult(
        false,
        $"已匹配规则 [{rule.Name}]，但未找到配置的按钮（可用按钮信息: {buttonsText ?? "无"}）"
      );
      LogRuleScanDetails(scanLines);
      return;
    }

    var clickRule = ToClickRule(rule, buttonAction);
    if (TryClick(args, clickRule, buttonAction, rule.Name))
    {
      LogMatchResult(
        true,
        $"已匹配规则 [{rule.Name}]；{buttonExplain} -> click={clickRule.Click}"
        + (clickRule.ClickResult.HasValue ? $" (clickResult={clickRule.ClickResult})" : "")
      );
    }
    else
    {
      LogMatchResult(false, $"已匹配规则 [{rule.Name}]；{buttonExplain} 但代点失败");
    }
  }

  private static void LogDialogContent(
    DialogBoxShowingEventArgs args,
    string dialogId,
    string dialogType,
    string title,
    string body,
    string combined,
    string? buttonsText
  )
  {
    PluginLog.Step("Doc", "---------- DialogBoxShowing 弹窗内容 ----------");
    PluginLog.Step("Doc", $"DialogId={dialogId}");
    PluginLog.Step("Doc", $"DialogType={dialogType}");
    PluginLog.Step("Doc", $"Title={title}");
    PluginLog.Step("Doc", $"Body={body}");
    PluginLog.Step("Doc", $"CombinedText={combined}");
    PluginLog.Step("Doc", $"ButtonsHint={buttonsText ?? "(Revit API 未提供按钮文案，仅 CommonButtons/DialogType)"}");

    foreach (var prop in args.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
    {
      if (!prop.CanRead)
      {
        continue;
      }

      try
      {
        var value = prop.GetValue(args);
        if (value == null)
        {
          continue;
        }

        PluginLog.Step("Doc", $"  {prop.Name} ({prop.PropertyType.Name}) = {value}");
      }
      catch (Exception ex)
      {
        PluginLog.Step("Doc", $"  {prop.Name} = <read failed: {ex.Message}>");
      }
    }

    PluginLog.Step("Doc", "---------- DialogBoxShowing 弹窗内容结束 ----------");
  }

  private static void LogMatchResult(bool matchedAndActed, string detail)
  {
    var status = matchedAndActed ? "已处理" : "未自动处理";
    PluginLog.Step("Doc", $"---------- DialogBoxShowing 匹配结果: {status} ----------");
    PluginLog.Step("Doc", detail);
    PluginLog.Step("Doc", "---------- DialogBoxShowing 匹配结果结束 ----------");
  }

  private static void LogRuleScanDetails(List<string> scanLines)
  {
    if (scanLines.Count == 0)
    {
      return;
    }

    PluginLog.Step("Doc", "规则扫描明细（titleContains/messageContains 均为 OR，组间为 AND）:");
    foreach (var line in scanLines)
    {
      PluginLog.Step("Doc", $"  {line}");
    }
  }

  private static bool IsNeverTouch(
    OpenDialogRulesConfig config,
    string title,
    string body,
    string dialogId,
    out string? matchedKeyword
  )
  {
    matchedKeyword = null;
    var titleText = title.ToLowerInvariant();
    var bodyText = body.ToLowerInvariant();
    var idText = dialogId.ToLowerInvariant();

    foreach (var keyword in config.Never.TitleContains)
    {
      if (titleText.Contains(keyword, StringComparison.Ordinal)
        || bodyText.Contains(keyword, StringComparison.Ordinal))
      {
        matchedKeyword = keyword;
        return true;
      }
    }

    foreach (var keyword in config.Never.MessageContains)
    {
      if (bodyText.Contains(keyword, StringComparison.Ordinal)
        || titleText.Contains(keyword, StringComparison.Ordinal)
        || idText.Contains(keyword, StringComparison.Ordinal))
      {
        matchedKeyword = keyword;
        return true;
      }
    }

    foreach (var keyword in config.Never.DialogIdContains)
    {
      if (idText.Contains(keyword, StringComparison.Ordinal))
      {
        matchedKeyword = keyword;
        return true;
      }
    }

    return false;
  }

  private static OpenDialogRule? MatchRule(
    OpenDialogRulesConfig config,
    string title,
    string body,
    string dialogId,
    string dialogType,
    out List<string> scanLines
  )
  {
    scanLines = new List<string>();
    var titleText = title.ToLowerInvariant();
    var bodyText = body.ToLowerInvariant();
    var typeKey = dialogType.Contains("MessageBox", StringComparison.Ordinal) ? "messagebox" : "task";

    foreach (var rule in config.Rules)
    {
      var name = string.IsNullOrWhiteSpace(rule.Name) ? "(unnamed)" : rule.Name;

      if (rule.DialogTypes.Count > 0
        && !rule.DialogTypes.Any(t => typeKey.Contains(t, StringComparison.OrdinalIgnoreCase)))
      {
        scanLines.Add($"[{name}] 跳过: dialogType={dialogType}");
        continue;
      }

      if (rule.TitleContains.Count > 0
        && !rule.TitleContains.Any(k => titleText.Contains(k, StringComparison.OrdinalIgnoreCase)))
      {
        scanLines.Add($"[{name}] 跳过: titleContains 未命中（OR）");
        continue;
      }

      if (rule.TitleNotContains.Any(k => titleText.Contains(k, StringComparison.OrdinalIgnoreCase)))
      {
        scanLines.Add($"[{name}] 跳过: titleNotContains 命中");
        continue;
      }

      if (rule.MessageContains.Count > 0
        && !rule.MessageContains.Any(k => bodyText.Contains(k, StringComparison.OrdinalIgnoreCase)))
      {
        scanLines.Add($"[{name}] 跳过: messageContains 未命中（OR）");
        continue;
      }

      if (rule.MessageNotContains.Any(k => bodyText.Contains(k, StringComparison.OrdinalIgnoreCase)
        || titleText.Contains(k, StringComparison.OrdinalIgnoreCase)))
      {
        scanLines.Add($"[{name}] 跳过: messageNotContains 命中");
        continue;
      }

      if (rule.DialogIdContains.Count > 0)
      {
        if (string.IsNullOrWhiteSpace(dialogId))
        {
          if (rule.TitleContains.Count == 0 && rule.MessageContains.Count == 0)
          {
            scanLines.Add($"[{name}] 跳过: 需要 DialogId 但为空");
            continue;
          }
        }
        else if (!rule.DialogIdContains.Any(k => dialogId.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
          scanLines.Add($"[{name}] 跳过: dialogIdContains 未命中");
          continue;
        }
      }

      if (rule.TitleContains.Count == 0
        && rule.MessageContains.Count == 0
        && rule.DialogIdContains.Count == 0)
      {
        scanLines.Add($"[{name}] 跳过: 未配置 titleContains/messageContains/dialogIdContains");
        continue;
      }

      scanLines.Add($"[{name}] 命中");
      return rule;
    }

    return null;
  }

  private static OpenDialogButtonAction? ResolveButtonAction(
    OpenDialogRule rule,
    string? buttonsText,
    string combinedText,
    out string explain
  )
  {
    var haystack = $"{buttonsText} {combinedText}".Trim();

    if (rule.ButtonActions.Count > 0)
    {
      foreach (var action in rule.ButtonActions)
      {
        if (action.ButtonContains.Count == 0)
        {
          explain = "buttonActions 未限定 buttonContains";
          return action;
        }

        if (!string.IsNullOrWhiteSpace(haystack)
          && action.ButtonContains.Any(b => haystack.Contains(b, StringComparison.OrdinalIgnoreCase)))
        {
          explain = $"buttonActions 命中 [{string.Join("|", action.ButtonContains)}]";
          return action;
        }
      }

      if (rule.ButtonActions.Count == 1)
      {
        explain =
          $"API 未读到按钮文案，仅配置 1 条 buttonActions，按 [{string.Join("|", rule.ButtonActions[0].ButtonContains)}] 代点";
        return rule.ButtonActions[0];
      }

      explain = "buttonActions 均未命中";
      return null;
    }

    if (rule.ButtonContains.Count > 0)
    {
      if (!string.IsNullOrWhiteSpace(haystack)
        && rule.ButtonContains.Any(b => haystack.Contains(b, StringComparison.OrdinalIgnoreCase)))
      {
        explain = $"buttonContains 命中 [{string.Join("|", rule.ButtonContains)}]";
        return new OpenDialogButtonAction { Click = rule.Click, ClickResult = rule.ClickResult };
      }

      explain = "buttonContains 未命中";
      return null;
    }

    explain = "未配置 buttonActions/buttonContains，使用规则默认 click";
    return new OpenDialogButtonAction { Click = rule.Click, ClickResult = rule.ClickResult };
  }

  private static OpenDialogRule ToClickRule(OpenDialogRule rule, OpenDialogButtonAction action)
  {
    return new OpenDialogRule
    {
      Name = rule.Name,
      Click = action.Click,
      ClickResult = action.ClickResult ?? rule.ClickResult,
    };
  }

  private static bool TryClick(
    DialogBoxShowingEventArgs args,
    OpenDialogRule rule,
    OpenDialogButtonAction? buttonAction,
    string reason
  )
  {
    var click = buttonAction?.Click ?? rule.Click;
    var clickResult = buttonAction?.ClickResult ?? rule.ClickResult;
    var resultCode = clickResult ?? MapClick(args, click);
    if (resultCode == null)
    {
      PluginLog.Step("Doc", $"代点失败: 未知 click=\"{click}\" rule={reason}");
      return false;
    }

    return TryOverride(args, resultCode.Value, reason, click);
  }

  private static bool TryOverride(
    DialogBoxShowingEventArgs args,
    int resultCode,
    string reason,
    string click
  )
  {
    switch (args)
    {
      case TaskDialogShowingEventArgs task:
        task.OverrideResult(resultCode);
        PluginLog.Step("Doc", $"代点成功: rule={reason} type=TaskDialog click={click} code={resultCode}");
        return true;

      case MessageBoxShowingEventArgs messageBox:
        messageBox.OverrideResult(resultCode);
        PluginLog.Step("Doc", $"代点成功: rule={reason} type=MessageBox click={click} code={resultCode}");
        return true;

      default:
        return TryOverrideReflection(args, resultCode, reason, click);
    }
  }

  private static bool TryOverrideReflection(
    DialogBoxShowingEventArgs args,
    int resultCode,
    string reason,
    string click
  )
  {
    try
    {
      var method = args.GetType().GetMethod("OverrideResult", new[] { typeof(int) });
      if (method == null)
      {
        PluginLog.Step("Doc", $"代点失败: {args.GetType().Name} 无 OverrideResult");
        return false;
      }

      method.Invoke(args, new object[] { resultCode });
      PluginLog.Step(
        "Doc",
        $"代点成功: rule={reason} type={args.GetType().Name} click={click} code={resultCode} (reflection)"
      );
      return true;
    }
    catch (Exception ex)
    {
      PluginLog.Step("Doc", $"代点失败: reflection {ex.GetType().Name} {ex.Message}");
      return false;
    }
  }

  private static int? MapClick(DialogBoxShowingEventArgs args, string click)
  {
    var normalized = click.Trim().ToLowerInvariant();
    if (normalized == "ok" && args is MessageBoxShowingEventArgs)
    {
      return MessageBoxOk;
    }

    return normalized switch
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

  private static string CollectTitle(DialogBoxShowingEventArgs args)
  {
    var parts = new List<string>();

    foreach (var name in new[] { "Title", "WindowTitle", "Caption", "MainInstruction", "Instruction" })
    {
      AppendPropertyString(parts, args, name);
    }

    var message = ReadMessage(args);
    if (!string.IsNullOrWhiteSpace(message))
    {
      var firstLine = message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
      if (!string.IsNullOrWhiteSpace(firstLine))
      {
        parts.Add(firstLine.Trim());
      }
    }

    return string.Join(" | ", parts.Distinct(StringComparer.Ordinal));
  }

  private static string CollectBody(DialogBoxShowingEventArgs args)
  {
    var parts = new List<string>();
    AppendIfPresent(parts, ReadMessage(args));

    foreach (var prop in args.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
    {
      if (prop.PropertyType != typeof(string) || !prop.CanRead)
      {
        continue;
      }

      if (prop.Name is "Title" or "WindowTitle" or "Caption" or "MainInstruction" or "Instruction")
      {
        continue;
      }

      try
      {
        AppendIfPresent(parts, prop.GetValue(args) as string);
      }
      catch
      {
        // ignore
      }
    }

    return string.Join(" ", parts.Distinct(StringComparer.Ordinal));
  }

  private static string? CollectAvailableButtonsText(DialogBoxShowingEventArgs args, string? buttonsHint)
  {
    var parts = new List<string>();
    AppendIfPresent(parts, buttonsHint);

    if (args is MessageBoxShowingEventArgs messageBox)
    {
      try
      {
        var dialogTypeProp = messageBox.GetType().GetProperty("DialogType");
        AppendIfPresent(parts, dialogTypeProp?.GetValue(messageBox)?.ToString());
      }
      catch
      {
        // ignore
      }
    }

    foreach (var prop in args.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
    {
      if (!prop.CanRead)
      {
        continue;
      }

      var name = prop.Name;
      if (!name.Contains("Button", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("Link", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("Command", StringComparison.OrdinalIgnoreCase))
      {
        continue;
      }

      try
      {
        AppendIfPresent(parts, prop.GetValue(args)?.ToString());
      }
      catch
      {
        // ignore
      }
    }

    if (args.DialogId?.Equals(DocWarnDialogId, StringComparison.OrdinalIgnoreCase) == true)
    {
      parts.Add("确定");
      parts.Add("取消");
      parts.Add("取消连接图元");
      parts.Add("Unjoin Elements");
      parts.Add("OK");
      parts.Add("Cancel");
    }

    return parts.Count == 0 ? null : string.Join(" | ", parts.Distinct(StringComparer.Ordinal));
  }

  private static string ReadMessage(DialogBoxShowingEventArgs args)
  {
    return args switch
    {
      TaskDialogShowingEventArgs task => task.Message ?? string.Empty,
      MessageBoxShowingEventArgs messageBox => messageBox.Message ?? string.Empty,
      _ => string.Empty,
    };
  }

  private static void AppendPropertyString(List<string> parts, DialogBoxShowingEventArgs args, string propertyName)
  {
    try
    {
      var prop = args.GetType().GetProperty(propertyName);
      AppendIfPresent(parts, prop?.GetValue(args) as string);
    }
    catch
    {
      // ignore
    }
  }

  private static string CombineText(string title, string body, string dialogId)
  {
    return string.Join(" ", new[] { title, body, dialogId }.Where(s => !string.IsNullOrWhiteSpace(s)));
  }

  private static void AppendIfPresent(List<string> parts, string? value)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      return;
    }

    parts.Add(value.Trim());
  }
}
