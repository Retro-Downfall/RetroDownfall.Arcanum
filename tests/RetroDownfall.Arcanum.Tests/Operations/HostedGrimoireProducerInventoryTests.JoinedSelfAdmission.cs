using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("joined")]
    [InlineData("foreign-receiver")]
    [InlineData("changed-tool")]
    [InlineData("replacement-token")]
    [InlineData("different-call")]
    [InlineData("detached")]
    [InlineData("missing-work")]
    [InlineData("inherited-work")]
    [InlineData("missing-postcheck")]
    public void GuildWorkspaceRevalidationRetainsEveryProducerAcrossTheJoinedAdmission(string shape)
    {
        const string target = "RetroDownfall.Arcanum.Infrastructure.Guild.GuildQuestStore.AuthorizeLocalToolAsync";

        const string call = "call.Registry.Parent.Store.RevalidateWorkspaceMutationToolAsync(call, cancellationToken)";

        bool inherited = shape == "inherited-work";

        string body = (inherited ? R2Admission : string.Empty)
            + "RetroDownfall.Arcanum.Infrastructure.Guild.GuildWorkspaceMutationFence.Lease mutationLease = null!; "
            + "await mutationLease.ValidateAsync(false, token).ConfigureAwait(false);";

        string action = shape switch
        {
            "foreign-receiver" => "replacement.RevalidateWorkspaceMutationToolAsync(call, cancellationToken)",
            "replacement-token" => call.Replace("cancellationToken", "CancellationToken.None", StringComparison.Ordinal),
            "different-call" => call.Replace("RevalidateWorkspaceMutationToolAsync", "OtherAsync", StringComparison.Ordinal),
            _ => call,
        };

        string revalidate = shape == "detached"
            ? "_ = " + action + ";"
            : "Result live = await " + action + ".ConfigureAwait(false); if (live.IsFailure) return live;";

        string ownWork = shape == "missing-work"
            ? string.Empty
            : """
                if (!gate.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? work))
                    return InvalidReceipt;
                await using IGrimoireWorkLease held = work!;
                """;

        string complete = shape == "missing-postcheck"
            ? "Result.Success()"
            : "OwnsWorkspaceMutationCall(call) ? Result.Success() : InvalidReceipt";

        string source = R2Source(body, $$"""
            namespace RetroDownfall.Arcanum.Core.Primitives
            {
                internal readonly record struct Result(bool IsFailure)
                {
                    internal Result Error => this;
                    internal static Result Success() => new(false);
                }
            }

            namespace RetroDownfall.Arcanum.Infrastructure.Guild
            {
                using RetroDownfall.Arcanum.Core.Primitives;
                using RetroDownfall.Arcanum.Infrastructure.Data;

                internal sealed class GuildChildToolAdmission
                {
                    internal GuildLocalToolRegistry Registry { get; } = null!;
                    internal GuildLocalToolRegistry.Invocation Frame { get; } = null!;
                    internal int Tool => {{(shape == "changed-tool" ? "0" : "Frame.Tool")}};
                    internal int Eligibility { get; }
                }

                internal sealed class GuildLocalToolRegistry
                {
                    internal GuildLocalInferenceExecution Parent { get; } = null!;

                    internal sealed class Invocation
                    {
                        internal int Tool { get; }
                    }
                }

                internal sealed class GuildLocalInferenceExecution
                {
                    internal GuildQuestStore Store { get; } = null!;
                }

                internal sealed class GuildWorkspaceMutationFence
                {
                    internal sealed class Lease
                    {
                        private readonly Invocation invocation = null!;
                        private readonly GuildQuestStore replacement = null!;

                        public async ValueTask<Result> ValidateAsync(bool rollback, CancellationToken cancellationToken)
                        {
                            if (!rollback && invocation.ToolAdmission is { } call)
                            {
                                {{revalidate}}
                            }

                            await Task.CompletedTask;
                            return Result.Success();
                        }
                    }

                    private sealed class Invocation
                    {
                        internal GuildChildToolAdmission? ToolAdmission { get; }
                    }
                }

                internal sealed class GuildQuestStore
                {
                    private static Result InvalidReceipt => new(true);
                    private readonly IGrimoireConnectionAdmissionGate gate = null!;

                    internal async Task<Result> RevalidateWorkspaceMutationToolAsync(GuildChildToolAdmission call, CancellationToken ct)
                    {
                        if (!OwnsWorkspaceMutationCall(call)) return InvalidReceipt;
                        var allowed = await AuthorizeLocalToolAsync(call.Registry, call.Tool, call.Eligibility, ct).ConfigureAwait(false);
                        return allowed.IsFailure ? allowed.Error : {{complete}};
                    }

                    internal Task<Result> OtherAsync(GuildChildToolAdmission call, CancellationToken ct) =>
                        AuthorizeLocalToolAsync(call.Registry, call.Tool, call.Eligibility, ct);

                    private bool OwnsWorkspaceMutationCall(GuildChildToolAdmission call)
                    {
                        _ = System.IO.File.Exists("fixture-host-lock");
                        return ReferenceEquals(call.Registry.Parent.Store, this);
                    }

                    private async Task<Result> AuthorizeLocalToolAsync(GuildLocalToolRegistry registry, int tool, int eligibility, CancellationToken ct)
                    {
                        {{ownWork}}
                        await using Microsoft.Data.Sqlite.SqliteConnection connection = new();
                        await connection.OpenAsync(ct).ConfigureAwait(false);
                        return Result.Success();
                    }
                }
            }
            """).Replace("WorkspaceIndexing = 4 }", "WorkspaceIndexing = 4, GuildStorage = 20 }", StringComparison.Ordinal);

        CSharpCompilation compilation = Compile(source).WithAssemblyName("RetroDownfall.Arcanum.Infrastructure");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerOperationEntry root = inherited
            ? OrdinaryRoot()
            : OrdinaryRoot() with { Authority = HostedProducerAuthorityKind.PreReadinessStartup, WorkKind = null };

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [root])], []);

        HostedProducerSite producer = Assert.Single(result.Items, static site =>
            site.EnclosingType.EndsWith(".GuildQuestStore", StringComparison.Ordinal)
            && site.Callee.EndsWith(".OpenAsync", StringComparison.Ordinal));

        IEnumerable<HostedProducerSite> links = result.Items.Where(static site => site.Kind == HostedProducerSiteKind.JoinedSelfAdmission);

        if (shape is "joined" or "missing-work")
        {
            HostedProducerSite link = Assert.Single(links);
            Assert.Equal(root.OperationId, link.AuthorityOperationId);
            Assert.Equal(target, link.Callee);
            Assert.Equal(target, producer.AuthorityOperationId);

            if (shape == "joined")
            {
                Assert.Empty(result.Diagnostics);

                Assert.Contains(result.Items, site => site.Callee == "System.IO.File.Exists"
                    && site.AuthorityOperationId == root.OperationId);

                Assert.DoesNotContain(result.Items, site => site.Callee == "System.IO.File.Exists"
                    && site.AuthorityOperationId == target);

                Assert.NotNull(producer.WorkFrontierCapsuleId);
                Assert.Contains(result.Items, site =>
                    site.Kind == HostedProducerSiteKind.EffectFrontier
                    && site.CapsuleId == producer.WorkFrontierCapsuleId
                    && site.AdmittedWorkKind == global::RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind.GuildStorage);

                Assert.Empty(JoinManifestDiagnostics(result.Items));

                VerifyJoinedWorkerAccounting(result, link);

                Assert.Contains(JoinManifestDiagnostics(result.Items.Where(site => site != link)),
                    static diagnostic => diagnostic.Code == "HOSTED_MANIFEST_JOINED_ADMISSION_UNBOUND");

                Assert.Contains(JoinManifestDiagnostics(result.Items.Where(site => site.RootType != producer.RootType)),
                    static diagnostic => diagnostic.Code == "HOSTED_MANIFEST_JOINED_ADMISSION_UNBOUND");

                Assert.Contains(JoinManifestDiagnostics(result.Items.Select(site => site == link
                        ? site with { Callee = "OtherWork" } : site)),
                    static diagnostic => diagnostic.Code == "HOSTED_MANIFEST_JOINED_ADMISSION_EDGE_INVALID");

                Assert.Contains(JoinManifestDiagnostics(result.Items.Select(site => site == producer
                        ? site with { WorkFrontierCapsuleId = null } : site)),
                    static diagnostic => diagnostic.Code == "HOSTED_MANIFEST_JOINED_ADMISSION_WORK_UNBOUND");
            }
            else
            {
                Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");
            }
        }
        else
        {
            Assert.Empty(links);
            Assert.Equal(root.OperationId, producer.AuthorityOperationId);

            Assert.DoesNotContain(JoinManifestDiagnostics(result.Items), static diagnostic =>
                diagnostic.Code.StartsWith("HOSTED_MANIFEST_JOINED_ADMISSION_", StringComparison.Ordinal));
        }
    }

    private static void VerifyJoinedWorkerAccounting(HostedProducerDiscovery<HostedProducerSite> result, HostedProducerSite link)
    {
        HostedProducerAnalysisMetrics observed = Assert.IsType<HostedProducerAnalysisMetrics>(result.AnalysisMetrics);

        HostedProducerRootTraversalMetric child = Assert.Single(observed.RootTraversals,
            static root => root.SelfAdmittedWorkKind.HasValue);

        HostedProducerRootTraversalMetric parent = Assert.Single(observed.RootTraversals,
            root => root.RootOperation == link.AuthorityOperationId);

        HostedProducerDiscovery<HostedProducerSite> Contribution(string owner, int states) =>
            new(result.Items.Select(site => site == link
                ? site with { RootType = owner, AuthorityOperationId = owner + ".StartAsync" }
                : site).ToArray(), [], observed with
            {
                RootTraversals =
                [
                    child with { AnalyzedStates = states },
                    parent with { RootType = owner, RootOperation = owner + ".StartAsync" },
                ],
            });

        int limit = HostedGrimoireProducerInventory.MaximumAnalyzedStatesPerRoot;

        HostedProducerDiscovery<HostedProducerSite> first = Contribution("First", 11);

        HostedProducerDiscovery<HostedProducerSite> second = Contribution("Second", 17);

        HostedProducerAnalysisMetrics merged = HostedProducerRootWorkers.MergeMetrics([first, second], limit);

        Assert.Equal(3, merged.RootTraversals.Count);

        Assert.Equal(28, Assert.Single(merged.RootTraversals, root => root.RootOperation == child.RootOperation).AnalyzedStates);

        Assert.Throws<InvalidOperationException>(() => HostedProducerRootWorkers.MergeMetrics([first, first], limit));

        Assert.Throws<InvalidOperationException>(() => HostedProducerRootWorkers.MergeMetrics(
            [first, second with
            {
                AnalysisMetrics = observed with { RootTraversals = [parent] },
            }], limit));

        Assert.Throws<InvalidOperationException>(() => HostedProducerRootWorkers.MergeMetrics(
            [first, second with
            {
                AnalysisMetrics = observed with { RootTraversals = [parent, child with { SelfAdmittedWorkKind = null }] },
            }], limit));

        Assert.Contains("HOSTED_TRAVERSAL_STATE_LIMIT_EXCEEDED", Assert.Throws<InvalidOperationException>(() =>
            HostedProducerRootWorkers.MergeMetrics([first, Contribution("Second", limit - 10)], limit)).Message);
    }

    private static IReadOnlyList<HostedProducerInventoryDiagnostic> JoinManifestDiagnostics(IEnumerable<HostedProducerSite> sites) =>
        HostedGrimoireProducerInventory.ParseCapsuleManifest(HostedGrimoireProducerInventory.RenderCapsuleManifest(sites)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)).Diagnostics;
}
