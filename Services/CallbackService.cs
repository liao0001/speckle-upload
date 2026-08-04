using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using SpeckleUpload.Models;

namespace SpeckleUpload.Services;

public static class CallbackService
{
  private static readonly HttpClient HttpClient = new();

  private static readonly JsonSerializerSettings CallbackJsonSettings = new()
  {
    NullValueHandling = NullValueHandling.Include,
  };

  public static async Task SendAsync(UploadCallbackPayload payload, string? callbackUrl = null)
  {
    var url = ResolveCallbackUrl(callbackUrl);
    PluginLog.Step(
      "Callback",
      $"SendAsync: begin url={url} success={payload.Success} requestId={payload.RequestId} progress={payload.Progress ?? "-"} progress_index={payload.ProgressIndex?.ToString() ?? "-"}"
    );

    var json = JsonConvert.SerializeObject(payload, CallbackJsonSettings);
    PluginLog.Step("Callback", $"SendAsync: body length={json.Length} bytes (UTF-8, snake_case)");

    using var content = new StringContent(json, Encoding.UTF8, "application/json");
    PluginLog.Step("Callback", "SendAsync: posting HTTP");
    using var response = await HttpClient.PostAsync(url, content).ConfigureAwait(false);

    var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    PluginLog.Step(
      "Callback",
      $"SendAsync: response status={(int)response.StatusCode} bodyLen={responseBody.Length}"
    );

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
      throw new InvalidOperationException($"Callback rejected: {detail}");
    }

    PluginLog.Step("Callback", "SendAsync: end OK ret=0");
  }

  public static void SendFireAndForget(UploadCallbackPayload payload, string? callbackUrl = null)
  {
    _ = Task.Run(async () =>
    {
      try
      {
        await SendAsync(payload, callbackUrl).ConfigureAwait(false);
      }
      catch (Exception ex)
      {
        PluginLog.Step(
          "Callback",
          $"status report failed requestId={payload.RequestId} progress={payload.Progress}: {ex.Message}"
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
