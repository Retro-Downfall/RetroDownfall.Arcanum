using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Environment;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

[Collection("ProcessEnvironment")]
public sealed class ArcanumSpellScriptToolMultiRootTests : IDisposable
{

    private readonly string _baseDir;

    private readonly string _rootA;

    private readonly string _rootB;

    private readonly HostProcessToolsEscapeHatchScope _hostProcessTools;

    public ArcanumSpellScriptToolMultiRootTests()
    {
        _hostProcessTools = new HostProcessToolsEscapeHatchScope();

        _baseDir = Path.Combine(Path.GetTempPath(), "arcanum-scripttool-" + Guid.NewGuid().ToString("N"));

        _rootA = Path.Combine(_baseDir, "a", "scripts");

        _rootB = Path.Combine(_baseDir, "b", "scripts");

        Directory.CreateDirectory(_rootA);

        Directory.CreateDirectory(_rootB);
    }

    public void Dispose()
    {
        _hostProcessTools.Dispose();

        try
        {
            if (Directory.Exists(_baseDir))
            {
                Directory.Delete(_baseDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task Invoke_ResolvesScriptAcrossSingleRoot()
    {
        string scriptName = OperatingSystem.IsWindows() ? "hello.ps1" : "hello.sh";

        string scriptPath = Path.Combine(_rootA, scriptName);

        string script = OperatingSystem.IsWindows()
            ? "Write-Output 'ok'\n"
            : "#!/bin/sh\necho ok\n";

        await File.WriteAllTextAsync(scriptPath, script);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        ArcanumSpellScriptTool tool = new(
            [_rootA],
            allowUnsandboxedToolChildren: true);

        Assert.DoesNotContain(
            "timeout",
            tool.Description,
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            "caller cancellation",
            tool.Description,
            StringComparison.OrdinalIgnoreCase);

        string? result = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["script_name"] = scriptName }))
            as string;

        Assert.NotNull(result);

        Assert.Contains("ok", result, StringComparison.Ordinal);

        Assert.EndsWith("--- exit code ---\n0", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_AmbiguousDuplicateFilename_ReturnsErrorWithoutThrow()
    {
        await File.WriteAllTextAsync(Path.Combine(_rootA, "dup.sh"), "#!/bin/sh\necho a\n");

        await File.WriteAllTextAsync(Path.Combine(_rootB, "dup.sh"), "#!/bin/sh\necho b\n");

        ArcanumSpellScriptTool tool = new(
            [_rootA, _rootB],
            allowUnsandboxedToolChildren: true);

        string? result = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["script_name"] = "dup.sh" }))
            as string;

        Assert.NotNull(result);

        Assert.StartsWith("run_spell_script: ambiguous script 'dup.sh'", result, StringComparison.Ordinal);

        Assert.Contains(_rootA, result, StringComparison.Ordinal);

        Assert.Contains(_rootB, result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_UnsupportedExtension_IsRejectedWithoutLaunch()
    {
        string scriptPath = Path.Combine(_rootA, "payload.bin");

        await File.WriteAllTextAsync(scriptPath, "not a real interpreter target");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        ArcanumSpellScriptTool tool = new(
            [_rootA],
            allowUnsandboxedToolChildren: true);

        string? result = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["script_name"] = "payload.bin" }))
            as string;

        Assert.NotNull(result);

        Assert.Contains("unsupported script type", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_PathTraversalInScriptName_IsRejected()
    {
        ArcanumSpellScriptTool tool = new(
            [_rootA],
            allowUnsandboxedToolChildren: true);

        string? result = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["script_name"] = "../outside.sh" }))
            as string;

        Assert.NotNull(result);

        Assert.Contains("bare file name", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// The advertisement gate resolves the edition from the bound <c>Arcanum:Edition</c> value. The
    /// invocation gate must read the same source, or an operator who enables Development in
    /// configuration rather than via <c>ARCANUM_EDITION</c> gets a tool that is advertised to the
    /// model and denied on every call.
    /// </summary>
    [Fact]
    public async Task Edition_set_in_configuration_alone_still_permits_invocation()
    {
        global::System.Environment.SetEnvironmentVariable(ArcanumEnvironment.EditionEnvVar, null);

        ArcanumSpellScriptTool tool = new(
            [_rootA],
            allowUnsandboxedToolChildren: true,
            configuredEdition: ArcanumEdition.Development);

        string? result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["script_name"] = "absent.sh" }))
            as string;

        Assert.NotNull(result);

        Assert.DoesNotContain(HostProcessToolPolicy.DeniedMessage, result, StringComparison.Ordinal);

        Assert.Contains("script not found", result, StringComparison.Ordinal);
    }

}
