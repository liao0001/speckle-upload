using System.Collections.Concurrent;
using System.Text;
using System.Diagnostics;
using Autodesk.Revit.DB;
using Objects.Converter.Revit;
using RevitSharedResources.Models;
using Speckle.Core.Api;
using Speckle.Core.Api.GraphQL.Models;
using Speckle.Core.Credentials;
using Speckle.Core.Models;
using Speckle.Core.Transports;
using SpeckleUpload.Models;

namespace SpeckleUpload.Services;

public static class SpeckleSendService
{
  /// <summary>在 Revit ExternalEvent 线程上同步执行，避免 await 切到线程池后无法安全使用 Revit API。</summary>
  public static UploadCallbackPayload SendPhysicalObjects(
    Document document,
    UploadRequest request,
    UploadCallbackReporter? reporter = null
  )
  {
    return SendPhysicalObjectsCore(document, request, reporter).GetAwaiter().GetResult();
  }

  private static async Task<UploadCallbackPayload> SendPhysicalObjectsCore(
    Document document,
    UploadRequest request,
    UploadCallbackReporter? reporter
  )
  {
    PluginLog.Step("Speckle", $"SendPhysicalObjects: begin requestId={request.RequestId}");

    PluginLog.Step("Speckle", "SendPhysicalObjectsAsync: RevitConverterState.Push");
    using var converterState = RevitConverterState.Push();

    PluginLog.Step("Speckle", "SendPhysicalObjectsAsync: new ConverterRevit");
    var converter = new ConverterRevit();
    PluginLog.Step("Speckle", "SendPhysicalObjectsAsync: SetContextDocument + clear report");
    converter.SetContextDocument(document);
    converter.Report.ReportObjects.Clear();

    PluginLog.Step("Speckle", "SendPhysicalObjectsAsync: GetPhysicalObjects");
    var collectWatch = Stopwatch.StartNew();
    var physicalObjects = DocumentService.GetPhysicalObjects(document);
    collectWatch.Stop();
    PluginLog.StepElapsed(
      "Speckle",
      $"SendPhysicalObjectsAsync: GetPhysicalObjects count={physicalObjects.Count}",
      collectWatch.ElapsedMilliseconds
    );
    if (physicalObjects.Count == 0)
    {
      PluginLog.Step("Speckle", "SendPhysicalObjectsAsync: no physical objects, abort");
      throw new InvalidOperationException("No physical objects found in the model.");
    }

    PluginLog.Step("Speckle", $"SendPhysicalObjectsAsync: physical count={physicalObjects.Count}");
    reporter?.ReportSpeckleStart();
    reporter?.BeginConvert(physicalObjects.Count);

    // 根对象带 ProjectInfo；提交树按 Level → Category → Type，不挂 Host
    var commitObject =
      converter.ConvertToSpeckle(document) as Collection
      ?? new Collection("Revit model", "model");
    commitObject.elements ??= new List<Base>();
    var commitBuilder = new LevelCategoryCommitBuilder(document);

    PluginLog.Step("Speckle", "SendPhysicalObjectsAsync: SetContextObjects from physical list");
    converter.SetContextObjects(
      physicalObjects
        .Select(
          element =>
            new ApplicationObject(element.UniqueId, element.GetType().ToString())
            {
              applicationId = element.UniqueId,
            }
        )
        .ToList()
    );

    var convertedCount = 0;
    var skippedNotSupported = 0;
    var skippedNull = 0;
    var conversionErrors = 0;
    var loggedConversionErrors = 0;
    const int maxLoggedConversionErrors = 20;
    var index = 0;
    var convertWatch = Stopwatch.StartNew();
    string? currentElementLabel = null;
    var lastUiYieldUtc = DateTime.MinValue;
    using var convertHeartbeat = StartConvertHeartbeat(
      convertWatch,
      () => index,
      physicalObjects.Count,
      () => currentElementLabel,
      reporter
    );
    foreach (var element in physicalObjects)
    {
      index++;
      currentElementLabel = FormatElementLabel(element);
      YieldToUiThread(ref lastUiYieldUtc);
      if (index == 1 || index == physicalObjects.Count || index % 500 == 0)
      {
        PluginLog.Step(
          "Speckle",
          $"SendPhysicalObjectsAsync: convert loop progress {index}/{physicalObjects.Count} {currentElementLabel}"
        );
      }

      reporter?.ReportConvert(index);

      if (!converter.CanConvertToSpeckle(element))
      {
        skippedNotSupported++;
        continue;
      }

      try
      {
        var elementWatch = Stopwatch.StartNew();
        var conversionResult = converter.ConvertToSpeckle(element);
        elementWatch.Stop();
        if (elementWatch.ElapsedMilliseconds >= 3000)
        {
          PluginLog.StepElapsed(
            "Speckle",
            $"slow convert {index}/{physicalObjects.Count} {currentElementLabel}",
            elementWatch.ElapsedMilliseconds
          );
        }

        if (conversionResult == null)
        {
          skippedNull++;
          continue;
        }

        if (conversionResult.applicationId != element.UniqueId)
        {
          conversionResult.applicationId = element.UniqueId;
        }

        commitBuilder.IncludeObject(conversionResult, element, commitObject);
        convertedCount++;
      }
      catch (Exception ex)
      {
        conversionErrors++;
        if (loggedConversionErrors < maxLoggedConversionErrors)
        {
          loggedConversionErrors++;
          PluginLog.Step(
            "Speckle",
            $"Convert element failed {currentElementLabel}: {ex.Message}"
          );
        }
      }
    }

    currentElementLabel = null;

    convertWatch.Stop();
    PluginLog.Step(
      "Speckle",
      $"SendPhysicalObjectsAsync: convert loop done converted={convertedCount} skippedNotSupported={skippedNotSupported} skippedNull={skippedNull} conversionErrors={conversionErrors}"
    );
    PluginLog.StepElapsed("Speckle", "SendPhysicalObjectsAsync: convert loop total", convertWatch.ElapsedMilliseconds);
    reporter?.ReportConvertComplete();

    if (convertedCount == 0)
    {
      PluginLog.Step("Speckle", "SendPhysicalObjectsAsync: zero converted, abort");
      throw new InvalidOperationException("Zero physical objects converted successfully.");
    }

    PluginLog.Step(
      "Speckle",
      $"SendPhysicalObjectsAsync: commit tree ready (Level→Category→Type) converted={convertedCount}"
    );

    var serverUrl = request.ServerUrl.TrimEnd('/');
    PluginLog.Step("Speckle", $"SendPhysicalObjectsAsync: serverUrl={serverUrl} streamId={request.StreamId}");

    var account = new Account { token = request.Token };
    account.serverInfo = new ServerInfo { url = serverUrl };

    var client = new Client(account);
    using var serverTransport = new ServerTransport(account, request.StreamId);
    var transports = new List<ITransport> { serverTransport };

    string objectId;
    try
    {
      PluginLog.Step(
        "Speckle",
        $"SendPhysicalObjectsAsync: Operations.Send begin (upload to {serverUrl}, converted={convertedCount})"
      );
      reporter?.BeginUpload(convertedCount);
      reporter?.ReportUploadStart();

      var lastUploadReport = 0;
      var lastUploadLogUtc = DateTime.MinValue;
      var heartbeatSeconds = PluginSettings.ProgressHeartbeatSeconds;
      Action<ConcurrentDictionary<string, int>> onProgress = dict =>
      {
        var uploaded = 0;
        foreach (var pair in dict)
        {
          uploaded += pair.Value;
        }

        var byCount = uploaded <= 1 || uploaded - lastUploadReport >= 500;
        var byHeartbeat =
          heartbeatSeconds > 0
          && lastUploadLogUtc != DateTime.MinValue
          && (DateTime.UtcNow - lastUploadLogUtc).TotalSeconds >= heartbeatSeconds;
        if (byCount || byHeartbeat || lastUploadLogUtc == DateTime.MinValue)
        {
          lastUploadReport = uploaded;
          lastUploadLogUtc = DateTime.UtcNow;
          PluginLog.Step(
            "Speckle",
            $"Operations.Send onProgress {FormatProgressDict(dict)} uploadedTotal={uploaded}"
          );
        }

        reporter?.ReportUpload(uploaded);
      };

      var sendWatch = Stopwatch.StartNew();
      using var heartbeat = StartSendHeartbeat(sendWatch);
      objectId = await Operations
        .Send(
          @object: commitObject,
          cancellationToken: CancellationToken.None,
          transports: transports,
          onProgressAction: onProgress,
          onErrorAction: null,
          disposeTransports: false
        )
        .ConfigureAwait(false);
      sendWatch.Stop();
      PluginLog.StepElapsed(
        "Speckle",
        $"SendPhysicalObjectsAsync: Operations.Send end objectId={objectId}",
        sendWatch.ElapsedMilliseconds
      );
      reporter?.FinishUpload(lastUploadReport);
      reporter?.ReportUploadComplete();
    }
    catch (Exception ex)
    {
      PluginLog.Step("Speckle", $"SendPhysicalObjectsAsync: Operations.Send failed: {ex}");
      throw new InvalidOperationException($"Speckle Operations.Send failed: {ex.Message}", ex);
    }

    var commitMessage = ResolveCommitMessage(request, convertedCount);
    var commitInput = new CommitCreateInput
    {
      streamId = request.StreamId,
      objectId = objectId,
      branchName = string.IsNullOrWhiteSpace(request.BranchName) ? "main" : request.BranchName,
      message = commitMessage,
      sourceApplication = ConverterRevit.RevitAppName,
    };

    try
    {
      PluginLog.Step(
        "Speckle",
        $"SendPhysicalObjectsAsync: CommitCreate begin branchName={commitInput.branchName} streamId={commitInput.streamId} messageLen={commitMessage.Length}"
      );
      var commitWatch = Stopwatch.StartNew();
#pragma warning disable CS0618
      var commitId = await client.CommitCreate(commitInput).ConfigureAwait(false);
#pragma warning restore CS0618
      commitWatch.Stop();
      PluginLog.StepElapsed("Speckle", $"SendPhysicalObjectsAsync: CommitCreate end commitId={commitId}", commitWatch.ElapsedMilliseconds);

      PluginLog.Step("Speckle", "SendPhysicalObjectsAsync: end OK");
      return new UploadCallbackPayload
      {
        RequestId = request.RequestId,
        Success = true,
        FilePath = request.FilePath,
        StreamId = request.StreamId,
        BranchName = commitInput.branchName,
        CommitMessage = commitMessage,
        ObjectId = objectId,
        CommitId = commitId,
        ObjectCount = convertedCount,
      };
    }
    catch (Exception ex)
    {
      LogCommitCreateFailure(ex, commitInput.branchName);

      // 对象已上传成功，仅创建 commit（挂到分支）失败
      return new UploadCallbackPayload
      {
        RequestId = request.RequestId,
        Success = false,
        FilePath = request.FilePath,
        StreamId = request.StreamId,
        BranchName = commitInput.branchName,
        CommitMessage = commitMessage,
        ObjectId = objectId,
        ObjectCount = convertedCount,
        Error =
          $"CommitCreate failed on branch \"{commitInput.branchName}\" (objects uploaded, objectId={objectId}): {ex.Message}",
      };
    }
  }

  private static string ResolveCommitMessage(UploadRequest request, int convertedCount)
  {
    var raw = string.IsNullOrWhiteSpace(request.CommitMessage)
      ? $"Sent {convertedCount} physical objects via SpeckleUpload."
      : request.CommitMessage;

    var utf8Bytes = Encoding.UTF8.GetBytes(raw);
    return Encoding.UTF8.GetString(utf8Bytes);
  }

  private static void LogCommitCreateFailure(Exception ex, string? branchName)
  {
    PluginLog.Step("Speckle", $"SendPhysicalObjectsAsync: CommitCreate failed branchName={branchName}: {ex.Message}");

    if (ex is SpeckleGraphQLException gqlEx)
    {
      PluginLog.Step("Speckle", $"CommitCreate GraphQL: {gqlEx}");
    }
  }

  private static IDisposable StartSendHeartbeat(Stopwatch sendWatch)
  {
    var cts = new CancellationTokenSource();
    _ = Task.Run(
      async () =>
      {
        try
        {
          while (!cts.Token.IsCancellationRequested)
          {
            await Task.Delay(15000, cts.Token).ConfigureAwait(false);
            PluginLog.StepElapsed(
              "Speckle",
              "Operations.Send still running (heartbeat)",
              sendWatch.ElapsedMilliseconds
            );
          }
        }
        catch (OperationCanceledException)
        {
          // expected when send completes
        }
      },
      cts.Token
    );

    return cts;
  }

  /// <summary>
  /// 转换循环卡在某个 ConvertToSpeckle 时，原进度回调不会触发；用后台心跳打出当前构件。
  /// </summary>
  /// <summary>
  /// 转换在 ExternalEvent/UI 线程同步执行时，定期泵消息，避免 Revit 长时间显示「无响应」。
  /// 对齐官方 Connector 的 YieldToUIThread（约每 150ms）。
  /// </summary>
  private static void YieldToUiThread(ref DateTime lastYieldUtc)
  {
    var now = DateTime.UtcNow;
    if (lastYieldUtc != DateTime.MinValue && (now - lastYieldUtc).TotalMilliseconds < 150)
    {
      return;
    }

    lastYieldUtc = now;
    try
    {
      System.Windows.Forms.Application.DoEvents();
    }
    catch
    {
      // 忽略泵消息失败，不影响转换
    }
  }

  private static IDisposable StartConvertHeartbeat(
    Stopwatch convertWatch,
    Func<int> getIndex,
    int total,
    Func<string?> getCurrentElementLabel,
    UploadCallbackReporter? reporter
  )
  {
    var cts = new CancellationTokenSource();
    var intervalSeconds = Math.Max(15, PluginSettings.ProgressHeartbeatSeconds);
    _ = Task.Run(
      async () =>
      {
        try
        {
          while (!cts.Token.IsCancellationRequested)
          {
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cts.Token).ConfigureAwait(false);
            var index = getIndex();
            var label = getCurrentElementLabel() ?? "(between elements)";
            PluginLog.StepElapsed(
              "Speckle",
              $"convert still running {index}/{total} {label}",
              convertWatch.ElapsedMilliseconds
            );
            // 心跳强制刷新进度（即使仍停在同一个 index）
            reporter?.ReportConvert(Math.Max(1, index));
          }
        }
        catch (OperationCanceledException)
        {
          // expected when convert loop completes
        }
      },
      cts.Token
    );

    return cts;
  }

  private static string FormatElementLabel(Element element)
  {
    try
    {
      var id =
#if REVIT2024
        element.Id.Value.ToString();
#else
        element.Id.IntegerValue.ToString();
#endif
      var category = element.Category?.Name ?? "(no category)";
      var name = string.IsNullOrWhiteSpace(element.Name) ? "(no name)" : element.Name;
      return $"id={id} category=\"{category}\" name=\"{name}\" type={element.GetType().Name}";
    }
    catch (Exception ex)
    {
      return $"(element label failed: {ex.GetType().Name})";
    }
  }

  private static string FormatProgressDict(ConcurrentDictionary<string, int> dict)
  {
    if (dict.IsEmpty)
    {
      return "(empty)";
    }

    return string.Join(", ", dict.Select(pair => $"{pair.Key}={pair.Value}"));
  }
}
