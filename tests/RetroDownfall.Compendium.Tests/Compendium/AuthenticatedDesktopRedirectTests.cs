using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Compendium.Ux.Services;
using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Compendium;

[Collection("EnvVarSensitive")]
public sealed class AuthenticatedDesktopRedirectTests
{
    private const string DummyKey = "desktop-redirect-dummy-key";

    private const string ProbeResponse = """
        {"isSuccess":true,"error":null,"data":{"providerName":"desktop-probe","providerType":"CodexCli","status":"Configured","version":"test","summary":"ready","remediation":"","remediationCommand":null,"enumeration":"Unknown","models":[],"hiddenModels":[]}}
        """;

    [Theory]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task Authenticated_probe_does_not_connect_to_or_send_credentials_to_a_redirected_origin(int status)
    {
        using ArcanumTestHomeScope home = new("compendium-redirect");

        using LoopbackHostEnvironment hostEnvironment = new();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));

        await using DesktopRedirectOrigin target = new(200, "application/json", ProbeResponse, deadline.Token);

        await using DesktopRedirectOrigin original = new(status, "text/plain", "", deadline.Token, target.RedirectUri);

        using ServiceProvider services = ServiceCollectionConfigurator.Build();

        FamiliarProbeClient client = CreateClient(services, original.Port);

        var response = await client.ProbeAsync("desktop-probe", deadline.Token);

        // Keep the real network boundary ahead of response assertions: the vulnerable
        // named registration reaches target and forwards the reusable dummy credential.
        Assert.Equal(0, target.AcceptedConnections);

        Assert.Empty(target.Requests);

        AssertOriginalRequest(original);

        Assert.True(response.IsFailure);
    }

    [Fact]
    public async Task Ordinary_authenticated_probe_uses_the_original_origin_and_returns_its_response()
    {
        using ArcanumTestHomeScope home = new("compendium-original");

        using LoopbackHostEnvironment hostEnvironment = new();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));

        await using DesktopRedirectOrigin target = new(200, "text/plain", "unused", deadline.Token);

        await using DesktopRedirectOrigin original = new(200, "application/json", ProbeResponse, deadline.Token);

        using ServiceProvider services = ServiceCollectionConfigurator.Build();

        FamiliarProbeClient client = CreateClient(services, original.Port);

        var response = await client.ProbeAsync("desktop-probe", deadline.Token);

        Assert.Equal(0, target.AcceptedConnections);

        Assert.Empty(target.Requests);

        AssertOriginalRequest(original);

        Assert.True(response.IsSuccess);

        Assert.Equal("desktop-probe", response.Value.ProviderName);

        Assert.Equal(FamiliarProbeStatus.Configured, response.Value.Status);

        Assert.Equal(FamiliarModelEnumeration.Unknown, response.Value.Enumeration);

        Assert.Empty(response.Value.Models);

        Assert.Empty(response.Value.HiddenModels);
    }

    private static FamiliarProbeClient CreateClient(ServiceProvider services, int port) => new(
        new DummyConfigurationStore(new ArcanumSettings
        {
            Host = new HostSettings
            {
                Port = port,
                ListenAny = false,
                Https = new HttpsSettings { Enabled = false },
            },
        }),
        new DummySecretStore(),
        services.GetRequiredService<IHttpClientFactory>());

    private static void AssertOriginalRequest(DesktopRedirectOrigin original)
    {
        Assert.Equal(1, original.AcceptedConnections);

        RecordedDesktopRequest request = Assert.Single(original.Requests);

        Assert.Equal("GET", request.Method);

        Assert.Equal("/api/providers/desktop-probe/familiar-probe", request.Path);

        Assert.Equal(DummyKey, request.ApiKey);

        Assert.Empty(request.Body);
    }

    // The production address resolver honors this boolean environment override. Restore it
    // under the existing nonparallel environment collection; never read credential variables.
    private sealed class LoopbackHostEnvironment : IDisposable
    {
        private readonly string? _original = Environment.GetEnvironmentVariable("ARCANUM_HOST_ANY");

        public LoopbackHostEnvironment() => Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", "false");

        public void Dispose() => Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", _original);
    }

    private sealed class DummyConfigurationStore(ArcanumSettings settings) : IArcanumConfigurationStore
    {
        public string ConfigurationFilePath => "unused-dummy-configuration";

        public event EventHandler? ExternalChange
        {
            add { }
            remove { }
        }

        public DateTimeOffset? GetLastWriteTimeUtc() => null;

        public Task<ArcanumSettings> ReadAsync(CancellationToken ct = default) => Task.FromResult(settings);

        public Task<ConfigurationWriteResult> WriteAsync(ArcanumSettings updated, CancellationToken ct = default) =>
            throw new InvalidOperationException("This HTTP test must not write configuration.");

        public void Dispose()
        {
        }
    }

    private sealed class DummySecretStore : ISecretStore
    {
        public Task<string?> GetApiKeyAsync() => throw new InvalidOperationException("The probe must use a pure Peek.");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() => throw new InvalidOperationException("The probe must use a pure Peek.");

        public Task<SecretStoreReadResult> PeekApiKeyReadResultAsync() => Task.FromResult(SecretStoreReadResult.Ok(DummyKey));

        public Task SaveApiKeyAsync(string apiKey) => throw new InvalidOperationException("This HTTP test must not persist credentials.");

        public Task<string?> GetGrimoireEncryptionSecretAsync() => throw new InvalidOperationException("This HTTP test must not read encryption secrets.");

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => throw new InvalidOperationException("This HTTP test must not persist encryption secrets.");
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
