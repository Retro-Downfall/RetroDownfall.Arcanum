using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public sealed class FoundryFloorViewModelTests
{

    [Fact]
    public void AppendLine_EnforcesLineCapAndKeepsTheNewestLines()
    {

        FoundryFloorViewModel viewModel = new(new NullLogService());

        for (int i = 0; i < FoundryFloorViewModel.MaxLines + 50; i++)
        {

            viewModel.AppendLine($"line-{i}");

        }

        Assert.Equal(FoundryFloorViewModel.MaxLines, viewModel.Lines.Count);

        Assert.Equal($"line-{FoundryFloorViewModel.MaxLines + 49}", viewModel.Lines[^1]);

        Assert.Equal("line-50", viewModel.Lines[0]);

        viewModel.Dispose();

    }

    [Fact]
    public void Clear_EmptiesTheBufferAndRestoresTheEmptyState()
    {

        FoundryFloorViewModel viewModel = new(new NullLogService());

        viewModel.AppendLine("first");

        viewModel.AppendLine("second");

        Assert.False(viewModel.HasNoLines);

        viewModel.ClearCommand.Execute(null);

        Assert.Empty(viewModel.Lines);

        Assert.True(viewModel.HasNoLines);

        Assert.Equal(string.Empty, viewModel.LatestLine);

        viewModel.Dispose();

    }

}
