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
    using var converterState = RevitConverterState.Push();

    var converter = new ConverterRevit();
    converter.SetContextDocument(document);
    converter.Report.ReportObjects.Clear();

    var physicalObjects = DocumentService.GetPhysicalObjects(document);
    if (physicalObjects.Count == 0)
    {
      throw new InvalidOperationException("No physical objects found in the model.");
    }

    if (converter is not IRevitCommitObjectBuilderExposer builderExposer)
    {
      throw new InvalidOperationException("ConverterRevit does not expose commit object builder.");
    }

    var commitBuilder = builderExposer.commitObjectBuilder;
    var commitObject = new Collection();

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
    foreach (var element in physicalObjects)
    {
      if (!converter.CanConvertToSpeckle(element))
      {
        continue;
      }

      var conversionResult = converter.ConvertToSpeckle(element);
      if (conversionResult == null)
      {
        continue;
      }

      if (conversionResult.applicationId != element.UniqueId)
      {
        conversionResult.applicationId = element.UniqueId;
      }

      commitBuilder.IncludeObject(conversionResult, element);
      convertedCount++;
    }

    if (convertedCount == 0)
    {
      throw new InvalidOperationException("Zero physical objects converted successfully.");
    }

    commitBuilder.BuildCommitObject(commitObject);

    var account = new Account { token = request.Token };
    account.serverInfo = new ServerInfo { url = request.ServerUrl.TrimEnd('/') };

    var client = new Client(account);
    using var serverTransport = new ServerTransport(account, request.StreamId);
    IReadOnlyList<ITransport> transports = new[] { serverTransport };

    var objectId = await Operations.Send(commitObject, transports).ConfigureAwait(false);

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

#pragma warning disable CS0618
    var commitId = await client.CommitCreate(commitInput).ConfigureAwait(false);
#pragma warning restore CS0618

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
