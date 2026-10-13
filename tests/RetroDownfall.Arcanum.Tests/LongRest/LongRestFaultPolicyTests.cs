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
