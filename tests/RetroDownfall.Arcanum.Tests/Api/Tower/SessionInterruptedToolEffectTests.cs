using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

[Collection("ApiHost")]
public sealed class SessionInterruptedToolEffectTests
{
    [SkippableFact]
    public async Task Restarted_session_discloses_unresolved_tool_effects_without_a_checkpoint()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using RestartableArcanumProfileFixture profile = new();

        InMemoryOsCredentialStore credentials = new();

        Guid session;

        Guid installation;

        Guid[] expected;

        SessionTurnClaimLease priorOwner;

        SessionTurnClaimLease ownerWithNativeDiscard;

        await using (var first = MemoryErasureRouteDriver.Host(credentials, profile))
        {
            using HttpClient client = first.CreateAuthenticatedClient();

            using IServiceScope scope = first.Services.CreateScope();

            installation = Guid.Parse(first.Services.GetRequiredService<ICovenantAuthoritySnapshotProvider>().Current!.InstallationIdentity);

            var sessions = scope.ServiceProvider.GetRequiredService<ISessionTurnBeginStore>();

            var created = await sessions.CreateBoundSessionAsync(CanonicalCampaignContext.GlobalOnly, "interrupted first turn", default);

            Assert.True(created.IsSuccess, created.Error.Message);

            session = created.Value;

            priorOwner = await AcquireAsync(scope.ServiceProvider, installation, session);

            expected = await BeginAndDispatchAsync(scope.ServiceProvider, priorOwner, outstandingCount: 2, verifiedCount: 16);

            Assert.False((await ReadSessionAsync(client, session)).TryGetProperty("interruptedToolEffects", out _));
        }

        await using (var second = MemoryErasureRouteDriver.Host(credentials, profile))
        {
            using HttpClient client = second.CreateAuthenticatedClient();

            using IServiceScope scope = second.Services.CreateScope();

            Assert.Equal(expected.Order(), (await ReadInterruptedEffectsAsync(client, session, expectEmptyFirstPage: true)).Order());

            SessionRecoveryCheckpointStore withoutInstallationAuthority = new(
                scope.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>(),
                scope.ServiceProvider.GetRequiredService<TimeProvider>(),
                scope.ServiceProvider.GetRequiredService<CovenantProcessBootIdentity>(),
                scope.ServiceProvider.GetRequiredService<GrimoireDatabaseHostedService>());

            var unproven = await withoutInstallationAuthority.ReadStatusAsync(session, null, default);

            Assert.True(unproven.IsSuccess, unproven.Error.Message);

            Assert.Empty(unproven.Value.InterruptedEffects.Outstanding);

            var unchanged = await scope.ServiceProvider.GetRequiredService<ISessionTurnClaimLookup>()
                .FindAsync(installation, priorOwner.Claim.ClientTurnId, default);

            Assert.Equal(SessionTurnClaimState.Begun, unchanged.Value!.State);

            ownerWithNativeDiscard = await AcquireAsync(scope.ServiceProvider, installation, session);

            expected = [.. expected, .. await BeginAndDispatchAsync(scope.ServiceProvider, ownerWithNativeDiscard,
                outstandingCount: 17, verifiedCount: 1)];

            var discard = await scope.ServiceProvider.GetRequiredService<IGrimoireTurnCommitter>().CommitTurnAsync(new(
                ownerWithNativeDiscard.FutureAssistantEntryId, session, AssistantFinalizationOutcome.Discarded, string.Empty,
                ownerWithNativeDiscard.Claim.RequestDigest, ContentSensitivity.None, GenerationProvenance.CreateExact([])), default);

            Assert.True(discard.IsSuccess, discard.Error.Message);
        }

        await using var restarted = MemoryErasureRouteDriver.Host(credentials, profile);

        using HttpClient reader = restarted.CreateAuthenticatedClient();

        using IServiceScope resumed = restarted.Services.CreateScope();

        Assert.Equal(expected.Order(), (await ReadInterruptedEffectsAsync(reader, session)).Order());

        var continued = await AcquireAsync(resumed.ServiceProvider, installation, session);

        Assert.Equal(SessionTurnClaimDisposition.Created, continued.Disposition);

        var discarded = await resumed.ServiceProvider.GetRequiredService<ISessionTurnClaimLookup>()
            .FindAsync(installation, ownerWithNativeDiscard.Claim.ClientTurnId, default);

        Assert.Equal(SessionTurnClaimState.Discarded, discarded.Value!.State);

        var checkpoints = resumed.ServiceProvider.GetRequiredService<ISessionRecoveryCheckpointStore>();

        Assert.Null((await checkpoints.FindAsync(session, null, default)).Value);

        using HttpClient unauthenticated = restarted.CreateClient();

        using HttpResponseMessage denied = await unauthenticated.GetAsync($"/api/sessions/{session:D}");

        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        var other = await resumed.ServiceProvider.GetRequiredService<ISessionTurnBeginStore>()
            .CreateBoundSessionAsync(CanonicalCampaignContext.GlobalOnly, "another session", default);

        Assert.True(other.IsSuccess, other.Error.Message);

        await resumed.ServiceProvider.GetRequiredService<IGrimoireRepository>()
            .AppendToolInteractionAsync(other.Value, "read_file", "{}", "legacy result without ledger", "model");

        Assert.False((await ReadSessionAsync(reader, other.Value)).TryGetProperty("interruptedToolEffects", out _));

        using HttpResponseMessage foreignCursor = await reader.GetAsync(
            $"/api/sessions/{other.Value:D}?interruptedToolEffectAfter={expected[0]:D}");

        Assert.Equal(HttpStatusCode.BadRequest, foreignCursor.StatusCode);

        Guid[] observed = await ReadInterruptedEffectsAsync(reader, session);

        Assert.Equal(expected.Order(), observed.Order());

        var tools = resumed.ServiceProvider.GetRequiredService<IToolInvocationStore>();

        foreach (Guid receipt in observed)
        {
            var retained = await tools.ReadAsync(session, receipt, default);

            Assert.Equal(ToolInvocationState.Dispatched, retained.Value!.State);
        }

        var begun = await resumed.ServiceProvider.GetRequiredService<ISessionTurnClaimBeginStore>()
            .BeginClaimedAssistantReplyAsync(continued, CanonicalCampaignContext.GlobalOnly, "new independent turn", "model", default);

        Assert.True(begun.IsSuccess, begun.Error.Message);
    }

    private static async Task<Guid[]> ReadInterruptedEffectsAsync(HttpClient client, Guid session, bool expectEmptyFirstPage = false)
    {
        List<Guid> observed = [];

        Guid? cursor = null;

        for (int page = 0; page < 4; page++)
        {
            JsonElement data = await ReadSessionAsync(client, session, cursor);

            Assert.False(data.TryGetProperty("recoveryCheckpoint", out _));

            Assert.True(data.TryGetProperty("interruptedToolEffects", out JsonElement effects),
                "A durably dispatched invocation without a saved checkpoint needs visible reconciliation.");

            Assert.InRange(effects.GetArrayLength(), 0, 16);

            foreach (JsonElement effect in effects.EnumerateArray())
            {
                Assert.Equal("toolExecution", effect.GetProperty("phase").GetString());

                Assert.Equal("unknown", effect.GetProperty("effect").GetString());

                Assert.Equal("reconcileTool", effect.GetProperty("nextAction").GetString());

                Assert.False(effect.TryGetProperty("recoveryCheckpoint", out _));

                Assert.False(effect.TryGetProperty("toolName", out _));

                observed.Add(effect.GetProperty("invocationId").GetGuid());
            }

            cursor = data.TryGetProperty("nextInterruptedToolEffectAfter", out JsonElement next) ? next.GetGuid() : null;

            if (expectEmptyFirstPage && page == 0)
            {
                Assert.Equal(0, effects.GetArrayLength());

                Assert.NotNull(cursor);
            }

            if (cursor is null) break;
        }

        Assert.Null(cursor);

        return [.. observed];
    }

    private static async Task<SessionTurnClaimLease> AcquireAsync(IServiceProvider services, Guid installation, Guid session)
    {
        var begin = services.GetRequiredService<ISessionTurnClaimBeginStore>();

        var input = await begin.ReadClaimInputAsync(session, default);

        Assert.True(input.IsSuccess, input.Error.Message);

        var acquired = await services.GetRequiredService<ISessionTurnClaimCoordinator>().AcquireAsync(new(
            installation, 0, Guid.NewGuid(), session, SessionTurnSurface.Intelligence, new(new byte[32]), new(new byte[32]),
            input.Value.HistoryWatermarkUtc, input.Value.HistoryRevision, input.Value.SensitivityRevision), default);

        Assert.True(acquired.IsSuccess, $"{acquired.Error.Code}: {acquired.Error.Message}");

        return acquired.Value;
    }

    private static async Task<Guid[]> BeginAndDispatchAsync(IServiceProvider services, SessionTurnClaimLease owner, int outstandingCount, int verifiedCount)
    {
        var begin = await services.GetRequiredService<ISessionTurnClaimBeginStore>()
            .BeginClaimedAssistantReplyAsync(owner, CanonicalCampaignContext.GlobalOnly, "interrupted", "model", default);

        Assert.True(begin.IsSuccess, begin.Error.Message);

        var tools = services.GetRequiredService<IToolInvocationStore>();

        List<Guid> unresolved = [];

        int dispatchedCount = outstandingCount + verifiedCount;

        for (int round = 0; round <= dispatchedCount; round++)
        {
            bool verified = round < verifiedCount;

            string toolName = verified ? "read_file" : "apply_patch";

            string callId = $"internal-call-{round}";

            const string arguments = """{"private":"tool-argument"}""";

            var key = ToolInvocationKey.Capture(owner.Claim.SessionId, owner.FutureAssistantEntryId,
                new(owner.FutureAssistantEntryId.ToString("D"), callId, round, 0, toolName), arguments);

            Assert.True((await tools.PrepareAsync(key, owner, default)).IsSuccess);

            if (round == dispatchedCount) continue;

            var dispatched = await tools.AdmitAsync(key, owner, default);

            Assert.True(dispatched.IsSuccess && dispatched.Value.MayDispatch);

            if (verified)
            {
                // This receipt is verified by the tool store even though the dispatch state
                // never received its acknowledgement.
                var persisted = await services.GetRequiredService<IGrimoireRepository>()
                    .AppendMandatoryToolInteractionAsync(new(key.SessionId, key.Receipt, callId, toolName,
                        arguments, "confirmed result", "model", dispatched.Value.Evidence.CreatedAt));

                Assert.Equal(MandatoryToolInteractionAppendOutcome.NewlyCommitted, persisted.Outcome);

                Assert.Equal(ToolInvocationState.Completed, (await tools.ReadAsync(key.SessionId, key.Receipt.Id, default)).Value!.State);
            }
            else
            {
                unresolved.Add(key.Receipt.Id);
            }
        }

        return [.. unresolved];
    }

    private static async Task<JsonElement> ReadSessionAsync(HttpClient client, Guid session, Guid? cursor = null)
    {
        string route = $"/api/sessions/{session:D}";

        if (cursor is { } after) route += $"?interruptedToolEffectAfter={after:D}";

        using HttpResponseMessage response = await client.GetAsync(route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("tool-argument", body, StringComparison.Ordinal);

        using JsonDocument document = JsonDocument.Parse(body);

        return document.RootElement.GetProperty("data").Clone();
    }
}
