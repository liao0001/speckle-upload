using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using SpeckleUpload.Models;

namespace SpeckleUpload.Services;

public static class CallbackService
{
  private static readonly HttpClient HttpClient = new();

  public static async Task SendAsync(UploadCallbackPayload payload)
  {
    var url = PluginSettings.CallbackUrl;
    PluginLog.Step("Callback", $"SendAsync: begin url={url} success={payload.Success} requestId={payload.RequestId}");

    var json = JsonConvert.SerializeObject(payload);
    PluginLog.Step("Callback", $"SendAsync: body length={json.Length} bytes");

    using var content = new StringContent(json, Encoding.UTF8, "application/json");
    PluginLog.Step("Callback", "SendAsync: posting HTTP");
    var response = await HttpClient.PostAsync(url, content).ConfigureAwait(false);

    PluginLog.Step("Callback", $"SendAsync: response status={(int)response.StatusCode} {response.ReasonPhrase}");
    response.EnsureSuccessStatusCode();

    PluginLog.Step("Callback", "SendAsync: end OK");
  }
}
