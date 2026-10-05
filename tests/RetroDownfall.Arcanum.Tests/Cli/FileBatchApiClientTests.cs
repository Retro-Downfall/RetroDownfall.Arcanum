using System.Net;
using System.Runtime.Versioning;
using System.Text;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class FileBatchApiClientTests
{
    /// <summary>Hang guard for every await on the deadline tests, never a behavioural bound.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

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

    /// <summary>
    /// The parent directory of the destination is prepared before the request is sent. A destination
    /// whose parent cannot be created is a write failure the operator can act on, not an exception
    /// that escapes as the generic unexpected CLI error.
    /// </summary>
    [Fact]
    public async Task DownloadFileAsync_returns_WriteFailed_when_destination_directory_cannot_be_created()
    {
        string root = Path.Combine(Path.GetTempPath(), $"arcanum-download-{Guid.NewGuid():N}");

        Directory.CreateDirectory(root);

        try
        {
            string blocker = Path.Combine(root, "blocker");

            await File.WriteAllTextAsync(blocker, "a regular file where a directory is needed");

            RecordingHandler handler = new(
                _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3]),
                });

            using ArcanumApiCredentialLease credentials =
                ArcanumApiCredentialLeaseTestFactory.Create("test-key");

            FileBatchApiClient client = new(
                new FakeHttpClientFactory(handler),
                credentials);

            Result<long> result = await client.DownloadFileAsync(
                "file-1",
                Path.Combine(blocker, "nested", "out.bin"),
                overwrite: false,
                CancellationToken.None);

            Assert.True(result.IsFailure);
            Assert.Equal("Files.WriteFailed", result.Error.Code);
            Assert.Empty(handler.Requests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The command only asks for no-overwrite when the file was absent a moment earlier. When it has
    /// appeared since, the move refuses to replace it and that is reported as such, not as a fault
    /// writing the download.
    /// </summary>
    [Fact]
    public async Task DownloadFileAsync_reports_DestinationExists_when_the_move_refuses_to_replace_a_file()
    {
        string root = Path.Combine(Path.GetTempPath(), $"arcanum-download-{Guid.NewGuid():N}");

        Directory.CreateDirectory(root);

        try
        {
            string destination = Path.Combine(root, "out.bin");

            await File.WriteAllTextAsync(destination, "existing");

            RecordingHandler handler = new(
                _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3]),
                });

            using ArcanumApiCredentialLease credentials =
                ArcanumApiCredentialLeaseTestFactory.Create("test-key");

            FileBatchApiClient client = new(
                new FakeHttpClientFactory(handler),
                credentials);

            Result<long> refused = await client.DownloadFileAsync(
                "file-1",
                destination,
                overwrite: false,
                CancellationToken.None);

            Assert.True(refused.IsFailure);
            Assert.Equal("Files.DestinationExists", refused.Error.Code);
            Assert.Equal("existing", await File.ReadAllTextAsync(destination));
            Assert.Equal([destination], Directory.GetFiles(root));

            Result<long> replaced = await client.DownloadFileAsync(
                "file-1",
                destination,
                overwrite: true,
                CancellationToken.None);

            Assert.True(replaced.IsSuccess);
            Assert.Equal(3, replaced.Value);
            Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A download is decrypted content, so its staging file must be owner-only before the first byte
    /// lands in it rather than after the final move. The response stream looks at the staging file
    /// when the first read happens, which is after it was created and before anything was written.
    /// </summary>
    [SkippableFact]
    [UnsupportedOSPlatform("windows")]
    public async Task DownloadFileAsync_stages_the_download_owner_only_before_the_first_write()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix mode bits are what this asserts against.");

        string root = Path.Combine(Path.GetTempPath(), $"arcanum-download-{Guid.NewGuid():N}");

        Directory.CreateDirectory(root);

        try
        {
            ModePeekingStream body = new(root, [1, 2, 3]);

            RecordingHandler handler = new(
                _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(body),
                });

            using ArcanumApiCredentialLease credentials =
                ArcanumApiCredentialLeaseTestFactory.Create("test-key");

            FileBatchApiClient client = new(
                new FakeHttpClientFactory(handler),
                credentials);

            Result<long> result = await client.DownloadFileAsync(
                "file-1",
                Path.Combine(root, "out.bin"),
                overwrite: false,
                CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.True(body.ObservedStagingFile, "The staging file did not exist when the body was first read.");

            const UnixFileMode GroupOrOtherAccess =
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

            Assert.Equal((UnixFileMode)0, body.StagingMode & GroupOrOtherAccess);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The file and batch verbs go through their own client, which used to send without any deadline,
    /// so a hung host wedged <c>file list</c>, <c>batch status</c> and the rest while the "timed out"
    /// branch below the sender stayed unreachable. Every short call now carries the same
    /// response-headers deadline as the rest of the CLI and reports the same typed timeout.
    /// </summary>
    [Fact]
    public async Task Short_file_and_batch_calls_fail_as_timeouts_when_the_host_never_answers_headers()
    {
        HangingHandler handler = new();

        using ArcanumApiCredentialLease credentials =
            ArcanumApiCredentialLeaseTestFactory.Create("test-key");

        FileBatchApiClient client = new(
            new FakeHttpClientFactory(handler),
            credentials)
        {
            RequestResponseHeadersTimeout = TimeSpan.FromMilliseconds(100),
        };

        (string Name, Func<Task<string>> Call)[] calls =
        [
            (nameof(FileBatchApiClient.ListFilesAsync), async () =>
                (await client.ListFilesAsync(purpose: null, CancellationToken.None)).Error.Code),
            (nameof(FileBatchApiClient.GetFileAsync), async () =>
                (await client.GetFileAsync("file-1", CancellationToken.None)).Error.Code),
            (nameof(FileBatchApiClient.DeleteFileAsync), async () =>
                (await client.DeleteFileAsync("file-1", CancellationToken.None)).Error.Code),
            (nameof(FileBatchApiClient.ListBatchesAsync), async () =>
                (await client.ListBatchesAsync(status: null, cursor: null, CancellationToken.None)).Error.Code),
            (nameof(FileBatchApiClient.GetBatchAsync), async () =>
                (await client.GetBatchAsync("batch-1", CancellationToken.None)).Error.Code),
            (nameof(FileBatchApiClient.CreateBatchAsync), async () =>
                (await client.CreateBatchAsync("file-1", CancellationToken.None)).Error.Code),
            (nameof(FileBatchApiClient.CancelBatchAsync), async () =>
                (await client.CancelBatchAsync("batch-1", CancellationToken.None)).Error.Code),
            (nameof(FileBatchApiClient.ResetBatchAsync), async () =>
                (await client.ResetBatchAsync("batch-1", CancellationToken.None)).Error.Code),
        ];

        List<string> notTimedOut = [];

        foreach ((string name, Func<Task<string>> call) in calls)
        {
            // Only the client's own deadline can finish these requests; the outer bound is a hang guard.
            string code = await call().WaitAsync(TimeSpan.FromSeconds(10));

            if (!string.Equals(code, ErrorCodes.Connection.Timeout, StringComparison.Ordinal))
            {
                notTimedOut.Add($"{name}: {code}");
            }
        }

        Assert.Empty(notTimedOut);

        Assert.Equal(calls.Length, handler.RequestCount);
    }

    /// <summary>
    /// The deadline is for the response headers. A body that takes longer than the deadline to
    /// arrive is a slow answer, not a hung host, and must not be cut off: the clock passes the
    /// deadline while the body is still being read, and the call still succeeds because the
    /// deadline timer was disposed with the headers instead of staying armed behind the body.
    /// </summary>
    [Fact]
    public async Task Short_file_call_headers_deadline_does_not_bound_the_response_body()
    {
        ManualTimerTimeProvider clock = new();

        GatedBodyStream body = new(
            Encoding.UTF8.GetBytes("""{"object":"list","data":[],"has_more":false}"""));

        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(body),
        });

        using ArcanumApiCredentialLease credentials =
            ArcanumApiCredentialLeaseTestFactory.Create("test-key");

        TimeSpan deadline = TimeSpan.FromMinutes(5);

        FileBatchApiClient client = new(
            new FakeHttpClientFactory(handler),
            credentials)
        {
            RequestResponseHeadersTimeout = deadline,
            HeadersDeadlineClock = clock,
        };

        var call = client.ListFilesAsync(purpose: null, CancellationToken.None);

        await body.ReadStarted.WaitAsync(HangGuard);

        Assert.Equal(1, clock.TimersCreated);

        Assert.Equal(0, clock.ActiveTimers);

        clock.Advance(deadline + deadline);

        Assert.False(call.IsCompleted);

        body.Release();

        var result = await call.WaitAsync(HangGuard);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : string.Empty);
    }

    /// <summary>
    /// An upload answers only after the whole file has been sent and stored, which can take as long
    /// as the file is large, so it is not a short call: it goes out on the unbounded client and the
    /// headers deadline never applies to it.
    /// </summary>
    [Fact]
    public async Task Upload_is_not_bounded_by_the_headers_deadline()
    {
        string filePath = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-file-batch-{Guid.NewGuid():N}.txt");

        await File.WriteAllTextAsync(filePath, "a file that takes a while to store");

        try
        {
            DelayedStartHandler handler = new(
                TimeSpan.FromMilliseconds(400),
                TimeSpan.Zero,
                """{"id":"file-1","bytes":34,"created_at":1,"filename":"a.txt","purpose":"assistants","object":"file"}""");

            using ArcanumApiCredentialLease credentials =
                ArcanumApiCredentialLeaseTestFactory.Create("test-key");

            FakeHttpClientFactory factory = new(handler);

            FileBatchApiClient client = new(factory, credentials)
            {
                RequestResponseHeadersTimeout = TimeSpan.FromMilliseconds(100),
            };

            var result = await client
                .UploadFileAsync(filePath, "assistants", "text/plain", CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : string.Empty);

            Assert.Equal(
                [ArcanumApiClient.StreamingHttpClientName],
                factory.RequestedNames);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    private sealed class FakeHttpClientFactory(
        HttpMessageHandler handler) : IHttpClientFactory
    {
        internal List<string> RequestedNames { get; } = [];

        public HttpClient CreateClient(string name)
        {
            RequestedNames.Add(name);

            return new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:5001/"),
            };
        }
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        private int _requestCount;

        internal int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _requestCount);

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);

            throw new InvalidOperationException("The hanging handler never answers.");
        }
    }

    private sealed class DelayedStartHandler(
        TimeSpan headersDelay,
        TimeSpan bodyDelay,
        string json) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // The request body is consumed first, as a real transport would, so a content factory
            // that fails is observed here rather than skipped.
            if (request.Content is not null)
            {
                _ = await request.Content
                    .ReadAsByteArrayAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            await Task.Delay(headersDelay, cancellationToken).ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new DelayedStartStream(Encoding.UTF8.GetBytes(json), bodyDelay)),
            };
        }
    }

    private sealed class DelayedStartStream(byte[] payload, TimeSpan delay) : Stream
    {
        private readonly MemoryStream _inner = new(payload);

        private bool _delayed;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_delayed)
            {
                _delayed = true;

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
