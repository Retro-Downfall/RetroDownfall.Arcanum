using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Answers for an installation that is present with no factory reset in progress, without asking
/// the machine the test happens to be running on.
/// </summary>
/// <remarks>
/// The production probe answers from <c>~/.config/arcanum</c> and from the <c>arcanum</c> /
/// <c>master-api-key</c> entry in the OS credential store. Neither belongs to a CLI test, and
/// neither moves with a redirected test home: a developer machine that has run <c>arcanum setup</c>
/// answers "installed" for every test in the process, while a machine that never has — a fresh
/// contributor checkout, or Windows CI — meets <c>run</c>'s setup gate instead. Tests that are not
/// about the setup gate need the answer to be theirs rather than the machine's.
/// </remarks>
internal sealed class InstalledStartupProbe : IInstallationStartupProbe
{

    public Task<Result<ActiveInstallationReset?>> ReadActiveResetAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<ActiveInstallationReset?>.Success(null));

    public Result<bool> IsFreshInstallation() =>
        Result<bool>.Success(false);

}
