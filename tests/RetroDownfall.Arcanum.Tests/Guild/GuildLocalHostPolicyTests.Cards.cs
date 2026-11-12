using Microsoft.Extensions.AI;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public void Declared_card_parser_preserves_omitted_tools_versus_explicit_empty_subset()
    {
        const string omitted = "---\nname: Card\ndescription: Card guidance\n---\nFollow the Card";

        const string empty = "---\nname: Card\ndescription: Card guidance\ntools: []\n---\nFollow the Card";

        var absent = GuildDeclaredAgentParser.Parse(omitted);

        var explicitEmpty = GuildDeclaredAgentParser.Parse(empty);

        Assert.True(absent.IsSuccess, absent.Error.Message);

        Assert.True(explicitEmpty.IsSuccess, explicitEmpty.Error.Message);

        var marker = typeof(GuildParsedAgent).GetProperty("RequestedToolsSpecified");

        Assert.NotNull(marker);

        Assert.Equal(false, marker.GetValue(absent.Value));

        Assert.Equal(true, marker.GetValue(explicitEmpty.Value));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Selected_local_card_shapes_native_and_model_child_guidance_and_tools_with_pinned_replay(bool native, bool protectAfterAdmission)
    {
        await using Fixture f = await Fixture.CreateAsync(productionOptions: true);

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Providers = [new() { Name = "provider", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://api.example.test/v1", Models = [new() { Name = "model" }] }];

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant creation = new(GuildToolSourceKind.InternalMcp, "arcanum", "create_agent_task");

        GuildToolGrant allowed = new(GuildToolSourceKind.InternalMcp, "arcanum", "get_agent_task");

        GuildToolGrant excluded = new(GuildToolSourceKind.InternalMcp, "arcanum", "cancel_agent_task");

        var parent = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with { Tools = [creation, allowed, excluded] } });

        Assert.True(parent.IsSuccess, parent.Error.Message);

        var execution = await ToolExecutionAsync(f, owner, parent.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        GuildStore cards = new(f.Connections, f.Gate, TimeProvider.System);

        GuildAgentCard template = new(Guid.Empty, Guid.Empty, 0, "Scoped Card", "Scoped Card", "1", [], ["text/plain"], ["text/plain"],
            new(false, false, false), new(GuildAgentSourceKind.DeclaredGlobal, null, null, true, null));

        var saved = await cards.UpsertCardAsync(new("selected-card", null, null, null,
            new(template, "PINNED-CARD-GUIDANCE: use the exact source revision", [allowed.Name]), new string('A', 64)), default);

        Assert.True(saved.IsSuccess, saved.Error.Message);

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, new ChildQueue());

        using GuildLocalToolRegistry registry = new(execution, input.Value, coordinator);

        var context = f.Store.CreateToolContext(execution, registry, _ => true);

        var function = await context.AdvertiseAsync(new InertChildTool(creation.Name), creation, default);

        Assert.NotNull(function);

        using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), creation);

        AIFunctionArguments arguments = new() { ["goal"] = "Perform selected Card work", ["cardId"] = saved.Value.Payload.Card.CardId.ToString() };

        var nativeParent = await f.Store.GetAsync(owner, parent.Value.Quest.Id, default);

        GuildIssueChildRequest nativeRequest = new(Guid.NewGuid(), nativeParent.Value.Quest.Revision, null, null,
            "Perform selected Card work", new(), CardId: saved.Value.Payload.Card.CardId);

        // Exercise the generated native child contract with its actual bound Run caller.
        // The future public/operator child route remains proposed and receives no authority here.
        nativeRequest = JsonSerializer.Deserialize(JsonSerializer.Serialize(nativeRequest, GuildJsonContext.Default.GuildIssueChildRequest),
            GuildJsonContext.Default.GuildIssueChildRequest)!;

        async Task<GuildQuestDto> IssueSelectedAsync()
        {
            if (!native)
                return Assert.IsType<GuildLocalChildToolResult>(await context.InvokeBoundAsync(function, arguments, default)).Quest;

            var accepted = await coordinator.IssueAsync(execution.Driver.Caller!,
                new(nativeRequest.IdempotencyKey, GuildQuestIntent.Compute(parent.Value.Quest.Id, nativeRequest).Value),
                parent.Value.Quest.Id, nativeRequest, default);

            Assert.True(accepted.IsSuccess, accepted.Error.Message);

            return accepted.Value;
        }

        GuildQuestDto child = await IssueSelectedAsync();

        Assert.Equal([allowed], child.ResolvedGrant.Tools.ToArray());

        var current = await f.Store.GetAsync(owner, child.Id, default);

        var childExecution = await ToolExecutionAsync(f, owner, current.Value);

        var childInput = await f.Store.ReadLocalInputAsync(childExecution.Driver, default);

        Assert.NotNull(childInput.Value.Card);

        Assert.Equal(saved.Value.Content, childInput.Value.Card.Content);

        var materialized = await f.Store.MaterializeProviderInputAsync(childExecution, childInput.Value, default);

        Assert.True(materialized.IsSuccess, materialized.Error.Message);

        Assert.Contains(materialized.Value.Files, file => file.Text.Contains("PINNED-CARD-GUIDANCE", StringComparison.Ordinal)
            && file.Name.Contains(child.CardId.ToString("D"), StringComparison.Ordinal));

        var refreshed = await cards.UpsertCardAsync(new("selected-card", null, null, null,
            new(template, "CHANGED-CARD-GUIDANCE", [excluded.Name]), new string('B', 64)), default);

        Assert.True(refreshed.IsSuccess, refreshed.Error.Message);

        GuildQuestDto replay = await IssueSelectedAsync();

        Assert.Equal(child.Id, replay.Id);

        Assert.Equal(child.CardRevision, replay.CardRevision);

        Assert.Equal([allowed], replay.ResolvedGrant.Tools.ToArray());

        var pinned = await f.Store.MaterializeProviderInputAsync(childExecution, childInput.Value, default);

        Assert.True(pinned.IsSuccess, pinned.Error.Message);

        Assert.Contains(pinned.Value.Files, file => file.Text.Contains("PINNED-CARD-GUIDANCE", StringComparison.Ordinal));

        Assert.DoesNotContain(pinned.Value.Files, file => file.Text.Contains("CHANGED-CARD-GUIDANCE", StringComparison.Ordinal));

        if (protectAfterAdmission)
        {
            // Later sensitivity evidence must close disclosure even if the older clean header remains.
            var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

            Assert.True(opened.IsSuccess, opened.Error.Message);

            await using var connection = opened.Value;

            await using SqliteTransaction transaction = connection.Connection.BeginTransaction();

            DerivedArtifactWrite protection = new(SensitiveArtifactKind.GuildContent, saved.Value.Content.ContentId,
                null, null, null, (ulong)saved.Value.Content.Revision,
                new CovenantDigest(Convert.FromHexString(saved.Value.Content.Digest)), ContentSensitivity.CovenantDerived,
                GenerationProvenance.CreateExact([Guid.NewGuid()]));

            var labelled = await ArtifactSensitivityLedger.WriteWithinAsync(connection.Connection, transaction, protection, default);

            Assert.True(labelled.IsSuccess, labelled.Error.Message);

            await transaction.CommitAsync();
        }
        else
        {
            // Pruning the old clean cache revision must close later disclosure for the pinned child.
            await f.ExecuteAsync($"DELETE FROM guild_content WHERE ContentId='{saved.Value.Content.ContentId:D}'".ToUpperInvariant());
        }

        var unavailable = await f.Store.MaterializeProviderInputAsync(childExecution, childInput.Value, default);

        Assert.True(unavailable.IsFailure);

        if (!protectAfterAdmission)
            Assert.Equal(RetroDownfall.Arcanum.Core.Primitives.ErrorCodes.Guild.ContentErased, unavailable.Error.Code);
    }
    [Theory]
    [InlineData(false, "protected")]
    [InlineData(true, "protected")]
    [InlineData(false, "erased")]
    [InlineData(true, "erased")]
    [InlineData(false, "foreign-scope")]
    [InlineData(true, "foreign-scope")]
    public async Task Native_and_model_card_selection_refuse_protected_erased_and_foreign_sources(bool native, string boundary)
    {
        await using Fixture f = await Fixture.CreateAsync(productionOptions: true);

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Providers = [new() { Name = "provider", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://api.example.test/v1", Models = [new() { Name = "model" }] }];

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant creation = new(GuildToolSourceKind.InternalMcp, "arcanum", "create_agent_task");

        var parent = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with { Tools = [creation] } });

        Assert.True(parent.IsSuccess, parent.Error.Message);

        Guid? session = boundary == "foreign-scope" ? Guid.NewGuid() : null;

        if (session is { } foreign) await f.AddSessionAsync(foreign, null, true);

        GuildAgentCard template = new(Guid.Empty, Guid.Empty, 0, "Unavailable Card", "Unavailable Card", "1", [], ["text/plain"], ["text/plain"],
            new(false, false, false), new(session is null ? GuildAgentSourceKind.DeclaredGlobal : GuildAgentSourceKind.Derived, null, null, true, null));

        ArtifactSensitivityLabel? label = boundary == "protected" ? new(Guid.NewGuid(), SensitiveArtifactKind.GuildContent,
            Guid.NewGuid(), null, null, null, 1, new CovenantDigest(new byte[32]), ContentSensitivity.CovenantDerived,
            GenerationProvenance.CreateExact([Guid.NewGuid()]), null, null, null, DateTimeOffset.UtcNow) : null;

        GuildStore cards = new(f.Connections, f.Gate, TimeProvider.System);

        var saved = await cards.UpsertCardAsync(new("unavailable-selected-card", null, session, null,
            new(template, "Never disclose this Card guidance", [creation.Name]), new string('A', 64), Label: label), default);

        Assert.True(saved.IsSuccess, saved.Error.Message);

        if (boundary == "erased")
            await f.ExecuteAsync($"DELETE FROM guild_content WHERE ContentId='{saved.Value.Content.ContentId:D}'".ToUpperInvariant());

        var execution = await ToolExecutionAsync(f, owner, parent.Value);

        var currentParent = await f.Store.GetAsync(owner, parent.Value.Quest.Id, default);

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, new ChildQueue());

        if (native)
        {
            GuildIssueChildRequest request = new(Guid.NewGuid(), currentParent.Value.Quest.Revision, null, null,
                "Selected Card work", new(), CardId: saved.Value.Payload.Card.CardId);

            var refused = await coordinator.IssueAsync(execution.Driver.Caller!,
                new(request.IdempotencyKey, GuildQuestIntent.Compute(parent.Value.Quest.Id, request).Value),
                parent.Value.Quest.Id, request, default);

            Assert.True(refused.IsFailure);
        }
        else
        {
            var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

            using GuildLocalToolRegistry registry = new(execution, input.Value, coordinator);

            var context = f.Store.CreateToolContext(execution, registry, _ => true);

            var function = await context.AdvertiseAsync(new InertChildTool(creation.Name), creation, default);

            Assert.NotNull(function);

            using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), creation);

            Assert.IsType<string>(await context.InvokeBoundAsync(function,
                new() { ["goal"] = "Selected Card work", ["cardId"] = saved.Value.Payload.Card.CardId.ToString() }, default));
        }

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Selected_card_empty_tool_subset_differs_from_omitted_subset(bool specified)
    {
        await using Fixture f = await Fixture.CreateAsync(productionOptions: true);

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Providers = [new() { Name = "provider", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://api.example.test/v1", Models = [new() { Name = "model" }] }];

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant creation = new(GuildToolSourceKind.InternalMcp, "arcanum", "create_agent_task");

        var parent = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with { Tools = [creation] } });

        var execution = await ToolExecutionAsync(f, owner, parent.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        GuildAgentCard template = new(Guid.Empty, Guid.Empty, 0, "Card", "Card", "1", [], ["text/plain"], ["text/plain"],
            new(false, false, false), new(GuildAgentSourceKind.DeclaredGlobal, null, null, true, null));

        string oldJson = JsonSerializer.Serialize(new GuildStoredAgentCardPayload(template, "Card guidance", []),
            GuildStorageJsonContext.Default.GuildStoredAgentCardPayload);

        string explicitJson = oldJson[..^1] + ",\"requestedToolsSpecified\":" + (specified ? "true" : "false") + "}";

        var payload = JsonSerializer.Deserialize(explicitJson, GuildStorageJsonContext.Default.GuildStoredAgentCardPayload)!;

        GuildStore cards = new(f.Connections, f.Gate, TimeProvider.System);

        var saved = await cards.UpsertCardAsync(new("empty-card", null, null, null, payload, new string('A', 64)), default);

        Assert.True(saved.IsSuccess, saved.Error.Message);

        using GuildLocalToolRegistry registry = new(execution, input.Value, new ConclaveArchmage(null!, f.Settings, f.Store, new ChildQueue()));

        var context = f.Store.CreateToolContext(execution, registry, _ => true);

        var function = await context.AdvertiseAsync(new InertChildTool(creation.Name), creation, default);

        Assert.NotNull(function);

        using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), creation);

        var child = Assert.IsType<GuildLocalChildToolResult>(await context.InvokeBoundAsync(function,
            new() { ["goal"] = "Selected Card work", ["cardId"] = saved.Value.Payload.Card.CardId.ToString() }, default));

        Assert.Equal(specified ? [] : new[] { creation }, child.Quest.ResolvedGrant.Tools.ToArray());
    }

}
