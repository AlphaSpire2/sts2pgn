using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace STS2PGN.Mod.Recording;

/// <summary>Best-effort publication of finalized recordings; never owns the local file.</summary>
internal static class RecordingUpload
{
    public const bool DefaultAutoUpload = false;
    private static readonly Uri Endpoint = new("https://alphaspire.dev/upload");
    private static readonly Lazy<HttpClient> Client = new(() => new HttpClient(
        new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(60),
    });

    public static bool ReadAutoUpload(JsonElement configuration) =>
        configuration.TryGetProperty("auto_upload", out JsonElement value)
        && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Schedule one attempt after finalization. Off means no file access or HTTP client creation.
    /// File opening and network work both run away from the game's synchronization context.
    /// </summary>
    public static Task Queue(string path, bool enabled, Action<string> log, Action<string> warn,
        HttpClient? client = null) =>
        enabled ? Task.Run(() => UploadAsync(path, log, warn, client)) : Task.CompletedTask;

    private static async Task UploadAsync(string path, Action<string> log, Action<string> warn,
        HttpClient? client)
    {
        try
        {
            using FileStream file = File.OpenRead(path);
            using MultipartFormDataContent form = new();
            using StreamContent content = new(file);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
            form.Add(content, "file", Path.GetFileName(path));
            using HttpRequestMessage request = new(HttpMethod.Post, Endpoint) { Content = form };
            using HttpResponseMessage response = await (client ?? Client.Value).SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

            // A single accepted file redirects to its replay. HTTP 200 can instead be an HTML
            // rejection report, so it must not be reported as successful publication.
            Uri? location = response.Headers.Location;
            if (response.StatusCode == HttpStatusCode.SeeOther && location is not null
                && Uri.TryCreate(Endpoint, location, out Uri? replay)
                && replay.Scheme == Endpoint.Scheme && replay.Authority == Endpoint.Authority
                && replay.AbsolutePath.StartsWith("/r/", StringComparison.Ordinal)
                && replay.AbsolutePath.Length > 3)
            {
                log($"Recording uploaded: {replay}");
                return;
            }

            warn($"Auto-upload was not accepted (HTTP {(int)response.StatusCode}). Recording kept at '{path}'; upload it manually at https://alphaspire.dev.");
        }
        catch (Exception exception)
        {
            warn($"Auto-upload failed: {exception.Message}. Recording kept at '{path}'; upload it manually at https://alphaspire.dev.");
        }
    }
}
