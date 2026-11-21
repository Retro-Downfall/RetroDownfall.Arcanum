using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.GuildProcessFixture;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Security;

public partial class Program
{
    internal static void RequireIsolatedProcessFixture()
    {
        string? home = Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME");

        if (Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") != "Testing"
            || Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") != "Testing"
            || Environment.GetEnvironmentVariable("ARCANUM_TEST_IN_MEMORY_CREDENTIALS") != "1"
            || string.IsNullOrWhiteSpace(home) || !Path.IsPathFullyQualified(home))
            throw new InvalidOperationException("The Guild process fixture requires an isolated Testing profile.");

        string testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "arcanum-tests"));

        StringComparison pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        DirectoryInfo directory = new(home);

        if (!directory.FullName.StartsWith(testRoot + Path.DirectorySeparatorChar, pathComparison))
            throw new InvalidOperationException("The Guild process fixture requires a profile under the test root.");

        for (DirectoryInfo? current = directory; current is not null; current = current.Parent)
        {
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0) break;

            if (string.Equals(current.FullName, testRoot, pathComparison)) return;
        }

        throw new InvalidOperationException("The Guild process fixture requires a retained physical test directory.");
    }

    // Only this test executable links the implementation. Each process has a real Kestrel
    // listener; the socket mapping represents the one configured peer's DNS/routing in the fixture.
    static partial void ConfigureProcessFixture(WebApplicationBuilder builder)
    {
        if (Environment.GetEnvironmentVariable(GuildProcessFixtureMarker.AcceptPeerVariable) == "1")
            builder.Services.PostConfigure<HostFilteringOptions>(settings =>
                settings.AllowedHosts = [.. settings.AllowedHosts, GuildProcessFixtureMarker.PeerHost]);

        if (Environment.GetEnvironmentVariable(GuildProcessFixtureMarker.ConnectPeerVariable) is not { } configured)
            return;

        if (!int.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
            throw new InvalidOperationException("The Guild process fixture requires one exact peer port.");

        builder.Services.AddSingleton<IDnsResolver, FixturePeerDns>();

        builder.Services.AddHttpClient(A2AClientService.OutboundHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                ConnectCallback = (context, ct) => ConnectFixturePeerAsync(context, port, ct),
            });
    }

    private static async ValueTask<Stream> ConnectFixturePeerAsync(SocketsHttpConnectionContext context, int port, CancellationToken ct)
    {
        if (context.DnsEndPoint.Host != GuildProcessFixtureMarker.PeerHost || context.DnsEndPoint.Port != port
            || context.InitialRequestMessage.RequestUri?.Scheme != Uri.UriSchemeHttp)
            throw new HttpRequestException("The test transport can only connect to the configured fixture peer.");

        Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), ct);

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();

            throw;
        }
    }

    private sealed class FixturePeerDns : IDnsResolver
    {
        private readonly SystemDnsResolver _system = new();

        public Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default) =>
            host == GuildProcessFixtureMarker.PeerHost
                ? Task.FromResult<IPAddress[]>([IPAddress.Parse("192.0.2.1")])
                : _system.GetHostAddressesAsync(host, cancellationToken);
    }
}
