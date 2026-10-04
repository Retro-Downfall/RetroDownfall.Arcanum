using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// The tool names the in-process MCP server registers a handler for, read from a server composed with
/// every optional feature on. It is the authoritative "every internal tool" set for tests that must
/// account for each tool one by one.
/// </summary>
internal static class InternalToolHandlerNames
{
    public static IReadOnlyCollection<string> Registered()
    {
        IServiceScopeFactory scopeFactory = new ServiceCollection()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        (_, _, ArcanumInternalToolServer server) = InProcessMcpTransport.CreateServerChannelPair(
            new HumanPromptRegistry(),
            scopeFactory,
            new InertPacer(),
            workspaceRootNormalizedOrNull: null,
            listDirectoryMaxPaths: 16,
            new IntelligenceSettings(),
            maxFileReadSizeBytes: 1024,
            conclaveEnabled: true,
            sagaEnabled: true,
            a2aClientEnabled: true,
            attachmentsToolEnabled: true,
            maxJsonRpcLineBytes: 1_048_576,
            logger: NullLogger<ArcanumInternalToolServer>.Instance);

        return [.. server.RegisteredToolHandlerNamesForTests];
    }

    private sealed class InertPacer : IUnseenServantPacer
    {
        public Task<bool> SetDynamicIntervalAsync(
            string jobName,
            int intervalMinutes,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public int GetEffectiveInterval(UnseenServantJob job) => 0;

        public Task HydrateAsync(
            IReadOnlyList<UnseenServantWatermark> watermarks,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
