using Autodesk.Revit.DB;

namespace SpeckleUpload.Revit;

/// <summary>
/// 本地实现，避免 Revit 2026 上 NuGet 版 RevitSharedResources 调用已移除的 ElementId.IntegerValue。
/// </summary>
internal static class RevitElementExtensions
{
  internal static bool IsPhysicalElement(this Element element)
  {
    if (element.Category == null)
    {
      return false;
    }

    if (element.ViewSpecific)
    {
      return false;
    }

    if (ElementIdCompat.ToBuiltInCategory(element.Category.Id) == BuiltInCategory.OST_HVAC_Zones)
    {
      return false;
    }

    return element.Category.CategoryType == CategoryType.Model && element.Category.CanAddSubcategory;
  }
}
