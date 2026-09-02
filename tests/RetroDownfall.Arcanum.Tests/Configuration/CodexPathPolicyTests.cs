using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class CodexPathPolicyTests : IClassFixture<TempWorkspace>
{

    private readonly TempWorkspace _workspace;

    public CodexPathPolicyTests(TempWorkspace workspace)
    {

        _workspace = workspace;

    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateContainedFile_EmptyCodexPath_ReturnsRequired(string codexPath)
    {

        Result<CodexValidationResult> result = CodexPathPolicy.ValidateContainedFile(
            codexPath,
            _workspace.Root,
            maxFileReadSizeBytes: 1024);

        Assert.True(result.IsFailure);

        Assert.Equal("Prompt.CodexPathRequired", result.Error.Code);

    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateContainedFile_EmptyContainmentRoot_ReturnsNotContained(string containmentRoot)
    {

        string codexPath = _workspace.WriteFile("CODEX.md", "# Codex");

        Result<CodexValidationResult> result = CodexPathPolicy.ValidateContainedFile(
            codexPath,
            containmentRoot,
            maxFileReadSizeBytes: 1024);

        Assert.True(result.IsFailure);

        Assert.Equal("Prompt.CodexPathNotContained", result.Error.Code);

    }

    [Fact]
    public void ValidateContainedFile_PathOutsideRoot_Denies()
    {

        string outsideRoot = Path.Combine(Path.GetTempPath(), "arcanum-codex-outside-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(outsideRoot);

        try
        {

            string codexPath = Path.Combine(outsideRoot, "CODEX.md");

            File.WriteAllText(codexPath, "# Outside");

            Result<CodexValidationResult> result = CodexPathPolicy.ValidateContainedFile(
                codexPath,
                _workspace.Root,
                maxFileReadSizeBytes: 1024 * 1024);

            Assert.True(result.IsFailure);

            Assert.Equal("Prompt.CodexPathNotContained", result.Error.Code);

        }
        finally
        {

            Directory.Delete(outsideRoot, recursive: true);

        }

    }

    [Fact]
    public void ValidateContainedFile_PathInsideRoot_Allows()
    {

        string codexPath = _workspace.WriteFile("CODEX.md", "# Inside");

        Result<CodexValidationResult> result = CodexPathPolicy.ValidateContainedFile(
            codexPath,
            _workspace.Root,
            maxFileReadSizeBytes: 1024 * 1024);

        Assert.True(result.IsSuccess);

        Assert.Equal(Path.GetFullPath(codexPath), result.Value.Path);

        Assert.Equal(1024 * 1024, result.Value.MaxBytes);

    }

    [Fact]
    public void ValidateContainedFile_MissingFile_ReturnsNotFound()
    {

        string missing = Path.Combine(_workspace.Root, "missing-CODEX.md");

        Result<CodexValidationResult> result = CodexPathPolicy.ValidateContainedFile(
            missing,
            _workspace.Root,
            maxFileReadSizeBytes: 1024);

        Assert.True(result.IsFailure);

        Assert.Equal("Prompt.CodexPathNotFound", result.Error.Code);

    }

    [Fact]
    public void ValidateContainedFile_ReturnsMaxBytesInsteadOfRejectingBySize()
    {

        string codexPath = _workspace.WriteFile("CODEX.md", new string('x', 64));

        Result<CodexValidationResult> result = CodexPathPolicy.ValidateContainedFile(
            codexPath,
            _workspace.Root,
            maxFileReadSizeBytes: 32);

        Assert.True(result.IsSuccess);

        Assert.Equal(Path.GetFullPath(codexPath), result.Value.Path);

        Assert.Equal(32, result.Value.MaxBytes);

    }

    [Fact]
    public async Task ReadCappedAsync_UnderLimit_ReturnsContent()
    {

        string codexPath = _workspace.WriteFile("CODEX.md", "# Hello");

        Result<string> result = await CodexPathPolicy.ReadCappedAsync(codexPath, 1024);

        Assert.True(result.IsSuccess);

        Assert.Equal("# Hello", result.Value);

    }

    [Fact]
    public async Task ReadCappedAsync_ExceedsLimit_ReturnsTooLarge()
    {

        string codexPath = _workspace.WriteFile("CODEX.md", new string('x', 64));

        Result<string> result = await CodexPathPolicy.ReadCappedAsync(codexPath, 32);

        Assert.True(result.IsFailure);

        Assert.Equal("Prompt.CodexPathTooLarge", result.Error.Code);

    }

    [Fact]
    public async Task ReadCappedAsync_CancelledToken_PropagatesCancellation()
    {

        string codexPath = _workspace.WriteFile("cancel-CODEX.md", new string('x', 4096));

        using CancellationTokenSource cts = new();

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CodexPathPolicy.ReadCappedAsync(codexPath, 1024 * 1024, cts.Token));

    }

    [Fact]
    public async Task ReadCappedAsync_MissingFile_ReturnsNotFound()
    {

        string missing = Path.Combine(_workspace.Root, "absent-CODEX.md");

        Result<string> result = await CodexPathPolicy.ReadCappedAsync(missing, 1024);

        Assert.True(result.IsFailure);

        Assert.Equal("Prompt.CodexPathNotFound", result.Error.Code);

    }

    [SkippableFact]
    public async Task ReadCappedAsync_PermissionDenied_ReturnsReadFailedNotNotFound()
    {

        Skip.If(
            OperatingSystem.IsWindows() || System.Environment.IsPrivilegedProcess,
            "Permission denial cannot be exercised on Windows or as a privileged process.");

        // Dead once Skip.If above has run, but kept so the platform-compatibility analyzer still
        // recognizes the guard clause protecting the Unix-only calls below.
        if (OperatingSystem.IsWindows() || System.Environment.IsPrivilegedProcess)
        {

            return;

        }

        string codexPath = _workspace.WriteFile("denied-CODEX.md", "# Denied");

        File.SetUnixFileMode(codexPath, UnixFileMode.None);

        try
        {

            Result<string> result = await CodexPathPolicy.ReadCappedAsync(codexPath, 1024);

            Assert.True(result.IsFailure);

            Assert.Equal("Prompt.CodexReadFailed", result.Error.Code);

        }
        finally
        {

            File.SetUnixFileMode(codexPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        }

    }

    [Fact]
    public async Task ReadCappedAsync_MaxBytesAtUpperBound_ReadsWithoutOverflow()
    {

        string codexPath = _workspace.WriteFile("wide-bound-CODEX.md", "# Wide");

        Result<string> result = await CodexPathPolicy.ReadCappedAsync(codexPath, long.MaxValue);

        Assert.True(result.IsSuccess);

        Assert.Equal("# Wide", result.Value);

    }

}
