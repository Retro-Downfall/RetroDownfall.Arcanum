using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Guild;

namespace RetroDownfall.Arcanum.Core.Conclave;

/// <summary>
/// Target-bound attribution issued only at the authenticated native operator boundary.
/// It supplies no Guild execution authority and has no wire representation.
/// </summary>
[JsonConverter(typeof(GuildHostHandleJsonConverter<ApprenticeOperatorInterventionContext>))]
public sealed class ApprenticeOperatorInterventionContext
{
    internal ApprenticeOperatorInterventionContext(Guid apprenticeId)
    {
        if (apprenticeId == Guid.Empty)
            throw new ArgumentException("Operator intervention requires an exact Apprentice.", nameof(apprenticeId));

        ApprenticeId = apprenticeId;
    }

    internal Guid ApprenticeId { get; }
}
