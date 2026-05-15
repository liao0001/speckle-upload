using Autodesk.Revit.DB;
using Objects.Converter.Revit;
using RevitSharedResources.Interfaces;
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
  public static async Task<UploadCallbackPayload> SendPhysicalObjectsAsync(
    Document document,
    UploadRequest request
  )
  {
    PluginLog.Step("Speckle", $"SendPhysicalObjectsAsync: begin requestId={request.RequestId}");

    PluginLog.Step("Speckle", "SendPhysicalObjectsAsync: RevitConverterState.Push");
    using var converterState = RevitConverterState.Push();

    PluginLog.Step("Speckle", "SendPhysicalObjectsAsync: new ConverterRevit");
    var converter = new ConverterRevit();
    PluginLog.Step("Speckle", "SendPhysicalObjectsAsync: SetContextDocument + clear report");
    converter.SetContextDocument(document);
    converter.Report.ReportObjects.Clear();

    PluginLog.Step("Speckle", "SendPhysicalObjectsAsync: GetPhysicalObjects");
    var physicalObjects = DocumentService.GetPhysicalObjects(document);
    if (physicalObjects.Count == 0)
    {
      PluginLog.Step("Speckle", "SendPhysicalObjectsAsync: no physical objects, abort");
      throw new InvalidOperationException("No physical objects found in the model.");
    }

    PluginLog.Step("Speckle", $"SendPhysicalObjectsAsync: physical count={physicalObjects.Count}");

    if (converter is not IRevitCommitObjectBuilderExposer builderExposer)
    {
      PluginLog.Step("Speckle", "SendPhysicalObjectsAsync: converter has no IRevitCommitObjectBuilderExposer");
      throw new InvalidOperationException("ConverterRevit does not expose commit object builder.");
    }

    var commitBuilder = builderExposer.commitObjectBuilder;
    var commitObject = new Collection();

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
    var index = 0;
    foreach (var element in physicalObjects)
    {
      index++;
      if (index == 1 || index == physicalObjects.Count || index % 500 == 0)
      {
        PluginLog.Step("Speckle", $"SendPhysicalObjectsAsync: convert loop progress {index}/{physicalObjects.Count}");
      }

      if (!converter.CanConvertToSpeckle(element))
      {
        skippedNotSupported++;
        continue;
      }

      var conversionResult = converter.ConvertToSpeckle(element);
      if (conversionResult == null)
      {
        skippedNull++;
        continue;
      }

      if (conversionResult.applicationId != element.UniqueId)
      {
        conversionResult.applicationId = element.UniqueId;
      }

      commitBuilder.IncludeObject(conversionResult, element);
      convertedCount++;
    }

    PluginLog.Step(
      "Speckle",
      $"SendPhysicalObjectsAsync: convert loop done converted={convertedCount} skippedNotSupported={skippedNotSupported} skippedNull={skippedNull}"
    );

    if (convertedCount == 0)
    {
      PluginLog.Step("Speckle", "SendPhysicalObjectsAsync: zero converted, abort");
      throw new InvalidOperationException("Zero physical objects converted successfully.");
    }

    PluginLog.Step("Speckle", "SendPhysicalObjectsAsync: BuildCommitObject");
    commitBuilder.BuildCommitObject(commitObject);

    PluginLog.Step("Speckle", $"SendPhysicalObjectsAsync: Account + ServerTransport streamId={request.StreamId}");
    var account = new Account { token = request.Token };
    account.serverInfo = new ServerInfo { url = request.ServerUrl.TrimEnd('/') };

    var client = new Client(account);
    using var serverTransport = new ServerTransport(account, request.StreamId);
    IReadOnlyList<ITransport> transports = new[] { serverTransport };

    PluginLog.Step("Speckle", "SendPhysicalObjectsAsync: Operations.Send begin");
    var objectId = await Operations.Send(commitObject, transports).ConfigureAwait(false);
    PluginLog.Step("Speckle", $"SendPhysicalObjectsAsync: Operations.Send end objectId={objectId}");

    var commitInput = new CommitCreateInput
    {
      streamId = request.StreamId,
      objectId = objectId,
      branchName = string.IsNullOrWhiteSpace(request.BranchName) ? "main" : request.BranchName,
      message =
        request.CommitMessage
        ?? $"Sent {convertedCount} physical objects via SpeckleUpload.",
      sourceApplication = ConverterRevit.RevitAppName,
    };

    PluginLog.Step("Speckle", "SendPhysicalObjectsAsync: CommitCreate begin");
#pragma warning disable CS0618
    var commitId = await client.CommitCreate(commitInput).ConfigureAwait(false);
#pragma warning restore CS0618
    PluginLog.Step("Speckle", $"SendPhysicalObjectsAsync: CommitCreate end commitId={commitId}");

    PluginLog.Step("Speckle", "SendPhysicalObjectsAsync: end OK");
    return new UploadCallbackPayload
    {
      RequestId = request.RequestId,
      Success = true,
      FilePath = request.FilePath,
      StreamId = request.StreamId,
      ObjectId = objectId,
      CommitId = commitId,
      ObjectCount = convertedCount,
    };
  }
}
