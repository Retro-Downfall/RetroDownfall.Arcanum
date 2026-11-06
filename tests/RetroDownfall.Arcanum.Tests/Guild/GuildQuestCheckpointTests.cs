using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using System.Reflection;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Operations;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildQuestCheckpointTests
{
    [Fact]
    public void Version_one_checkpoint_contains_only_quest_identity_version_and_recovery_phase()
    {
        byte[] bytes = GuildQuestCheckpoint.Encode(Id);

        using JsonDocument document = JsonDocument.Parse(bytes);

        Assert.Equal(new[] { "phase", "questId", "version" }, document.RootElement.EnumerateObject().Select(property => property.Name).Order());

        Assert.Equal(Id, document.RootElement.GetProperty("questId").GetGuid());

        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());

        Assert.Equal("admitted", document.RootElement.GetProperty("phase").GetString());
    }

    [Fact]
    public void Quest_recovery_is_registered_only_for_atomic_checkpoint_version_one()
    {
        var descriptor = LongRunningOperationRecoveryRegistry.Find("guild-quest");

        Assert.NotNull(descriptor);

        Assert.Equal(LongRunningOperationRecoveryPolicy.ReconcileAndComplete, descriptor.Policy);

        Assert.Equal(LongRunningOperationStartupPriority.Readiness, descriptor.StartupPriority);

        Assert.Equal(1, descriptor.MinCheckpointVersion);

        Assert.Equal(1, descriptor.MaxCheckpointVersion);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 2)]
    [InlineData(2, 5)]
    public void Quest_recovery_maintenance_classification_fails_closed_for_unknown_versions(int version, int expected)
    {
        var operation = new LongRunningOperation(Id, "guild-quest", LongRunningOperationState.Running, LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
            null, null, null, null, null, null, null, DateTimeOffset.UnixEpoch, null, null, null, null, null,
            0, version, null, null, "Quest", null, 1);

        Assert.Equal((LongRunningRecoveryAdmissionKind)expected, LongRunningOperationRecoveryAdmission.Classify(operation, null).Kind);
    }

    [Fact]
    public void Generated_checkpoint_round_trips_the_original_quest_and_phase()
    {
        var decoded = Decode(1, GuildQuestCheckpoint.Encode(Id, "awaitingInput"));

        Assert.True(decoded.IsSuccess, decoded.Error.Code);

        Assert.Equal(Id, decoded.Value.QuestId);

        Assert.Equal(1, decoded.Value.Version);

        Assert.Equal("awaitingInput", decoded.Value.Phase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void A_ledger_checkpoint_version_outside_one_is_never_decoded(int version)
    {
        Assert.Equal("operation.checkpoint_version_unsupported", Decode(version, GuildQuestCheckpoint.Encode(Id)).Error.Code);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("truncated")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("version")]
    [InlineData("empty-id")]
    [InlineData("phase")]
    [InlineData("oversized")]
    public void Corrupt_checkpoints_do_not_supply_recoverable_work_identity(string corruption)
    {
        string valid = System.Text.Encoding.UTF8.GetString(GuildQuestCheckpoint.Encode(Id));

        byte[]? payload = corruption switch
        {
            "null" => null,
            "truncated" => "{"u8.ToArray(),
            "missing" => "{}"u8.ToArray(),
            "extra" => System.Text.Encoding.UTF8.GetBytes(valid[..^1] + ",\"goal\":\"must not be accepted\"}"),
            "duplicate" => System.Text.Encoding.UTF8.GetBytes(valid[..^1] + ",\"phase\":\"execution\"}"),
            "version" => System.Text.Encoding.UTF8.GetBytes(valid.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal)),
            "empty-id" => System.Text.Encoding.UTF8.GetBytes(valid.Replace(Id.ToString(), Guid.Empty.ToString(), StringComparison.Ordinal)),
            "phase" => System.Text.Encoding.UTF8.GetBytes(valid.Replace("admitted", "prompt content", StringComparison.Ordinal)),
            "oversized" => new byte[65_537],
            _ => throw new InvalidOperationException()
        };

        Assert.Equal("operation.checkpoint_corrupt", Decode(1, payload).Error.Code);
    }

    [Fact]
    public void Encoding_refuses_missing_identity_and_non_phase_content()
    {
        Assert.Throws<ArgumentException>(() => GuildQuestCheckpoint.Encode(Guid.Empty));

        Assert.Throws<ArgumentException>(() => GuildQuestCheckpoint.Encode(Id, "prompt content"));
    }

    private static Result<GuildQuestCheckpointPayload> Decode(int version, byte[]? bytes) =>
        GuildQuestCheckpoint.Decode(version, bytes);

    [Fact]
    public async Task Production_recovery_registers_one_scoped_handler_and_an_explicit_unavailable_observer()
    {
        ServiceCollection services = [];

        services.AddArcanumInfrastructure(new ConfigurationBuilder().Build());

        ServiceDescriptor descriptor = Assert.Single(services, item => item.ServiceType == typeof(ILongRunningOperationRecoveryHandler)
            && item.ImplementationType == typeof(GuildQuestRecoveryHandler));

        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);

        ServiceDescriptor observerDescriptor = Assert.Single(services, item => item.ServiceType == typeof(IGuildQuestRecoveryObserver));

        var observer = Assert.IsAssignableFrom<IGuildQuestRecoveryObserver>(Activator.CreateInstance(observerDescriptor.ImplementationType!));

        GuildQuestRecoveryTarget target = new(Id, Id, Id, Id, Id, Id, Id, 1, GuildDispatchEvidence.Confirmed,
            null, "existing-peer-task", null, false, "lease-owner", 1);

        Assert.False(observer.CanObserve(target));

        var refused = await observer.ObserveAsync(target, default);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Guild.Unavailable, refused.Error.Code);
    }

    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");
}
