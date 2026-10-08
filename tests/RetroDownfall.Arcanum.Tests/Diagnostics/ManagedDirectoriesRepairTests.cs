using RetroDownfall.Arcanum.Core.Cli;
using RetroDownfall.Arcanum.Infrastructure.Diagnostics;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Diagnostics;

/// <summary>
/// The managed-directories repair promises "created, owner-only". It may only say so when the
/// owner-only posture was verified, not merely attempted.
/// </summary>
[Collection("ProcessEnvironment")]
public sealed class ManagedDirectoriesRepairTests
{
    [Fact]
    public async Task Apply_reports_failure_when_the_posture_is_not_owner_only()
    {
        using ArcanumTestHomeScope home = new("arcanum-managed-directories");

        SecureFilePermissions.StrictOwnerOnlyVerificationForTests =
            static (_, isDirectory) => isDirectory ? false : null;

        try
        {
            DoctorRepairResult result = await new ManagedDirectoriesRepair()
                .ApplyAsync(CancellationToken.None);

            Assert.Equal(DoctorRepairState.Failed, result.State);

            Assert.NotEmpty(result.Steps);

            Assert.DoesNotContain(result.Steps, static step => step.After == "created, owner-only");

            Assert.NotNull(result.Failure);
        }
        finally
        {
            SecureFilePermissions.StrictOwnerOnlyVerificationForTests = null;
        }
    }

    [Fact]
    public async Task Apply_reports_owner_only_directories_it_verified()
    {
        using ArcanumTestHomeScope home = new("arcanum-managed-directories");

        DoctorRepairResult result = await new ManagedDirectoriesRepair()
            .ApplyAsync(CancellationToken.None);

        Assert.Equal(DoctorRepairState.Applied, result.State);

        Assert.All(result.Steps, static step => Assert.Equal("created, owner-only", step.After));

        Assert.Equal(
            DoctorRepairState.AlreadyConverged,
            (await new ManagedDirectoriesRepair().ApplyAsync(CancellationToken.None)).State);
    }
}
