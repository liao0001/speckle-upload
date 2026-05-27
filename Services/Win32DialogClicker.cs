using System.Runtime.InteropServices;
using System.Text;

namespace SpeckleUpload.Services;

/// <summary>
/// Dialog_Revit_DocWarnDialog 在 Revit 2024 上常为 DialogBox，OverrideResult 会 accepted 但仍导致 Opening was canceled。
/// 在 OpenAndActivateDocument 阻塞期间由后台线程查找并点击真实按钮。
/// </summary>
internal static class Win32DialogClicker
{
  private const uint BmClick = 0x00F5;

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

  public static bool TryClickButton(IReadOnlyList<string> buttonTextCandidates, int timeoutMs, out string? matchedText)
  {
    matchedText = null;
    if (buttonTextCandidates.Count == 0)
    {
      return false;
    }

    var deadline = Environment.TickCount + timeoutMs;
    while (Environment.TickCount < deadline)
    {
      foreach (var candidate in buttonTextCandidates)
      {
        if (string.IsNullOrWhiteSpace(candidate))
        {
          continue;
        }

        if (TryClickButtonOnce(candidate))
        {
          matchedText = candidate;
          return true;
        }
      }

      Thread.Sleep(150);
    }

    return false;
  }

  private static bool TryClickButtonOnce(string buttonText)
  {
    var clicked = false;
    EnumWindows(
      (hWnd, _) =>
      {
        if (!IsWindowVisible(hWnd))
        {
          return true;
        }

        SearchAndClick(hWnd, buttonText, ref clicked);
        return !clicked;
      },
      IntPtr.Zero
    );
    return clicked;
  }

  private static void SearchAndClick(IntPtr hWnd, string buttonText, ref bool clicked)
  {
    if (clicked)
    {
      return;
    }

    var className = ReadClassName(hWnd);
    if (className.Equals("Button", StringComparison.OrdinalIgnoreCase))
    {
      var text = ReadWindowText(hWnd);
      if (TextMatches(text, buttonText))
      {
        SendMessage(hWnd, BmClick, IntPtr.Zero, IntPtr.Zero);
        clicked = true;
        return;
      }
    }

    EnumChildWindows(
      hWnd,
      (child, _) =>
      {
        SearchAndClick(child, buttonText, ref clicked);
        return !clicked;
      },
      IntPtr.Zero
    );
  }

  private static bool TextMatches(string controlText, string expected)
  {
    if (string.IsNullOrWhiteSpace(controlText))
    {
      return false;
    }

    return controlText.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0
      || expected.IndexOf(controlText, StringComparison.OrdinalIgnoreCase) >= 0;
  }

  private static string ReadWindowText(IntPtr hWnd)
  {
    var buffer = new StringBuilder(512);
    _ = GetWindowText(hWnd, buffer, buffer.Capacity);
    return buffer.ToString();
  }

  private static string ReadClassName(IntPtr hWnd)
  {
    var buffer = new StringBuilder(256);
    _ = GetClassName(hWnd, buffer, buffer.Capacity);
    return buffer.ToString();
  }
}
