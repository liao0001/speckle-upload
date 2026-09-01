using Autodesk.Revit.DB;

namespace SpeckleUpload.Revit;

internal static class ElementIdCompat
{
  internal static long ToLong(ElementId id)
  {
#if REVIT2022
    return id.IntegerValue;
#else
    return id.Value;
#endif
  }

  internal static int ToInt32(ElementId id)
  {
#if REVIT2022
    return id.IntegerValue;
#else
    return checked((int)id.Value);
#endif
  }

  internal static BuiltInCategory ToBuiltInCategory(ElementId categoryId)
  {
    return (BuiltInCategory)ToInt32(categoryId);
  }
}
