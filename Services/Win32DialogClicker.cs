using System.Runtime.InteropServices;
using System.Text;
using SpeckleUpload.Models;

namespace SpeckleUpload.Services;

/// <summary>
/// Dialog_Revit_DocWarnDialog (DialogBox) 在 Open 期间不能用 OverrideResult，改为枚举可见按钮并模拟点击。
/// </summary>
internal static class Win32DialogClicker
{
  private const uint BmClick = 0x00F5;
  private const int SwRestore = 9;

  private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

  [DllImport("user32.dll")]
  private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

  [DllImport("user32.dll")]
  private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

  [DllImport("user32.dll")]
  private static extern bool IsWindowVisible(IntPtr hWnd);

  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

  [DllImport("user32.dll")]
  private static extern bool SetForegroundWindow(IntPtr hWnd);

  [DllImport("user32.dll")]
  private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

  public static void TryActivateRevitMainWindow()
  {
    var hWnd = FindRevitMainWindow();
    if (hWnd == IntPtr.Zero)
    {
      PluginLog.Step("Doc", "Win32: 未找到 Revit 主窗口标题");
      return;
    }

    ShowWindow(hWnd, SwRestore);
    SetForegroundWindow(hWnd);
    PluginLog.Step("Doc", "Win32: 已将 Revit 主窗口置前");
  }

  /// <summary>
  /// 轮询可见对话框：先按正文匹配规则选按钮；否则按候选列表顺序点击第一个可见按钮。
  /// </summary>
  public static bool TryAutoClickDocWarnDialog(
    OpenDialogRulesConfig config,
    int sequenceIndex,
    IReadOnlyList<string> fallbackCandidates,
    int timeoutMs,
    out string? detail
  )
  {
    detail = null;
    var deadline = Environment.TickCount + timeoutMs;

    while (Environment.TickCount < deadline)
    {
      TryActivateRevitMainWindow();

      var snapshot = CollectVisibleDialogSnapshot();
      if (snapshot.Buttons.Count == 0)
      {
        Thread.Sleep(150);
        continue;
      }

      var visibleLog =
        $"buttons=[{string.Join("|", snapshot.Buttons)}] static=[{Truncate(snapshot.StaticText, 200)}]";
      PluginLog.Step("Doc", $"Win32: 可见弹窗 {visibleLog}");

      var haystack = Normalize(snapshot.StaticText + " " + string.Join(" ", snapshot.Buttons));
      if (TryPickCandidatesFromRules(config, haystack, out var ruleCandidates, out var ruleReason))
      {
        if (TryClickFirstVisibleButton(ruleCandidates, snapshot, out var matched))
        {
          detail = $"{ruleReason} -> [{matched}]";
          return true;
        }
      }

      if (TryClickFirstVisibleButton(fallbackCandidates, snapshot, out var fallbackMatched))
      {
        detail = $"顺序第{sequenceIndex}个 -> [{fallbackMatched}]";
        return true;
      }

      Thread.Sleep(150);
    }

    detail = "超时：未找到可点击按钮";
    return false;
  }

  private static bool TryPickCandidatesFromRules(
    OpenDialogRulesConfig config,
    string haystackNormalized,
    out List<string> candidates,
    out string reason
  )
  {
    candidates = new List<string>();
    reason = string.Empty;

    foreach (var rule in config.Rules)
    {
      var titleHit = rule.TitleContains.Count == 0
        || rule.TitleContains.Any(k => haystackNormalized.Contains(Normalize(k), StringComparison.Ordinal));
      var messageHit = rule.MessageContains.Count == 0
        || rule.MessageContains.Any(k => haystackNormalized.Contains(Normalize(k), StringComparison.Ordinal));
      if (rule.TitleContains.Count > 0 && rule.MessageContains.Count > 0)
      {
        if (!titleHit && !messageHit)
        {
          continue;
        }
      }
      else if (rule.TitleContains.Count > 0 && !titleHit)
      {
        continue;
      }
      else if (rule.MessageContains.Count > 0 && !messageHit)
      {
        continue;
      }

      if (rule.MessageNotContains.Any(k => haystackNormalized.Contains(Normalize(k), StringComparison.Ordinal)))
      {
        continue;
      }

      foreach (var action in rule.ButtonActions)
      {
        candidates.AddRange(action.ButtonContains);
        if (!string.IsNullOrWhiteSpace(action.Click))
        {
          candidates.AddRange(DefaultKeywordsForClick(action.Click));
        }
      }

      if (candidates.Count > 0)
      {
        reason = $"可见文案命中规则 [{rule.Name}]";
        candidates = DistinctCandidates(candidates);
        return true;
      }
    }

    return false;
  }

  private static bool TryClickFirstVisibleButton(
    IReadOnlyList<string> candidatesInOrder,
    DialogSnapshot snapshot,
    out string? matched
  )
  {
    matched = null;
    foreach (var candidate in candidatesInOrder)
    {
      if (string.IsNullOrWhiteSpace(candidate))
      {
        continue;
      }

      foreach (var button in snapshot.Buttons)
      {
        if (!TextMatches(button.Text, candidate))
        {
          continue;
        }

        SendMessage(button.Handle, BmClick, IntPtr.Zero, IntPtr.Zero);
        matched = button.Text;
        return true;
      }
    }

    return false;
  }

  private static DialogSnapshot CollectVisibleDialogSnapshot()
  {
    var snapshot = new DialogSnapshot();

    EnumWindows(
      (hWnd, _) =>
      {
        if (!IsWindowVisible(hWnd))
        {
          return true;
        }

        CollectInWindow(hWnd, snapshot);
        return true;
      },
      IntPtr.Zero
    );

    return snapshot;
  }

  private static void CollectInWindow(IntPtr hWnd, DialogSnapshot snapshot)
  {
    var className = ReadClassName(hWnd);
    if (className.Equals("Button", StringComparison.OrdinalIgnoreCase))
    {
      var text = ReadWindowText(hWnd);
      if (!string.IsNullOrWhiteSpace(text))
      {
        snapshot.Buttons.Add(new ButtonInfo(hWnd, text));
      }
    }
    else if (className.Equals("Static", StringComparison.OrdinalIgnoreCase))
    {
      var text = ReadWindowText(hWnd);
      if (!string.IsNullOrWhiteSpace(text) && text.Length > 2)
      {
        snapshot.StaticParts.Add(text);
      }
    }

    EnumChildWindows(
      hWnd,
      (child, _) =>
      {
        CollectInWindow(child, snapshot);
        return true;
      },
      IntPtr.Zero
    );
  }

  private static IntPtr FindRevitMainWindow()
  {
    IntPtr found = IntPtr.Zero;
    EnumWindows(
      (hWnd, _) =>
      {
        if (!IsWindowVisible(hWnd))
        {
          return true;
        }

        var title = ReadWindowText(hWnd);
        if (title.Contains("Revit", StringComparison.OrdinalIgnoreCase)
          && (title.Contains("Autodesk", StringComparison.OrdinalIgnoreCase)
            || title.Contains("RVT", StringComparison.OrdinalIgnoreCase)))
        {
          found = hWnd;
          return false;
        }

        return true;
      },
      IntPtr.Zero
    );
    return found;
  }

  private static List<string> DistinctCandidates(IEnumerable<string> items)
  {
    return items
      .Where(s => !string.IsNullOrWhiteSpace(s))
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToList();
  }

  private static IEnumerable<string> DefaultKeywordsForClick(string click)
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

  private static bool TextMatches(string controlText, string expected)
  {
    return controlText.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0
      || expected.IndexOf(controlText, StringComparison.OrdinalIgnoreCase) >= 0;
  }

  private static string ReadWindowText(IntPtr hWnd)
  {
    var buffer = new StringBuilder(1024);
    _ = GetWindowText(hWnd, buffer, buffer.Capacity);
    return buffer.ToString();
  }

  private static string ReadClassName(IntPtr hWnd)
  {
    var buffer = new StringBuilder(256);
    _ = GetClassName(hWnd, buffer, buffer.Capacity);
    return buffer.ToString();
  }

  private static string Normalize(string text) => text.Replace('—', '-').Replace('–', '-').ToLowerInvariant();

  private static string Truncate(string text, int max)
  {
    if (text.Length <= max)
    {
      return text;
    }

    return text.Substring(0, max) + "...";
  }

  private sealed class DialogSnapshot
  {
    public List<ButtonInfo> Buttons { get; } = new();
    public List<string> StaticParts { get; } = new();
    public string StaticText => string.Join(" ", StaticParts);
  }

  private readonly struct ButtonInfo
  {
    public ButtonInfo(IntPtr handle, string text)
    {
      Handle = handle;
      Text = text;
    }

    public IntPtr Handle { get; }
    public string Text { get; }
  }
}
