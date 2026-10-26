using System.Reflection;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed class LongRestFaultPolicyTests
{
    public static TheoryData<string, LongRestFaultClass> Table => new()
    {
        { "long_rest.interrupted", LongRestFaultClass.ResumeLater },

        { "long_rest.maintenance_deferred", LongRestFaultClass.ResumeLater },

        { "long_rest.worker_unavailable", LongRestFaultClass.ResumeLater },

        { "long_rest.readmit_requested", LongRestFaultClass.ResumeLater },

        { "LongRest.Unavailable", LongRestFaultClass.ResumeLater },

        { "LongRest.OperationBusy", LongRestFaultClass.ResumeLater },

        { "LongRest.ProviderUnavailable", LongRestFaultClass.ResumeLater },

        { "LongRest.ProviderUnreachable", LongRestFaultClass.ResumeLater },

        { "LongRest.StaleInput", LongRestFaultClass.RestartGeneration },

        { "LongRest.StaleFence", LongRestFaultClass.RestartGeneration },

        { "LongRest.CurrentnessLost", LongRestFaultClass.RestartGeneration },

        { "LongRest.ConfigurationChanged", LongRestFaultClass.RestartGeneration },

        { "long_rest.stale_configuration", LongRestFaultClass.RestartGeneration },

        { "long_rest.not_started", LongRestFaultClass.RestartGeneration },

        { "LongRest.InvalidRequest", LongRestFaultClass.NeedsOperator },

        { "LongRest.NotFound", LongRestFaultClass.NeedsOperator },

        { "LongRest.MissingClaim", LongRestFaultClass.NeedsOperator },

        { "LongRest.IntegrityFailure", LongRestFaultClass.NeedsOperator },

        { "LongRest.InvalidAdmission", LongRestFaultClass.NeedsOperator },

        { "LongRest.AccountingCompletionRefused", LongRestFaultClass.NeedsOperator },

        { "LongRest.AccountingRefused", LongRestFaultClass.NeedsOperator },

        { "LongRest.InvalidOutput", LongRestFaultClass.NeedsOperator },

        { "LongRest.CancelledAfterPayment", LongRestFaultClass.NeedsOperator },

        { "LongRest.SweepParked", LongRestFaultClass.NeedsOperator },

        { "LongRest.ProviderRejected", LongRestFaultClass.NeedsOperator },

        { "LongRest.CredentialUnavailable", LongRestFaultClass.NeedsOperator },

        { "LongRest.TransportUnknown", LongRestFaultClass.NeedsOperator },

        { "LongRest.ModelContextTooSmall", LongRestFaultClass.NeedsOperator },

        { "long_rest.authorized_request_required", LongRestFaultClass.NeedsOperator },

        { "long_rest.checkpoint_invalid", LongRestFaultClass.NeedsOperator },

        { "long_rest.readmit_exhausted", LongRestFaultClass.NeedsOperator },

        { "long_rest.cancelled", LongRestFaultClass.NeedsOperator },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Every_known_code_maps_to_its_class(string code, LongRestFaultClass expected) =>
        Assert.Equal(expected, LongRestFaultPolicy.Classify(code));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("long_rest.something_new")]
    [InlineData("LongRest.SomethingNew")]
    [InlineData("LONG_REST.INTERRUPTED")]
    [InlineData("long_rest.interrupted ")]
    [InlineData("operation.recovery_failed")]
    public void An_unknown_or_malformed_code_needs_an_operator(string? code) =>
        Assert.Equal(LongRestFaultClass.NeedsOperator, LongRestFaultPolicy.Classify(code));

    [Fact]
    public void The_table_classifies_every_code_the_Long_Rest_surface_can_produce()
    {
        string[] produced =
        [
            .. Constants(typeof(ErrorCodes.LongRest)),
            .. Constants(typeof(LongRestParkCodes)),
        ];

        string[] unclassified = [.. produced.Where(code => !LongRestFaultPolicy.KnownCodes.ContainsKey(code))];

        Assert.Empty(unclassified);

        Assert.Equal(produced.Order(StringComparer.Ordinal).Distinct(), LongRestFaultPolicy.KnownCodes.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_readmittable_code_set_is_exactly_the_resume_later_rows()
    {
        string[] expected = [.. LongRestFaultPolicy.KnownCodes.Where(static pair => pair.Value == LongRestFaultClass.ResumeLater)
            .Select(static pair => pair.Key).Order(StringComparer.Ordinal)];

        Assert.Equal(expected, LongRestFaultPolicy.ResumeLaterCodes.Order(StringComparer.Ordinal));

        Assert.Contains(LongRestParkCodes.ReadmitRequested, LongRestFaultPolicy.ResumeLaterCodes);

        Assert.DoesNotContain(LongRestParkCodes.AuthorizedRequestRequired, LongRestFaultPolicy.ResumeLaterCodes);

        Assert.DoesNotContain(LongRestParkCodes.ReadmitExhausted, LongRestFaultPolicy.ResumeLaterCodes);
    }

    [Fact]
    public void The_free_park_set_is_exactly_the_maintenance_yield_and_the_operator_retry_and_each_is_resumable()
    {
        string[] expected = [LongRestParkCodes.MaintenanceDeferred, LongRestParkCodes.ReadmitRequested];

        Assert.Equal(expected.Order(StringComparer.Ordinal), LongRestFaultPolicy.FreeParkCodes.Order(StringComparer.Ordinal));

        Assert.All(LongRestFaultPolicy.FreeParkCodes, static code =>
        {
            Assert.True(LongRestFaultPolicy.IsFreePark(code));

            Assert.Equal(LongRestFaultClass.ResumeLater, LongRestFaultPolicy.Classify(code));

            Assert.Contains(code, LongRestFaultPolicy.ResumeLaterCodes);
        });

        // Every other code, resumable or not, pays for its readmission.
        Assert.All(LongRestFaultPolicy.KnownCodes.Keys.Except(expected), static code => Assert.False(LongRestFaultPolicy.IsFreePark(code), code));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("long_rest.maintenance_deferred ")]
    [InlineData("LONG_REST.READMIT_REQUESTED")]
    [InlineData("long_rest.something_new")]
    public void A_missing_or_malformed_code_is_never_a_free_park(string? code) => Assert.False(LongRestFaultPolicy.IsFreePark(code));

    [Theory]
    [InlineData(0, 30)]
    [InlineData(1, 60)]
    [InlineData(2, 120)]
    [InlineData(3, 240)]
    public void The_cooldown_doubles_with_each_readmission(int readmitCount, int seconds)
    {
        Assert.True(LongRestFaultPolicy.CanReadmit(readmitCount));

        Assert.Equal(TimeSpan.FromSeconds(seconds), LongRestFaultPolicy.Cooldown(readmitCount));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(int.MaxValue)]
    public void An_exhausted_or_malformed_budget_cannot_readmit(int readmitCount)
    {
        Assert.False(LongRestFaultPolicy.CanReadmit(readmitCount));

        Assert.Throws<ArgumentOutOfRangeException>(() => LongRestFaultPolicy.Cooldown(readmitCount));
    }

    [Theory]
    [InlineData("LongRest.StaleInput", true)]
    [InlineData("LongRest.Unavailable", true)]
    [InlineData("LongRest.SweepParked", true)]
    [InlineData("LongRest.OperationBusy", true)]
    [InlineData("LongRest.SomethingNew", true)]
    [InlineData("Covenant.StaleSnapshot", false)]
    [InlineData("Hub.Error", false)]
    [InlineData("long_rest.interrupted", false)]
    [InlineData("longrest.StaleInput", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_Long_Rest_error_family_is_exactly_the_LongRest_prefix(string? code, bool expected) =>
        Assert.Equal(expected, LongRestFaultPolicy.IsLongRestError(code));

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(-1, false)]
    public void A_work_unit_may_send_until_it_has_ended_two_attempts_with_unknown_usage(long unknownAttempts, bool mayStillSend) =>
        Assert.Equal(mayStillSend, LongRestFaultPolicy.MaySendAfterUnknownUsage(unknownAttempts));

    [Fact]
    public void The_unknown_usage_cap_is_two_attempts_per_work_unit() => Assert.Equal(2, LongRestFaultPolicy.UnknownUsageCap);

    [Fact]
    public void The_budget_is_four_readmissions()
    {
        Assert.Equal(4, LongRestFaultPolicy.MaxReadmissions);

        Assert.Equal(TimeSpan.FromSeconds(30), LongRestFaultPolicy.ReadmitBaseCooldown);
    }

    private static IEnumerable<string> Constants(Type type) => type
        .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
        .Where(static field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(static field => (string)field.GetRawConstantValue()!);
}
