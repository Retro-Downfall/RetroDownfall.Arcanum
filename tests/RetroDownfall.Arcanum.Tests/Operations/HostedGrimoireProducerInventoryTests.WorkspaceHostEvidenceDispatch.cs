using Microsoft.CodeAnalysis;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("Authenticates", "complete")]
    [InlineData("ResolveAsync", "complete")]
    [InlineData("ResolveOperatorAsync", "complete")]
    [InlineData("Authenticates", "missing-policy")]
    [InlineData("ResolveAsync", "missing-policy")]
    [InlineData("ResolveOperatorAsync", "missing-policy")]
    [InlineData("Authenticates", "missing-unavailable")]
    [InlineData("ResolveAsync", "missing-unavailable")]
    [InlineData("ResolveOperatorAsync", "missing-unavailable")]
    [InlineData("Authenticates", "extra")]
    [InlineData("ResolveAsync", "extra")]
    [InlineData("ResolveOperatorAsync", "extra")]
    public void Workspace_host_evidence_dispatch_traverses_both_bodies_and_refuses_census_drift(string operation, string census)
    {
        const string policy = """
            internal sealed class GuildLocalHostPolicy : IGuildWorkspaceLeaseHostEvidence
            {
                public bool Authenticates(RetroDownfall.Arcanum.Core.Guild.GuildCallerContext caller)
                {
                    System.IO.File.Delete("policy-authentication-effect");
                    return false;
                }
                public async Task<RetroDownfall.Arcanum.Core.Primitives.Result<GuildWorkspaceHostEvidence>> ResolveAsync(Guid workspaceId, CancellationToken token)
                {
                    await Task.CompletedTask.ConfigureAwait(false);
                    System.IO.File.Delete("policy-resolution-effect");
                    return RetroDownfall.Arcanum.Core.Primitives.Result<GuildWorkspaceHostEvidence>.Success(new());
                }
                public async Task<RetroDownfall.Arcanum.Core.Primitives.Result<GuildOperatorWorkspaceHostEvidence>> ResolveOperatorAsync(Guid workspaceId, CancellationToken token)
                {
                    await Task.CompletedTask.ConfigureAwait(false);
                    System.IO.File.Delete("policy-operator-resolution-effect");
                    return RetroDownfall.Arcanum.Core.Primitives.Result<GuildOperatorWorkspaceHostEvidence>.Success(new());
                }
            }
            """;

        const string unavailable = """
            internal sealed class UnavailableGuildWorkspaceLeaseHostEvidence : IGuildWorkspaceLeaseHostEvidence
            {
                public bool Authenticates(RetroDownfall.Arcanum.Core.Guild.GuildCallerContext caller)
                {
                    System.IO.File.Delete("unavailable-authentication-effect");
                    return false;
                }
                public async Task<RetroDownfall.Arcanum.Core.Primitives.Result<GuildWorkspaceHostEvidence>> ResolveAsync(Guid workspaceId, CancellationToken token)
                {
                    await Task.CompletedTask.ConfigureAwait(false);
                    System.IO.File.Delete("unavailable-resolution-effect");
                    return RetroDownfall.Arcanum.Core.Primitives.Result<GuildWorkspaceHostEvidence>.Success(new());
                }
                public async Task<RetroDownfall.Arcanum.Core.Primitives.Result<GuildOperatorWorkspaceHostEvidence>> ResolveOperatorAsync(Guid workspaceId, CancellationToken token)
                {
                    await Task.CompletedTask.ConfigureAwait(false);
                    System.IO.File.Delete("unavailable-operator-resolution-effect");
                    return RetroDownfall.Arcanum.Core.Primitives.Result<GuildOperatorWorkspaceHostEvidence>.Success(new());
                }
            }
            """;

        string implementations = (census == "missing-policy" ? string.Empty : policy)
            + (census == "missing-unavailable" ? string.Empty : unavailable)
            + (census == "extra" ? unavailable.Replace("UnavailableGuildWorkspaceLeaseHostEvidence", "UnexpectedGuildWorkspaceLeaseHostEvidence", StringComparison.Ordinal) : string.Empty);

        string invocation = operation == "Authenticates"
            ? "host.Authenticates(null!); await Task.CompletedTask;"
            : $"await host.{operation}(Guid.Empty, token).ConfigureAwait(false);";

        string source = R2Source("RetroDownfall.Arcanum.Infrastructure.Guild.IGuildWorkspaceLeaseHostEvidence host = null!; " + invocation, """
            namespace RetroDownfall.Arcanum.Infrastructure.Guild
            {
                internal sealed record GuildWorkspaceHostEvidence;
                internal sealed record GuildOperatorWorkspaceHostEvidence;
                internal interface IGuildWorkspaceLeaseHostEvidence
                {
                    bool Authenticates(RetroDownfall.Arcanum.Core.Guild.GuildCallerContext caller);
                    Task<RetroDownfall.Arcanum.Core.Primitives.Result<GuildWorkspaceHostEvidence>> ResolveAsync(Guid workspaceId, CancellationToken token);
                    Task<RetroDownfall.Arcanum.Core.Primitives.Result<GuildOperatorWorkspaceHostEvidence>> ResolveOperatorAsync(Guid workspaceId, CancellationToken token);
                }
                /* IMPLEMENTATIONS */
            }
            """.Replace("/* IMPLEMENTATIONS */", implementations, StringComparison.Ordinal));

        var compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        if (census != "complete")
        {
            Assert.Contains(discovery.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_CLOSED_DISPATCH_CHANGED"
                && diagnostic.Detail.StartsWith("RetroDownfall.Arcanum.Infrastructure.Guild.IGuildWorkspaceLeaseHostEvidence." + operation + ";", StringComparison.Ordinal));

            return;
        }

        HostedProducerSite[] effects = discovery.Items.Where(static site => site.Callee == "System.IO.File.Delete").ToArray();

        Assert.Equal(2, effects.Length);

        Assert.All(effects, static effect =>
        {
            Assert.Null(effect.WorkFrontierCapsuleId);

            Assert.Null(effect.EffectFrontierCapsuleId);
        });

        Assert.Contains(discovery.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");

        Assert.Contains(discovery.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING");

        Assert.DoesNotContain(discovery.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_CALL_TARGET_UNRESOLVED"
            or "HOSTED_CLOSED_DISPATCH_CHANGED" or "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }
}
