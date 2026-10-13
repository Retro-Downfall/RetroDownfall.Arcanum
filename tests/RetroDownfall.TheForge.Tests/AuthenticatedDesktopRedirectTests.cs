using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Serialization;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux;
using RetroDownfall.TheForge.Ux.Services;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

[Collection(TheForgeProcessEnvironmentCollection.Name)]
public sealed class AuthenticatedDesktopRedirectTests
{
    private const string DummyKey = "desktop-redirect-dummy-key";

    [Theory]
    [InlineData(302, "unary")]
    [InlineData(307, "unary")]
    [InlineData(308, "unary")]
    [InlineData(302, "ndjson")]
    [InlineData(307, "ndjson")]
    [InlineData(308, "ndjson")]
    [InlineData(302, "content")]
    [InlineData(307, "content")]
    [InlineData(308, "content")]
    public async Task Authenticated_calls_do_not_connect_to_or_send_credentials_to_a_redirected_origin(int status, string pathKind)
    {
        using TheForgeTestHomeScope home = new("forge-redirect");

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));

        var fixture = ResponseFor(pathKind, "from-target");

        await using DesktopRedirectOrigin target = new(200, fixture.ContentType, fixture.Body, deadline.Token);

        await using DesktopRedirectOrigin original = new(status, "text/plain", "", deadline.Token, target.RedirectUri);

        using ServiceProvider services = ServiceCollectionConfigurator.Build();

        CallOutcome outcome = await CallAsync(services, original.BaseUri, pathKind, deadline.Token);

        // The vulnerable registration reaches target and forwards DummyKey. Keep this safety
        // assertion ahead of response assertions so RED identifies the production boundary.
        Assert.Equal(0, target.AcceptedConnections);

        Assert.Empty(target.Requests);

        AssertOriginalRequest(original, pathKind);

        Assert.False(outcome.Success);
    }

    [Theory]
    [InlineData("unary")]
    [InlineData("ndjson")]
    [InlineData("content")]
    public async Task Ordinary_authenticated_calls_use_the_original_origin_and_return_its_response(string pathKind)
    {
        using TheForgeTestHomeScope home = new("forge-original");

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));

        var fixture = ResponseFor(pathKind, "from-original");

        await using DesktopRedirectOrigin target = new(200, "text/plain", "unused", deadline.Token);

        await using DesktopRedirectOrigin original = new(200, fixture.ContentType, fixture.Body, deadline.Token);

        using ServiceProvider services = ServiceCollectionConfigurator.Build();

        CallOutcome outcome = await CallAsync(services, original.BaseUri, pathKind, deadline.Token);

        Assert.Equal(0, target.AcceptedConnections);

        Assert.Empty(target.Requests);

        AssertOriginalRequest(original, pathKind);

        Assert.True(outcome.Success);

        Assert.Equal(pathKind == "unary" ? "true" : "from-original", outcome.Payload);
    }

    private static void AssertOriginalRequest(DesktopRedirectOrigin original, string pathKind)
    {
        Assert.Equal(1, original.AcceptedConnections);

        RecordedDesktopRequest request = Assert.Single(original.Requests);

        Assert.Equal(DummyKey, request.ApiKey);

        Assert.Equal(pathKind == "ndjson" ? "POST" : "GET", request.Method);

        Assert.Equal(pathKind switch
        {
            "unary" => "/api/config/redirect-probe",
            "ndjson" => "/api/intelligence/ping-stream",
            "content" => "/v1/files/file-probe/content",
            _ => throw new ArgumentOutOfRangeException(nameof(pathKind)),
        }, request.Path);

        if (pathKind == "ndjson")
        {
            Assert.Contains("\"prompt\":\"redirect probe\"", request.Body, StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(request.Body);
        }
    }

    private static (string ContentType, string Body) ResponseFor(string pathKind, string payload) => pathKind switch
    {
        "unary" => ("application/json", "{\"isSuccess\":true,\"data\":true,\"error\":null}"),
        "ndjson" => ("application/x-ndjson", "{\"type\":\"token\",\"message\":\"\",\"data\":\"" + payload + "\"}\n"),
        "content" => ("text/plain", payload),
        _ => throw new ArgumentOutOfRangeException(nameof(pathKind)),
    };

    private static async Task<CallOutcome> CallAsync(ServiceProvider services, Uri baseUri, string pathKind, CancellationToken cancellationToken)
    {
        IHttpClientFactory factory = services.GetRequiredService<IHttpClientFactory>();

        StaticTheForgeSettingsMonitor settings = new(new TheForgeSettings { BaseUrl = baseUri.AbsoluteUri });

        DummyApiKeyProvider key = new();

        ArcanumApiClient normal = new(factory, settings, key, NullLogger<ArcanumApiClient>.Instance);

        if (pathKind == "unary")
        {
            var response = await normal.GetAsync("/api/config/redirect-probe", TheForgeJsonContext.Default.ApiResponseBoolean, cancellationToken);

            return new CallOutcome(response?.IsSuccess == true, response?.Data == true ? "true" : null);
        }

        if (pathKind == "ndjson")
        {
            List<IntelligenceEvent> frames = [];

            try
            {
                await foreach (IntelligenceEvent frame in normal.PostNdjsonStreamAsync(
                    "/api/intelligence/ping-stream",
                    new PingRequest("redirect probe"),
                    TheForgeJsonContext.Default.PingRequest,
                    TheForgeJsonContext.Default.IntelligenceEvent,
                    cancellationToken))
                {
                    frames.Add(frame);
                }
            }
            catch (HttpRequestException)
            {
                return new CallOutcome(false, null);
            }

            bool oneToken = frames.Count == 1 && frames[0].Type == IntelligenceEventType.Token;

            return new CallOutcome(oneToken, frames.FirstOrDefault()?.Data);
        }

        if (pathKind == "content")
        {
            OpenAiCompatApiClient compatible = new(factory, settings, key, NullLogger<OpenAiCompatApiClient>.Instance, new RefusingTheForgeLocalMutationRunner());

            var response = await compatible.OpenContentStreamAsync("/v1/files/file-probe/content", cancellationToken);

            if (!response.Success || response.Data is null)
            {
                return new CallOutcome(false, null);
            }

            await using Stream content = response.Data;

            using StreamReader reader = new(content, Encoding.UTF8, leaveOpen: true);

            return new CallOutcome(true, await reader.ReadToEndAsync(cancellationToken));
        }

        throw new ArgumentOutOfRangeException(nameof(pathKind));
    }

    private sealed record CallOutcome(bool Success, string? Payload);

    private sealed class DummyApiKeyProvider : ITheForgeApiKeyProvider
    {
        public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(DummyKey);

        public Task PersistPastedKeyAsync(string key, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This HTTP test must not persist credentials.");

        public void ClearPasteDecline() =>
            throw new InvalidOperationException("This HTTP test must not mutate UI key state.");
    }
}

// A real HTTP/1.1 loopback origin. It records connections before reading any request bytes.
// StringContent in the covered production POST has a known Content-Length; no providers run.
internal sealed class DesktopRedirectOrigin : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    private readonly CancellationTokenSource _stop;

    private readonly ConcurrentQueue<RecordedDesktopRequest> _requests = new();

    private readonly Task _serve;

    private int _acceptedConnections;

    public DesktopRedirectOrigin(int status, string contentType, string body, CancellationToken cancellationToken, Uri? location = null)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _listener.Start();

        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _serve = ServeAsync(status, contentType, body, location);
    }

    public int Port { get; }

    public Uri BaseUri => new($"http://localhost:{Port}/");

    public Uri RedirectUri => new($"http://127.0.0.1:{Port}/captured");

    public int AcceptedConnections => Volatile.Read(ref _acceptedConnections);

    public RecordedDesktopRequest[] Requests => _requests.ToArray();

    private async Task ServeAsync(int status, string contentType, string body, Uri? location)
    {
        try
        {
            while (true)
            {
                using TcpClient connection = await _listener.AcceptTcpClientAsync(_stop.Token);

                Interlocked.Increment(ref _acceptedConnections);

                using NetworkStream stream = connection.GetStream();

                RecordedDesktopRequest request = await ReadRequestAsync(stream, _stop.Token);

                _requests.Enqueue(request);

                byte[] payload = Encoding.UTF8.GetBytes(body);

                string headers = $"HTTP/1.1 {status} Test\r\nContent-Type: {contentType}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n";

                if (location is not null)
                {
                    headers += $"Location: {location.AbsoluteUri}\r\n";
                }

                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers + "\r\n"), _stop.Token);

                await stream.WriteAsync(payload, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (SocketException) when (_stop.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested)
        {
        }
    }

    private static async Task<RecordedDesktopRequest> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        List<byte> headerBytes = [];

        byte[] single = new byte[1];

        while (true)
        {
            int count = await stream.ReadAsync(single, cancellationToken);

            if (count == 0)
            {
                throw new IOException("The loopback request ended before its headers.");
            }

            headerBytes.Add(single[0]);

            int length = headerBytes.Count;

            if (length > 16 * 1024)
            {
                throw new IOException("The loopback request headers exceeded the test bound.");
            }

            if (length >= 4 && headerBytes[length - 4] == 13 && headerBytes[length - 3] == 10
                && headerBytes[length - 2] == 13 && headerBytes[length - 1] == 10)
            {
                break;
            }
        }

        string[] lines = Encoding.ASCII.GetString(headerBytes.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        string[] requestLine = lines[0].Split(' ');

        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);

        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');

            headers.Add(line[..colon], line[(colon + 1)..].Trim());
        }

        if (headers.ContainsKey("Transfer-Encoding"))
        {
            throw new IOException("The covered StringContent request must have a known Content-Length.");
        }

        int bodyLength = headers.TryGetValue("Content-Length", out string? value)
            ? int.Parse(value, CultureInfo.InvariantCulture)
            : 0;

        if (bodyLength is < 0 or > 64 * 1024)
        {
            throw new IOException("The loopback request body exceeded the test bound.");
        }

        byte[] body = new byte[bodyLength];

        await stream.ReadExactlyAsync(body, cancellationToken);

        headers.TryGetValue("X-Arcanum-Key", out string? apiKey);

        return new RecordedDesktopRequest(requestLine[0], requestLine[1], apiKey, Encoding.UTF8.GetString(body));
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();

        _listener.Stop();

        try
        {
            await _serve;
        }
        finally
        {
            _stop.Dispose();
        }
    }
}

internal sealed record RecordedDesktopRequest(string Method, string Path, string? ApiKey, string Body);
