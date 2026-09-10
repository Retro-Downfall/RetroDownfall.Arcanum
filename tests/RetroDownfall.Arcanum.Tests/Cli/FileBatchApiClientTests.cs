using System.Net;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class FileBatchApiClientTests
{
    [Fact]
    public async Task Credential_storage_failure_remains_typed_without_sending_a_file_request()
    {
        RecordingHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.OK));

        using ArcanumApiCredentialLease credentials =
            ArcanumApiCredentialLeaseTestFactory.Create(
                static _ => Task.FromResult(
                    SecretStoreReadResult.Corrupted("mirror unreadable")),
                static _ => Task.FromResult(
                    SecretStoreReadResult.Corrupted("primary unreadable")),
                "test-key");

        FileBatchApiClient client = new(
            new FakeHttpClientFactory(handler),
            credentials);

        var result = await client.ListFilesAsync(
            purpose: null,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Security.CredentialUnreadable, result.Error.Code);
        Assert.Contains(
            "could not be read",
            result.Error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Upload_rebuilds_the_file_body_after_capability_renewal()
    {
        const string FileContents = "file-batch-replay-canary";

        string filePath = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-file-batch-{Guid.NewGuid():N}.txt");

        await File.WriteAllTextAsync(filePath, FileContents);

        try
        {
            int requestCount = 0;

            RecordingHandler handler = new(request =>
            {
                if (Interlocked.Increment(ref requestCount) == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {"id":"file-1","bytes":25,"created_at":1,"filename":"probe.txt","purpose":"assistants","object":"file"}
                        """),
                };
            });

            using ArcanumApiCredentialLease credentials =
                ArcanumApiCredentialLeaseTestFactory.Create("test-key");

            FileBatchApiClient client = new(
                new FakeHttpClientFactory(handler),
                credentials);

            var result = await client.UploadFileAsync(
                filePath,
                "assistants",
                "text/plain",
                CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Equal("file-1", result.Value.Id);
            Assert.Equal(2, handler.Requests.Count);
            Assert.All(
                handler.Requests,
                request => Assert.Contains(
                    FileContents,
                    request.Body,
                    StringComparison.Ordinal));

            Assert.NotEqual(
                handler.Requests[0].Capability,
                handler.Requests[1].Capability);

            Assert.All(
                handler.Requests,
                static request => Assert.False(request.HasReusableApiKey));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    private sealed class FakeHttpClientFactory(
        HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            _ = name;

            return new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:5001/"),
            };
        }
    }

    private sealed class RecordingHandler(
        Func<RecordedRequest, HttpResponseMessage> responder) : HttpMessageHandler
    {
        internal List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string body = request.Content is null
                ? string.Empty
                : await request.Content
                    .ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);

            string capability = Assert.Single(
                request.Headers.GetValues(
                    ArcanumApiHeaders.ProcessCapability));

            RecordedRequest recorded = new(
                body,
                capability,
                request.Headers.Contains(ArcanumApiHeaders.ApiKey));

            Requests.Add(recorded);

            return responder(recorded);
        }
    }

    private sealed record RecordedRequest(
        string Body,
        string Capability,
        bool HasReusableApiKey);
}
