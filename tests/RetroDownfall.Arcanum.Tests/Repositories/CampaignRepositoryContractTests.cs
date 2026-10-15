using System.Reflection;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Tests.Repositories;

/// <summary>
/// The shape of <see cref="ICampaignRepository"/> that no behavioural test can see, because every implementer already answers the question
/// and a restored default would still compile and still pass.
/// </summary>
public sealed class CampaignRepositoryContractTests
{
    /// <summary>
    /// A default exact-match body would silently drop the Sanctum for every directory below a Campaign root (and for the root spelled another
    /// way) in any store, decorator or test double that forgot to answer containment itself. Without a body, the compiler makes each
    /// implementer decide.
    /// </summary>
    [Fact]
    public void Containment_lookup_has_no_default_body_so_every_store_answers_it()
    {
        MethodInfo method = Assert.IsAssignableFrom<MethodInfo>(
            typeof(ICampaignRepository).GetMethod(
                nameof(ICampaignRepository.GetByContainedPathAsync)));

        Assert.True(method.IsAbstract);

        Assert.Null(method.GetMethodBody());
    }
}
