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
  private readonly ExternalEvent _externalEvent;
  private readonly HttpListener _listener = new();
  private CancellationTokenSource? _cts;
  private Task? _listenTask;

  public HttpUploadServer(UploadEventHandler handler, ExternalEvent externalEvent)
  {
    _handler = handler;
    _externalEvent = externalEvent;
  }

  public void Start()
  {
    var prefix = $"http://localhost:{PluginSettings.HttpPort}/";
    _listener.Prefixes.Add(prefix);
    _listener.Start();

    _cts = new CancellationTokenSource();
    _listenTask = Task.Run(() => ListenAsync(_cts.Token));
  }

  public void Stop()
  {
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

    if (request.HttpMethod == "GET" && (path == "" || path == "/health"))
    {
      await WriteJsonAsync(
        response,
        HttpStatusCode.OK,
        new { status = "ok", port = PluginSettings.HttpPort }
      ).ConfigureAwait(false);
      return;
    }

    if (request.HttpMethod == "POST" && path == "/upload")
    {
      await HandleUploadAsync(request, response).ConfigureAwait(false);
      return;
    }

    await WriteJsonAsync(
      response,
      HttpStatusCode.NotFound,
      new { success = false, error = "Not found" }
    ).ConfigureAwait(false);
  }

  private async Task HandleUploadAsync(HttpListenerRequest request, HttpListenerResponse response)
  {
    string body;
    using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
    {
      body = await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    UploadRequest? uploadRequest;
    try
    {
      uploadRequest = JsonConvert.DeserializeObject<UploadRequest>(body);
    }
    catch (Exception ex)
    {
      await WriteJsonAsync(
        response,
        HttpStatusCode.BadRequest,
        new { success = false, error = $"Invalid JSON: {ex.Message}" }
      ).ConfigureAwait(false);
      return;
    }

    if (uploadRequest == null)
    {
      await WriteJsonAsync(
        response,
        HttpStatusCode.BadRequest,
        new { success = false, error = "Request body is empty." }
      ).ConfigureAwait(false);
      return;
    }

    if (string.IsNullOrWhiteSpace(uploadRequest.FilePath))
    {
      await WriteJsonAsync(
        response,
        HttpStatusCode.BadRequest,
        new { success = false, error = "filePath is required." }
      ).ConfigureAwait(false);
      return;
    }

    if (string.IsNullOrWhiteSpace(uploadRequest.StreamId))
    {
      await WriteJsonAsync(
        response,
        HttpStatusCode.BadRequest,
        new { success = false, error = "streamId is required." }
      ).ConfigureAwait(false);
      return;
    }

    if (string.IsNullOrWhiteSpace(uploadRequest.Token))
    {
      await WriteJsonAsync(
        response,
        HttpStatusCode.BadRequest,
        new { success = false, error = "token is required." }
      ).ConfigureAwait(false);
      return;
    }

    if (string.IsNullOrWhiteSpace(uploadRequest.RequestId))
    {
      uploadRequest.RequestId = Guid.NewGuid().ToString("N");
    }

    var workItem = new UploadWorkItem(uploadRequest, _externalEvent);
    if (!_handler.TryEnqueue(workItem))
    {
      await WriteJsonAsync(
        response,
        HttpStatusCode.Conflict,
        new { success = false, error = "Another upload is in progress." }
      ).ConfigureAwait(false);
      return;
    }

    await WriteJsonAsync(
      response,
      HttpStatusCode.Accepted,
      new
      {
        success = true,
        accepted = true,
        requestId = uploadRequest.RequestId,
        message = "Upload queued. Result will be sent to callback URL.",
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
