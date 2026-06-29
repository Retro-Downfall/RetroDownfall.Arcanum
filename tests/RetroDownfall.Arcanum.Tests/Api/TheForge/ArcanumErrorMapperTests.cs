using Microsoft.AspNetCore.Http;

using RetroDownfall.Arcanum.Api.TheForge;

using RetroDownfall.Arcanum.Core.Primitives;


namespace RetroDownfall.Arcanum.Tests.Api.TheForge;

public sealed class ArcanumErrorMapperTests
{

    [Theory]
    [InlineData(ErrorCodes.Validation.InvalidPrompt, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Validation.AttachedFiles, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Validation.InvalidBody, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Hub.ToolLoop, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(ErrorCodes.Hub.Timeout, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(ErrorCodes.Hub.Model, StatusCodes.Status404NotFound)]
    [InlineData(ErrorCodes.Hub.Error, StatusCodes.Status500InternalServerError)]
    [InlineData(ErrorCodes.Ollama.Pull, StatusCodes.Status404NotFound)]
    [InlineData(ErrorCodes.Ollama.ListModels, StatusCodes.Status404NotFound)]
    [InlineData(ErrorCodes.Ollama.Error, StatusCodes.Status500InternalServerError)]
    [InlineData(ErrorCodes.Campaign.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorCodes.Campaign.InvalidPath, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Campaign.PathNotAllowed, StatusCodes.Status403Forbidden)]
    [InlineData(ErrorCodes.Campaign.MaxReached, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Session.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorCodes.Grimoire.LoreNotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorCodes.Apprentice.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorCodes.Apprentice.Disabled, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Apprentice.AlreadyRunning, StatusCodes.Status409Conflict)]
    [InlineData(ErrorCodes.Apprentice.Running, StatusCodes.Status409Conflict)]
    [InlineData(ErrorCodes.Apprentice.NotPaused, StatusCodes.Status409Conflict)]
    [InlineData(ErrorCodes.Apprentice.CannotReweave, StatusCodes.Status409Conflict)]
    [InlineData(ErrorCodes.Apprentice.InvalidGuidance, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Apprentice.NotEscalated, StatusCodes.Status409Conflict)]
    [InlineData(ErrorCodes.Apprentice.PendingQueueFull, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Apprentice.MaxReached, StatusCodes.Status409Conflict)]
    [InlineData(ErrorCodes.Apprentice.InvalidPlan, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Apprentice.InvalidGoal, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Apprentice.InvalidWorkspace, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Apprentice.ConclaveDisabled, StatusCodes.Status409Conflict)]
    [InlineData(ErrorCodes.Apprentice.ConclaveDepthExceeded, StatusCodes.Status409Conflict)]
    [InlineData(ErrorCodes.Apprentice.ConclaveBreadthExceeded, StatusCodes.Status409Conflict)]
    [InlineData(ErrorCodes.Workspace.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorCodes.Workspace.NameEmpty, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Workspace.PathNotAllowed, StatusCodes.Status403Forbidden)]
    [InlineData(ErrorCodes.Spell.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorCodes.Spell.PathNotAllowed, StatusCodes.Status403Forbidden)]
    [InlineData(ErrorCodes.Spell.NoWorkspace, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Spell.InvalidWorkspace, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Prompt.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorCodes.Prompt.CodexPathNotContained, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Intelligence.HumanPromptNotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorCodes.Mcp.AmbiguousServer, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Mcp.MissingWorkspace, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Mcp.ServerNotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorCodes.Llama.ModelNotCached, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Daemon.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorCodes.CommLink.Suppressed, StatusCodes.Status502BadGateway)]
    [InlineData(ErrorCodes.Api.TooManyConnections, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(ErrorCodes.RateLimit.TooManyRequests, StatusCodes.Status429TooManyRequests)]
    [InlineData(ErrorCodes.Connection.Timeout, StatusCodes.Status504GatewayTimeout)]
    [InlineData(ErrorCodes.Security.MissingApiKey, StatusCodes.Status401Unauthorized)]
    [InlineData(ErrorCodes.Security.BlockedOutboundUrl, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.ProvingGrounds.InvalidTrial, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.ProvingGrounds.TooManyInquisitors, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.ProvingGrounds.WorkspaceNotAllowed, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.ProvingGrounds.SpellNotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorCodes.ProvingGrounds.PromptNotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorCodes.ProvingGrounds.InferenceFailed, StatusCodes.Status500InternalServerError)]
    [InlineData("Unknown.Code", StatusCodes.Status500InternalServerError)]
    public void ResolveStatusCode_MapsExpectedValue(string code, int expected)
    {

        int actual = ArcanumErrorMapper.ResolveStatusCode(code);

        Assert.Equal(expected, actual);

    }

    [Fact]
    public void ResolveStatusCodeDefaultBadRequest_UnknownCode_Returns400()
    {

        int actual = ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest("Unknown.Code");

        Assert.Equal(StatusCodes.Status400BadRequest, actual);

    }

    [Fact]
    public void ResolveStatusCodeDefaultBadRequest_InferenceFailed_Returns500()
    {

        int actual = ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(ErrorCodes.ProvingGrounds.InferenceFailed);

        Assert.Equal(StatusCodes.Status500InternalServerError, actual);

    }

}
