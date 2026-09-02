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

        // Belt-and-suspenders: the class shares process state ([Collection("ProcessEnvironment")]), so a
        // test that threw before its own finally ran would otherwise leak the fault into every later test.
        ArcanumSpellScriptTool.ResolveLinkTargetFaultForTests = null;

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

    /// <summary>
    /// A spell script must not be able to read one of Arcanum's own credentials out of its own
    /// environment just because the operator pointed <c>Arcanum:Providers:*:CredentialEnvironmentVariable</c>
    /// at a name of their choosing.
    /// </summary>
    /// <remarks>
    /// The SpellScript profile's <c>ARCANUM_</c> prefix scrub covers only the derived default names,
    /// so a provider key the operator legitimately put in <c>MY_OPENAI_KEY</c> reached this child
    /// untouched while the <c>execute_command</c> path scrubbed it. Both names below deliberately
    /// avoid the <c>ARCANUM_</c> prefix and the loader-hijack denylist, or the prefix scrub would
    /// remove them and the test would pass without exercising the declared-name list at all. The
    /// undeclared marker is asserted <em>present</em> for the same reason: a child that inherited
    /// nothing would otherwise satisfy the secret assertion for the wrong reason.
    /// </remarks>
    [Fact]
    public async Task Invoke_DoesNotLeakOperatorDeclaredSecretsToTheScript()
    {
        const string declaredSecretName = "ARCTEST_SPELLSCRIPT_DECLARED_PROVIDER_KEY";

        const string declaredSecretValue = "declared-secret-value-must-not-reach-the-script";

        const string markerName = "ARCTEST_SPELLSCRIPT_INHERITANCE_MARKER";

        const string markerValue = "marker-value-that-may-reach-the-script";

        string scriptName = OperatingSystem.IsWindows() ? "env.ps1" : "env.sh";

        string scriptPath = Path.Combine(_rootA, scriptName);

        string script = OperatingSystem.IsWindows()
            ? $"Write-Output \"secret=[$env:{declaredSecretName}]\"\nWrite-Output \"marker=[$env:{markerName}]\"\n"
            : $"#!/bin/sh\necho \"secret=[${declaredSecretName}]\"\necho \"marker=[${markerName}]\"\n";

        await File.WriteAllTextAsync(scriptPath, script);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        global::System.Environment.SetEnvironmentVariable(declaredSecretName, declaredSecretValue);

        global::System.Environment.SetEnvironmentVariable(markerName, markerValue);

        try
        {
            ArcanumSpellScriptTool tool = new(
                [_rootA],
                allowUnsandboxedToolChildren: true,
                operatorDeclaredSecretEnvironmentVariables: [declaredSecretName]);

            string? result = await tool.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object?> { ["script_name"] = scriptName }))
                as string;

            Assert.NotNull(result);

            Assert.Contains($"marker=[{markerValue}]", result, StringComparison.Ordinal);

            Assert.DoesNotContain(declaredSecretValue, result, StringComparison.Ordinal);

            Assert.Contains("secret=[]", result, StringComparison.Ordinal);
        }
        finally
        {
            global::System.Environment.SetEnvironmentVariable(declaredSecretName, null);

            global::System.Environment.SetEnvironmentVariable(markerName, null);
        }
    }

    /// <summary>
    /// A genuine symlink cycle or over-limit chain long enough to make the real
    /// <c>File.ResolveLinkTarget</c>/<c>Directory.ResolveLinkTarget</c> calls throw already makes
    /// <c>FindScriptMatches</c>'s earlier <c>File.Exists</c> gate fail first on every platform this was
    /// measured on, so this exercises the fail-closed catch through
    /// <see cref="ArcanumSpellScriptTool.ResolveLinkTargetFaultForTests"/> instead -- the tool is still
    /// invoked through the real <c>InvokeAsync</c> path with a real, resolvable script; only the
    /// resolution call inside it is forced to fail the way a real ELOOP or permission failure would.
    /// </summary>
    [Fact]
    public async Task Invoke_RealpathResolutionFailure_RefusesInsteadOfRunningTheUnresolvedScript()
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

        ArcanumSpellScriptTool.ResolveLinkTargetFaultForTests =
            static _ => throw new IOException("Too many levels of symbolic links.");

        try
        {
            string? result = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["script_name"] = scriptName }))
                as string;

            Assert.NotNull(result);

            Assert.Contains("could not resolve", result, StringComparison.OrdinalIgnoreCase);

            // The defect this replaces silently continued past the failed resolution and launched the
            // child anyway, on the pre-resolution candidate -- so the un-fixed catch makes this script
            // run and print "ok" instead of refusing. Asserting the exit-code marker's absence, not just
            // the refusal text's presence, is what makes this RED for the right reason: on the old code
            // this assertion fails because the child actually ran, not because the message differs.
            Assert.DoesNotContain("--- exit code ---", result, StringComparison.Ordinal);
        }
        finally
        {
            ArcanumSpellScriptTool.ResolveLinkTargetFaultForTests = null;
        }
    }

}
