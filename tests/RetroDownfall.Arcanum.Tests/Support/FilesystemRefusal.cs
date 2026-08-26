namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Asserts that the filesystem refused an operation, without pinning which exception says so.
/// </summary>
/// <remarks>
/// The two are not related by inheritance — <see cref="IOException"/> and
/// <see cref="UnauthorizedAccessException"/> both descend straight from <c>SystemException</c> — and
/// which one arrives is a property of the operating system rather than of the code under test. A
/// denied write raises <see cref="IOException"/> on macOS and <see cref="UnauthorizedAccessException"/>
/// on Windows, so three suites asserting the exact type passed everywhere they had ever run and failed
/// the first time the Windows lane executed them.
///
/// <para>Naming either type alone would be asserting the platform. Accepting anything would stop the
/// assertion meaning anything — a <c>NullReferenceException</c> is not a refusal, and a test that
/// accepted one would keep passing through a crash in the code it exists to check. So the pair is
/// named, and the failure message reports what actually arrived.</para>
///
/// <para>Production already treats them as one case wherever it handles them, which is what makes this
/// the test's problem rather than the product's: <c>BackupArchiveCodec</c> and
/// <c>GrimoireDatabaseBootstrapper</c> both catch <c>IOException or UnauthorizedAccessException</c>.
/// The one place that did not — a best-effort temp-file cleanup in <c>DataProtectionSecretStore</c> —
/// was a real defect, because an uncaught throw from a <c>finally</c> replaces the exception that
/// explains the failure with one about the tidying afterwards.</para>
/// </remarks>
internal static class FilesystemRefusal
{

    /// <summary>Runs the action, requiring it to fail as a filesystem refusal, and returns why.</summary>
    internal static async Task<Exception> ThrowsAsync(Func<Task> action)
    {

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(action);

        AssertIsRefusal(failure);

        return failure;

    }

    /// <summary>The synchronous form, for the paths that do not await.</summary>
    internal static Exception Throws(Action action)
    {

        Exception failure = Assert.ThrowsAny<Exception>(action);

        AssertIsRefusal(failure);

        return failure;

    }

    private static void AssertIsRefusal(Exception failure) =>
        Assert.True(
            failure is IOException or UnauthorizedAccessException,
            "Expected the filesystem to refuse the operation, which it reports as IOException on some "
            + $"platforms and UnauthorizedAccessException on others. Got {failure.GetType().Name}: "
            + failure.Message);

}
