using System.Net;

using System.Reflection;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Core.Mcp;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Mcp;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpConnectionManagerOwnedHttpClientTests
{
    [Fact]
    public async Task The_manager_owns_one_client_with_no_overall_deadline_and_disposes_it_with_itself()
    {
        ProbeHandler primary = new();

        McpConnectionManager manager = McpConnectionManagerHarness.Create(httpHandler: primary, managerOwnsHttpHandler: true);

        HttpClient client = Assert.IsType<HttpClient>(typeof(McpConnectionManager)
            .GetField("httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager));

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);

        Assert.False(primary.Disposed);

        await manager.DisposeAsync();

        Assert.True(primary.Disposed, "Disposing the manager must dispose the owned client and its handler chain.");
    }

    [Fact]
    public async Task A_failed_HTTP_start_leaves_the_shared_client_usable_for_the_next_start()
    {
        ProbeHandler primary = new();

        await using McpConnectionManager manager = McpConnectionManagerHarness.Create(httpHandler: primary, managerOwnsHttpHandler: true);

        await manager.RegisterFromConfigAsync(
            new McpConfig
            {
                McpServers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
                {
                    // A public address literal passes the egress policy without any DNS lookup.
                    ["first"] = new() { Url = "https://93.184.216.34/mcp" },
                    ["second"] = new() { Url = "https://93.184.216.35/mcp" },
                },
            },
            scopeWorkingDirectory: null,
            CancellationToken.None);

        Result first = await manager.StartAsync("first", workingDirectory: null);

        Assert.True(first.IsFailure);

        Assert.Equal("Mcp.StartFailed", first.Error.Code);

        int afterFirst = primary.Requests;

        Assert.True(afterFirst > 0, "The start never reached the owned HTTP client.");

        // The client is shared by every transport, so one session's teardown must not dispose it.
        Assert.False(primary.Disposed);

        Result second = await manager.StartAsync("second", workingDirectory: null);

        Assert.True(second.IsFailure);

        Assert.Equal("Mcp.StartFailed", second.Error.Code);

        Assert.True(primary.Requests > afterFirst, "The second start did not reach the shared client.");

        Assert.False(primary.Disposed);
    }

    // The seam replaces the SSRF-guarded primary handler of the manager's one client. Nothing but a comment
    // keeps it out of production code, so this pin reads every source file under src/.
    [Fact]
    public void The_test_seam_that_replaces_the_guarded_primary_handler_is_referenced_by_no_other_source()
    {
        string src = Path.Combine(global::RetroDownfall.Arcanum.Tests.Support.TestRepositoryPaths.RepositoryRoot(), "src");

        string declaring = Path.Combine("RetroDownfall.Arcanum.Infrastructure", "Mcp", "McpConnectionManager.cs");

        string factory = Path.Combine("RetroDownfall.Arcanum.Infrastructure", "Mcp", "ConnectionManager", "McpConnectionManager.TransportFactory.cs");

        List<string> seamReferences = [];

        List<string> primaryParameterReferences = [];

        List<string> twoArgumentCalls = [];

        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(src, file);

            if (relative.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            string text = File.ReadAllText(file);

            if (text.Contains("UseHttpPrimaryHandlerForTests", StringComparison.Ordinal))
            {
                seamReferences.Add(relative);
            }

            if (text.Contains("primaryForTests", StringComparison.Ordinal))
            {
                primaryParameterReferences.Add(relative);
            }

            if (!text.Contains("CreateOwnedHttpPipeline", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (InvocationExpressionSyntax call in CSharpSyntaxTree.ParseText(text).GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                string? name = call.Expression switch
                {
                    IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                    MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
                    _ => null,
                };

                if (name != "CreateOwnedHttpPipeline")
                {
                    continue;
                }

                string container = call.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault() switch
                {
                    MethodDeclarationSyntax method => method.Identifier.ValueText,
                    ConstructorDeclarationSyntax => "<constructor>",
                    _ => "<other>",
                };

                // A call that passes a primary handler is allowed only inside the seam itself.
                if (call.ArgumentList.Arguments.Count != 1)
                {
                    twoArgumentCalls.Add(relative + " in " + container + " with " + call.ArgumentList.Arguments.Count + " arguments");
                }
            }
        }

        // Only the declaring file mentions the seam, and only the factory file declares the primary parameter.
        Assert.Equal([declaring], seamReferences);

        Assert.Equal([factory], primaryParameterReferences);

        // The seam's own call is the one place a second argument is passed. Production calls pass the connect timeout alone.
        Assert.Equal([declaring + " in UseHttpPrimaryHandlerForTests with 2 arguments"], twoArgumentCalls);

        MethodInfo seam = Assert.Single(typeof(McpConnectionManager).GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic), static method => method.Name == "UseHttpPrimaryHandlerForTests");

        Assert.True(seam.IsAssembly, "The test seam must stay internal.");
    }

    private sealed class ProbeHandler : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _requests);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;

            base.Dispose(disposing);
        }
    }
}
