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
    [InlineData(ErrorCodes.Validation.UnsupportedReasoningControl, StatusCodes.Status400BadRequest)]
    [InlineData(
        ErrorCodes.Validation.ReasoningEffortAndBudgetMutuallyExclusive,
        StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Hub.ToolLoop, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(ErrorCodes.Hub.Timeout, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(ErrorCodes.Hub.Model, StatusCodes.Status404NotFound)]
    [InlineData(ErrorCodes.Hub.Error, StatusCodes.Status500InternalServerError)]
    [InlineData(ErrorCodes.Campaign.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorCodes.Campaign.InvalidPath, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Campaign.PathNotAllowed, StatusCodes.Status403Forbidden)]
    [InlineData(ErrorCodes.Campaign.MaxReached, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Session.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorCodes.Session.Archived, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Session.TooManyEntries, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Session.EntryTooLarge, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Session.EmptyContent, StatusCodes.Status400BadRequest)]
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
    [InlineData(ErrorCodes.Session.ForkDepthExceeded, StatusCodes.Status409Conflict)]
    [InlineData(ErrorCodes.Session.EntryNotFound, StatusCodes.Status404NotFound)]
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
    [InlineData(ErrorCodes.Validation.InvalidProviderType, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Connection.Unreachable, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(ErrorCodes.Saga.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorCodes.Saga.NotEmpty, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Saga.SearchFailed, StatusCodes.Status500InternalServerError)]
    // FeatureDisabled means an operator turned a feature off in config, not that the caller lacks
    // permission, so it maps to 503 (retry later) rather than sharing the 403 used by genuine
    // access-control failures (PathNotAllowed, AccessDenied, etc.) above.
    [InlineData(ErrorCodes.Embeddings.FeatureDisabled, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(ErrorCodes.Sending.AgentUnreachable, StatusCodes.Status502BadGateway)]
    [InlineData(ErrorCodes.Sending.AgentCardInvalid, StatusCodes.Status502BadGateway)]
    [InlineData(ErrorCodes.Sending.TaskTimeout, StatusCodes.Status504GatewayTimeout)]
    [InlineData(ErrorCodes.Sending.TaskRejected, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Sending.Disabled, StatusCodes.Status403Forbidden)]
    [InlineData(ErrorCodes.Sending.AgentNotAllowed, StatusCodes.Status403Forbidden)]
    [InlineData(ErrorCodes.Sending.MaxTasksReached, StatusCodes.Status429TooManyRequests)]
    [InlineData(ErrorCodes.StructuredOutput.ValidationFailed, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.StructuredOutput.SchemaInvalid, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Budget.Exceeded, StatusCodes.Status429TooManyRequests)]
    [InlineData(ErrorCodes.WebBrowsing.SsrfBlocked, StatusCodes.Status403Forbidden)]
    [InlineData(ErrorCodes.WebBrowsing.InvalidUrl, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.WebBrowsing.TooLarge, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.WebBrowsing.Timeout, StatusCodes.Status504GatewayTimeout)]
    [InlineData(ErrorCodes.ClientTools.Disabled, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.ClientTools.TooMany, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.ClientTools.InvalidSchema, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Guardrails.PiiDetected, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Guardrails.Blocked, StatusCodes.Status400BadRequest)]
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
