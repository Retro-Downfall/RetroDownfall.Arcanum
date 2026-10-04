using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// A republisher over an availability snapshot nothing reads.
/// </summary>
/// <remarks>
/// For suites that compose a Covenant writer by hand and assert durable state. The writer still makes
/// its post-commit read on its own connection, as it does in a host, but the snapshot it publishes into
/// is not the one the suite's gate or fakes answer from.
/// </remarks>
internal static class DetachedAvailabilityRepublisher
{
    internal static CovenantAvailabilityRepublisher Create() =>
        new(new CovenantAvailability(), NullLogger<CovenantAvailabilityRepublisher>.Instance);
}
