using System.IO;
using System.Net;
using System.Text;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using SpeckleUpload.Models;
using SpeckleUpload.Services;

namespace SpeckleUpload.Http;

public sealed class HttpUploadServer : IDisposable
{
  private readonly UploadEventHandler _handler;
  private readonly HttpListener _listener = new();
  private CancellationTokenSource? _cts;
  private Task? _listenTask;

  public HttpUploadServer(UploadEventHandler handler)
  {
    _handler = handler;
  }

  public void Start()
  {
    var prefix = $"http://localhost:{PluginSettings.HttpPort}/";
    PluginLog.Step("Http", $"Start: prefix={prefix}");
    _listener.Prefixes.Add(prefix);
    _listener.Start();

    _cts = new CancellationTokenSource();
    _listenTask = Task.Run(() => ListenAsync(_cts.Token));
    PluginLog.Step("Http", "Start: listener task started");
  }

  public void Stop()
  {
    PluginLog.Step("Http", "Stop: begin");
    _cts?.Cancel();
    if (_listener.IsListening)
    {
      _listener.Stop();
    }

    try
    {
      _listenTask?.Wait(TimeSpan.FromSeconds(3));
    }
    catch
    {
      // Ignore shutdown race.
    }

    PluginLog.Step("Http", "Stop: end");
  }

  public void Dispose()
  {
    Stop();
    _cts?.Dispose();
    _listener.Close();
  }

  private async Task ListenAsync(CancellationToken cancellationToken)
  {
    while (!cancellationToken.IsCancellationRequested)
    {
      HttpListenerContext? context = null;
      try
      {
        context = await _listener.GetContextAsync().ConfigureAwait(false);
        await HandleRequestAsync(context).ConfigureAwait(false);
      }
      catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
      {
        break;
      }
      catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
      {
        break;
      }
      catch (Exception ex)
      {
        PluginLog.Step("Http", $"ListenAsync: unhandled exception {ex}");
        if (context?.Response != null)
        {
          await WriteJsonAsync(
            context.Response,
            HttpStatusCode.InternalServerError,
            new { success = false, error = ex.Message }
          ).ConfigureAwait(false);
        }
      }
    }
  }

  private async Task HandleRequestAsync(HttpListenerContext context)
  {
    var request = context.Request;
    var response = context.Response;
    var path = request.Url?.AbsolutePath?.TrimEnd('/') ?? string.Empty;
    var remote = request.RemoteEndPoint?.ToString() ?? "?";
    PluginLog.Step("Http", $"{request.HttpMethod} {path} remote={remote}");

    if (request.HttpMethod == "GET" && (path == "" || path == "/health"))
    {
      PluginLog.Step("Http", "HandleRequest: health OK");
      await WriteJsonAsync(
        response,
        HttpStatusCode.OK,
        new { status = "ok", port = PluginSettings.HttpPort }
      ).ConfigureAwait(false);
      return;
    }

    if (request.HttpMethod == "POST" && path == "/upload")
    {
      PluginLog.Step("Http", "HandleRequest: route /upload");
      await HandleUploadAsync(request, response).ConfigureAwait(false);
      return;
    }

    PluginLog.Step("Http", $"HandleRequest: 404 path={path}");
    await WriteJsonAsync(
      response,
      HttpStatusCode.NotFound,
      new { success = false, error = "Not found" }
    ).ConfigureAwait(false);
  }

  private async Task HandleUploadAsync(HttpListenerRequest request, HttpListenerResponse response)
  {
    PluginLog.Step("Http", "HandleUpload: reading body");
    string body;
    using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
    {
      body = await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    PluginLog.Step("Http", $"HandleUpload: body length={body.Length}");

    UploadRequest? uploadRequest;
    try
    {
      uploadRequest = JsonConvert.DeserializeObject<UploadRequest>(body);
      PluginLog.Step("Http", "HandleUpload: JSON deserialized");
    }
    catch (Exception ex)
    {
      PluginLog.Step("Http", $"HandleUpload: JSON error {ex.Message}");
      await WriteJsonAsync(
        response,
        HttpStatusCode.BadRequest,
        new { success = false, error = $"Invalid JSON: {ex.Message}" }
      ).ConfigureAwait(false);
      return;
    }

    if (uploadRequest == null)
    {
      PluginLog.Step("Http", "HandleUpload: body empty after deserialize");
      await WriteJsonAsync(
        response,
        HttpStatusCode.BadRequest,
        new { success = false, error = "Request body is empty." }
      ).ConfigureAwait(false);
      return;
    }

    if (string.IsNullOrWhiteSpace(uploadRequest.FilePath))
    {
      PluginLog.Step("Http", "HandleUpload: validation fail filePath");
      await WriteJsonAsync(
        response,
        HttpStatusCode.BadRequest,
        new { success = false, error = "filePath is required." }
      ).ConfigureAwait(false);
      return;
    }

    if (string.IsNullOrWhiteSpace(uploadRequest.StreamId))
    {
      PluginLog.Step("Http", "HandleUpload: validation fail streamId");
      await WriteJsonAsync(
        response,
        HttpStatusCode.BadRequest,
        new { success = false, error = "streamId is required." }
      ).ConfigureAwait(false);
      return;
    }

    if (string.IsNullOrWhiteSpace(uploadRequest.Token))
    {
      PluginLog.Step("Http", "HandleUpload: validation fail token");
      await WriteJsonAsync(
        response,
        HttpStatusCode.BadRequest,
        new { success = false, error = "token is required." }
      ).ConfigureAwait(false);
      return;
    }

    PluginLog.Step(
      "Http",
      $"HandleUpload: validated filePath=\"{uploadRequest.FilePath}\" streamId=\"{uploadRequest.StreamId}\" serverUrl=\"{uploadRequest.ServerUrl}\""
    );

    if (string.IsNullOrWhiteSpace(uploadRequest.RequestId))
    {
      uploadRequest.RequestId = Guid.NewGuid().ToString("N");
      PluginLog.Step("Http", $"HandleUpload: generated requestId={uploadRequest.RequestId}");
    }
    else
    {
      PluginLog.Step("Http", $"HandleUpload: requestId={uploadRequest.RequestId}");
    }

    var workItem = new UploadWorkItem(uploadRequest);
    PluginLog.Step("Http", "HandleUpload: TryEnqueue");
    var enqueueResult = _handler.TryEnqueue(workItem);

    switch (enqueueResult.Status)
    {
      case UploadEnqueueStatus.Busy:
        PluginLog.Step("Http", "HandleUpload: enqueue Busy -> 409");
        await WriteJsonAsync(
          response,
          HttpStatusCode.Conflict,
          new { success = false, error = "Another upload is in progress." }
        ).ConfigureAwait(false);
        return;

      case UploadEnqueueStatus.Denied:
        PluginLog.Step("Http", $"HandleUpload: enqueue Denied -> 503 msg={enqueueResult.Message}");
        await WriteJsonAsync(
          response,
          HttpStatusCode.ServiceUnavailable,
          new { success = false, error = enqueueResult.Message }
        ).ConfigureAwait(false);
        return;
    }

    PluginLog.Step("Http", $"HandleUpload: enqueue {enqueueResult.Status} -> 202");
    await WriteJsonAsync(
      response,
      HttpStatusCode.Accepted,
      new
      {
        success = true,
        accepted = true,
        requestId = uploadRequest.RequestId,
        queueStatus = enqueueResult.Status.ToString(),
        message =
          enqueueResult.Message
          ?? "Upload queued. Result will be sent to callback URL.",
      }
    ).ConfigureAwait(false);
  }

  private static async Task WriteJsonAsync(
    HttpListenerResponse response,
    HttpStatusCode statusCode,
    object payload
  )
  {
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
