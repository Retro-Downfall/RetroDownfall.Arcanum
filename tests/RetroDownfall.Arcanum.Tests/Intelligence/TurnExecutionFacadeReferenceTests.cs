using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// A member of the turn-execution facade that no production code calls is a second implementation
/// waiting to drift from the one that is: the OpenAI SSE entry point shipped for months with only its
/// tests as callers while <c>/v1</c> streamed through a separate, hand-written mapper.
/// </summary>
public sealed class TurnExecutionFacadeReferenceTests
{
    [Fact]
    public void Every_facade_member_has_a_non_test_reference_in_src()
    {
        // A file that takes the facade, other than its declaration and its one implementation. The
        // name alone is not enough: IModelCallExecutor declares an ExecuteBufferedAsync of its own.
        ProductionSource[] consumers =
        [
            .. ProductionSourceInventory.Sources().Where(static source =>
                !source.Is("ITurnExecutionFacade.cs")
                && !source.Is("TurnExecutionCoordinator.cs")
                && (source.Names("ITurnExecutionFacade") || source.Names("TurnExecutionCoordinator"))),
        ];

        Assert.NotEmpty(consumers);

        string[] unreferenced =
        [
            .. typeof(ITurnExecutionFacade)
                .GetMethods()
                .Select(static method => method.Name)
                .Distinct(StringComparer.Ordinal)
                .Where(name => !consumers.Any(consumer => consumer.Names("." + name + "(")))
                .Order(StringComparer.Ordinal),
        ];

        Assert.True(
            unreferenced.Length == 0,
            "ITurnExecutionFacade members with no production caller: "
            + string.Join(", ", unreferenced));
    }
}
