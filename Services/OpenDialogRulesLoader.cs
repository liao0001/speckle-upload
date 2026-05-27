using System.Reflection;
using Newtonsoft.Json;
using SpeckleUpload.Models;

namespace SpeckleUpload.Services;

public static class OpenDialogRulesLoader
{
  private static OpenDialogRulesConfig? _cached;

  public static OpenDialogRulesConfig Load()
  {
    if (_cached != null)
    {
      return _cached;
    }

    foreach (var path in GetCandidatePaths())
    {
      if (!File.Exists(path))
      {
        continue;
      }

      try
      {
        _cached = JsonConvert.DeserializeObject<OpenDialogRulesConfig>(File.ReadAllText(path))
          ?? CreateHardcodedDefaults();
        PluginLog.Step(
          "Doc",
          $"OpenDialogRules: loaded \"{path}\" rules={_cached.Rules.Count} neverKeywords={_cached.Never.MessageContains.Count} fallbackButtons={_cached.UnmatchedFallback.TryButtons.Count}"
        );
        return _cached;
      }
      catch (Exception ex)
      {
        PluginLog.Step("Doc", $"OpenDialogRules: parse failed \"{path}\": {ex.Message}");
      }
    }

    _cached = CreateHardcodedDefaults();
    PluginLog.Step("Doc", "OpenDialogRules: using hardcoded defaults");
    return _cached;
  }

  private static IEnumerable<string> GetCandidatePaths()
  {
    string? pluginDir = null;
    try
    {
      var assemblyPath = Assembly.GetExecutingAssembly().Location;
      pluginDir = string.IsNullOrEmpty(assemblyPath) ? null : Path.GetDirectoryName(assemblyPath);
    }
    catch
    {
      // ignore
    }

    if (!string.IsNullOrEmpty(pluginDir))
    {
      yield return Path.Combine(pluginDir, "SpeckleUpload.open-dialog-rules.json");
    }

    yield return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SpeckleUpload.open-dialog-rules.json");
  }

  private static OpenDialogRulesConfig CreateHardcodedDefaults()
  {
    return new OpenDialogRulesConfig
    {
      Never = new OpenDialogNeverRules
      {
        MessageContains =
        [
          "取消升级",
          "cancel upgrade",
          "cancelling upgrade",
          "正在升级",
          "upgrade in progress",
          "upgrading the project",
        ],
      },
      Rules =
      [
        new OpenDialogRule
        {
          Name = "incompatible-elements-close",
          MessageContains =
          [
            "不兼容",
            "incompatib",
            "无法复制",
            "不能复制",
            "could not be copied",
            "cannot copy",
            "lost element",
          ],
          MessageNotContains = ["取消升级", "cancel upgrade"],
          Click = "close",
          DialogTypes = ["task", "messagebox"],
        },
      ],
      UnmatchedFallback = CreateDefaultUnmatchedFallback(),
      DocWarnEmptyMessageSequence = CreateDefaultDocWarnEmptyMessageSequence(),
    };
  }

  public static List<OpenDialogFallbackButton> CreateDefaultDocWarnEmptyMessageSequence()
  {
    return
    [
      new OpenDialogFallbackButton
      {
        Label = "确定（第1个弹窗-警告）",
        Click = "ok",
      },
      new OpenDialogFallbackButton
      {
        Label = "取消连接图元（第2个弹窗-连接）",
        Click = "commandLink1",
        ClickResult = 1001,
      },
      new OpenDialogFallbackButton
      {
        Label = "关闭（第3个及以后）",
        Click = "close",
        ClickResult = 8,
      },
    ];
  }

  private static OpenDialogUnmatchedFallback CreateDefaultDocWarnEmptyMessageSequence()
  {
    return new OpenDialogUnmatchedFallback
    {
      TryButtons = CreateDefaultDocWarnEmptyMessageSequence(),
    };
  }

  private static OpenDialogUnmatchedFallback CreateDefaultUnmatchedFallback()
  {
    return new OpenDialogUnmatchedFallback
    {
      Enabled = true,
      TryButtons =
      [
        new OpenDialogFallbackButton
        {
          Label = "取消连接图元",
          ButtonContains = ["取消连接图元", "Unjoin Elements"],
          Click = "commandLink1",
          ClickResult = 1001,
        },
        new OpenDialogFallbackButton
        {
          Label = "确定",
          ButtonContains = ["确定", "OK"],
          Click = "ok",
          ClickResult = 1,
        },
        new OpenDialogFallbackButton
        {
          Label = "关闭",
          ButtonContains = ["关闭", "Close"],
          Click = "close",
          ClickResult = 8,
        },
      ],
    };
  }
}
