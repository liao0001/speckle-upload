using System.Reflection;

namespace SpeckleUpload.Services;

public static class PluginLog
{
  private static readonly object Sync = new();
  private static string? _logFilePath;
  private static bool _headerWritten;

  /// <summary>
  /// 日志文件路径（插件目录下的 SpeckleUpload.log）。首次写入前根据程序集位置解析。
  /// </summary>
  public static string? LogFilePath
  {
    get
    {
      EnsureInitialized();
      return _logFilePath;
    }
  }

  public static void EnsureInitialized()
  {
    lock (Sync)
    {
      if (_logFilePath != null)
      {
        return;
      }

      try
      {
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        var dir = string.IsNullOrEmpty(assemblyPath)
          ? null
          : Path.GetDirectoryName(assemblyPath);

        if (string.IsNullOrEmpty(dir))
        {
          dir = Path.Combine(Path.GetTempPath(), "SpeckleUpload");
        }

        _logFilePath = Path.Combine(dir, "SpeckleUpload.log");
      }
      catch
      {
        _logFilePath = Path.Combine(Path.GetTempPath(), "SpeckleUpload", "SpeckleUpload.log");
      }
    }
  }

  /// <summary>
  /// 记录一步操作（便于检索）。
  /// </summary>
  public static void Step(string phase, string message)
  {
    Write($"[{phase}] {message}");
  }

  public static void StepElapsed(string phase, string message, long elapsedMs)
  {
    Step(phase, $"{message} elapsedMs={elapsedMs}");
  }

  public static void Write(string message)
  {
    try
    {
      EnsureInitialized();
      var path = _logFilePath;
      if (string.IsNullOrEmpty(path))
      {
        return;
      }

      var directory = Path.GetDirectoryName(path);
      if (!string.IsNullOrEmpty(directory))
      {
        Directory.CreateDirectory(directory);
      }

      lock (Sync)
      {
        if (!_headerWritten)
        {
          _headerWritten = true;
          File.AppendAllText(
            path,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [Init] Log file: {path}{Environment.NewLine}"
          );
        }

        File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
      }
    }
    catch
    {
      // Ignore logging failures.
    }
  }
}
