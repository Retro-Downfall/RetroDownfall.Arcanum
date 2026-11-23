using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

internal sealed record WorkspaceCheckAppleContainerRunResult(
    CappedChildProcessRunResult ProcessResult,
    bool CleanupVerified,
    bool GuestStartAttempted = false);

/// <summary>Runs one portable check in a disposable Apple container VM.</summary>
internal sealed partial class WorkspaceCheckAppleContainerRunner
{
    private const string Executable = "/usr/local/bin/container";

    private const string SupportedRelease = "1.7.1";

    private const string GuestDriver = "/bin/sh";

    internal const string GuestWorkspaceRoot = "/tmp/arcanum-check/source";

    private const string IdentityLabel = "arcanum.workspace-check.run";

    internal const string RecoveryMarkerName = "apple-container.identity";

    private const string LockFile = "apple-container.lock";

    private const string StopReceiptFile = "apple-container.stopped";

    private const string NamePrefix = "arcanum-check-";

    private const long ControlOutputCap = 2 * 1024 * 1024;

    private static readonly TimeSpan InspectionBudget = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan CreationBudget = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(45);

    // Shutdown can inspect the VM, stop/delete it, and attest launchd absence before and after
    // deletion. Bound observation after caller cancellation without adding an execution deadline.
    internal static readonly TimeSpan MaximumCancellationSettlement =
        InspectionBudget * 5 + CleanupBudget * 2 + TimeSpan.FromSeconds(10);

    private static readonly string[] InstallationFiles =
    [
        Executable,
        "/usr/local/bin/container-apiserver",
        "/usr/local/libexec/container/plugins/container-core-images/bin/container-core-images",
        "/usr/local/libexec/container/plugins/container-core-images/config.toml",
        "/usr/local/libexec/container/plugins/container-runtime-linux/bin/container-runtime-linux",
        "/usr/local/libexec/container/plugins/container-runtime-linux/config.toml",
        "/usr/local/libexec/container/plugins/container-network-vmnet/bin/container-network-vmnet",
        "/usr/local/libexec/container/plugins/container-network-vmnet/config.toml",
    ];

    private readonly Func<bool> _available;

    private readonly Func<string[], long, TimeSpan, CancellationToken, Task<CappedChildProcessRunResult>> _command;

    internal WorkspaceCheckAppleContainerRunner()
        : this(IsAvailable, RunCommandAsync)
    {
    }

    internal WorkspaceCheckAppleContainerRunner(
        Func<bool> available,
        Func<string[], long, TimeSpan, CancellationToken, Task<CappedChildProcessRunResult>> command)
    {
        _available = available;

        _command = command;
    }

    internal static bool IsAvailable() =>
        OperatingSystem.IsMacOS()
        && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        && !Path.Exists("/usr/local/libexec/container-plugins")
        && new DirectoryInfo("/usr/local/libexec/container-plugins").LinkTarget is null
        && InstallationFiles.All(IsRootOwnedInstallationPath);

    internal async Task<WorkspaceCheckAppleContainerRunResult> RunAsync(
        string imageReference,
        string initImageReference,
        string stagedSource,
        string stagedPackages,
        string targetRelativePath,
        WorkspaceCheckKind kind,
        string? configuration,
        string? framework,
        int memoryMb,
        long outputCapBytes,
        CancellationToken cancellationToken,
        string expectedSdkVersion,
        string? verbosity = null,
        ResourceLimits? limits = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_available())
        {
            return Unavailable("The trusted Apple container installation is unavailable.");
        }

        string identity = Guid.NewGuid().ToString("N");

        string name = NamePrefix + identity;

        ResourceLimits effectiveLimits = limits ?? new ResourceLimits();

        string[] create;

        try
        {
            create = CreateArguments(
                name, identity, imageReference, initImageReference, stagedSource, stagedPackages,
                targetRelativePath, kind, configuration, framework, memoryMb, expectedSdkVersion, verbosity, effectiveLimits);
        }
        catch (ArgumentException)
        {
            return Unavailable("The portable workspace check has unsupported image, target, or profile settings.");
        }

        string? stagingRoot = Path.GetDirectoryName(stagedSource);

        if (stagingRoot is null
            || !string.Equals(stagingRoot, Path.GetDirectoryName(stagedPackages), StringComparison.Ordinal)
            || string.Equals(stagedSource, stagedPackages, StringComparison.Ordinal)
            || !IsPrivateStagingRoot(stagingRoot)
            || !IsPhysicalDirectory(stagedSource)
            || !IsPhysicalDirectory(stagedPackages))
        {
            return Unavailable("The portable workspace check requires a fresh private staging directory.");
        }

        string? localImageDigest = await ResolveImagesAsync(
            imageReference, initImageReference, cancellationToken).ConfigureAwait(false);

        if (localImageDigest is null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Unavailable("The pinned workspace-check and init images must both be present locally for Linux ARM64.");
        }

        string journal = Path.Combine(stagingRoot, RecoveryMarkerName);

        using FileStream ownership = new(Path.Combine(stagingRoot, LockFile), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);

        WriteIdentity(journal, identity);

        CappedChildProcessRunResult? processResult = null;

        bool startAttempted = false;

        bool cleanupVerified;

        try
        {
            CappedChildProcessRunResult created = await _command(
                create, ControlOutputCap, CreationBudget, cancellationToken).ConfigureAwait(false);

            if (ReportsIdentity(created, name))
            {
                CappedChildProcessRunResult inspected = await InspectAsync(name, cancellationToken).ConfigureAwait(false);

                string[] expectedGuestArguments = create[(Array.IndexOf(create, "--entrypoint") + 3)..];

                if (MatchesLaunchConfiguration(inspected, name, identity, imageReference, localImageDigest,
                        stagedSource, stagedPackages, expectedGuestArguments, expectedSdkVersion, memoryMb, effectiveLimits))
                {
                    startAttempted = true;

                    processResult = await _command(
                        ["start", "--attach", name], outputCapBytes, Timeout.InfiniteTimeSpan, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            processResult = new() { Outcome = CappedChildProcessOutcome.Canceled };
        }
        catch (Exception)
        {
            // The native service may have acted even when its CLI failed to return a receipt.
            // Report through the same cleanup protocol before releasing the private inputs.
        }
        finally
        {
            try
            {
                cleanupVerified = await RemoveOwnedVmAsync(
                    name, identity, stagingRoot, neverStarted: !startAttempted).ConfigureAwait(false);

                if (cleanupVerified)
                {
                    File.Delete(journal);
                }
            }
            catch (Exception)
            {
                cleanupVerified = false;
            }
        }

        if (!cleanupVerified)
        {
            return Unavailable("The workspace-check VM could not be confirmed removed. Its staging directory is retained for recovery.", false);
        }

        bool guestStartAttempted = startAttempted && processResult?.Outcome != CappedChildProcessOutcome.CanceledBeforeStart;

        return processResult is null
            ? Unavailable("The Apple container service could not create and verify the isolated workspace-check VM.")
                with { GuestStartAttempted = guestStartAttempted }
            : new WorkspaceCheckAppleContainerRunResult(processResult, true, guestStartAttempted);
    }

    internal async Task<bool> AreImagesAvailableAsync(
        string imageReference,
        string initImageReference,
        CancellationToken cancellationToken) =>
        await ResolveImagesAsync(imageReference, initImageReference, cancellationToken).ConfigureAwait(false) is not null;

    private async Task<string?> ResolveImagesAsync(
        string imageReference,
        string initImageReference,
        CancellationToken cancellationToken)
    {
        if (!_available() || !WorkspaceCheckPortableDotNetPolicy.IsDigestPinnedImage(imageReference)
            || !WorkspaceCheckPortableDotNetPolicy.IsDigestPinnedImage(initImageReference)
            || string.Equals(imageReference, initImageReference, StringComparison.Ordinal)
            || !await HasTrustedServiceAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        CappedChildProcessRunResult result = await _command(
            ["image", "inspect", imageReference, initImageReference],
            ControlOutputCap, InspectionBudget, cancellationToken).ConfigureAwait(false);

        string? localImageDigest = null;

        return ReadJson(result, entries =>
            entries.GetArrayLength() == 2
            && (localImageDigest = GetLocalImageDigest(entries, imageReference)) is not null
            && GetLocalImageDigest(entries, initImageReference) is not null)
            ? localImageDigest
            : null;
    }

    /// <summary>Reconciles a retained stage after restart. An active invocation still owns its lock.</summary>
    internal async Task<bool> RecoverAsync(string stagingRoot)
    {
        if (!_available()
            || !IsPrivateStagingRoot(stagingRoot)
            || !await HasTrustedServiceAsync(CancellationToken.None).ConfigureAwait(false))
        {
            return false;
        }

        string journal = Path.Combine(stagingRoot, RecoveryMarkerName);

        try
        {
            string lockPath = Path.Combine(stagingRoot, LockFile);

            if (!IsSingleRegularFile(lockPath))
            {
                return false;
            }

            using FileStream ownership = new(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            string? identity = ReadIdentity(journal);

            if (identity is null
                || !await RemoveOwnedVmAsync(NamePrefix + identity, identity, stagingRoot, neverStarted: false).ConfigureAwait(false))
            {
                return false;
            }

            File.Delete(journal);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsSingleRegularFile(string path) =>
        FileHandleIdentityInterop.TryGetPathMetadataNoFollow(path, out FileHandleMetadata metadata)
        && metadata.Kind == FileSystemObjectKind.RegularFile && metadata.HardLinkCount == 1;

    private static string? ReadIdentity(string path)
    {
        if (!IsSingleRegularFile(path))
        {
            return null;
        }

        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        if (file.Length != 32)
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[32];

        file.ReadExactly(bytes);

        string identity = Encoding.ASCII.GetString(bytes);

        return IsLowerHex(identity) ? identity : null;
    }

    private static void WriteIdentity(string path, string identity)
    {
        using FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);

        file.Write(Encoding.ASCII.GetBytes(identity));

        file.Flush(flushToDisk: true);
    }

    private Task<CappedChildProcessRunResult> InspectAsync(string name, CancellationToken token) =>
        _command(["inspect", name], ControlOutputCap, InspectionBudget, token);

    private async Task<bool> HasTrustedServiceAsync(CancellationToken token)
    {
        CappedChildProcessRunResult result = await _command(
            ["system", "status", "--format", "json"], ControlOutputCap, InspectionBudget, token).ConfigureAwait(false);

        return ReadJson(result, status =>
            status.GetProperty("status").GetString() == "running"
            && status.GetProperty("paths").GetProperty("installRoot").GetString() is "/usr/local" or "/usr/local/"
            && status.GetProperty("client").GetProperty("version").GetString() == SupportedRelease
            && status.GetProperty("server").GetProperty("version").GetString() == SupportedRelease
            && status.GetProperty("server").GetProperty("build").GetString() == "release");
    }

    private async Task<bool> RemoveOwnedVmAsync(string name, string identity, string stagingRoot, bool neverStarted)
    {
        string receipt = Path.Combine(stagingRoot, StopReceiptFile);

        CappedChildProcessRunResult inspected = await InspectAsync(name, CancellationToken.None).ConfigureAwait(false);

        if (!MatchesIdentity(inspected, name, identity))
        {
            // After a possible start, inventory absence is meaningful only if the marker
            // proves that an earlier attempt stopped this exact VM before requesting deletion.
            if (!neverStarted && ReadIdentity(receipt) != identity)
            {
                return false;
            }

            CappedChildProcessRunResult listed = await _command(
                ["list", "--all", "--format", "json"], ControlOutputCap, InspectionBudget, CancellationToken.None)
                .ConfigureAwait(false);

            return ReadJson(listed, entries => entries.EnumerateArray()
                    .All(entry => entry.GetProperty("id").GetString() != name))
                && (neverStarted || await HasNoRuntimeAsync(name).ConfigureAwait(false));
        }

        CappedChildProcessRunResult stopped = await _command(
            ["stop", "--signal", "SIGKILL", "--time", "5", name],
            ControlOutputCap, CleanupBudget, CancellationToken.None).ConfigureAwait(false);

        if (!ReportsIdentity(stopped, name) || !await HasNoRuntimeAsync(name).ConfigureAwait(false))
        {
            return false;
        }

        if (ReadIdentity(receipt) != identity)
        {
            string pendingReceipt = receipt + "." + Guid.NewGuid().ToString("N");

            WriteIdentity(pendingReceipt, identity);

            File.Move(pendingReceipt, receipt, overwrite: true);
        }

        CappedChildProcessRunResult deleted = await _command(
            ["delete", "--force", name], ControlOutputCap, CleanupBudget, CancellationToken.None)
            .ConfigureAwait(false);

        return ReportsIdentity(deleted, name) && await HasNoRuntimeAsync(name).ConfigureAwait(false);
    }

    private async Task<bool> HasNoRuntimeAsync(string name)
    {
        CappedChildProcessRunResult session = await _command(
            ["launchctl", "managername"], ControlOutputCap, InspectionBudget, CancellationToken.None).ConfigureAwait(false);

        if (!Succeeded(session) || session.Stdout.Truncated)
        {
            return false;
        }

        string? domain = session.Stdout.Text.Trim() switch
        {
            "Aqua" => "gui/" + GetUserId().ToString(CultureInfo.InvariantCulture),
            "Background" => "user/" + GetUserId().ToString(CultureInfo.InvariantCulture),
            _ => null,
        };

        if (domain is null)
        {
            return false;
        }

        CappedChildProcessRunResult status = await _command(
            ["launchctl", "print", domain + "/com.apple.container.container-runtime-linux." + name],
            ControlOutputCap, InspectionBudget, CancellationToken.None).ConfigureAwait(false);

        // Apple 1.7.1's ServiceManager interprets this exact launchctl exit status as absent.
        return status.Outcome == CappedChildProcessOutcome.Completed && status.ExitCode == 113;
    }

    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint GetUserId();

    // The script is entirely host-authored and executes inside the guest. Repository and profile
    // values enter only through positional arguments. The stock SDK image provides /usr/share/dotnet.
    internal const string GuestScript = """
        set -eu
        action="$1"
        target="./$2"
        configuration="$3"
        framework="${4:-}"
        verbosity="${5:-}"
        umask 077
        scratch=/tmp/arcanum-check
        mkdir "$scratch"
        mkdir "$scratch/source" "$scratch/home" "$scratch/nuget" "$scratch/temp" "$scratch/results"
        cp -R /arcanum/source/. "$scratch/source/"
        chmod -R u+rwX "$scratch/source"
        export HOME="$scratch/home" DOTNET_CLI_HOME="$scratch/home" NUGET_PACKAGES="$scratch/nuget"
        export TMPDIR="$scratch/temp" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
        export DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true
        export MSBUILDDISABLENODEREUSE=1
        cd "$scratch/source"
        [ -f "$target" ] || exit 66
        dotnet=/usr/share/dotnet/dotnet
        selected="$("$dotnet" --version)"
        if [ "$selected" != "${DOTNET_SDK_VERSION:?}" ] || [ "$selected" != "${ARCANUM_EXPECTED_SDK:?}" ]; then
            printf '%s\n' 'The selected SDK does not match the pinned workspace-check SDK.' >&2
            exit 78
        fi
        printf 'Arcanum workspace check SDK %s, %s %s\n' "$selected" "$action" "$configuration"
        cat > "$scratch/NuGet.Config" <<'XML'
        <configuration><packageSources><clear/><add key="offline" value="/arcanum/packages"/></packageSources><auditSources><clear/></auditSources></configuration>
        XML
        "$dotnet" restore "$target" --configfile "$scratch/NuGet.Config" --source /arcanum/packages --disable-parallel -p:NuGetAudit=false -m:1 -p:UseSharedCompilation=false
        set --
        if [ -n "$framework" ]; then
            set -- --framework "$framework"
        fi
        if [ -n "$verbosity" ]; then
            set -- "$@" --verbosity "$verbosity"
        fi
        if [ "$action" = test ]; then
            set -- "$@" --results-directory "$scratch/results" --logger trx
        fi
        exec "$dotnet" "$action" "$target" --no-restore --configuration "$configuration" -m:1 -p:UseSharedCompilation=false "$@"
        """;

    internal static string[] CreateArguments(
        string name,
        string identity,
        string imageReference,
        string initImageReference,
        string stagedSource,
        string stagedPackages,
        string target,
        WorkspaceCheckKind kind,
        string? configuration,
        string? framework,
        int memoryMb,
        string expectedSdkVersion,
        string? verbosity = null,
        ResourceLimits? limits = null)
    {
        ResourceLimits effectiveLimits = limits ?? new ResourceLimits();

        if (!WorkspaceCheckPortableDotNetPolicy.IsDigestPinnedImage(imageReference)
            || !WorkspaceCheckPortableDotNetPolicy.IsDigestPinnedImage(initImageReference)
            || string.Equals(imageReference, initImageReference, StringComparison.Ordinal)
            || !ValidMountPath(stagedSource) || !ValidMountPath(stagedPackages)
            || configuration is not (null or "Debug" or "Release")
            || verbosity is not (null or "quiet" or "minimal" or "normal" or "detailed" or "diagnostic")
            || kind is not (WorkspaceCheckKind.Build or WorkspaceCheckKind.Test)
            || memoryMb is < 4096 or > 8192
            || effectiveLimits.MaxCpuSeconds < 0 || effectiveLimits.MaxFileDescriptors < 0
            || !WorkspaceCheckSdkVersion.TryParse(expectedSdkVersion, out WorkspaceCheckSdkVersion sdk)
            || sdk.Band.Feature < 1
            || !IsSafeTarget(target)
            || (framework is not null && !IsSafeFramework(framework)))
        {
            throw new ArgumentException("Invalid portable workspace-check settings.");
        }

        List<string> args =
        [
            "create",
            "--name", name,
            "--label", IdentityLabel + "=" + identity,
            "--platform", "linux/arm64",
            "--network", "none",
            "--no-dns",
            "--read-only",
            "--cap-drop", "ALL",
            "--user", "65532:65532",
            "--env", "ARCANUM_EXPECTED_SDK=" + expectedSdkVersion,
            "--workdir", "/tmp",
            "--cpus", "2",
            "--memory", memoryMb.ToString(CultureInfo.InvariantCulture) + "M",
            "--tmpfs", "/tmp",
            "--mount", $"type=bind,source={stagedSource},target=/arcanum/source,readonly",
            "--mount", $"type=bind,source={stagedPackages},target=/arcanum/packages,readonly",
            "--runtime", "container-runtime-linux",
            "--init-image", initImageReference,
        ];

        AddLimit("cpu", effectiveLimits.MaxCpuSeconds);

        AddLimit("nofile", effectiveLimits.MaxFileDescriptors);

        args.AddRange(
        [
            "--entrypoint", GuestDriver,
            imageReference,
            "-c",
            GuestScript,
            "arcanum-workspace-check",
            kind == WorkspaceCheckKind.Build ? "build" : "test",
            target,
            configuration ?? "Debug",
        ]);

        if (framework is not null || verbosity is not null)
        {
            args.Add(framework ?? "");
        }

        if (verbosity is not null)
        {
            args.Add(verbosity);
        }

        return args.ToArray();

        void AddLimit(string resource, int value)
        {
            if (value > 0)
            {
                string number = value.ToString(CultureInfo.InvariantCulture);

                args.Add("--ulimit");

                args.Add(resource + "=" + number + ":" + number);
            }
        }
    }

    private static bool IsLowerHex(string value) =>
        value.All(static c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    private static bool ValidMountPath(string path) =>
        Path.IsPathFullyQualified(path)
        && path == Path.GetFullPath(path)
        && !path.Contains(',')
        && !path.Any(char.IsControl);

    private static bool IsSafeTarget(string target) =>
        target.Length is > 0 and <= 1024
        && !target.StartsWith('-')
        && !target.Contains((char)92)
        && !target.Contains(':')
        && !target.Any(char.IsControl)
        && target.Split('/').All(static piece => piece is not ("" or "." or ".."))
        && WorkspaceCheckTargetResolver.HasSupportedTargetExtension(target);

    internal static bool IsSafeFramework(string framework) =>
        framework.Length is > 3 and <= 64
        && framework.StartsWith("net", StringComparison.Ordinal)
        && char.IsAsciiDigit(framework[3])
        && framework.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-');

    private static bool IsPrivateStagingRoot(string path) =>
        !OperatingSystem.IsWindows()
        && IsPhysicalDirectory(path)
        && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0;

    private static bool IsPhysicalDirectory(string path) =>
        Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is null;

    private static bool IsRootOwnedInstallationPath(string path)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (new FileInfo(current).LinkTarget is not null
                || !FileHandleIdentityInterop.TryGetUnixOwnerUserId(current, out uint owner)
                || owner != 0
                || (File.GetUnixFileMode(current) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            {
                return false;
            }
        }

        return File.Exists(path);
    }

    private static string? GetLocalImageDigest(JsonElement entries, string reference)
    {
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            JsonElement config = entry.GetProperty("configuration");

            if (config.GetProperty("name").GetString() != reference)
            {
                continue;
            }

            string? localDigest = config.GetProperty("descriptor").GetProperty("digest").GetString();

            if (localDigest is not { Length: 71 } || !localDigest.StartsWith("sha256:", StringComparison.Ordinal)
                || !IsLowerHex(localDigest[7..]))
            {
                return null;
            }

            // For a manifest pin, Apple creates a local OCI index wrapping the exact manifest.
            // Preserve the observed index digest for the subsequent VM configuration receipt.
            JsonElement[] linuxArm64 = entry.GetProperty("variants").EnumerateArray()
                .Where(static variant => IsLinuxArm64(variant.GetProperty("platform"))).ToArray();

            if (linuxArm64.Length > 0
                && (localDigest == Digest(reference)
                    || linuxArm64.All(variant => variant.GetProperty("digest").GetString() == Digest(reference))))
            {
                return localDigest;
            }
        }

        return null;
    }

    private static string Digest(string reference) => reference[(reference.LastIndexOf('@') + 1)..];

    private static bool IsLinuxArm64(JsonElement platform) =>
        platform.GetProperty("os").GetString() == "linux"
        && platform.GetProperty("architecture").GetString() == "arm64";

    private static bool MatchesIdentity(CappedChildProcessRunResult result, string name, string identity) =>
        ReadConfiguration(result, config => Owns(config, name, identity));

    private static bool MatchesLaunchConfiguration(
        CappedChildProcessRunResult result, string name, string identity, string image, string localImageDigest,
        string source, string packages, string[] guestArguments, string expectedSdkVersion, int memoryMb, ResourceLimits limits)
    {
        return ReadJson(result, entries =>
            entries.GetArrayLength() == 1
            && entries[0].GetProperty("status").GetProperty("state").GetString() == "stopped"
            && LaunchConfigMatches(entries[0].GetProperty("configuration")));

        bool LaunchConfigMatches(JsonElement config) =>
            Owns(config, name, identity)
            && IsLinuxArm64(config.GetProperty("platform"))
            && config.GetProperty("image").GetProperty("reference").GetString() == image
            && config.GetProperty("image").GetProperty("descriptor").GetProperty("digest").GetString() == localImageDigest
            && config.GetProperty("networks").GetArrayLength() == 0
            && config.GetProperty("publishedPorts").GetArrayLength() == 0
            && config.GetProperty("publishedSockets").GetArrayLength() == 0
            && !config.GetProperty("ssh").GetBoolean()
            && config.GetProperty("readOnly").GetBoolean()
            && !config.GetProperty("virtualization").GetBoolean()
            && config.GetProperty("runtimeHandler").GetString() == "container-runtime-linux"
            && (!config.TryGetProperty("dns", out JsonElement dns) || dns.ValueKind == JsonValueKind.Null)
            && config.GetProperty("capAdd").GetArrayLength() == 0
            && config.GetProperty("capDrop").EnumerateArray().Select(static cap => cap.GetString()).SequenceEqual(["ALL"])
            && config.GetProperty("resources").GetProperty("cpus").GetInt32() == 2
            && config.GetProperty("resources").GetProperty("memoryInBytes").GetInt64() == memoryMb * 1024L * 1024
            && config.GetProperty("initProcess").GetProperty("executable").GetString() == GuestDriver
            && config.GetProperty("initProcess").GetProperty("workingDirectory").GetString() == "/tmp"
            && config.GetProperty("initProcess").GetProperty("user").GetProperty("raw").GetProperty("userString").GetString() == "65532:65532"
            && config.GetProperty("initProcess").GetProperty("arguments").EnumerateArray()
                .Select(static argument => argument.GetString()).SequenceEqual(guestArguments)
            && config.GetProperty("initProcess").GetProperty("environment").EnumerateArray()
                .Any(variable => variable.GetString() == "ARCANUM_EXPECTED_SDK=" + expectedSdkVersion)
            && HasExpectedLimits(config.GetProperty("initProcess").GetProperty("rlimits"), limits)
            && HasExpectedMounts(config.GetProperty("mounts"), source, packages);
    }

    private static bool HasExpectedLimits(JsonElement resources, ResourceLimits expected)
    {
        int count = (expected.MaxCpuSeconds > 0 ? 1 : 0) + (expected.MaxFileDescriptors > 0 ? 1 : 0);

        return resources.GetArrayLength() == count
            && HasLimit("RLIMIT_CPU", expected.MaxCpuSeconds)
            && HasLimit("RLIMIT_NOFILE", expected.MaxFileDescriptors);

        bool HasLimit(string type, int value) => value == 0 || resources.EnumerateArray().Any(resource =>
            resource.GetProperty("limit").GetString() == type
            && resource.GetProperty("soft").GetInt32() == value
            && resource.GetProperty("hard").GetInt32() == value);
    }

    private static bool Owns(JsonElement config, string name, string identity) =>
        config.GetProperty("id").GetString() == name
        && config.GetProperty("labels").GetProperty(IdentityLabel).GetString() == identity;

    private static bool HasExpectedMounts(JsonElement mounts, string source, string packages) =>
        mounts.GetArrayLength() == 3
        && mounts.EnumerateArray().Any(mount =>
            mount.GetProperty("type").TryGetProperty("tmpfs", out _)
            && mount.GetProperty("destination").GetString() == "/tmp")
        && HasReadOnlyShare(mounts, source, "/arcanum/source")
        && HasReadOnlyShare(mounts, packages, "/arcanum/packages");

    private static bool HasReadOnlyShare(JsonElement mounts, string source, string guestPath) =>
        mounts.EnumerateArray().Any(mount =>
            mount.GetProperty("type").TryGetProperty("virtiofs", out _)
            && mount.GetProperty("source").GetString() == source
            && mount.GetProperty("destination").GetString() == guestPath
            && mount.GetProperty("options").EnumerateArray().Any(option => option.GetString() == "ro"));

    private static bool ReadConfiguration(CappedChildProcessRunResult result, Func<JsonElement, bool> predicate) =>
        ReadJson(result, entries => entries.GetArrayLength() == 1 && predicate(entries[0].GetProperty("configuration")));

    private static bool ReadJson(CappedChildProcessRunResult result, Func<JsonElement, bool> predicate)
    {
        if (!Succeeded(result) || result.Stdout.Truncated)
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.Stdout.Text);

            return predicate(document.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }

    private static bool ReportsIdentity(CappedChildProcessRunResult result, string name) =>
        Succeeded(result) && !result.Stdout.Truncated && result.Stdout.Text.Trim() == name;

    private static bool Succeeded(CappedChildProcessRunResult result) =>
        result.Outcome == CappedChildProcessOutcome.Completed && result.ExitCode == 0;

    private static WorkspaceCheckAppleContainerRunResult Unavailable(string reason, bool cleanupVerified = true) =>
        new(new CappedChildProcessRunResult
        {
            Outcome = CappedChildProcessOutcome.FilesystemSandboxUnavailable,
            FilesystemSandboxDenialMessage = reason,
        }, cleanupVerified);

    internal static ProcessStartInfo CreateStartInfo(string[] arguments)
    {
        bool inspectLaunchd = arguments[0] == "launchctl";

        ProcessStartInfo start = new(inspectLaunchd ? "/bin/launchctl" : Executable)
        {
            WorkingDirectory = "/",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        start.Environment.Clear();

        start.Environment["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin";

        start.Environment["HOME"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        start.Environment["LANG"] = "en_US.UTF-8";

        foreach (string argument in inspectLaunchd ? arguments[1..] : arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    private static async Task<CappedChildProcessRunResult> RunCommandAsync(
        string[] arguments, long outputCap, TimeSpan timeout, CancellationToken token)
    {
        if (!IsAvailable())
        {
            return new() { Outcome = CappedChildProcessOutcome.FailedToStart };
        }

        using CancellationTokenSource deadline = new(timeout);

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);

        using Process process = new() { StartInfo = CreateStartInfo(arguments) };

        long streamCap = Math.Max(1, outputCap / 2);

        try
        {
            if (linked.IsCancellationRequested)
            {
                return new() { Outcome = CappedChildProcessOutcome.CanceledBeforeStart };
            }

            if (!process.Start())
            {
                return new() { Outcome = CappedChildProcessOutcome.FailedToStart };
            }

            process.StandardInput.Close();

            Task<CappedStreamOutput> stdout = ReadCappedAsync(process.StandardOutput.BaseStream, streamCap, linked.Token);

            Task<CappedStreamOutput> stderr = ReadCappedAsync(process.StandardError.BaseStream, streamCap, linked.Token);

            Task<CappedStreamOutput[]> outputTask = Task.WhenAll(stdout, stderr);

            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);

                CappedStreamOutput[] output = await outputTask
                    .WaitAsync(TimeSpan.FromSeconds(5), linked.Token).ConfigureAwait(false);

                return new()
                {
                    Outcome = CappedChildProcessOutcome.Completed,
                    Stdout = output[0],
                    Stderr = output[1],
                    ExitCode = process.ExitCode,
                    PerStreamCapBytes = streamCap,
                };
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException)
            {
                linked.Cancel();

                TryKill(process);

                _ = outputTask.ContinueWith(
                    static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

                return new()
                {
                    Outcome = token.IsCancellationRequested
                        ? CappedChildProcessOutcome.Canceled
                        : deadline.IsCancellationRequested
                            ? CappedChildProcessOutcome.TimedOut
                            : CappedChildProcessOutcome.IoErrorReadingOutput,
                };
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            TryKill(process);

            return new() { Outcome = CappedChildProcessOutcome.FailedToStart };
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
    }

    private static async Task<CappedStreamOutput> ReadCappedAsync(Stream stream, long cap, CancellationToken token)
    {
        byte[] buffer = new byte[8192];

        using MemoryStream captured = new();

        long total = 0;

        int read;

        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            total += read;

            int remaining = (int)Math.Clamp(cap - captured.Length, 0, read);

            captured.Write(buffer, 0, remaining);
        }

        return new CappedStreamOutput(Encoding.UTF8.GetString(captured.ToArray()), total > cap, TotalBytes: total);
    }
}
