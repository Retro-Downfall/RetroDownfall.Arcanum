using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class CodexPathPolicyTests : IDisposable
{

    private readonly string _root;

    private readonly List<string> _cleanup = [];

    public CodexPathPolicyTests()
    {

        _root = Path.Combine(Path.GetTempPath(), "arcanum-codex-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_root);

        _cleanup.Add(_root);

    }

    [Fact]
    public void ValidateContainedFile_PathOutsideRoot_Denies()
    {

        string outside = Path.Combine(Path.GetTempPath(), "arcanum-codex-outside-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(outside);

        _cleanup.Add(outside);

        string codexPath = Path.Combine(outside, "CODEX.md");

        File.WriteAllText(codexPath, "# Outside");

        Result<string> result = CodexPathPolicy.ValidateContainedFile(
            codexPath,
            _root,
            maxFileReadSizeBytes: 1024 * 1024);

        Assert.True(result.IsFailure);

        Assert.Equal("Prompt.CodexPathNotContained", result.Error.Code);

    }

    [Fact]
    public void ValidateContainedFile_PathInsideRoot_Allows()
    {

        string codexPath = Path.Combine(_root, "CODEX.md");

        File.WriteAllText(codexPath, "# Inside");

        Result<string> result = CodexPathPolicy.ValidateContainedFile(
            codexPath,
            _root,
            maxFileReadSizeBytes: 1024 * 1024);

        Assert.True(result.IsSuccess);

        Assert.Equal(Path.GetFullPath(codexPath), result.Value);

    }

    [Fact]
    public void ValidateContainedFile_ExceedsMaxSize_Denies()
    {

        string codexPath = Path.Combine(_root, "CODEX.md");

        File.WriteAllText(codexPath, new string('x', 64));

        Result<string> result = CodexPathPolicy.ValidateContainedFile(
            codexPath,
            _root,
            maxFileReadSizeBytes: 32);

        Assert.True(result.IsFailure);

        Assert.Equal("Prompt.CodexPathTooLarge", result.Error.Code);

    }

    public void Dispose()
    {

        foreach (string path in _cleanup)
        {

            try
            {

                if (Directory.Exists(path))
                {

                    Directory.Delete(path, recursive: true);

                }

            }
            catch
            {

                // Best-effort temp cleanup.

            }

        }

    }

}
