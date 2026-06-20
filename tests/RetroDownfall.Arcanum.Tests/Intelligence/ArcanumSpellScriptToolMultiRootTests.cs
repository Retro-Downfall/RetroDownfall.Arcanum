using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ArcanumSpellScriptToolMultiRootTests : IDisposable
{

    private readonly string _rootA;

    private readonly string _rootB;

    public ArcanumSpellScriptToolMultiRootTests()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "arcanum-scripttool-" + Guid.NewGuid().ToString("N"));

        _rootA = Path.Combine(baseDir, "a", "scripts");

        _rootB = Path.Combine(baseDir, "b", "scripts");

        Directory.CreateDirectory(_rootA);

        Directory.CreateDirectory(_rootB);
    }

    public void Dispose()
    {
        try
        {
            string? parent = Directory.GetParent(_rootA)?.Parent?.Parent?.FullName;

            if (parent is not null && Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task Invoke_ResolvesScriptAcrossSingleRoot()
    {
        string scriptPath = Path.Combine(_rootA, "hello.sh");

        await File.WriteAllTextAsync(scriptPath, "#!/bin/sh\necho ok\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        ArcanumSpellScriptTool tool = new(
            [_rootA],
            TimeSpan.FromSeconds(10),
            10);

        string? result = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["script_name"] = "hello.sh" }))
            as string;

        Assert.NotNull(result);

        Assert.Contains("--- exit code ---", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_AmbiguousDuplicateFilename_ReturnsErrorWithoutThrow()
    {
        await File.WriteAllTextAsync(Path.Combine(_rootA, "dup.sh"), "#!/bin/sh\necho a\n");

        await File.WriteAllTextAsync(Path.Combine(_rootB, "dup.sh"), "#!/bin/sh\necho b\n");

        ArcanumSpellScriptTool tool = new(
            [_rootA, _rootB],
            TimeSpan.FromSeconds(10),
            10);

        string? result = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["script_name"] = "dup.sh" }))
            as string;

        Assert.NotNull(result);

        Assert.StartsWith("run_spell_script: ambiguous script 'dup.sh'", result, StringComparison.Ordinal);

        Assert.Contains(_rootA, result, StringComparison.Ordinal);

        Assert.Contains(_rootB, result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_PathTraversalInScriptName_IsRejected()
    {
        ArcanumSpellScriptTool tool = new(
            [_rootA],
            TimeSpan.FromSeconds(10),
            10);

        string? result = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["script_name"] = "../outside.sh" }))
            as string;

        Assert.NotNull(result);

        Assert.Contains("bare file name", result, StringComparison.Ordinal);
    }

}
