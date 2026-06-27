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

}
