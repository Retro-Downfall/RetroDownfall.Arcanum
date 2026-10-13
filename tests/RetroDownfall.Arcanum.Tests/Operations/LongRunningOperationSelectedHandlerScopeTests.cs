using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed class LongRunningOperationSelectedHandlerScopeTests
{
    [Fact]
    public async Task Each_operation_constructs_only_its_selected_handler_in_a_fresh_owned_scope()
    {
        await using SelectedRecoveryScopeFixture fixture = new();

        for (int index = 0; index < 8; index++)
        {
            _ = fixture.Store.Seed(
                SelectedRecoveryScopeFixture.SelectedKind,
                LongRunningOperationRecoveryPolicy.RestartIdempotently);
        }

        AssertOuterValidationCompleted(fixture);

        // Both handlers were successfully constructed and accepted above. Only subsequent scope
        // construction of the unrelated handler throws, so the baseline failure identifies fan-out.
        fixture.ForbidUnrelatedConstruction = true;

        LongRunningOperationReconciliationSummary? summary = null;

        Exception? failure = await Record.ExceptionAsync(async () =>
        {
            summary = await fixture.Reconciler.ReconcileAsync(
                fixture.Time.GetUtcNow(), "selected-kind-test", maxOperations: 100, maxConcurrency: 4);
        });

        if (failure is not null)
        {
            Assert.Contains("UNRELATED_RECOVERY_HANDLER_CONSTRUCTED_AFTER_OUTER_VALIDATION", failure.ToString());
        }

        Assert.True(
            failure is null,
            $"Recovery constructed outside the selected kind after outer validation: {failure}");

        Assert.NotNull(summary);

        Assert.Equal(8, summary.Claimed);

        Assert.Equal(8, summary.Completed);

        SelectedRecoveryScopeFixture.ScopeState[] scopes = fixture.OperationScopes;

        Assert.Equal(8, scopes.Length);

        Assert.Equal(8, scopes.Select(static scope => scope.Store).Distinct().Count());

        Assert.Equal(8, scopes.Select(static scope => scope.Acquisition).Distinct().Count());

        Assert.Equal(8, scopes.SelectMany(static scope => scope.Handlers).Distinct().Count());

        Assert.Equal(8, scopes.SelectMany(static scope => scope.Acquisition.OperationIds).Distinct().Count());

        Assert.All(scopes, scope =>
        {
            Assert.Equal(1, scope.AsyncDisposals);

            Assert.Equal(1, scope.StoreResolutions);

            Assert.Equal(1, scope.AcquisitionResolutions);

            Assert.Equal(0, scope.UnkeyedConstructions);

            Assert.Equal(0, scope.UnrelatedConstructionAttempts);

            Assert.Equal(SelectedRecoveryScopeFixture.SelectedKind, Assert.Single(scope.RequestedKeys));

            SelectedRecoveryScopeFixture.Handler handler = Assert.Single(scope.Handlers);

            Assert.Equal(SelectedRecoveryScopeFixture.SelectedKind, handler.Kind);

            Assert.Equal(Assert.Single(scope.Acquisition.OperationIds), Assert.Single(handler.OperationIds));

            Assert.Equal(1, handler.AsyncDisposals);
        });

        Assert.All(fixture.Store.Operations, operation =>
            Assert.Equal(LongRunningOperationState.Completed, operation.State));

        Assert.All(fixture.Outer.Handlers, handler => Assert.Empty(handler.OperationIds));

        Assert.Equal(0, fixture.Outer.AsyncDisposals);
    }

    [Theory]
    [InlineData("unknown-key")]
    [InlineData("duplicate-key")]
    [InlineData("wrong-key")]
    public async Task Scoped_key_registration_cannot_fall_back_to_the_outer_handler(string shape)
    {
        await using SelectedRecoveryScopeFixture fixture = new(shape);

        LongRunningOperation operation = fixture.Store.Seed(
            SelectedRecoveryScopeFixture.SelectedKind,
            LongRunningOperationRecoveryPolicy.RestartIdempotently);

        AssertOuterValidationCompleted(fixture);

        if (shape == "unknown-key")
        {
            LongRunningOperationReconciliationSummary summary = await fixture.Reconciler.ReconcileNowAsync("missing-key-test");

            LongRunningOperation persisted = Assert.Single(fixture.Store.Operations);

            Assert.Equal(LongRunningOperationState.ReconciliationRequired, persisted.State);

            Assert.Equal(LongRunningOperationErrorCodes.RecoveryHandlerMissing, persisted.TerminalErrorCode);

            Assert.Equal(1, summary.RequiresAttention);

            Assert.Equal(1, summary.Claimed);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Reconciler.ReconcileNowAsync("invalid-key-test"));

            Assert.Empty(fixture.Store.LeaseAcquisitions);

            Assert.Equal(operation.Revision, Assert.Single(fixture.Store.Operations).Revision);
        }

        SelectedRecoveryScopeFixture.ScopeState scope = Assert.Single(fixture.OperationScopes);

        Assert.Equal(1, scope.AsyncDisposals);

        Assert.Equal(0, scope.UnkeyedConstructions);

        Assert.All(scope.Handlers, handler =>
        {
            Assert.Empty(handler.OperationIds);

            Assert.Equal(1, handler.AsyncDisposals);
        });

        Assert.All(fixture.Outer.Handlers, handler => Assert.Empty(handler.OperationIds));

        if (shape == "unknown-key")
        {
            Assert.Empty(scope.Handlers);

            Assert.Equal(operation.Id, Assert.Single(scope.Acquisition.OperationIds));
        }
        else
        {
            Assert.Empty(scope.Acquisition.OperationIds);

            Assert.Equal(shape == "duplicate-key" ? 2 : 1, scope.Handlers.Count);

            Assert.All(scope.RequestedKeys, key => Assert.Equal(SelectedRecoveryScopeFixture.SelectedKind, key));
        }
    }

    [Fact]
    public async Task Native_keyed_provider_returns_all_duplicates_and_never_an_unkeyed_fallback()
    {
        await using SelectedRecoveryScopeFixture fixture = new("duplicate-key");

        AssertOuterValidationCompleted(fixture);

        await using AsyncServiceScope scope = fixture.Provider.CreateAsyncScope();

        Assert.IsAssignableFrom<IKeyedServiceProvider>(scope.ServiceProvider);

        ILongRunningOperationRecoveryHandler[] handlers =
            [.. scope.ServiceProvider.GetKeyedServices<ILongRunningOperationRecoveryHandler>(SelectedRecoveryScopeFixture.SelectedKind)];

        Assert.Equal(2, handlers.Length);

        Assert.NotSame(handlers[0], handlers[1]);

        Assert.All(handlers, handler => Assert.Equal(SelectedRecoveryScopeFixture.SelectedKind, handler.Kind));

        // A singular lookup is last-wins in the actual container, not duplicate validation.
        Assert.Same(handlers[1], scope.ServiceProvider.GetKeyedService<ILongRunningOperationRecoveryHandler>(SelectedRecoveryScopeFixture.SelectedKind));

        Assert.Empty(scope.ServiceProvider.GetKeyedServices<ILongRunningOperationRecoveryHandler>("unregistered-test-kind"));

        Assert.Null(scope.ServiceProvider.GetKeyedService<ILongRunningOperationRecoveryHandler>("unregistered-test-kind"));

        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredKeyedService<ILongRunningOperationRecoveryHandler>("unregistered-test-kind"));
    }

    private static void AssertOuterValidationCompleted(SelectedRecoveryScopeFixture fixture)
    {
        Assert.Equal(2, fixture.Outer.Handlers.Count);

        Assert.Single(fixture.Outer.Handlers, handler => handler.Kind == SelectedRecoveryScopeFixture.SelectedKind);

        Assert.Single(fixture.Outer.Handlers, handler => handler.Kind == SelectedRecoveryScopeFixture.UnrelatedKind);

        Assert.DoesNotContain(SelectedRecoveryScopeFixture.SelectedKind, fixture.Reconciler.MissingHandlerKinds);

        Assert.DoesNotContain(SelectedRecoveryScopeFixture.UnrelatedKind, fixture.Reconciler.MissingHandlerKinds);

        Assert.Empty(fixture.OperationScopes);

        Assert.False(fixture.ForbidUnrelatedConstruction);
    }
}
