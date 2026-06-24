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

        Result<string> result = CodexPathPolicy.ValidateContainedFile(
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

        Result<string> result = CodexPathPolicy.ValidateContainedFile(
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

            Result<string> result = CodexPathPolicy.ValidateContainedFile(
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

        Result<string> result = CodexPathPolicy.ValidateContainedFile(
            codexPath,
            _workspace.Root,
            maxFileReadSizeBytes: 1024 * 1024);

        Assert.True(result.IsSuccess);

        Assert.Equal(Path.GetFullPath(codexPath), result.Value);

    }

    [Fact]
    public void ValidateContainedFile_MissingFile_ReturnsNotFound()
    {

        string missing = Path.Combine(_workspace.Root, "missing-CODEX.md");

        Result<string> result = CodexPathPolicy.ValidateContainedFile(
            missing,
            _workspace.Root,
            maxFileReadSizeBytes: 1024);

        Assert.True(result.IsFailure);

        Assert.Equal("Prompt.CodexPathNotFound", result.Error.Code);

    }

    [Fact]
    public void ValidateContainedFile_ExceedsMaxSize_Denies()
    {

        string codexPath = _workspace.WriteFile("CODEX.md", new string('x', 64));

        Result<string> result = CodexPathPolicy.ValidateContainedFile(
            codexPath,
            _workspace.Root,
            maxFileReadSizeBytes: 32);

        Assert.True(result.IsFailure);

        Assert.Equal("Prompt.CodexPathTooLarge", result.Error.Code);

    }

}
