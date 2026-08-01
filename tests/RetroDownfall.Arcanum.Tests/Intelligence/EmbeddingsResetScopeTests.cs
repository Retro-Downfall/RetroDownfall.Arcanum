using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class EmbeddingsResetScopeTests
{

    [Theory]
    [InlineData(null, EmbeddingsResetScope.All)]
    [InlineData("", EmbeddingsResetScope.All)]
    [InlineData("all", EmbeddingsResetScope.All)]
    [InlineData("ALL", EmbeddingsResetScope.All)]
    [InlineData("entry", EmbeddingsResetScope.Entry)]
    [InlineData("ENTRY", EmbeddingsResetScope.Entry)]
    [InlineData("workspacefile", EmbeddingsResetScope.WorkspaceFile)]
    [InlineData("workspace_file", EmbeddingsResetScope.WorkspaceFile)]
    [InlineData("WORKSPACEFILE", EmbeddingsResetScope.WorkspaceFile)]
    [InlineData("saga", EmbeddingsResetScope.Saga)]
    [InlineData("SAGA", EmbeddingsResetScope.Saga)]
    [InlineData("sessionattachment", EmbeddingsResetScope.SessionAttachment)]
    [InlineData("session_attachment", EmbeddingsResetScope.SessionAttachment)]
    public void ParseScope_ValidValues_ReturnsExpectedScope(string? value, EmbeddingsResetScope expected)
    {

        EmbeddingsResetScope? actual = EmbeddingsResetEndpoints.ParseScope(value);

        Assert.NotNull(actual);

        Assert.Equal(expected, actual.Value);

    }

    [Theory]
    [InlineData("entr")]
    [InlineData("workspace")]
    [InlineData("everything")]
    [InlineData("sagas")]
    [InlineData("unknown")]
    [InlineData(" ")]
    public void ParseScope_UnknownValues_ReturnsNull(string? value)
    {

        EmbeddingsResetScope? actual = EmbeddingsResetEndpoints.ParseScope(value);

        Assert.Null(actual);

    }

}
