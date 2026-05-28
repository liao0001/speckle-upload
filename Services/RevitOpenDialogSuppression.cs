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
  /// <summary>
  /// Dialog_Revit_DocWarnDialog 为 DialogBox（非 TaskDialog）时，Ok 需用 DialogResult.Retry(4)，
  /// 用 1/6 会导致 Opening was canceled。参见 Autodesk 论坛 Dialog_Result_DocWarnDialog。
  /// </summary>
  private const int DialogBoxDocWarnOk = 4;
  private const int DialogBoxDocWarnIgnore = 5;
  private const string DocWarnDialogId = "Dialog_Revit_DocWarnDialog";

  private static DateTime _armedUntilUtc = DateTime.MinValue;
  private static int _openDialogSequenceIndex;
  private static volatile bool _openDocumentInProgress;

  public static void BeginOpenDocument()
  {
    _openDocumentInProgress = true;
    PluginLog.Step("Doc", "OpenDocument: 进入打开阶段（DocWarn DialogBox 使用 Win32 点击，不用 OverrideResult）");
  }

  public static void EndOpenDocument()
  {
    _openDocumentInProgress = false;
    PluginLog.Step("Doc", "OpenDocument: 打开阶段结束");
    var config = OpenDialogRulesLoader.Load();
    Win32DialogClicker.RunOpenPhaseClick(() =>
    {
      if (Win32DialogClicker.TryDismissWarningStrip(45000, out var warnDetail))
      {
        PluginLog.Step("Doc", $"Win32: 警告条处理 {warnDetail}");
      }
      else
      {
        PluginLog.Step("Doc", $"Win32: 警告条处理未完成 {warnDetail}");
      }
    });
  }

  public static void ArmForOpen(TimeSpan? duration = null)
  {
    var seconds = PluginSettings.OpenDialogSuppressSeconds;
    _armedUntilUtc = DateTime.UtcNow.Add(duration ?? TimeSpan.FromSeconds(seconds));
    _openDialogSequenceIndex = 0;
    OpenDialogRulesLoader.Load();
    PluginLog.Step("Doc", $"OpenDialogSuppression: armed for {seconds}s");
  }

  public static void Disarm()
  {
    if (IsArmed)
    {
      PluginLog.Step("Doc", "OpenDialogSuppression: disarmed");
    }

    _armedUntilUtc = DateTime.MinValue;
  }

  public static bool IsArmed => DateTime.UtcNow < _armedUntilUtc;

  /// <summary>打开/关其它文档阶段结束，停止代点，避免 Speckle 转换期间误关弹窗。</summary>
  public static void CompleteOpenPhase()
  {
    Disarm();
  }

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
    var deepText = CollectDeepDialogText(args);
    var combined = CombineText(title, body, deepText, dialogId);
    var buttonsText = CollectAvailableButtonsText(args, buttonsHint: GetButtonsHint(args));

    LogDialogContent(args, dialogId, dialogType, title, body, deepText, combined, buttonsText);

    var config = OpenDialogRulesLoader.Load();
    if (IsNeverTouch(config, title, body, dialogId, out var neverKeyword))
    {
      LogMatchResult(false, $"never 规则命中（关键词: {neverKeyword}），不代点");
      return;
    }

    if (_openDocumentInProgress && GetDialogSurfaceKind(args) == "dialogbox")
    {
      ScheduleOpenPhaseWin32Click(config, dialogId, combined);
      return;
    }

    if (TryHandleDocWarnByDialogId(
      args,
      dialogId,
      dialogType,
      title,
      body,
      deepText,
      combined,
      buttonsText,
      config
    ))
    {
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

    var rule = MatchRule(config, title, body, deepText, dialogId, dialogType, out var scanLines);
    if (rule == null)
    {
      LogRuleScanDetails(scanLines);
      if (TryUnmatchedFallback(config, args, out var fallbackExplain))
      {
        LogMatchResult(true, fallbackExplain);
        return;
      }

      LogUnmatchedNoAction(dialogId, dialogType, title, body, combined, buttonsText);
      LogMatchResult(false, "未匹配 JSON 规则，兜底代点均未成功");
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
    string deepText,
    string combined,
    string? buttonsText
  )
  {
    PluginLog.Step("Doc", "---------- DialogBoxShowing 弹窗内容 ----------");
    PluginLog.Step("Doc", $"DialogId={dialogId}");
    PluginLog.Step("Doc", $"DialogType={dialogType}");
    PluginLog.Step("Doc", $"DialogSurface={GetDialogSurfaceKind(args)}");
    PluginLog.Step("Doc", $"Title={title}");
    PluginLog.Step("Doc", $"Body={body}");
    PluginLog.Step("Doc", $"DeepText={deepText}");
    PluginLog.Step("Doc", $"CombinedText={combined}");
    PluginLog.Step("Doc", $"Buttons={buttonsText ?? "(Revit API 未读到按钮)"}");

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

    PluginLog.Step("Doc", "规则扫描明细（title/message 均在全文匹配；组间 OR，组内 OR）:");
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

  private static bool TryHandleDocWarnByDialogId(
    DialogBoxShowingEventArgs args,
    string dialogId,
    string dialogType,
    string title,
    string body,
    string deepText,
    string combined,
    string? buttonsText,
    OpenDialogRulesConfig config
  )
  {
    var haystack = NormalizeForMatch($"{combined} {buttonsText}");
    var isDocWarnId = dialogId.Equals(DocWarnDialogId, StringComparison.OrdinalIgnoreCase);
    var hasReadableText = HasReadableDialogText(haystack);
    var hasReadableButtons = !string.IsNullOrWhiteSpace(buttonsText);
    var looksLikeDocWarn =
      isDocWarnId
      || ContainsNormalized(haystack, "无法使图元保持连接")
      || ContainsNormalized(haystack, "不能忽略")
      || ContainsNormalized(haystack, "不能创建放样")
      || ContainsNormalized(haystack, "结构分析模型升级");

    if (!looksLikeDocWarn)
    {
      return false;
    }

    var surface = GetDialogSurfaceKind(args);
    PluginLog.Step(
      "Doc",
      $"DocWarn: 可读正文={hasReadableText} 可读按钮={hasReadableButtons} surface={surface}"
    );

    var rule = MatchRule(config, title, body, deepText, dialogId, dialogType, out var scanLines);
    if (rule != null)
    {
      PluginLog.Step("Doc", $"DocWarn: 命中 JSON 规则 [{rule.Name}]（按正文/标题匹配）");
      var buttonAction = ResolveButtonAction(rule, buttonsText, combined, out var buttonExplain);
      if (buttonAction != null)
      {
        var clickRule = ToClickRule(rule, buttonAction);
        if (TryClick(args, clickRule, buttonAction, rule.Name))
        {
          LogMatchResult(
            true,
            $"DocWarn 规则 [{rule.Name}]；{buttonExplain} -> click={clickRule.Click}"
            + (clickRule.ClickResult.HasValue ? $" (code={clickRule.ClickResult})" : "")
          );
          return true;
        }

        LogMatchResult(false, $"DocWarn 规则 [{rule.Name}] 已匹配但代点失败");
        return true;
      }

      PluginLog.Step("Doc", $"DocWarn: 规则 [{rule.Name}] 命中但未匹配到 buttonActions");
      LogRuleScanDetails(scanLines);
    }

    if (TryResolveDocWarnByContentOrButtons(haystack, config, out var resolved, out var resolveReason))
    {
      PluginLog.Step("Doc", $"DocWarn: 按正文/按钮关键词 -> {resolveReason} click={resolved.Click}");
      return TryDocWarnClickResolved(args, resolved, resolveReason);
    }

    _openDialogSequenceIndex++;
    PluginLog.Step(
      "Doc",
      "DocWarn: API 无正文，顺序兜底 第 " + _openDialogSequenceIndex + " 个"
    );
    return TryDocWarnSequenceEntry(args, config, _openDialogSequenceIndex);
  }

  private static void ScheduleOpenPhaseWin32Click(OpenDialogRulesConfig config, string dialogId, string combined)
  {
    _openDialogSequenceIndex++;
    var sequenceIndex = _openDialogSequenceIndex;

    var sequence = config.DocWarnEmptyMessageSequence.TryButtons;
    if (sequence.Count == 0)
    {
      sequence = OpenDialogRulesLoader.CreateDefaultDocWarnEmptyMessageSequence();
    }

    var entryIndex = Math.Min(sequenceIndex - 1, sequence.Count - 1);
    var entry = sequence[entryIndex];
    var fallbackCandidates = BuildWin32KeywordsFromFallback(entry);
    var candidateLog = string.Join(" > ", fallbackCandidates);

    Win32DialogClicker.RunOpenPhaseClick(() =>
    {
      if (Win32DialogClicker.TryAutoClickDialog(
        config,
        sequenceIndex,
        fallbackCandidates,
        90000,
        out var detail
      ))
      {
        PluginLog.Step("Doc", $"Win32: 成功 DialogId={dialogId} {detail}");
      }
      else
      {
        PluginLog.Step("Doc", $"Win32: 失败 DialogId={dialogId} {detail} candidates={candidateLog}");
      }
    });

    LogMatchResult(
      true,
      $"打开阶段 Win32 已调度 #{sequenceIndex} DialogId={dialogId} 顺序候选: {candidateLog}"
    );
  }

  private static List<string> BuildWin32KeywordsFromFallback(OpenDialogFallbackButton entry)
  {
    var list = new List<string>();
    if (!string.IsNullOrWhiteSpace(entry.Label))
    {
      list.Add(entry.Label);
    }

    list.AddRange(entry.ButtonContains);
    list.AddRange(GetDefaultKeywordsForClick(entry.Click));
    return list
      .Where(s => !string.IsNullOrWhiteSpace(s))
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToList();
  }

  private static OpenDialogFallbackButton ToFallbackButton(OpenDialogButtonAction action)
  {
    return new OpenDialogFallbackButton
    {
      Click = action.Click,
      ClickResult = action.ClickResult,
      ButtonContains = action.ButtonContains,
    };
  }

  private static bool HasReadableDialogText(string normalizedHaystack)
  {
    var withoutId = normalizedHaystack.Replace(NormalizeForMatch(DocWarnDialogId), " ");
    withoutId = string.Join(" ", withoutId.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
    return withoutId.Length >= 8;
  }

  private static bool TryResolveDocWarnByContentOrButtons(
    string haystackNormalized,
    OpenDialogRulesConfig config,
    out OpenDialogFallbackButton resolved,
    out string reason
  )
  {
    resolved = new OpenDialogFallbackButton();
    reason = string.Empty;

    foreach (var rule in config.Rules)
    {
      foreach (var action in rule.ButtonActions)
      {
        if (action.ButtonContains.Count == 0)
        {
          continue;
        }

        if (!action.ButtonContains.Any(b => ContainsNormalized(haystackNormalized, b)))
        {
          continue;
        }

        resolved = ToFallbackButton(action);
        reason = $"规则 [{rule.Name}] 按钮关键词";
        return true;
      }
    }

    foreach (var entry in config.DocWarnEmptyMessageSequence.TryButtons)
    {
      var keywords = new List<string>();
      if (!string.IsNullOrWhiteSpace(entry.Label))
      {
        keywords.Add(entry.Label);
      }

      keywords.AddRange(entry.ButtonContains);
      keywords.AddRange(GetDefaultKeywordsForClick(entry.Click));

      if (keywords.Any(k => ContainsNormalized(haystackNormalized, k)))
      {
        resolved = entry;
        reason = $"顺序表关键词 [{entry.Label}]";
        return true;
      }
    }

    foreach (var (keys, click, result, name) in DocWarnContentPatterns)
    {
      if (keys.Any(k => ContainsNormalized(haystackNormalized, k)))
      {
        resolved = new OpenDialogFallbackButton { Click = click, ClickResult = result };
        reason = name;
        return true;
      }
    }

    return false;
  }

  private static IEnumerable<string> GetDefaultKeywordsForClick(string click)
  {
    switch (click.Trim().ToLowerInvariant())
    {
      case "commandlink1":
      case "unjoin":
        yield return "取消连接图元";
        yield return "取消关联图元";
        yield return "Unjoin Elements";
        yield break;
      case "ok":
      case "docwarnok":
        yield return "确定";
        yield return "OK";
        yield break;
      case "close":
        yield return "关闭";
        yield return "Close";
        yield break;
    }
  }

  private static readonly (string[] Keys, string Click, int? Result, string Name)[] DocWarnContentPatterns =
  [
    (
      ["无法使图元保持连接", "不能忽略", "取消连接图元", "取消关联图元", "unjoin elements", "cannot keep elements joined"],
      "commandLink1",
      1001,
      "连接错误-取消连接/关联图元"
    ),
    (["结构分析模型升级"], "close", 8, "结构分析-关闭"),
    (
      ["不能创建放样", "删除图元", "0 错误", "警告", "防撞侧石", "族"],
      "docWarnOk",
      4,
      "警告-确定"
    ),
  ];

  private static bool TryDocWarnClickResolved(
    DialogBoxShowingEventArgs args,
    OpenDialogFallbackButton resolved,
    string reason
  )
  {
    var code = ResolveOverrideCode(args, resolved.Click, resolved.ClickResult, DocWarnDialogId);
    if (!code.HasValue)
    {
      LogMatchResult(false, $"DocWarn {reason}：无法解析 click={resolved.Click}");
      return true;
    }

    if (TryOverrideResult(args, code.Value, $"doc-warn/{reason}"))
    {
      LogMatchResult(true, $"DocWarn {reason} (code={code})");
      return true;
    }

    LogMatchResult(false, $"DocWarn {reason}：code={code} 未被 Revit 接受");
    return true;
  }

  private static bool TryDocWarnSequenceEntry(
    DialogBoxShowingEventArgs args,
    OpenDialogRulesConfig config,
    int sequenceIndex
  )
  {
    var sequence = config.DocWarnEmptyMessageSequence.TryButtons;
    if (sequence.Count == 0)
    {
      sequence = OpenDialogRulesLoader.CreateDefaultDocWarnEmptyMessageSequence();
    }

    var entryIndex = Math.Min(sequenceIndex - 1, sequence.Count - 1);
    var entry = sequence[entryIndex];
    var label = string.IsNullOrWhiteSpace(entry.Label) ? $"#{sequenceIndex}" : entry.Label;
    var code = ResolveOverrideCode(args, entry.Click, entry.ClickResult, DocWarnDialogId);
    if (!code.HasValue)
    {
      LogMatchResult(false, $"DocWarn 顺序第 {sequenceIndex} 个弹窗 ({label}) 无法解析 click={entry.Click}");
      return true;
    }

    PluginLog.Step("Doc", $"DocWarn 顺序代点 [{sequenceIndex}] {label} code={code}");
    if (TryOverrideResult(args, code.Value, $"doc-warn-seq-{sequenceIndex}/{label}"))
    {
      LogMatchResult(true, $"DocWarn 顺序第 {sequenceIndex} 个弹窗 -> {label} (code={code})");
      return true;
    }

    LogMatchResult(false, $"DocWarn 顺序第 {sequenceIndex} 个弹窗 ({label}) code={code} 未被接受");
    return true;
  }

  private static string GetDialogSurfaceKind(DialogBoxShowingEventArgs args)
  {
    if (args is TaskDialogShowingEventArgs)
    {
      return "taskdialog";
    }

    if (args is MessageBoxShowingEventArgs)
    {
      return "messagebox";
    }

    return "dialogbox";
  }

  private static bool IsDocWarnDialog(string? dialogId) =>
    dialogId != null && dialogId.Equals(DocWarnDialogId, StringComparison.OrdinalIgnoreCase);

  private static int? ResolveOverrideCode(
    DialogBoxShowingEventArgs args,
    string click,
    int? clickResult,
    string? dialogId = null
  )
  {
    if (clickResult.HasValue)
    {
      return clickResult.Value;
    }

    var clickNorm = click.Trim().ToLowerInvariant();
    if (IsDocWarnDialog(dialogId) && GetDialogSurfaceKind(args) == "dialogbox")
    {
      return clickNorm switch
      {
        "ok" or "docwarnok" => DialogBoxDocWarnOk,
        "commandlink1" or "unjoin" => TaskDialogCommandLink1,
        "commandlink2" => TaskDialogCommandLink2,
        "ignore" => DialogBoxDocWarnIgnore,
        "close" => TaskDialogClose,
        "cancel" => TaskDialogCancel,
        _ => MapClick(args, clickNorm),
      };
    }

    return MapClick(args, clickNorm);
  }

  private static bool TryUnmatchedFallback(
    OpenDialogRulesConfig config,
    DialogBoxShowingEventArgs args,
    out string explain
  )
  {
    explain = string.Empty;
    var fallback = config.UnmatchedFallback;
    if (!fallback.Enabled || fallback.TryButtons.Count == 0)
    {
      explain = "unmatchedFallback 未启用或未配置 tryButtons";
      return false;
    }

    var index = 0;
    foreach (var entry in fallback.TryButtons)
    {
      index++;
      var label = string.IsNullOrWhiteSpace(entry.Label) ? $"(#{index})" : entry.Label;
      var resultCode = entry.ClickResult ?? ResolveOverrideCode(args, entry.Click, null, args.DialogId);
      if (resultCode == null)
      {
        PluginLog.Step("Doc", $"unmatchedFallback 跳过 [{index}] {label}: 未知 click=\"{entry.Click}\"");
        continue;
      }

      PluginLog.Step("Doc", $"unmatchedFallback 尝试 [{index}] {label} -> click={entry.Click} code={resultCode}");
      if (TryOverrideResult(args, resultCode.Value, $"unmatchedFallback/{label}"))
      {
        explain = $"未匹配 rules，unmatchedFallback 第 {index} 项成功: {label} (code={resultCode})";
        return true;
      }
    }

    explain = "unmatchedFallback 全部尝试均未成功代点";
    return false;
  }

  private static void LogUnmatchedNoAction(
    string dialogId,
    string dialogType,
    string title,
    string body,
    string combined,
    string? buttonsText
  )
  {
    PluginLog.Step("Doc", "========== 未匹配到处理措施（请据此补充 SpeckleUpload.open-dialog-rules.json）==========");
    PluginLog.Step("Doc", $"DialogId={dialogId}");
    PluginLog.Step("Doc", $"DialogType={dialogType}");
    PluginLog.Step("Doc", $"Title={title}");
    PluginLog.Step("Doc", $"Body={body}");
    PluginLog.Step("Doc", $"CombinedText={combined}");
    PluginLog.Step(
      "Doc",
      $"Buttons={buttonsText ?? "(Revit API 未提供按钮文案)"}"
    );
    PluginLog.Step("Doc", "========== 未匹配到处理措施 结束 ==========");
  }

  private static OpenDialogRule? MatchRule(
    OpenDialogRulesConfig config,
    string title,
    string body,
    string deepText,
    string dialogId,
    string dialogType,
    out List<string> scanLines
  )
  {
    scanLines = new List<string>();
    var combinedNorm = NormalizeForMatch(CombineText(title, body, deepText, dialogId));
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

      var titleRequired = rule.TitleContains.Count > 0;
      var messageRequired = rule.MessageContains.Count > 0;
      var titleHit = !titleRequired
        || rule.TitleContains.Any(k => ContainsNormalized(combinedNorm, k));
      var messageHit = !messageRequired
        || rule.MessageContains.Any(k => ContainsNormalized(combinedNorm, k));

      if (titleRequired && messageRequired)
      {
        if (!titleHit && !messageHit)
        {
          scanLines.Add($"[{name}] 跳过: titleContains 与 messageContains 均未命中（组间 OR）");
          continue;
        }
      }
      else if (titleRequired && !titleHit)
      {
        scanLines.Add($"[{name}] 跳过: titleContains 未命中（OR）");
        continue;
      }
      else if (messageRequired && !messageHit)
      {
        scanLines.Add($"[{name}] 跳过: messageContains 未命中（OR）");
        continue;
      }

      if (rule.TitleNotContains.Any(k => ContainsNormalized(combinedNorm, k)))
      {
        scanLines.Add($"[{name}] 跳过: titleNotContains 命中");
        continue;
      }

      if (rule.MessageNotContains.Any(k => ContainsNormalized(combinedNorm, k)))
      {
        scanLines.Add($"[{name}] 跳过: messageNotContains 命中");
        continue;
      }

      if (rule.DialogIdContains.Count > 0)
      {
        if (string.IsNullOrWhiteSpace(dialogId))
        {
          scanLines.Add($"[{name}] 跳过: 需要 DialogId 但为空");
          continue;
        }

        if (!rule.DialogIdContains.Any(k => dialogId.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
          scanLines.Add($"[{name}] 跳过: dialogIdContains 未命中");
          continue;
        }
      }

      if (!titleRequired && !messageRequired && rule.DialogIdContains.Count == 0)
      {
        scanLines.Add($"[{name}] 跳过: 未配置匹配条件");
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
    var haystack = NormalizeForMatch($"{buttonsText} {combinedText}".Trim());

    if (rule.DialogIdContains.Any(id => id.Equals(DocWarnDialogId, StringComparison.OrdinalIgnoreCase))
      && rule.ButtonActions.Count > 0)
    {
      explain = $"DocWarn 规则 [{rule.Name}] 直接采用首个 buttonActions";
      return rule.ButtonActions[0];
    }

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
          && action.ButtonContains.Any(b => ContainsNormalized(haystack, b)))
        {
          explain = $"buttonActions 命中 [{string.Join("|", action.ButtonContains)}]";
          return action;
        }
      }

      if (rule.ButtonActions.Count == 1)
      {
        explain =
          $"未读到按钮文案，仅 1 条 buttonActions，按 [{string.Join("|", rule.ButtonActions[0].ButtonContains)}] 代点";
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
    var dialogId = args.DialogId;
    var resultCode = clickResult ?? ResolveOverrideCode(args, click, null, dialogId);
    if (resultCode == null)
    {
      PluginLog.Step("Doc", $"代点失败: 未知 click=\"{click}\" rule={reason}");
      return false;
    }

    return TryOverrideResult(args, resultCode.Value, $"{reason} click={click}");
  }

  private static bool TryOverrideResult(DialogBoxShowingEventArgs args, int resultCode, string reason)
  {
    try
    {
      var accepted = args.OverrideResult(resultCode);
      PluginLog.Step(
        "Doc",
        $"OverrideResult surface={GetDialogSurfaceKind(args)} code={resultCode} accepted={accepted} ({reason})"
      );
      return accepted;
    }
    catch (Exception ex)
    {
      PluginLog.Step("Doc", $"OverrideResult 异常: {reason} code={resultCode}: {ex.Message}");
      return false;
    }
  }

  private static bool TryOverride(
    DialogBoxShowingEventArgs args,
    int resultCode,
    string reason,
    string click
  ) => TryOverrideResult(args, resultCode, $"{reason} click={click}");

  private static string NormalizeForMatch(string text)
  {
    if (string.IsNullOrWhiteSpace(text))
    {
      return string.Empty;
    }

    return text
      .Replace('—', '-')
      .Replace('–', '-')
      .Replace('－', '-')
      .Replace('“', '"')
      .Replace('”', '"')
      .Replace('‘', '\'')
      .Replace('’', '\'')
      .ToLowerInvariant();
  }

  private static bool ContainsNormalized(string haystackNormalized, string keyword)
  {
    return haystackNormalized.Contains(NormalizeForMatch(keyword), StringComparison.Ordinal);
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
      "docwarnok" => DialogBoxDocWarnOk,
      "cancel" => TaskDialogCancel,
      "yes" => TaskDialogYes,
      "no" => TaskDialogNo,
      "commandlink1" => TaskDialogCommandLink1,
      "commandlink2" => TaskDialogCommandLink2,
      "ignore" => DialogBoxDocWarnIgnore,
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

    return parts.Count == 0 ? null : string.Join(" | ", parts.Distinct(StringComparer.Ordinal));
  }

  private static string CollectDeepDialogText(DialogBoxShowingEventArgs args)
  {
    var parts = new List<string>();
    var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    for (var type = args.GetType(); type != null; type = type.BaseType)
    {
      foreach (var prop in type.GetProperties(flags))
      {
        if (!prop.CanRead || prop.GetIndexParameters().Length > 0)
        {
          continue;
        }

        try
        {
          if (prop.PropertyType == typeof(string))
          {
            AppendIfPresent(parts, prop.GetValue(args) as string);
            continue;
          }

          if (prop.PropertyType.IsEnum)
          {
            AppendIfPresent(parts, prop.GetValue(args)?.ToString());
          }
        }
        catch
        {
          // ignore
        }
      }
    }

    return string.Join(" ", parts.Distinct(StringComparer.Ordinal));
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

  private static string CombineText(params string[] parts)
  {
    return string.Join(" ", parts.Where(s => !string.IsNullOrWhiteSpace(s)));
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
