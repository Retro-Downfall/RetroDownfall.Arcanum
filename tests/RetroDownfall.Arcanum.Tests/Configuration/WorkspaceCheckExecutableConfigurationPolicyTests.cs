using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class WorkspaceCheckExecutableConfigurationPolicyTests
{
    [Fact]
    public void Validate_RequiresFullyQualifiedPath()
    {
        WorkspaceCheckExecutableConfigurationPolicy policy =
            WorkspaceCheckExecutableConfigurationPolicy.ForTrustedRoots([Path.GetTempPath()]);

        WorkspaceCheckExecutableConfigurationResult result =
            policy.Validate(Path.Combine("relative", NativeDotNetFileName), workspace: null);

        Assert.False(result.IsValid);
        Assert.Contains("fully qualified", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public void Validate_RejectsLexicalWorkspaceContainmentWhenSymlinkResolvesOutward()
    {
        Skip.If(OperatingSystem.IsWindows(), "File symlink creation requires elevation on Windows.");
        using TestDirectory tree = new();
        string workspace = tree.CreateDirectory("workspace");
        string trustedRoot = tree.CreateDirectory("trusted");
        string trustedExecutable = CreateExecutable(trustedRoot);
        string configuredPath = Path.Combine(workspace, NativeDotNetFileName);
        File.CreateSymbolicLink(configuredPath, trustedExecutable);
        WorkspaceCheckExecutableConfigurationPolicy policy =
            WorkspaceCheckExecutableConfigurationPolicy.ForTrustedRoots([trustedRoot]);

        WorkspaceCheckExecutableConfigurationResult result =
            policy.Validate(configuredPath, workspace);

        Assert.False(result.IsValid);
        Assert.Contains("lexically inside", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public void Validate_RejectsResolvedWorkspaceContainmentThroughSymlinkedParent()
    {
        Skip.If(OperatingSystem.IsWindows(), "Directory symlink creation requires elevation on Windows.");
        using TestDirectory tree = new();
        string workspace = tree.CreateDirectory("workspace");
        string trustedRoot = tree.CreateDirectory("workspace/trusted");
        CreateExecutable(trustedRoot);
        string outside = tree.CreateDirectory("outside");
        string symlinkedParent = Path.Combine(outside, "sdk");
        Directory.CreateSymbolicLink(symlinkedParent, trustedRoot);
        string configuredPath = Path.Combine(symlinkedParent, NativeDotNetFileName);
        WorkspaceCheckExecutableConfigurationPolicy policy =
            WorkspaceCheckExecutableConfigurationPolicy.ForTrustedRoots([trustedRoot]);

        WorkspaceCheckExecutableConfigurationResult result =
            policy.Validate(configuredPath, workspace);

        Assert.False(result.IsValid);
    }

    [SkippableFact]
    public void Validate_CanonicalizesEverySymlinkedParentComponent()
    {
        Skip.If(OperatingSystem.IsWindows(), "Directory symlink creation requires elevation on Windows.");
        using TestDirectory tree = new();
        string trustedRoot = tree.CreateDirectory("trusted");
        string trustedExecutable = CreateExecutable(trustedRoot);
        string outside = tree.CreateDirectory("outside");
        string symlinkedParent = Path.Combine(outside, "sdk");
        Directory.CreateSymbolicLink(symlinkedParent, trustedRoot);
        WorkspaceCheckExecutableConfigurationPolicy policy =
            WorkspaceCheckExecutableConfigurationPolicy.ForTrustedRoots([trustedRoot]);

        WorkspaceCheckExecutableConfigurationResult direct =
            policy.Validate(trustedExecutable, workspace: null);
        WorkspaceCheckExecutableConfigurationResult throughSymlink =
            policy.Validate(
                Path.Combine(symlinkedParent, NativeDotNetFileName),
                workspace: null);

        Assert.True(direct.IsValid, direct.Error);
        Assert.True(
            throughSymlink.IsValid,
            $"{throughSymlink.Error} Candidate: {throughSymlink.CanonicalPath}; expected: {direct.CanonicalPath}");
        Assert.Equal(direct.CanonicalPath, throughSymlink.CanonicalPath);
    }

    [Fact]
    public void Validate_RejectsNativeNamedExecutableOutsidePositiveTrustedRoots()
    {
        using TestDirectory tree = new();
        string trustedRoot = tree.CreateDirectory("trusted");
        string untrustedRoot = tree.CreateDirectory("untrusted");
        string configuredPath = CreateExecutable(untrustedRoot);
        WorkspaceCheckExecutableConfigurationPolicy policy =
            WorkspaceCheckExecutableConfigurationPolicy.ForTrustedRoots([trustedRoot]);

        WorkspaceCheckExecutableConfigurationResult result =
            policy.Validate(configuredPath, workspace: null);

        Assert.False(result.IsValid);
        Assert.Contains("trusted dotnet installation root", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsScriptNamedDotNetInsideTrustedRoot()
    {
        using TestDirectory tree = new();
        string trustedRoot = tree.CreateDirectory("trusted");
        string configuredPath = Path.Combine(trustedRoot, NativeDotNetFileName);
        File.WriteAllText(configuredPath, "#!/bin/sh\nexit 0\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                configuredPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        WorkspaceCheckExecutableConfigurationPolicy policy =
            WorkspaceCheckExecutableConfigurationPolicy.ForTrustedRoots([trustedRoot]);

        WorkspaceCheckExecutableConfigurationResult result =
            policy.Validate(configuredPath, workspace: null);

        Assert.False(result.IsValid);
        Assert.Contains("native executable format", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AcceptsTrustedNativeCandidateAndDefersOsIdentityVerification()
    {
        using TestDirectory tree = new();
        string trustedRoot = tree.CreateDirectory("trusted");
        string configuredPath = CreateExecutable(trustedRoot);
        WorkspaceCheckExecutableConfigurationPolicy policy =
            WorkspaceCheckExecutableConfigurationPolicy.ForTrustedRoots([trustedRoot]);

        WorkspaceCheckExecutableConfigurationResult result =
            policy.Validate(configuredPath, workspace: null);

        Assert.True(result.IsValid, result.Error);
        string canonicalPath = Assert.IsType<string>(result.CanonicalPath);
        Assert.True(Path.IsPathFullyQualified(canonicalPath));
        Assert.Equal(NativeDotNetFileName, Path.GetFileName(canonicalPath));
        Assert.True(result.RequiresRuntimeIdentityValidation);
    }

    private static string CreateExecutable(string directory)
    {
        string path = Path.Combine(directory, NativeDotNetFileName);
        byte[] nativeHeader = OperatingSystem.IsWindows()
            ? [(byte)'M', (byte)'Z', 0x00, 0x00]
            : OperatingSystem.IsLinux()
                ? [0x7F, (byte)'E', (byte)'L', (byte)'F']
                : [0xCF, 0xFA, 0xED, 0xFE];
        File.WriteAllBytes(path, nativeHeader);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private static string NativeDotNetFileName =>
        OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

    private sealed class TestDirectory : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), $"arcanum-executable-policy-{Guid.NewGuid():N}");

        public TestDirectory() => Directory.CreateDirectory(_root);

        public string CreateDirectory(string relativePath)
        {
            string path = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
