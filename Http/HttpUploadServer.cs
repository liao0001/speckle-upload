using System.IO;
using System.Net;
using System.Text;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using SpeckleUpload.Models;
using SpeckleUpload.Services;
using PluginSettings = SpeckleUpload.PluginSettings;

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
          await LwhaleJsonResponse
            .WriteErrorAsync(
              context.Response,
              LwhaleJsonResponse.RetSystemError,
              ex.Message
            )
            .ConfigureAwait(false);
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
      await WriteHealthJsonAsync(
        response,
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
    await LwhaleJsonResponse
      .WriteErrorAsync(response, LwhaleJsonResponse.RetSystemError, "Not found")
      .ConfigureAwait(false);
  }

  private async Task HandleUploadAsync(HttpListenerRequest request, HttpListenerResponse response)
  {
    PluginLog.Step("Http", "HandleUpload: reading body (UTF-8)");
    string body;
    using (var ms = new MemoryStream())
    {
      await request.InputStream.CopyToAsync(ms).ConfigureAwait(false);
      body = Encoding.UTF8.GetString(ms.ToArray());
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
      await LwhaleJsonResponse
        .WriteErrorAsync(
          response,
          LwhaleJsonResponse.RetInvalidParam,
          $"Invalid JSON: {ex.Message}"
        )
        .ConfigureAwait(false);
      return;
    }

    if (uploadRequest == null)
    {
      PluginLog.Step("Http", "HandleUpload: body empty after deserialize");
      await LwhaleJsonResponse
        .WriteErrorAsync(response, LwhaleJsonResponse.RetInvalidParam, "Request body is empty.")
        .ConfigureAwait(false);
      return;
    }

    if (string.IsNullOrWhiteSpace(uploadRequest.FilePath))
    {
      PluginLog.Step("Http", "HandleUpload: validation fail filePath");
      await LwhaleJsonResponse
        .WriteErrorAsync(response, LwhaleJsonResponse.RetInvalidParam, "filePath is required.")
        .ConfigureAwait(false);
      return;
    }

    if (string.IsNullOrWhiteSpace(uploadRequest.StreamId))
    {
      PluginLog.Step("Http", "HandleUpload: validation fail streamId");
      await LwhaleJsonResponse
        .WriteErrorAsync(response, LwhaleJsonResponse.RetInvalidParam, "streamId is required.")
        .ConfigureAwait(false);
      return;
    }

    if (string.IsNullOrWhiteSpace(uploadRequest.Token))
    {
      PluginLog.Step("Http", "HandleUpload: validation fail token");
      await LwhaleJsonResponse
        .WriteErrorAsync(response, LwhaleJsonResponse.RetInvalidParam, "token is required.")
        .ConfigureAwait(false);
      return;
    }

    var callbackTarget = string.IsNullOrWhiteSpace(uploadRequest.CallbackUrl)
      ? PluginSettings.CallbackUrl
      : uploadRequest.CallbackUrl.Trim();
    PluginLog.Step(
      "Http",
      $"HandleUpload: validated filePath=\"{uploadRequest.FilePath}\" streamId=\"{uploadRequest.StreamId}\" serverUrl=\"{uploadRequest.ServerUrl}\" callbackUrl=\"{callbackTarget}\""
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
        PluginLog.Step("Http", "HandleUpload: enqueue Busy -> ret 500");
        await LwhaleJsonResponse
          .WriteErrorAsync(
            response,
            LwhaleJsonResponse.RetSystemError,
            "Another upload is in progress."
          )
          .ConfigureAwait(false);
        return;

      case UploadEnqueueStatus.Denied:
        PluginLog.Step("Http", $"HandleUpload: enqueue Denied -> ret 500 msg={enqueueResult.Message}");
        await LwhaleJsonResponse
          .WriteErrorAsync(
            response,
            LwhaleJsonResponse.RetSystemError,
            enqueueResult.Message ?? "Upload denied."
          )
          .ConfigureAwait(false);
        return;
    }

    PluginLog.Step("Http", $"HandleUpload: enqueue {enqueueResult.Status} -> ret 0");
    await LwhaleJsonResponse.WriteSuccessAsync(response, HttpStatusCode.OK, null).ConfigureAwait(false);
  }

  private static async Task WriteHealthJsonAsync(HttpListenerResponse response, object payload)
  {
    var json = JsonConvert.SerializeObject(payload);
    var buffer = Encoding.UTF8.GetBytes(json);

    response.StatusCode = (int)HttpStatusCode.OK;
    response.ContentType = "application/json; charset=utf-8";
    response.ContentEncoding = Encoding.UTF8;
    response.ContentLength64 = buffer.Length;

    await response.OutputStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
    response.OutputStream.Close();
  }
}
