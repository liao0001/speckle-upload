using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PatchElementIdForRevit2026;

internal static class Program
{
  private const string ElementIdTypeName = "Autodesk.Revit.DB.ElementId";

  private static readonly HashSet<string> SkipAssemblies = new(StringComparer.OrdinalIgnoreCase)
  {
    "RevitAPI",
    "RevitAPIUI",
    "Nice3point.Revit.Api.RevitAPI",
    "Nice3point.Revit.Api.RevitAPIUI",
    "PatchElementIdForRevit2026",
  };

  internal static int Main(string[] args)
  {
    if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
    {
      Console.Error.WriteLine("Usage: PatchElementIdForRevit2026 <plugin-output-directory> [RevitAPI.dll]");
      return 1;
    }

    var directory = Path.GetFullPath(args[0]);
    if (!Directory.Exists(directory))
    {
      Console.Error.WriteLine($"Directory not found: {directory}");
      return 1;
    }

    var revitApiPath = ResolveRevitApiPath(args.Length > 1 ? args[1] : null);
    if (revitApiPath == null)
    {
      Console.Error.WriteLine(
        "RevitAPI.dll not found. Pass path as 2nd argument or set REVIT_API_DLL / install Nice3point.Revit.Api.RevitAPI."
      );
      return 1;
    }

    Console.WriteLine($"RevitAPI: {revitApiPath}");

    using var revitApiAssembly = AssemblyDefinition.ReadAssembly(revitApiPath);
    var elementIdType = revitApiAssembly.MainModule.GetType(ElementIdTypeName);
    if (elementIdType == null)
    {
      Console.Error.WriteLine($"Type not found in RevitAPI: {ElementIdTypeName}");
      return 1;
    }

    var getValueDef = elementIdType.Methods.FirstOrDefault(method =>
      method.Name == "get_Value" && method.Parameters.Count == 0 && !method.IsStatic
    );
    var longCtorDef = elementIdType.Methods.FirstOrDefault(method =>
      method.IsConstructor
      && method.Parameters.Count == 1
      && method.Parameters[0].ParameterType.FullName == "System.Int64"
    );

    if (getValueDef == null || longCtorDef == null)
    {
      Console.Error.WriteLine("RevitAPI ElementId.get_Value or .ctor(long) not found.");
      return 1;
    }

    // 在临时目录批量补丁，避免 Cecil 解析依赖时锁住输出目录中的其它 DLL
    var workDir = Path.Combine(Path.GetTempPath(), "speckle-patch-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(workDir);

    try
    {
      foreach (var dllPath in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
      {
        File.Copy(dllPath, Path.Combine(workDir, Path.GetFileName(dllPath)), overwrite: true);
      }

      var patchedNames = new List<string>();
      foreach (var dllPath in Directory.EnumerateFiles(workDir, "*.dll", SearchOption.TopDirectoryOnly))
      {
        if (ShouldSkip(Path.GetFileNameWithoutExtension(dllPath)))
        {
          continue;
        }

        if (PatchAssembly(dllPath, revitApiPath, getValueDef, longCtorDef))
        {
          patchedNames.Add(Path.GetFileName(dllPath));
          Console.WriteLine($"patched: {Path.GetFileName(dllPath)}");
        }
      }

      foreach (var fileName in patchedNames)
      {
        var source = Path.Combine(workDir, fileName);
        var target = Path.Combine(directory, fileName);
        File.Copy(source, target, overwrite: true);
      }

      Console.WriteLine($"PatchElementIdForRevit2026: {patchedNames.Count} file(s) in {directory}");
      return 0;
    }
    finally
    {
      try
      {
        Directory.Delete(workDir, recursive: true);
      }
      catch
      {
        // 临时目录清理失败不影响补丁结果
      }
    }
  }

  private static string? ResolveRevitApiPath(string? explicitPath)
  {
    if (!string.IsNullOrWhiteSpace(explicitPath))
    {
      var full = Path.GetFullPath(explicitPath);
      return File.Exists(full) ? full : null;
    }

    var fromEnv = Environment.GetEnvironmentVariable("REVIT_API_DLL");
    if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
    {
      return Path.GetFullPath(fromEnv);
    }

    var nugetRoot =
      Environment.GetEnvironmentVariable("NUGET_PACKAGES")
      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    var packageRoot = Path.Combine(nugetRoot, "nice3point.revit.api.revitapi");
    if (!Directory.Exists(packageRoot))
    {
      return null;
    }

    return PickRevitApiDll(Directory.EnumerateFiles(packageRoot, "RevitAPI.dll", SearchOption.AllDirectories));
  }

  private static string? PickRevitApiDll(IEnumerable<string> candidates)
  {
    var paths = candidates.ToList();
    if (paths.Count == 0)
    {
      return null;
    }

    static bool HasPathSegment(string path, string segment) =>
      path.Contains($"{Path.DirectorySeparatorChar}{segment}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    var fromContent = paths
      .Where(path => HasPathSegment(path, "content"))
      .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
      .FirstOrDefault();
    if (fromContent != null)
    {
      return fromContent;
    }

    var notRef = paths
      .Where(path => !HasPathSegment(path, "ref"))
      .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
      .FirstOrDefault();
    if (notRef != null)
    {
      return notRef;
    }

    return paths.OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
  }

  private static bool ShouldSkip(string assemblyName)
  {
    foreach (var skip in SkipAssemblies)
    {
      if (assemblyName.StartsWith(skip, StringComparison.OrdinalIgnoreCase))
      {
        return true;
      }
    }

    return false;
  }

  private static bool PatchAssembly(
    string path,
    string revitApiPath,
    MethodDefinition getValueDef,
    MethodDefinition longCtorDef
  )
  {
    using var resolver = new RevitApiAssemblyResolver(revitApiPath, Path.GetDirectoryName(path)!);

    var readerParameters = new ReaderParameters
    {
      AssemblyResolver = resolver,
      InMemory = true,
      ReadWrite = false,
    };

    using var assembly = AssemblyDefinition.ReadAssembly(path, readerParameters);
    var module = assembly.MainModule;
    var getValueRef = module.ImportReference(getValueDef);
    var longCtorRef = module.ImportReference(longCtorDef);

    var changed = false;
    foreach (var type in module.Types)
    {
      changed |= PatchType(type, getValueRef, longCtorRef);
    }

    if (!changed)
    {
      return false;
    }

    var tempPath = path + ".patchtmp";
    assembly.Write(tempPath);
    File.Move(tempPath, path, overwrite: true);
    return true;
  }

  private static bool PatchType(TypeDefinition type, MethodReference getValueRef, MethodReference longCtorRef)
  {
    var changed = false;

    foreach (var method in type.Methods)
    {
      if (!method.HasBody)
      {
        continue;
      }

      changed |= PatchMethod(method, getValueRef, longCtorRef);
    }

    foreach (var nested in type.NestedTypes)
    {
      changed |= PatchType(nested, getValueRef, longCtorRef);
    }

    return changed;
  }

  private static bool PatchMethod(MethodDefinition method, MethodReference getValueRef, MethodReference longCtorRef)
  {
    var instructions = method.Body.Instructions;
    if (instructions.Count == 0)
    {
      return false;
    }

    var changed = false;
    var processor = method.Body.GetILProcessor();

    for (var index = 0; index < instructions.Count; index++)
    {
      var instruction = instructions[index];
      if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)
      {
        continue;
      }

      if (instruction.Operand is not MethodReference methodReference)
      {
        continue;
      }

      if (methodReference.DeclaringType?.FullName != ElementIdTypeName)
      {
        continue;
      }

      if (methodReference.Name == "get_IntegerValue")
      {
        instruction.Operand = getValueRef;
        processor.InsertAfter(instruction, processor.Create(OpCodes.Conv_Ovf_I4));
        changed = true;
        index++;
        continue;
      }

      if (methodReference.Name == ".ctor" && methodReference.Parameters.Count == 1
          && methodReference.Parameters[0].ParameterType.FullName == "System.Int32")
      {
        instruction.Operand = longCtorRef;
        processor.InsertBefore(instruction, processor.Create(OpCodes.Conv_I8));
        changed = true;
        index++;
      }
    }

    return changed;
  }

  private sealed class RevitApiAssemblyResolver : DefaultAssemblyResolver
  {
    private readonly string _revitApiPath;
    private AssemblyDefinition? _revitApiAssembly;

    internal RevitApiAssemblyResolver(string revitApiPath, string dependencySearchDirectory)
    {
      _revitApiPath = revitApiPath;
      if (!string.IsNullOrWhiteSpace(dependencySearchDirectory))
      {
        AddSearchDirectory(dependencySearchDirectory);
      }
    }

    public override AssemblyDefinition Resolve(AssemblyNameReference name)
    {
      if (string.Equals(name.Name, "RevitAPI", StringComparison.OrdinalIgnoreCase))
      {
        _revitApiAssembly ??= AssemblyDefinition.ReadAssembly(_revitApiPath);
        return _revitApiAssembly;
      }

      return base.Resolve(name);
    }
  }
}
