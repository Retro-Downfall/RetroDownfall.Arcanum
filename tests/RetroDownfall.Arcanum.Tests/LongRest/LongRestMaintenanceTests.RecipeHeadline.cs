using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// Recipe headline. With Long Rest enabled but its recipe unresolved, a request ends with LongRest.Unavailable and the one content-free
// sentence the configuration validator, the doctor row and the health component give, so the turn that ends on it says why instead of
// the generic "could not be proved". A disabled feature and a stale stamp keep the generic text, and a resolvable recipe is untouched.
public sealed partial class LongRestMaintenanceTests
{
    // The chat model FastModel names is advertised by no provider, so the recipe fails closed.
    private static ArcanumSettings UnresolvedRecipe()
    {
        ArcanumSettings settings = LongRestConfigurationEpochTests.Settings();

        settings.FastModel = "not-configured";

        return settings;
    }

    private static Task<Result<LongRunningOperationDto>> RequestDoorAsync(MaintenanceFixture f, string door) => door switch
    {
        "clean" => f.Coordinator.RequestAsync(f.Scope, CancellationToken.None),
        "protected" => f.Coordinator.RequestAsync(f.Scope, f.Partition, CancellationToken.None),
        _ => f.Coordinator.RequestAsync(f.Scope, f.Partition, f.Authority, CancellationToken.None),
    };

    [Theory]
    [InlineData("clean")]
    [InlineData("protected")]
    [InlineData("protected-with-authority")]
    public async Task A_request_on_an_unresolved_recipe_ends_with_the_headline_every_other_reader_of_the_condition_gives(string door)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: door != "clean");

        ArcanumSettings unresolved = UnresolvedRecipe();

        f.Options.Set(unresolved);

        IReadOnlyList<string> problems = LongRestRecipeResolver.Problems(unresolved);

        Assert.NotEmpty(problems);

        long changes = (long)(await f.H.ScalarAsync("SELECT total_changes();"))!;

        Result<LongRunningOperationDto> result = await RequestDoorAsync(f, door);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.Unavailable, result.Error.Code);

        Assert.StartsWith(LongRestRecipeResolver.UnresolvedHeadline, result.Error.Message, StringComparison.Ordinal);

        Assert.Equal(LongRestRecipeResolver.Describe(problems), result.Error.Message);

        Assert.DoesNotContain("could not be proved", result.Error.Message, StringComparison.Ordinal);

        Assert.Equal(changes, (long)(await f.H.ScalarAsync("SELECT total_changes();"))!);
    }

    [Theory]
    [InlineData("clean")]
    [InlineData("protected")]
    public async Task A_disabled_Long_Rest_keeps_the_generic_refusal_and_never_claims_an_unresolved_recipe(string door)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: door != "clean");

        ArcanumSettings disabled = UnresolvedRecipe();

        disabled.Features.LongRest = false;

        f.Options.Set(disabled);

        Result<LongRunningOperationDto> result = await RequestDoorAsync(f, door);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.Unavailable, result.Error.Code);

        Assert.DoesNotContain(LongRestRecipeResolver.UnresolvedHeadline, result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("could not be proved", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_resolvable_recipe_is_not_refused_and_carries_no_headline()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        Assert.Empty(LongRestRecipeResolver.Problems(f.Options.CurrentValue));

        Result<LongRunningOperationDto> result = await RequestDoorAsync(f, "clean");

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
    }

    [Fact]
    public void The_epochs_recipe_failure_is_the_shared_sentence_and_its_success_is_the_resolver_recipe()
    {
        using LongRestConfigurationEpoch unresolved = new(new LongRestConfigurationEpochTests.NotifyingOptions(UnresolvedRecipe()), Guid.NewGuid());

        Result<LongRestRecipeIdentity> failure = ((ILongRestConfigurationProvider)unresolved).Capture().Recipe;

        Assert.True(failure.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.Unavailable, failure.Error.Code);

        Assert.Equal(LongRestRecipeResolver.Describe(LongRestRecipeResolver.Problems(UnresolvedRecipe())), failure.Error.Message);

        ArcanumSettings resolved = LongRestConfigurationEpochTests.Settings();

        using LongRestConfigurationEpoch healthy = new(new LongRestConfigurationEpochTests.NotifyingOptions(resolved), Guid.NewGuid());

        Assert.Equal(LongRestRecipeResolver.Resolve(resolved).Value, ((ILongRestConfigurationProvider)healthy).Capture().Recipe.Value);
    }
}
