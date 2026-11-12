using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Api.Primitives;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed class GuildErrorMappingTests
{
    [Theory]
    [InlineData(ErrorCodes.Guild.ToolNotGranted, StatusCodes.Status403Forbidden)]
    [InlineData(ErrorCodes.Guild.MemoryNotGranted, StatusCodes.Status403Forbidden)]
    [InlineData(ErrorCodes.Guild.PeerGrantRequired, StatusCodes.Status403Forbidden)]
    [InlineData(ErrorCodes.Guild.DelegationCycle, StatusCodes.Status409Conflict)]
    [InlineData(ErrorCodes.Guild.LegacyRunUnavailable, StatusCodes.Status409Conflict)]
    [InlineData(ErrorCodes.Guild.UnsupportedTransportState, StatusCodes.Status409Conflict)]
    [InlineData(ErrorCodes.Guild.MembershipConflict, StatusCodes.Status409Conflict)]
    [InlineData(ErrorCodes.Guild.CohortClosed, StatusCodes.Status409Conflict)]
    [InlineData(ErrorCodes.Guild.NoEligibleBidder, StatusCodes.Status409Conflict)]
    [InlineData(ErrorCodes.Guild.BidConflict, StatusCodes.Status409Conflict)]
    [InlineData(ErrorCodes.Guild.OfferClosed, StatusCodes.Status409Conflict)]
    [InlineData(ErrorCodes.Guild.DestinationChanged, StatusCodes.Status409Conflict)]
    [InlineData(ErrorCodes.Guild.LeaseReconciliationRequired, StatusCodes.Status409Conflict)]
    public void Guild_domain_errors_have_the_documented_http_status(string code, int expected) =>
        Assert.Equal(expected, ArcanumErrorMapper.ResolveStatusCode(code));
}
