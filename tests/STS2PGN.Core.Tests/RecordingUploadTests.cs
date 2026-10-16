using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using STS2PGN.Mod.Recording;

namespace STS2PGN.Tests;

public sealed class RecordingUploadTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"auto_upload\":false}")]
    [InlineData("{\"auto_upload\":null}")]
    [InlineData("{\"auto_upload\":\"true\"}")]
    [InlineData("{\"auto_upload\":1}")]
    public async Task MissingDisabledOrInvalidOptionDoesNotReadAFileOrSendARequest(string json)
    {
        using JsonDocument config = JsonDocument.Parse(json);
        using HttpClient client = new(new Handler((_, _) => throw new InvalidOperationException("No request expected")));
        bool enabled = RecordingUpload.ReadAutoUpload(config.RootElement);

        Assert.False(enabled);
        Assert.False(RecordingUpload.DefaultAutoUpload);
        await RecordingUpload.Queue("missing.sts2pgn.gz", enabled,
            _ => Assert.Fail("No success expected"), _ => Assert.Fail("No file access expected"), client);
    }

    [Fact]
    public async Task OptInPostsTheSavedGzipAndLogsTheReplayLink()
    {
        using JsonDocument config = JsonDocument.Parse("{\"auto_upload\":true}");
        using Recording recording = new();
        int requests = 0;
        List<string> messages = [];
        List<string> warnings = [];
        using HttpClient client = new(new Handler(async (request, cancellationToken) =>
        {
            requests++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://alphaspire.dev/upload", request.RequestUri!.AbsoluteUri);
            MultipartFormDataContent form = Assert.IsType<MultipartFormDataContent>(request.Content);
            HttpContent content = Assert.Single(form);
            Assert.Equal("multipart/form-data", form.Headers.ContentType!.MediaType);
            Assert.Equal("file", content.Headers.ContentDisposition!.Name!.Trim('"'));
            Assert.Equal(Path.GetFileName(recording.Path), content.Headers.ContentDisposition.FileName!.Trim('"'));
            Assert.Equal("application/gzip", content.Headers.ContentType!.MediaType);
            Assert.Equal(recording.Bytes, await content.ReadAsByteArrayAsync(cancellationToken));
            return Accepted();
        }));

        await RecordingUpload.Queue(recording.Path, RecordingUpload.ReadAutoUpload(config.RootElement),
            messages.Add, warnings.Add, client);

        Assert.Equal(1, requests);
        Assert.Empty(warnings);
        Assert.Contains("https://alphaspire.dev/r/0123456789abcdef", Assert.Single(messages));
        Assert.Equal(recording.Bytes, File.ReadAllBytes(recording.Path));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, null)]
    [InlineData(HttpStatusCode.BadRequest, null)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, null)]
    [InlineData(HttpStatusCode.InternalServerError, null)]
    [InlineData(HttpStatusCode.SeeOther, null)]
    [InlineData(HttpStatusCode.SeeOther, "/")]
    [InlineData(HttpStatusCode.SeeOther, "https://example.invalid/r/0123456789abcdef")]
    public async Task RejectedOrUnexpectedResponseWarnsWithoutRetryOrFileChanges(HttpStatusCode status, string? location)
    {
        using Recording recording = new();
        int requests = 0;
        List<string> messages = [];
        List<string> warnings = [];
        using HttpClient client = new(new Handler((_, _) =>
        {
            requests++;
            HttpResponseMessage response = new(status);
            if (location is not null)
            {
                response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            }
            return Task.FromResult(response);
        }));

        await RecordingUpload.Queue(recording.Path, true, messages.Add, warnings.Add, client);

        Assert.Empty(messages);
        Assert.Contains($"HTTP {(int)status}", Assert.Single(warnings));
        Assert.Equal(1, requests);
        Assert.Equal(recording.Bytes, File.ReadAllBytes(recording.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetworkErrorsAndTimeoutsWarnWithoutRetryOrFileChanges(bool timeout)
    {
        using Recording recording = new();
        int requests = 0;
        List<string> warnings = [];
        using HttpClient client = new(new Handler((_, _) =>
        {
            requests++;
            return Task.FromException<HttpResponseMessage>(timeout
                ? new TaskCanceledException("Timed out")
                : new HttpRequestException("Offline"));
        }));

        await RecordingUpload.Queue(recording.Path, true,
            _ => Assert.Fail("No success expected"), warnings.Add, client);

        Assert.Contains("Auto-upload failed", Assert.Single(warnings));
        Assert.Equal(1, requests);
        Assert.Equal(recording.Bytes, File.ReadAllBytes(recording.Path));
    }

    [Fact]
    public async Task MissingFileIsReportedWithoutSendingARequest()
    {
        int requests = 0;
        List<string> warnings = [];
        using HttpClient client = new(new Handler((_, _) =>
        {
            requests++;
            return Task.FromResult(Accepted());
        }));
        await RecordingUpload.Queue(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sts2pgn.gz"), true,
            _ => Assert.Fail("No success expected"), warnings.Add, client);
        Assert.Contains("Auto-upload failed", Assert.Single(warnings));
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task SchedulingReturnsWhileTheUploadIsStillPending()
    {
        using Recording recording = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> warnings = [];
        using HttpClient client = new(new Handler(async (_, _) =>
        {
            started.SetResult();
            await release.Task;
            return Accepted();
        }));

        Task upload = RecordingUpload.Queue(recording.Path, true, _ => { }, warnings.Add, client);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(upload.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await upload.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Empty(warnings);
    }

    private static HttpResponseMessage Accepted()
    {
        HttpResponseMessage response = new(HttpStatusCode.SeeOther);
        response.Headers.Location = new Uri("/r/0123456789abcdef", UriKind.Relative);
        return response;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class Recording : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid():N}.sts2pgn.gz");
        public byte[] Bytes { get; }

        public Recording()
        {
            using MemoryStream bytes = new();
            using (GZipStream gzip = new(bytes, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(Encoding.UTF8.GetBytes("[Result \"victory\"]\n"));
            }
            Bytes = bytes.ToArray();
            File.WriteAllBytes(Path, Bytes);
        }

        public void Dispose() => File.Delete(Path);
    }
}
