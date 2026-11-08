using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("same-service")]
    [InlineData("foreign-service")]
    [InlineData("catalog-alias")]
    [InlineData("missing-owner")]
    [InlineData("ambiguous-owner")]
    public void Self_admitted_task_handoff_uses_its_unique_declared_catalog_owner(string arrangement)
    {
        const string type = "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService";

        string source = ApprenticeSelfHandoffSource("exact") + """
            namespace RetroDownfall.Arcanum.Infrastructure.Hosting
            {
                public sealed class QuestCaller
                {
                    public async System.Threading.Tasks.Task BeginAsync(ApprenticeService service, System.Threading.CancellationToken token)
                    {
                        await service.StartAsync(token);
                    }
                }
            }
            """;

        HostedProducerOperationEntry startup = new(type + ".StartAsync", "src/Fixture.cs", type, "StartAsync", HostedProducerAuthorityKind.PreReadinessStartup, null, type + ".StartAsync: awaited startup", []);

        HostedProducerOperationEntry execution = new(type + ".RunApprenticeAsync", "src/Fixture.cs", type, "RunApprenticeAsync", HostedProducerAuthorityKind.OrdinaryHostedWork, GrimoireWorkKind.ApprenticeExecution, null, []);

        string owner = arrangement == "catalog-alias" ? "ReviewedExecutionOwner" : "ApprenticeService";

        List<HostedProducerServiceEntry> catalog = [new(owner, arrangement == "missing-owner" ? [startup] : [startup, execution])];

        if (arrangement != "same-service")
        {
            const string caller = "RetroDownfall.Arcanum.Infrastructure.Hosting.QuestCaller";

            catalog.Add(new("QuestCaller", [new(caller + ".BeginAsync", "src/Fixture.cs", caller, "BeginAsync", HostedProducerAuthorityKind.FiniteRequest, null, caller + ".BeginAsync: requests a tracked independently admitted task", [])]));
        }

        if (arrangement == "ambiguous-owner")
        {
            catalog.Add(new("OtherExecutionOwner", [execution]));
        }

        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.DiscoverProducerSites([Compile(source)], new(["ApprenticeService"], []), catalog, []);

        bool refused = arrangement is "missing-owner" or "ambiguous-owner";

        Assert.Equal(refused, discovery.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN" && diagnostic.Detail.StartsWith("System.Threading.Tasks.Task.Run;", StringComparison.Ordinal)));

        Assert.Contains(discovery.Items, static site => site.Callee == "System.IO.File.Exists");

        if (!refused)
        {
            Assert.Empty(discovery.Diagnostics);

            HostedProducerSite[] executionSites = discovery.Items.Where(site => site.AuthorityOperationId == execution.OperationId).ToArray();

            Assert.NotEmpty(executionSites);

            Assert.All(executionSites, site => Assert.Equal(owner, site.RootType));

            Assert.DoesNotContain(discovery.Items, site => site.RootType == "QuestCaller" && site.AuthorityOperationId == execution.OperationId);
        }
    }

    [Theory]
    [InlineData("same-service")]
    [InlineData("foreign-service")]
    [InlineData("catalog-alias")]
    [InlineData("missing-owner")]
    [InlineData("ambiguous-owner")]
    public void Host_owned_task_handoff_uses_its_unique_declared_catalog_owner(string arrangement)
    {
        string source = R2Source("background = Task.Run(() => ContinueAsync(token));")
            .Replace("public Task StopAsync(CancellationToken token) => Task.CompletedTask;", "private Task? background; private async Task ContinueAsync(CancellationToken token) { " + R2Admission + " System.IO.File.Delete(\"path\"); } public async Task StopAsync(CancellationToken token) { if (background is not null) await background; }", StringComparison.Ordinal)
            + "public sealed class QuestCaller { public async System.Threading.Tasks.Task BeginAsync(Worker service, System.Threading.CancellationToken token) { await service.StartAsync(token); } }";

        HostedProducerOperationEntry startup = OrdinaryRoot() with { Authority = HostedProducerAuthorityKind.PreReadinessStartup, WorkKind = null, Proof = "Worker.StartAsync: awaited startup" };

        HostedProducerOperationEntry execution = OrdinaryRoot("Worker.ContinueAsync") with { Member = "ContinueAsync" };

        string owner = arrangement == "catalog-alias" ? "ReviewedExecutionOwner" : "Worker";

        List<HostedProducerServiceEntry> catalog = [new(owner, arrangement == "missing-owner" ? [startup] : [startup, execution])];

        if (arrangement != "same-service")
        {
            catalog.Add(new("QuestCaller", [new("QuestCaller.BeginAsync", "src/Fixture.cs", "QuestCaller", "BeginAsync", HostedProducerAuthorityKind.FiniteRequest, null, "QuestCaller.BeginAsync: requests a tracked independently admitted task", [])]));
        }

        if (arrangement == "ambiguous-owner")
        {
            catalog.Add(new("OtherExecutionOwner", [execution]));
        }

        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.DiscoverProducerSites([Compile(source)], new(["Worker"], []), catalog, []);

        bool refused = arrangement is "missing-owner" or "ambiguous-owner";

        Assert.Equal(refused, discovery.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN" && diagnostic.Detail.StartsWith("System.Threading.Tasks.Task.Run;", StringComparison.Ordinal)));

        Assert.Contains(discovery.Items, static site => site.Callee == "System.IO.File.Delete");

        if (!refused)
        {
            Assert.Empty(discovery.Diagnostics);

            HostedProducerSite[] executionSites = discovery.Items.Where(site => site.AuthorityOperationId == execution.OperationId).ToArray();

            Assert.NotEmpty(executionSites);

            Assert.All(executionSites, site => Assert.Equal(owner, site.RootType));

            Assert.DoesNotContain(discovery.Items, site => site.RootType == "QuestCaller" && site.AuthorityOperationId == execution.OperationId);
        }
    }
}
