using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>Explicit isolated-test readiness; real host lifecycle tests delegate to the installed owner.</summary>
internal sealed class TestGuildLocalHostReadiness : IGuildLocalHostReadiness
{
    internal IGuildLocalHostReadiness? Inner { get; set; }

    internal bool Available { get; set; } = true;

    public Result RequireExecutionReady() => Inner?.RequireExecutionReady() ?? (Available
        ? Result.Success() : Result.Failure(GuildLocalHostReadiness.Unavailable));

    public Task<Result> EnsureReadyAsync(CancellationToken cancellationToken) =>
        Inner?.EnsureReadyAsync(cancellationToken) ?? Task.FromResult(RequireExecutionReady());
}
