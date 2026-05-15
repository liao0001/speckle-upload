using SpeckleUpload.Models;

namespace SpeckleUpload.Services;

public sealed class UploadWorkItem
{
  public UploadWorkItem(UploadRequest request)
  {
    Request = request;
  }

  public UploadRequest Request { get; }
  public TaskCompletionSource<UploadCallbackPayload> Completion { get; } = new(
    TaskCreationOptions.RunContinuationsAsynchronously
  );
}
