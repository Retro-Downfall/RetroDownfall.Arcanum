using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class TurnBudgetAndMaterializerTests
{

    [Fact]
    public void TurnBudget_TryConsumeToolRoundAndToolCalls_EnforcesCeilings()
    {
        TurnBudget budget = new(
            maxToolRounds: 2,
            maxModelCalls: 10,
            maxToolCalls: 3,
            maxToolCallsPerRound: 2,
            maxSideEffectingToolCalls: 1);

        Assert.True(budget.TryConsumeToolRound());
        Assert.True(budget.TryConsumeToolCalls(2, sideEffectingCount: 1));
        Assert.False(budget.TryConsumeToolCalls(1, sideEffectingCount: 1));
        Assert.True(budget.TryConsumeToolRound());
        Assert.False(budget.TryConsumeToolRound());
        Assert.False(budget.TryConsumeToolCalls(-1));
        Assert.False(budget.TryConsumeToolCalls(3));
    }

    [Fact]
    public void ModelCallExecutor_DelegatesToBudget()
    {
        ModelCallExecutor executor = new();
        TurnBudget budget = new(maxModelCalls: 1);

        Assert.True(executor.TryBeginModelCall(budget));
        Assert.False(executor.TryBeginModelCall(budget));
    }

    [Fact]
    public void ToolResultMaterializer_TruncatesAndMarks()
    {
        ToolResultMaterializer materializer = new();
        string huge = new('x', 20_000);

        ToolResultMaterialization result = materializer.Materialize(
            "read_file",
            huge,
            new ToolResultMaterializerOptions(MaxTokens: 32));

        Assert.True(result.WasTruncated);
        Assert.Contains("[truncated", result.TextForModel, StringComparison.Ordinal);
        Assert.True(result.TextForModel.Length < huge.Length);
    }

}
