using Autodesk.Revit.DB;
using Speckle.Core.Models;

namespace SpeckleUpload.Services;

/// <summary>
/// 按官方 Speckle Next 风格组织提交树：Level → Category → Type。
/// 不把宿主构件（如依附楼板的结构柱）嵌套到 Host 下。
/// </summary>
public sealed class LevelCategoryCommitBuilder
{
  private readonly Document _document;
  private readonly Dictionary<string, Collection> _collectionCache = new(StringComparer.Ordinal);

  public LevelCategoryCommitBuilder(Document document)
  {
    _document = document;
  }

  public void IncludeObject(Base conversionResult, Element nativeElement, Collection rootCommitObject)
  {
    var hostCollection = GetOrCreateHostCollection(nativeElement, rootCommitObject);
    hostCollection.elements ??= new List<Base>();
    hostCollection.elements.Add(conversionResult);
  }

  private Collection GetOrCreateHostCollection(Element element, Collection root)
  {
    var path = new[]
    {
      ResolveLevelName(element),
      element.Category?.Name ?? "No category",
      ResolveTypeName(element),
    };

    var cacheKey = string.Join("\u001f", path);
    if (_collectionCache.TryGetValue(cacheKey, out var cached))
    {
      return cached;
    }

    root.elements ??= new List<Base>();
    Collection parent = root;
    var partialKey = string.Empty;

    for (var i = 0; i < path.Length; i++)
    {
      var segment = path[i];
      partialKey = i == 0 ? segment : partialKey + "\u001f" + segment;

      if (_collectionCache.TryGetValue(partialKey, out var existing))
      {
        parent = existing;
        continue;
      }

      var collectionType = i switch
      {
        0 => "Revit Level",
        1 => "Revit Category",
        _ => "Revit Type",
      };

      var child = new Collection(segment, collectionType);
      parent.elements ??= new List<Base>();
      parent.elements.Add(child);
      _collectionCache[partialKey] = child;
      parent = child;
    }

    return parent;
  }

  private string ResolveLevelName(Element element)
  {
    var level = ResolveLevel(element);
    return level?.Name ?? "No Level";
  }

  private Level? ResolveLevel(Element element)
  {
    if (element.LevelId != ElementId.InvalidElementId)
    {
      return _document.GetElement(element.LevelId) as Level;
    }

    // 柱等常用底部标高参数，LevelId 可能无效
    var baseLevelId = element.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)?.AsElementId();
    if (baseLevelId != null && baseLevelId != ElementId.InvalidElementId)
    {
      return _document.GetElement(baseLevelId) as Level;
    }

    var scheduleLevelId = element.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)?.AsElementId();
    if (scheduleLevelId != null && scheduleLevelId != ElementId.InvalidElementId)
    {
      return _document.GetElement(scheduleLevelId) as Level;
    }

    return null;
  }

  private static string ResolveTypeName(Element element)
  {
    var typeId = element.GetTypeId();
    if (typeId == ElementId.InvalidElementId)
    {
      return "No type";
    }

    var typeElement = element.Document.GetElement(typeId);
    return string.IsNullOrWhiteSpace(typeElement?.Name) ? "No type" : typeElement!.Name;
  }
}
