using System.Reflection;
using System.Runtime.InteropServices;

namespace SpeckleUpload.Services;

/// <summary>
/// Revit 进程不会从插件目录探测 e_sqlite3.dll。上传前把本地库指到插件目录。
/// </summary>
internal static class SqliteNative
{
  private static int _ready;

  public static void EnsureLoaded()
  {
    if (Interlocked.Exchange(ref _ready, 1) != 0)
    {
      return;
    }

    var dir = Path.GetDirectoryName(typeof(SqliteNative).Assembly.Location);
    if (string.IsNullOrEmpty(dir))
    {
      PluginLog.Step("Speckle", "sqlite native: plugin directory unknown");
      return;
    }

    var dllPath = Path.Combine(dir, "e_sqlite3.dll");
    if (!File.Exists(dllPath))
    {
      PluginLog.Step("Speckle", $"sqlite native missing: {dllPath}");
      return;
    }

#if REVIT2026
    var provider = LoadProviderAssembly();
    if (provider == null)
    {
      return;
    }

    NativeLibrary.SetDllImportResolver(
      provider,
      (libraryName, _, _) =>
      {
        if (!libraryName.Equals("e_sqlite3", StringComparison.OrdinalIgnoreCase))
        {
          return IntPtr.Zero;
        }

        return NativeLibrary.Load(dllPath);
      }
    );
    PluginLog.Step("Speckle", $"sqlite native resolver set path={dllPath}");
#else
    if (SetDllDirectory(dir))
    {
      PluginLog.Step("Speckle", $"sqlite native SetDllDirectory path={dir}");
    }
    else
    {
      PluginLog.Step("Speckle", $"sqlite native SetDllDirectory failed path={dir}");
    }
#endif
  }

#if REVIT2026
  private static Assembly? LoadProviderAssembly()
  {
    var loaded = AppDomain.CurrentDomain
      .GetAssemblies()
      .FirstOrDefault(assembly => assembly.GetName().Name == "SQLitePCLRaw.provider.e_sqlite3");
    if (loaded != null)
    {
      return loaded;
    }

    try
    {
      return Assembly.Load("SQLitePCLRaw.provider.e_sqlite3");
    }
    catch (Exception ex)
    {
      PluginLog.Step("Speckle", $"sqlite provider load failed: {ex.GetType().Name} {ex.Message}");
      return null;
    }
  }
#else
  [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern bool SetDllDirectory(string lpPathName);
#endif
}
