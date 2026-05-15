namespace SpeckleUpload.Services;

public enum UploadEnqueueStatus
{
  Accepted,
  Pending,
  Denied,
  Busy,
}

public readonly struct UploadEnqueueResult
{
  public UploadEnqueueResult(UploadEnqueueStatus status, string? message = null)
  {
    Status = status;
    Message = message;
  }

  public UploadEnqueueStatus Status { get; }
  public string? Message { get; }
}
