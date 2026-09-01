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

    var patchedFiles = 0;
    foreach (var dllPath in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
    {
      if (ShouldSkip(Path.GetFileNameWithoutExtension(dllPath)))
      {
        continue;
      }

      if (PatchAssembly(dllPath))
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

  private static bool PatchAssembly(string path)
  {
    var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(Path.GetDirectoryName(path)!);

    var readerParameters = new ReaderParameters
    {
      AssemblyResolver = resolver,
      ReadWrite = true,
      InMemory = true,
    };

    using var assembly = AssemblyDefinition.ReadAssembly(path, readerParameters);
    var changed = false;

    foreach (var module in assembly.Modules)
    {
      foreach (var type in module.Types)
      {
        changed |= PatchType(type, module);
      }
    }

    if (!changed)
    {
      return false;
    }

    assembly.Write(path);
    return true;
  }

  private static bool PatchType(TypeDefinition type, ModuleDefinition module)
  {
    var changed = false;

    foreach (var method in type.Methods)
    {
      if (!method.HasBody)
      {
        continue;
      }

      changed |= PatchMethod(method, module);
    }

    foreach (var nested in type.NestedTypes)
    {
      changed |= PatchType(nested, module);
    }

    return changed;
  }

  private static bool PatchMethod(MethodDefinition method, ModuleDefinition module)
  {
    var instructions = method.Body.Instructions;
    if (instructions.Count == 0)
    {
      return false;
    }

    var elementIdType = ResolveElementIdType(module);
    if (elementIdType == null)
    {
      return false;
    }

    var getValue = new MethodReference("get_Value", module.TypeSystem.Int64, elementIdType)
    {
      HasThis = true,
    };

    var intCtor = new MethodReference(".ctor", module.TypeSystem.Void, elementIdType)
    {
      HasThis = true,
    };
    intCtor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));

    var longCtor = new MethodReference(".ctor", module.TypeSystem.Void, elementIdType)
    {
      HasThis = true,
    };
    longCtor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int64));

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
        instruction.Operand = getValue;
        processor.InsertAfter(instruction, processor.Create(OpCodes.Conv_Ovf_I4));
        changed = true;
        index++;
        continue;
      }

      if (methodReference.Name == ".ctor" && methodReference.Parameters.Count == 1
          && methodReference.Parameters[0].ParameterType.FullName == "System.Int32")
      {
        instruction.Operand = longCtor;
        processor.InsertBefore(instruction, processor.Create(OpCodes.Conv_I8));
        changed = true;
        index++;
      }
    }

    return changed;
  }

  private static TypeReference? ResolveElementIdType(ModuleDefinition module)
  {
    var revitApi = module.AssemblyReferences.FirstOrDefault(reference =>
      string.Equals(reference.Name, "RevitAPI", StringComparison.OrdinalIgnoreCase)
    );

    if (revitApi == null)
    {
      return null;
    }

    return new TypeReference("Autodesk.Revit.DB", "ElementId", module, revitApi);
  }
}
