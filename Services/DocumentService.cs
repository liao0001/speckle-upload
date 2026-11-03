using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SpeckleUpload;
using SpeckleUpload.Revit;
using System.Diagnostics;

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
      RevitOpenDialogSuppression.CompleteOpenPhase();
      return active;
    }

    PluginLog.Step("Doc", "PrepareDocumentForUpload: opening target (switches active document)");
    var opened = OpenDocument(uiApp, filePath);
    CloseOtherDocumentsExcept(uiApp, opened);

    RevitOpenDialogSuppression.CompleteOpenPhase();
    PluginLog.Step("Doc", "PrepareDocumentForUpload: end (dialog suppression off before Speckle)");
    return opened;
  }

  /// <summary>
  /// 打开 API 返回后、Speckle 转换前：等待 Win32 收尾、泵 UI、Regenerate，确保模型就绪。
  /// </summary>
  public static void EnsureDocumentReadyForConversion(UIApplication uiApp, Document document)
  {
    PluginLog.Step("Doc", "EnsureDocumentReadyForConversion: begin");

    Win32DialogClicker.WaitForOpenPhaseComplete();

    var settleSeconds = PluginSettings.PostOpenSettleSeconds;
    if (settleSeconds > 0)
    {
      var settleWatch = Stopwatch.StartNew();
      var deadline = DateTime.UtcNow.AddSeconds(settleSeconds);
      while (DateTime.UtcNow < deadline)
      {
        try
        {
          System.Windows.Forms.Application.DoEvents();
        }
        catch
        {
          // ignore
        }

        Thread.Sleep(50);
      }

      settleWatch.Stop();
      PluginLog.StepElapsed(
        "Doc",
        $"EnsureDocumentReadyForConversion: UI settle {settleSeconds}s",
        settleWatch.ElapsedMilliseconds
      );
    }

    try
    {
      var regenWatch = Stopwatch.StartNew();
      using (var transaction = new Transaction(document, "SpeckleUpload pre-convert regenerate"))
      {
        if (transaction.Start() == TransactionStatus.Started)
        {
          document.Regenerate();
          transaction.Commit();
          regenWatch.Stop();
          PluginLog.StepElapsed("Doc", "EnsureDocumentReadyForConversion: Regenerate done", regenWatch.ElapsedMilliseconds);
        }
        else
        {
          PluginLog.Step("Doc", "EnsureDocumentReadyForConversion: Regenerate transaction not started, skip");
        }
      }
    }
    catch (Exception ex)
    {
      PluginLog.Step(
        "Doc",
        $"EnsureDocumentReadyForConversion: Regenerate failed {ex.GetType().Name} {ex.Message} (continue convert)"
      );
    }

    PluginLog.Step(
      "Doc",
      $"EnsureDocumentReadyForConversion: end title=\"{document.Title}\" isModifiable={document.IsModifiable}"
    );
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

  private static long ElementIdToLong(ElementId id) => ElementIdCompat.ToLong(id);

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
    var workshared = TryGetIsWorkshared(filePath, out var fileInfoDetail);
    PluginLog.Step("Doc", $"OpenDocument: {fileInfoDetail}");
    if (PluginSettings.EnableDialogSuppression)
    {
      RevitOpenDialogSuppression.ArmForOpen();
      RevitOpenDialogSuppression.BeginOpenDocument();
    }
    else
    {
      PluginLog.Step("Doc", "OpenDocument: built-in dialog suppression disabled (use AHK or SPECKLE_UPLOAD_ENABLE_DIALOG_SUPPRESSION)");
    }

    PluginLog.Step("Doc", "OpenDocument: calling OpenAndActivateDocument");
    var openWatch = Stopwatch.StartNew();
    try
    {
      OpenAndActivateWithWorkshareFallback(uiApp, modelPath, workshared);
    }
    finally
    {
      openWatch.Stop();
      PluginLog.StepElapsed("Doc", "OpenDocument: OpenAndActivateDocument returned", openWatch.ElapsedMilliseconds);
      if (PluginSettings.EnableDialogSuppression)
      {
        RevitOpenDialogSuppression.EndOpenDocument();
      }
    }

    var activeDoc = uiApp.ActiveUIDocument?.Document;

    if (activeDoc == null)
    {
      PluginLog.Step("Doc", "OpenDocument: ActiveUIDocument is null after open");
      throw new InvalidOperationException($"Failed to open document: {filePath}");
    }

    PluginLog.Step("Doc", $"OpenDocument: success title=\"{activeDoc.Title}\" path=\"{activeDoc.PathName}\"");
    return activeDoc;
  }

  /// <summary>
  /// 普通文件用 DoNotDetach。工作共享文件才分离并丢弃工作集，避免去找中心文件。
  /// 判断与实际文件不一致时，用另一套选项再打开一次。
  /// </summary>
  private static void OpenAndActivateWithWorkshareFallback(
    UIApplication uiApp,
    ModelPath modelPath,
    bool workshared
  )
  {
    try
    {
      OpenAndActivateOnce(uiApp, modelPath, workshared);
    }
    catch (Exception ex) when (CanRetryOpen(ex, workshared))
    {
      PluginLog.Step(
        "Doc",
        $"OpenDocument: first open failed {ex.GetType().Name}: {ex.Message}; retry workshared={!workshared}"
      );
      OpenAndActivateOnce(uiApp, modelPath, !workshared);
    }
  }

  private static void OpenAndActivateOnce(UIApplication uiApp, ModelPath modelPath, bool workshared)
  {
    var openOptions = CreateOpenOptions(workshared);
    PluginLog.Step("Doc", DescribeOpenOptions(workshared));
    uiApp.OpenAndActivateDocument(modelPath, openOptions, false);
  }

  private static OpenOptions CreateOpenOptions(bool workshared)
  {
    var openOptions = new OpenOptions
    {
      Audit = false,
      DetachFromCentralOption = workshared
        ? DetachFromCentralOption.DetachAndDiscardWorksets
        : DetachFromCentralOption.DoNotDetach,
    };

    if (workshared)
    {
      openOptions.AllowOpeningLocalByWrongUser = true;
    }

    return openOptions;
  }

  private static string DescribeOpenOptions(bool workshared)
  {
    return workshared
      ? "OpenDocument: OpenOptions detach=DetachAndDiscardWorksets AllowOpeningLocalByWrongUser=true (workshared, no central required)"
      : "OpenDocument: OpenOptions detach=DoNotDetach (not workshared)";
  }

  private static bool TryGetIsWorkshared(string filePath, out string detail)
  {
    try
    {
      var info = BasicFileInfo.Extract(filePath);
      try
      {
        detail =
          $"BasicFileInfo IsWorkshared={info.IsWorkshared} IsCentral={info.IsCentral} IsLocal={info.IsLocal} central=\"{info.CentralPath}\"";
        return info.IsWorkshared;
      }
      finally
      {
        info.Dispose();
      }
    }
    catch (Exception ex)
    {
      detail = $"BasicFileInfo failed {ex.GetType().Name}: {ex.Message}; assume not workshared";
      return false;
    }
  }

  private static bool CanRetryOpen(Exception ex, bool openedAsWorkshared)
  {
    var typeName = ex.GetType().Name;
    var message = ex.Message ?? string.Empty;

    if (openedAsWorkshared)
    {
      return ContainsText(message, "not workshared")
        || ContainsText(message, "未启用工作共享")
        || ContainsText(message, "不是工作共享");
    }

    return typeName.IndexOf("Central", StringComparison.OrdinalIgnoreCase) >= 0
      || ContainsText(message, "central")
      || ContainsText(message, "workset")
      || ContainsText(message, "workshar")
      || ContainsText(message, "中心")
      || ContainsText(message, "工作集")
      || ContainsText(message, "工作共享");
  }

  private static bool ContainsText(string text, string value)
  {
    return text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
  }

  public static void CloseActiveDocument(UIApplication uiApp)
  {
    CloseUploadedDocument(uiApp, uiApp.ActiveUIDocument?.Document?.PathName);
  }

  /// <summary>
  /// 最终回调完成后异步关闭上传用的 RVT：先打开空白项目切换活动文档，再关闭目标文件。
  /// </summary>
  public static void CloseUploadedDocument(UIApplication uiApp, string? uploadedFilePath)
  {
    PluginLog.Step("Doc", $"CloseUploadedDocument: begin path=\"{uploadedFilePath ?? "(active)"}\"");

    var target = FindOpenDocumentByPath(uiApp, uploadedFilePath) ?? uiApp.ActiveUIDocument?.Document;
    if (target == null)
    {
      PluginLog.Step("Doc", "CloseUploadedDocument: target not found, skip");
      return;
    }

    if (target.IsLinked)
    {
      PluginLog.Step("Doc", "CloseUploadedDocument: target is linked, skip");
      return;
    }

    var targetLabel = SafeDocumentLabel(target);
    Document? blankDocument = null;

    if (IsSameDocument(uiApp.ActiveUIDocument?.Document, target))
    {
      PluginLog.Step("Doc", "CloseUploadedDocument: target is active, opening blank project to switch away");
      try
      {
        var unitSystem = target.DisplayUnitSystem == DisplayUnit.IMPERIAL
          ? UnitSystem.Imperial
          : UnitSystem.Metric;
        blankDocument = uiApp.Application.NewProjectDocument(unitSystem);
        PluginLog.Step("Doc", $"CloseUploadedDocument: blank project opened title=\"{blankDocument.Title}\"");
      }
      catch (Exception ex)
      {
        PluginLog.Step("Doc", $"CloseUploadedDocument: NewProjectDocument failed: {ex.GetType().Name} {ex.Message}");
        return;
      }
    }

    if (!TryCloseDocument(target, targetLabel))
    {
      PluginLog.Step("Doc", "CloseUploadedDocument: failed to close uploaded document");
    }

    if (blankDocument != null)
    {
      var blankLabel = SafeDocumentLabel(blankDocument);
      PluginLog.Step("Doc", $"CloseUploadedDocument: closing temporary blank document {blankLabel}");
      TryCloseDocument(blankDocument, blankLabel);
    }

    PluginLog.Step("Doc", "CloseUploadedDocument: end");
  }

  private static Document? FindOpenDocumentByPath(UIApplication uiApp, string? filePath)
  {
    var normalized = NormalizePath(filePath);
    if (string.IsNullOrWhiteSpace(normalized))
    {
      return null;
    }

    return uiApp
      .Application.Documents.Cast<Document>()
      .FirstOrDefault(document => !document.IsLinked && PathsEqual(NormalizePath(document.PathName), normalized));
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
    PluginLog.Step("Doc", $"FilterHiddenDesignOptions: activeDesignOption id={ElementIdToLong(activeDesignOption)}");

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
