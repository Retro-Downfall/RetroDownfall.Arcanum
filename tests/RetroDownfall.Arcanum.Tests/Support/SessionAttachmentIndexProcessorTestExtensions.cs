using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static class SessionAttachmentIndexProcessorTestExtensions
{

    /// <summary>
    /// Indexes one request under an admitted work lease taken from a gate that is not closing.
    /// </summary>
    /// <remarks>
    /// For the suites that are about what indexing <em>does</em> — extraction, chunking, provenance,
    /// retrieval scoping, version lifecycle — rather than about what a maintenance window does to it.
    /// Those cases need a lease only because the processor's effect groups hang off one, and a real
    /// gate with ordinary admission open is a truer stand-in than a fake: it admits every group, so
    /// the frontier is present and invisible, exactly as it is in a host nobody is resetting.
    ///
    /// <para>Deliberately not named <c>ProcessAsync</c>. An extension overload sharing that name
    /// would be selected silently by arity, and a call site that meant to pass a lease and did not
    /// would compile into this instead of failing.</para>
    /// </remarks>
    internal static async Task<SessionAttachmentIndexOutcome> ProcessUnderOpenAdmissionAsync(
        this SessionAttachmentIndexProcessor processor,
        SessionAttachmentIndexRequest request,
        CancellationToken cancellationToken)
    {

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        Assert.True(gate.TryAcquireWorkLease(
            GrimoireWorkKind.SessionAttachmentIndexing,
            out IGrimoireWorkLease? lease));

        await using IGrimoireWorkLease admitted = lease!;

        return await processor.ProcessAsync(request, admitted, cancellationToken);

    }

}
