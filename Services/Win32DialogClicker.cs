using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using SpeckleUpload.Models;

namespace SpeckleUpload.Services;

/// <summary>
/// DialogBox 在 Open 期间不能用 OverrideResult；通过 Win32 点击前台模态框上的真实按钮。
/// </summary>
internal static class Win32DialogClicker
{
  private const uint BmClick = 0x00F5;
  private const int SwRestore = 9;
  private static readonly object OpenClickGate = new();

  private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

  [DllImport("user32.dll")]
  private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

  [DllImport("user32.dll")]
  private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

  [DllImport("user32.dll")]
  private static extern IntPtr GetForegroundWindow();

  [DllImport("user32.dll")]
  private static extern IntPtr GetParent(IntPtr hWnd);

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
      PluginLog.Step("Doc", "Win32: 未找到 Revit 主窗口");
      return;
    }

    ShowWindow(hWnd, SwRestore);
    SetForegroundWindow(hWnd);
  }

  public static void RunOpenPhaseClick(Action clickAction)
  {
    var task = Task.Run(() =>
    {
      lock (OpenClickGate)
      {
        clickAction();
      }
    });

    lock (OpenClickGate)
    {
      _openPhaseTask = task;
    }
  }

  /// <summary>等待打开阶段 Win32 代点结束，避免与 Speckle 转换并行。</summary>
  public static void WaitForOpenPhaseComplete(int timeoutMs = 60000)
  {
    Task? task;
    lock (OpenClickGate)
    {
      task = _openPhaseTask;
    }

    if (task == null)
    {
      return;
    }

    var watch = Stopwatch.StartNew();
    if (!task.Wait(timeoutMs))
    {
      PluginLog.Step("Doc", $"Win32: WaitForOpenPhaseComplete timeout after {timeoutMs}ms");
      return;
    }

    lock (OpenClickGate)
    {
      if (ReferenceEquals(_openPhaseTask, task))
      {
        _openPhaseTask = null;
      }
    }

    PluginLog.StepElapsed("Doc", "Win32: WaitForOpenPhaseComplete done", watch.ElapsedMilliseconds);
  }

  private static Task? _openPhaseTask;

  public static bool TryAutoClickDialog(
    OpenDialogRulesConfig config,
    int sequenceIndex,
    IReadOnlyList<string> sequenceFallbackCandidates,
    int timeoutMs,
    out string? detail
  )
  {
    detail = null;
    var deadline = Environment.TickCount + timeoutMs;

    while (Environment.TickCount < deadline)
    {
      TryActivateRevitMainWindow();
      Thread.Sleep(200);

      var snapshot = CollectForegroundDialogSnapshot();
      if (snapshot.Buttons.Count == 0)
      {
        Thread.Sleep(150);
        continue;
      }

      var visibleLog =
        $"buttons=[{string.Join("|", snapshot.Buttons)}] static=[{Truncate(snapshot.StaticText, 300)}]";
      PluginLog.Step("Doc", $"Win32: 前台弹窗 {visibleLog}");

      var haystack = Normalize(snapshot.StaticText + " " + string.Join(" ", snapshot.Buttons));

      if (TryInferCandidatesFromVisibleText(haystack, out var inferred, out var inferReason)
        && TryClickFirstVisibleButton(inferred, snapshot, out var inferredBtn))
      {
        detail = $"{inferReason} -> [{inferredBtn}]";
        return true;
      }

      if (TryPickCandidatesFromRules(config, haystack, out var ruleCandidates, out var ruleReason)
        && TryClickFirstVisibleButton(ruleCandidates, snapshot, out var ruleBtn))
      {
        detail = $"{ruleReason} -> [{ruleBtn}]";
        return true;
      }

      if (TryClickFirstVisibleButton(sequenceFallbackCandidates, snapshot, out var seqBtn))
      {
        detail = $"顺序第{sequenceIndex}个 -> [{seqBtn}]";
        return true;
      }

      Thread.Sleep(150);
    }

    detail = "超时：前台弹窗未找到可点按钮";
    return false;
  }

  /// <summary>打开完成后尝试关闭右下角「警告 - n 超出 m」等非模态提示条。</summary>
  public static bool TryDismissWarningStrip(int timeoutMs, out string? detail)
  {
    detail = null;
    var deadline = Environment.TickCount + timeoutMs;

    while (Environment.TickCount < deadline)
    {
      TryActivateRevitMainWindow();
      Thread.Sleep(300);

      var snapshot = CollectRevitClientSnapshot();
      var haystack = Normalize(snapshot.StaticText + " " + string.Join(" ", snapshot.Buttons));
      if (!ContainsAny(
        haystack,
        "警告",
        "超出",
        "分析图元",
        "analytical",
        "warning"
      ))
      {
        detail = "未发现右下角警告条";
        return true;
      }

      PluginLog.Step("Doc", $"Win32: 警告条 static=[{Truncate(snapshot.StaticText, 200)}] buttons=[{string.Join("|", snapshot.Buttons)}]");

      var dismissCandidates = new[]
      {
        "关闭",
        "Close",
        "忽略",
        "解除",
        "Dismiss",
        "×",
        "X",
      };

      if (TryClickFirstVisibleButton(dismissCandidates, snapshot, out var matched))
      {
        detail = $"已点击警告条按钮 [{matched}]";
        return true;
      }

      Thread.Sleep(200);
    }

    detail = "超时：未能关闭警告条";
    return false;
  }

  private static bool TryInferCandidatesFromVisibleText(
    string haystackNormalized,
    out List<string> candidates,
    out string reason
  )
  {
    candidates = new List<string>();
    reason = string.Empty;

    if (ContainsAny(
      haystackNormalized,
      "无法使图元保持连接",
      "不能忽略",
      "unjoin",
      "cannot keep elements joined"
    ))
    {
      candidates = ["取消连接图元", "取消关联图元", "Unjoin Elements"];
      reason = "可见正文-连接错误";
      return true;
    }

    if (ContainsAny(haystackNormalized, "结构分析模型升级"))
    {
      candidates = ["关闭", "Close"];
      reason = "可见正文-结构分析";
      return true;
    }

    if (ContainsAny(
      haystackNormalized,
      "删除图元",
      "不能创建放样",
      "0 错误",
      "警告",
      "族"
    ))
    {
      candidates = ["确定", "OK"];
      reason = "可见正文-警告/确定";
      return true;
    }

    return false;
  }

  private static bool ContainsAny(string haystack, params string[] keys)
  {
    return keys.Any(k => haystack.Contains(Normalize(k), StringComparison.Ordinal));
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
        candidates.AddRange(DefaultKeywordsForClick(action.Click));
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

  private static DialogSnapshot CollectForegroundDialogSnapshot()
  {
    var snapshot = new DialogSnapshot();
    var fg = GetForegroundWindow();
    if (fg != IntPtr.Zero)
    {
      CollectWindowTree(fg, snapshot, maxDepth: 12);
    }

    if (snapshot.Buttons.Count > 0)
    {
      return snapshot;
    }

    EnumWindows(
      (hWnd, _) =>
      {
        if (!IsWindowVisible(hWnd))
        {
          return true;
        }

        var cls = ReadClassName(hWnd);
        if (cls == "#32770")
        {
          var snap = new DialogSnapshot();
          CollectInWindow(hWnd, snap);
          if (snap.Buttons.Count > snapshot.Buttons.Count)
          {
            snapshot.Buttons.Clear();
            snapshot.StaticParts.Clear();
            snapshot.Buttons.AddRange(snap.Buttons);
            snapshot.StaticParts.AddRange(snap.StaticParts);
          }
        }

        return true;
      },
      IntPtr.Zero
    );

    return snapshot;
  }

  private static DialogSnapshot CollectRevitClientSnapshot()
  {
    var snapshot = new DialogSnapshot();
    var revit = FindRevitMainWindow();
    if (revit == IntPtr.Zero)
    {
      return snapshot;
    }

    CollectInWindow(revit, snapshot);
    return snapshot;
  }

  private static void CollectWindowTree(IntPtr hWnd, DialogSnapshot snapshot, int maxDepth)
  {
    if (maxDepth < 0)
    {
      return;
    }

    CollectInWindow(hWnd, snapshot);
    var parent = GetParent(hWnd);
    if (parent != IntPtr.Zero && parent != hWnd)
    {
      CollectWindowTree(parent, snapshot, maxDepth - 1);
    }

    EnumChildWindows(
      hWnd,
      (child, _) =>
      {
        CollectWindowTree(child, snapshot, maxDepth - 1);
        return true;
      },
      IntPtr.Zero
    );
  }

  private static void CollectInWindow(IntPtr hWnd, DialogSnapshot snapshot)
  {
    var className = ReadClassName(hWnd);
    if (className.Equals("Button", StringComparison.OrdinalIgnoreCase))
    {
      var text = ReadWindowText(hWnd);
      snapshot.Buttons.Add(new ButtonInfo(hWnd, string.IsNullOrWhiteSpace(text) ? "(无标题按钮)" : text));
    }
    else if (className.Equals("Static", StringComparison.OrdinalIgnoreCase))
    {
      var text = ReadWindowText(hWnd);
      if (!string.IsNullOrWhiteSpace(text) && text.Length > 1)
      {
        snapshot.StaticParts.Add(text);
      }
    }
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
            || title.Contains(".rvt", StringComparison.OrdinalIgnoreCase)))
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
      .Where(s => !string.IsNullOrWhiteSpace(s) && s != "(无标题按钮)")
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToList();
  }

  private static IEnumerable<string> DefaultKeywordsForClick(string click)
  {
    switch (click.Trim().ToLowerInvariant())
    {
      case "commandlink1":
      case "unjoin":
        return ["取消连接图元", "取消关联图元", "Unjoin Elements"];
      case "ok":
      case "docwarnok":
        return ["确定", "OK"];
      case "close":
        return ["关闭", "Close"];
      default:
        return Array.Empty<string>();
    }
  }

  private static bool TextMatches(string controlText, string expected)
  {
    if (string.IsNullOrWhiteSpace(controlText) || controlText == "(无标题按钮)")
    {
      return false;
    }

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
