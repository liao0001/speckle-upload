using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitSharedResources.Helpers.Extensions;

namespace SpeckleUpload.Services;

public static class DocumentService
{
  /// <summary>
  /// 关闭除 <paramref name="keepOpen"/> 以外的所有非链接文档。
  /// 注意：不能先关闭当前活动文档；应先 <see cref="OpenDocument"/> 再调用本方法。
  /// </summary>
  public static void CloseOtherDocumentsExcept(UIApplication uiApp, Document keepOpen)
  {
    var keepLabel = SafeDocumentLabel(keepOpen);
    PluginLog.Step("Doc", $"CloseOtherDocumentsExcept: begin keep={keepLabel}");

    var toClose = uiApp
      .Application.Documents.Cast<Document>()
      .Where(document => !document.IsLinked && !IsSameDocument(document, keepOpen))
      .Select(document => new DocumentCloseTarget(document, SafeDocumentLabel(document)))
      .ToList();

    PluginLog.Step("Doc", $"CloseOtherDocumentsExcept: open count={uiApp.Application.Documents.Size} closeOthers={toClose.Count}");

    foreach (var target in toClose)
    {
      PluginLog.Step("Doc", $"CloseOtherDocumentsExcept: closing {target.Label}");
      TryCloseDocument(target.Document, target.Label);
    }

    PluginLog.Step("Doc", "CloseOtherDocumentsExcept: end");
  }

  /// <summary>
  /// 确保上传目标 RVT 为当前活动文档：若已是当前文档则只关其它；否则先打开目标再关其它。
  /// </summary>
  public static Document PrepareDocumentForUpload(UIApplication uiApp, string filePath)
  {
    PluginLog.Step("Doc", $"PrepareDocumentForUpload: begin target=\"{filePath}\"");

    if (!File.Exists(filePath))
    {
      PluginLog.Step("Doc", "PrepareDocumentForUpload: file not found");
      throw new FileNotFoundException($"Revit file not found: {filePath}");
    }

    var active = uiApp.ActiveUIDocument?.Document;
    if (active != null && PathsEqual(NormalizePath(active.PathName), NormalizePath(filePath)))
    {
      PluginLog.Step("Doc", "PrepareDocumentForUpload: target already active");
      CloseOtherDocumentsExcept(uiApp, active);
      return active;
    }

    PluginLog.Step("Doc", "PrepareDocumentForUpload: opening target (switches active document)");
    var opened = OpenDocument(uiApp, filePath);
    CloseOtherDocumentsExcept(uiApp, opened);

    PluginLog.Step("Doc", "PrepareDocumentForUpload: end");
    return opened;
  }

  public static bool TryCloseDocument(Document document, string? label = null)
  {
    var docLabel = label ?? SafeDocumentLabel(document);
    try
    {
      document.Close(false);
      PluginLog.Step("Doc", $"TryCloseDocument: closed {docLabel}");
      return true;
    }
    catch (Exception ex)
    {
      PluginLog.Step("Doc", $"TryCloseDocument: failed {docLabel}: {ex.GetType().Name} {ex.Message}");
      return false;
    }
  }

  private static string SafeDocumentLabel(Document document)
  {
    try
    {
      var path = string.IsNullOrWhiteSpace(document.PathName) ? "(no path)" : document.PathName;
      return $"\"{document.Title}\" path=\"{path}\"";
    }
    catch (Exception ex)
    {
      return $"(document unavailable: {ex.GetType().Name})";
    }
  }

  private readonly struct DocumentCloseTarget
  {
    public DocumentCloseTarget(Document document, string label)
    {
      Document = document;
      Label = label;
    }

    public Document Document { get; }
    public string Label { get; }
  }

  private static string? NormalizePath(string? path)
  {
    if (string.IsNullOrWhiteSpace(path))
    {
      return null;
    }

    try
    {
      return Path.GetFullPath(path);
    }
    catch
    {
      return path.Trim();
    }
  }

  private static bool PathsEqual(string? a, string? b)
  {
    if (a == null || b == null)
    {
      return false;
    }

    return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>
  /// Revit 有时对同一文档返回不同 Document 实例，需用路径辅助判断。
  /// </summary>
  private static bool IsSameDocument(Document a, Document b)
  {
    if (ReferenceEquals(a, b))
    {
      return true;
    }

    return PathsEqual(NormalizePath(a.PathName), NormalizePath(b.PathName));
  }

  /// <summary>
  /// 关闭所有非链接文档（含当前活动文档）。Revit 在多数上下文中不允许 API 关闭活动文档，上传流程请改用
  /// <see cref="PrepareDocumentForUpload"/>。
  /// </summary>
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
        PluginLog.Step("Doc", $"CloseAllDocuments: skip linked {SafeDocumentLabel(document)}");
        continue;
      }

      var label = SafeDocumentLabel(document);
      PluginLog.Step("Doc", $"CloseAllDocuments: closing {label}");
      TryCloseDocument(document, label);
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

    var label = SafeDocumentLabel(activeDoc);
    PluginLog.Step("Doc", $"CloseActiveDocument: closing {label}");
    if (!TryCloseDocument(activeDoc, label))
    {
      PluginLog.Step("Doc", "CloseActiveDocument: TryCloseDocument returned false (e.g. still active in this API context)");
    }

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
