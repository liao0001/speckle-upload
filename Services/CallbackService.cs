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
    var json = JsonConvert.SerializeObject(payload);
    using var content = new StringContent(json, Encoding.UTF8, "application/json");
    var response = await HttpClient.PostAsync(PluginSettings.CallbackUrl, content).ConfigureAwait(false);
    response.EnsureSuccessStatusCode();
  }
}
