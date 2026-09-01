using System.Reflection;
using Autodesk.Revit.DB;
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
      Console.Error.WriteLine("Usage: PatchElementIdForRevit2026 <plugin-output-directory>");
      return 1;
    }

    var directory = Path.GetFullPath(args[0]);
    if (!Directory.Exists(directory))
    {
      Console.Error.WriteLine($"Directory not found: {directory}");
      return 1;
    }

    var revitApiPath = typeof(ElementId).Assembly.Location;
    if (string.IsNullOrWhiteSpace(revitApiPath) || !File.Exists(revitApiPath))
    {
      Console.Error.WriteLine("RevitAPI.dll not found. Ensure Nice3point.Revit.Api.RevitAPI is restored.");
      return 1;
    }

    Console.WriteLine($"RevitAPI: {revitApiPath}");

    var getValueMethod = typeof(ElementId).GetProperty(nameof(ElementId.Value))!.GetMethod!;
    var longCtor = typeof(ElementId).GetConstructor([typeof(long)])!;

    var patchedFiles = 0;
    foreach (var dllPath in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
    {
      if (ShouldSkip(Path.GetFileNameWithoutExtension(dllPath)))
      {
        continue;
      }

      if (PatchAssembly(dllPath, revitApiPath, getValueMethod, longCtor))
      {
        patchedFiles++;
        Console.WriteLine($"patched: {Path.GetFileName(dllPath)}");
      }
    }

    Console.WriteLine($"PatchElementIdForRevit2026: {patchedFiles} file(s) in {directory}");
    return 0;
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
    MethodInfo getValueMethod,
    ConstructorInfo longCtorInfo
  )
  {
    var resolver = new RevitApiAssemblyResolver(revitApiPath, [Path.GetDirectoryName(path)!]);

    var readerParameters = new ReaderParameters
    {
      AssemblyResolver = resolver,
      ReadWrite = true,
      InMemory = true,
    };

    using var assembly = AssemblyDefinition.ReadAssembly(path, readerParameters);
    var module = assembly.MainModule;
    var getValueRef = module.ImportReference(getValueMethod);
    var longCtorRef = module.ImportReference(longCtorInfo);

    var changed = false;
    foreach (var type in module.Types)
    {
      changed |= PatchType(type, getValueRef, longCtorRef);
    }

    if (!changed)
    {
      return false;
    }

    assembly.Write(path, new WriterParameters { AssemblyResolver = resolver });
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

    internal RevitApiAssemblyResolver(string revitApiPath, IEnumerable<string> searchDirectories)
    {
      _revitApiPath = revitApiPath;
      foreach (var directory in searchDirectories)
      {
        if (!string.IsNullOrWhiteSpace(directory))
        {
          AddSearchDirectory(directory);
        }
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
