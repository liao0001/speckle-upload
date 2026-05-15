using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitSharedResources.Helpers.Extensions;

namespace SpeckleUpload.Services;

public static class DocumentService
{
  public static void CloseAllDocuments(UIApplication uiApp)
  {
    PluginLog.Step("Doc", "CloseAllDocuments: begin");
    var app = uiApp.Application;
    var documents = app.Documents.Cast<Document>().ToList();
    PluginLog.Step("Doc", $"CloseAllDocuments: found {documents.Count} document(s) in application");

    foreach (var document in documents)
    {
      if (document.IsLinked)
      {
        PluginLog.Step("Doc", $"CloseAllDocuments: skip linked document title=\"{document.Title}\"");
        continue;
      }

      PluginLog.Step("Doc", $"CloseAllDocuments: closing document title=\"{document.Title}\" path=\"{document.PathName}\"");
      document.Close(false);
      PluginLog.Step("Doc", $"CloseAllDocuments: closed title=\"{document.Title}\"");
    }

    PluginLog.Step("Doc", "CloseAllDocuments: end");
  }

  public static Document OpenDocument(UIApplication uiApp, string filePath)
  {
    PluginLog.Step("Doc", $"OpenDocument: begin filePath=\"{filePath}\"");

    if (!File.Exists(filePath))
    {
      PluginLog.Step("Doc", $"OpenDocument: file not found \"{filePath}\"");
      throw new FileNotFoundException($"Revit file not found: {filePath}");
    }

    PluginLog.Step("Doc", "OpenDocument: file exists, building ModelPath");
    var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(filePath);
    var openOptions = new OpenOptions
    {
      DetachFromCentralOption = DetachFromCentralOption.DoNotDetach,
      Audit = false,
    };

    PluginLog.Step("Doc", "OpenDocument: calling OpenAndActivateDocument");
    uiApp.OpenAndActivateDocument(modelPath, openOptions, false);
    var activeDoc = uiApp.ActiveUIDocument?.Document;

    if (activeDoc == null)
    {
      PluginLog.Step("Doc", "OpenDocument: ActiveUIDocument is null after open");
      throw new InvalidOperationException($"Failed to open document: {filePath}");
    }

    PluginLog.Step("Doc", $"OpenDocument: success title=\"{activeDoc.Title}\" path=\"{activeDoc.PathName}\"");
    return activeDoc;
  }

  public static void CloseActiveDocument(UIApplication uiApp)
  {
    PluginLog.Step("Doc", "CloseActiveDocument: begin");
    var activeDoc = uiApp.ActiveUIDocument?.Document;
    if (activeDoc == null)
    {
      PluginLog.Step("Doc", "CloseActiveDocument: no active document, skip");
      return;
    }

    if (activeDoc.IsLinked)
    {
      PluginLog.Step("Doc", "CloseActiveDocument: active is linked, skip");
      return;
    }

    PluginLog.Step("Doc", $"CloseActiveDocument: closing title=\"{activeDoc.Title}\"");
    activeDoc.Close(false);
    PluginLog.Step("Doc", "CloseActiveDocument: end");
  }

  public static List<Element> GetPhysicalObjects(Document document)
  {
    PluginLog.Step("Doc", $"GetPhysicalObjects: begin document=\"{document.Title}\"");

    var elements = new FilteredElementCollector(document)
      .WhereElementIsNotElementType()
      .WhereElementIsViewIndependent()
      .ToElements()
      .Where(element => element.IsPhysicalElement())
      .ToList();

    PluginLog.Step("Doc", $"GetPhysicalObjects: after collector count={elements.Count}");

    var filtered = FilterHiddenDesignOptions(document, elements);
    PluginLog.Step("Doc", $"GetPhysicalObjects: after design-option filter count={filtered.Count}");

    return filtered;
  }

  private static List<Element> FilterHiddenDesignOptions(Document document, List<Element> selection)
  {
    PluginLog.Step("Doc", $"FilterHiddenDesignOptions: input count={selection.Count}");

    var hasSecondaryDesignOptions = new FilteredElementCollector(document)
      .OfClass(typeof(DesignOption))
      .Cast<DesignOption>()
      .Any(option => !option.IsPrimary);

    PluginLog.Step("Doc", $"FilterHiddenDesignOptions: hasSecondaryDesignOptions={hasSecondaryDesignOptions}");

    if (!hasSecondaryDesignOptions)
    {
      PluginLog.Step("Doc", "FilterHiddenDesignOptions: no secondary options, return as-is");
      return selection;
    }

    var activeDesignOption = DesignOption.GetActiveDesignOptionId(document);
    PluginLog.Step("Doc", $"FilterHiddenDesignOptions: activeDesignOption id={activeDesignOption.IntegerValue}");

    if (activeDesignOption != ElementId.InvalidElementId)
    {
      var result = selection
        .Where(element => element.DesignOption == null || element.DesignOption.Id == activeDesignOption)
        .ToList();
      PluginLog.Step("Doc", $"FilterHiddenDesignOptions: filtered by active option count={result.Count}");
      return result;
    }

    var primary = selection
      .Where(element => element.DesignOption == null || element.DesignOption.IsPrimary)
      .ToList();
    PluginLog.Step("Doc", $"FilterHiddenDesignOptions: filtered by primary option count={primary.Count}");
    return primary;
  }
}
