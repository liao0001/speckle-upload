using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitSharedResources.Helpers.Extensions;

namespace SpeckleUpload.Services;

public static class DocumentService
{
  public static void CloseAllDocuments(UIApplication uiApp)
  {
    var app = uiApp.Application;
    var documents = app.Documents.Cast<Document>().ToList();

    foreach (var document in documents)
    {
      if (document.IsLinked)
      {
        continue;
      }

      document.Close(false);
    }
  }

  public static Document OpenDocument(UIApplication uiApp, string filePath)
  {
    if (!File.Exists(filePath))
    {
      throw new FileNotFoundException($"Revit file not found: {filePath}");
    }

    var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(filePath);
    var openOptions = new OpenOptions
    {
      DetachFromCentralOption = DetachFromCentralOption.DoNotDetach,
      Audit = false,
    };

    uiApp.OpenAndActivateDocument(modelPath, openOptions, false);
    var activeDoc = uiApp.ActiveUIDocument?.Document;

    if (activeDoc == null)
    {
      throw new InvalidOperationException($"Failed to open document: {filePath}");
    }

    return activeDoc;
  }

  public static void CloseActiveDocument(UIApplication uiApp)
  {
    var activeDoc = uiApp.ActiveUIDocument?.Document;
    if (activeDoc == null || activeDoc.IsLinked)
    {
      return;
    }

    activeDoc.Close(false);
  }

  public static List<Element> GetPhysicalObjects(Document document)
  {
    var elements = new FilteredElementCollector(document)
      .WhereElementIsNotElementType()
      .WhereElementIsViewIndependent()
      .ToElements()
      .Where(element => element.IsPhysicalElement())
      .ToList();

    return FilterHiddenDesignOptions(document, elements);
  }

  private static List<Element> FilterHiddenDesignOptions(Document document, List<Element> selection)
  {
    var hasSecondaryDesignOptions = new FilteredElementCollector(document)
      .OfClass(typeof(DesignOption))
      .Cast<DesignOption>()
      .Any(option => !option.IsPrimary);

    if (!hasSecondaryDesignOptions)
    {
      return selection;
    }

    var activeDesignOption = DesignOption.GetActiveDesignOptionId(document);
    if (activeDesignOption != ElementId.InvalidElementId)
    {
      return selection
        .Where(element => element.DesignOption == null || element.DesignOption.Id == activeDesignOption)
        .ToList();
    }

    return selection
      .Where(element => element.DesignOption == null || element.DesignOption.IsPrimary)
      .ToList();
  }
}
