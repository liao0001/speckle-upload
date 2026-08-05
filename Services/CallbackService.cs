using System.Diagnostics;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using SpeckleUpload.Models;

namespace SpeckleUpload.Services;

public static class CallbackService
{
  private static readonly HttpClient HttpClient = CreateHttpClient();

  private static readonly JsonSerializerSettings CallbackJsonSettings = new()
  {
    NullValueHandling = NullValueHandling.Include,
  };

  private static HttpClient CreateHttpClient()
  {
    var timeoutSeconds = PluginSettings.CallbackTimeoutSeconds;
    PluginLog.Step("Callback", $"HttpClient init timeoutSeconds={timeoutSeconds}");
    return new HttpClient
    {
      Timeout = TimeSpan.FromSeconds(timeoutSeconds),
    };
  }

  public static async Task SendAsync(UploadCallbackPayload payload, string? callbackUrl = null)
  {
    var url = ResolveCallbackUrl(callbackUrl);
    var totalWatch = Stopwatch.StartNew();
    PluginLog.Step(
      "Callback",
      $"SendAsync: begin url={url} timeoutSeconds={HttpClient.Timeout.TotalSeconds} success={payload.Success} requestId={payload.RequestId} progress={payload.Progress ?? "-"} progress_index={payload.ProgressIndex?.ToString() ?? "-"} threadId={Environment.CurrentManagedThreadId}"
    );

    var json = JsonConvert.SerializeObject(payload, CallbackJsonSettings);
    PluginLog.Step("Callback", $"SendAsync: body length={json.Length} bytes (UTF-8, snake_case)");

    using var content = new StringContent(json, Encoding.UTF8, "application/json");
    HttpResponseMessage response;
    var postWatch = Stopwatch.StartNew();
    try
    {
      PluginLog.Step("Callback", "SendAsync: PostAsync start");
      response = await HttpClient.PostAsync(url, content).ConfigureAwait(false);
      postWatch.Stop();
      PluginLog.StepElapsed(
        "Callback",
        $"SendAsync: PostAsync end status={(int)response.StatusCode}",
        postWatch.ElapsedMilliseconds
      );
    }
    catch (TaskCanceledException ex)
    {
      postWatch.Stop();
      PluginLog.StepElapsed(
        "Callback",
        $"SendAsync: PostAsync TIMEOUT or canceled (HttpClient.Timeout={HttpClient.Timeout.TotalSeconds}s) ex={ex.GetType().Name}",
        postWatch.ElapsedMilliseconds
      );
      throw new TimeoutException(
        $"Callback HTTP timeout after {postWatch.ElapsedMilliseconds}ms (limit {HttpClient.Timeout.TotalSeconds}s): {url}",
        ex
      );
    }
    catch (HttpRequestException ex)
    {
      postWatch.Stop();
      PluginLog.StepElapsed(
        "Callback",
        $"SendAsync: PostAsync HTTP error ex={ex.GetType().Name} msg={ex.Message}",
        postWatch.ElapsedMilliseconds
      );
      throw;
    }

    using (response)
    {
      var readWatch = Stopwatch.StartNew();
      var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
      readWatch.Stop();
      PluginLog.StepElapsed(
        "Callback",
        $"SendAsync: response body read status={(int)response.StatusCode} bodyLen={responseBody.Length}",
        readWatch.ElapsedMilliseconds
      );

      if (responseBody.Length > 0 && responseBody.Length <= 500)
      {
        PluginLog.Step("Callback", $"SendAsync: response body={responseBody}");
      }
      else if (responseBody.Length > 500)
      {
        PluginLog.Step("Callback", $"SendAsync: response body preview={responseBody[..500]}...");
      }

      LwhaleResponse? rr;
      try
      {
        rr = JsonConvert.DeserializeObject<LwhaleResponse>(responseBody);
      }
      catch (Exception ex)
      {
        throw new InvalidOperationException(
          $"Callback response is not valid JSON (HTTP {(int)response.StatusCode}): {ex.Message}",
          ex
        );
      }

      if (rr == null)
      {
        throw new InvalidOperationException(
          $"Callback response empty (HTTP {(int)response.StatusCode})"
        );
      }

      if (!rr.IsSuccess)
      {
        var detail = string.IsNullOrWhiteSpace(rr.Error) ? $"ret={rr.Ret}" : rr.Error;
        PluginLog.Step("Callback", $"SendAsync: rejected ret={rr.Ret} error={detail}");
        throw new InvalidOperationException($"Callback rejected: {detail}");
      }
    }

    totalWatch.Stop();
    PluginLog.StepElapsed("Callback", "SendAsync: end OK ret=0", totalWatch.ElapsedMilliseconds);
  }

  public static void SendFireAndForget(UploadCallbackPayload payload, string? callbackUrl = null)
  {
    var progress = payload.Progress ?? "-";
    PluginLog.Step(
      "Callback",
      $"SendFireAndForget: queued progress={progress} progress_index={payload.ProgressIndex?.ToString() ?? "-"} requestId={payload.RequestId}"
    );

    _ = Task.Run(async () =>
    {
      var watch = Stopwatch.StartNew();
      try
      {
        await SendAsync(payload, callbackUrl).ConfigureAwait(false);
        watch.Stop();
        PluginLog.StepElapsed(
          "Callback",
          $"SendFireAndForget: done progress={progress} requestId={payload.RequestId}",
          watch.ElapsedMilliseconds
        );
      }
      catch (Exception ex)
      {
        watch.Stop();
        PluginLog.StepElapsed(
          "Callback",
          $"SendFireAndForget: failed progress={progress} requestId={payload.RequestId} ex={ex.GetType().Name} msg={ex.Message}",
          watch.ElapsedMilliseconds
        );
      }
    });
  }

  private static string ResolveCallbackUrl(string? callbackUrl)
  {
    if (!string.IsNullOrWhiteSpace(callbackUrl))
    {
      return callbackUrl.Trim();
    }

    return PluginSettings.CallbackUrl;
  }
}
