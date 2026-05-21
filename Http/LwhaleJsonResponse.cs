using System.Net;
using System.Text;
using Newtonsoft.Json;

namespace SpeckleUpload.Http;

public static class LwhaleJsonResponse
{
  public const int RetSuccess = 0;
  public const int RetSystemError = 500;
  public const int RetInvalidParam = 1002;

  public static Task WriteSuccessAsync(
    HttpListenerResponse response,
    HttpStatusCode statusCode = HttpStatusCode.OK,
    object? msg = null
  ) => WriteAsync(response, statusCode, RetSuccess, msg, null);

  public static Task WriteErrorAsync(
    HttpListenerResponse response,
    int ret,
    string error,
    HttpStatusCode statusCode = HttpStatusCode.InternalServerError
  ) => WriteAsync(response, statusCode, ret, null, error);

  public static async Task WriteAsync(
    HttpListenerResponse response,
    HttpStatusCode statusCode,
    int ret,
    object? msg,
    string? error
  )
  {
    object payload = ret == RetSuccess
      ? new { ret, msg }
      : new { ret, error = error ?? "error", msg = (object?)null };

    var json = JsonConvert.SerializeObject(payload);
    var buffer = Encoding.UTF8.GetBytes(json);

    response.StatusCode = (int)statusCode;
    response.ContentType = "application/json; charset=utf-8";
    response.ContentEncoding = Encoding.UTF8;
    response.ContentLength64 = buffer.Length;

    await response.OutputStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
    response.OutputStream.Close();
  }
}
