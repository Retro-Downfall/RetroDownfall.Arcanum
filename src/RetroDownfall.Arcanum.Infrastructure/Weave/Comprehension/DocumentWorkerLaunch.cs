using System.Runtime.CompilerServices;

using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// The executable a document worker is started from, bound to the file it named when it was captured. The host captures
/// its own binary once, at start; <see cref="IsUnchanged"/> runs immediately before every start, so a binary replaced or
/// rewritten while the host runs is refused rather than started as the worker of a different build.
/// </summary>
internal sealed class DocumentWorkerLaunch
{
    private readonly FileHandleIdentity _identity;

    private readonly long _length;

    private readonly long _lastWriteUtcTicks;

    private DocumentWorkerLaunch(
        ExecutableSnapshot snapshot,
        ChildProcessEnvironmentProfile environmentProfile,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<string> additionalReadExecuteRoots,
        int? maxFileDescriptors)
    {
        Executable = snapshot.Path;

        AdditionalReadExecuteRoots = additionalReadExecuteRoots;

        MaxFileDescriptors = maxFileDescriptors;

        _identity = snapshot.Identity;

        _length = snapshot.Length;

        _lastWriteUtcTicks = snapshot.LastWriteUtcTicks;

        EnvironmentProfile = environmentProfile;

        Environment = environment;
    }

    /// <summary>The full path of the file that is started: a symbolic link is resolved, so the file checked is the file run.</summary>
    internal string Executable { get; }

    /// <summary>How the runner treats the child environment.</summary>
    internal ChildProcessEnvironmentProfile EnvironmentProfile { get; }

    /// <summary>The only variables the child starts with, before the jail adds its private temp directory.</summary>
    internal IReadOnlyDictionary<string, string> Environment { get; }

    /// <summary>Directories the jail grants beyond the executable's own; none for the host's binary.</summary>
    internal IReadOnlyList<string> AdditionalReadExecuteRoots { get; }

    /// <summary>
    /// A descriptor ceiling replacing the code-owned one, or <see langword="null"/> (always, for the host's binary). A
    /// managed apphost keeps far more files open than the Native AOT binary the limit is sized for.
    /// </summary>
    internal int? MaxFileDescriptors { get; }

    /// <summary>
    /// The launch of the host's own binary, with an environment cleared entirely, when this host is a Native AOT binary
    /// (the runtime reports no dynamic code). There is none for any managed host, a dev host or a non-AOT publish: its
    /// runtime lives outside the binary, which a cleared environment and a jail cannot reach, so a worker started from it
    /// would fail without a word and the host says up front that PDF extraction is unavailable. There is also none
    /// without a process path, for a file that cannot be identified, or for the <c>dotnet</c> muxer of a host started as
    /// <c>dotnet host.dll</c>, which would read the worker arguments as an SDK command.
    /// </summary>
    internal static DocumentWorkerLaunch? ForHostExecutable(string? processPath) =>
        ForHostExecutable(processPath, nativeAot: !RuntimeFeature.IsDynamicCodeSupported);

    /// <inheritdoc cref="ForHostExecutable(string?)"/>
    /// <param name="processPath">The host's process path.</param>
    /// <param name="nativeAot">Whether the host is a Native AOT binary; a managed host has no launch.</param>
    internal static DocumentWorkerLaunch? ForHostExecutable(string? processPath, bool nativeAot)
    {
        if (!nativeAot || string.IsNullOrWhiteSpace(processPath) || IsDotNetMuxer(processPath))
        {
            return null;
        }

        return TrySnapshot(processPath) is { } snapshot && !IsDotNetMuxer(snapshot.Path)
            ? new DocumentWorkerLaunch(snapshot, ChildProcessEnvironmentProfile.DocumentWorker, new Dictionary<string, string>(), [], null)
            : null;
    }

    /// <summary>
    /// A launch of another executable for a test, which starts from a cleared environment holding exactly
    /// <paramref name="environment"/>, with <paramref name="additionalReadExecuteRoots"/> readable as well and, when
    /// given, <paramref name="maxFileDescriptors"/> as its descriptor ceiling (a managed apphost needs its shared runtime
    /// and more descriptors than the Native AOT binary). The runner's document-worker profile would clear those variables
    /// too, so this launch uses the profile whose caller has already rebuilt the whole environment.
    /// </summary>
    internal static DocumentWorkerLaunch ForTests(
        string executable,
        IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyList<string>? additionalReadExecuteRoots = null,
        int? maxFileDescriptors = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        ExecutableSnapshot snapshot = TrySnapshot(executable)
            ?? throw new InvalidOperationException("The test worker executable cannot be identified.");

        return new DocumentWorkerLaunch(
            snapshot,
            ChildProcessEnvironmentProfile.WorkspaceCheck,
            new Dictionary<string, string>(environment ?? new Dictionary<string, string>(), StringComparer.Ordinal),
            [.. additionalReadExecuteRoots ?? []],
            maxFileDescriptors);
    }

    /// <summary>Whether the file at <see cref="Executable"/> is still the one captured: the same file, size and write time.</summary>
    internal bool IsUnchanged() =>
        TrySnapshot(Executable) is { } current
        && string.Equals(current.Path, Executable, StringComparison.Ordinal)
        && FileHandleIdentity.IdentitiesMatch(_identity, current.Identity)
        && current.Length == _length
        && current.LastWriteUtcTicks == _lastWriteUtcTicks;

    private static bool IsDotNetMuxer(string path) =>
        string.Equals(Path.GetFileNameWithoutExtension(path.Trim()), "dotnet", StringComparison.OrdinalIgnoreCase);

    private static ExecutableSnapshot? TrySnapshot(string path)
    {
        try
        {
            FileInfo file = new(Path.GetFullPath(path));

            if (!file.Exists)
            {
                return null;
            }

            string target = Path.GetFullPath(file.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? file.FullName);

            if (!FileHandleIdentityInterop.TryGetPathMetadata(target, out FileHandleMetadata metadata)
                || metadata.Kind != FileSystemObjectKind.RegularFile)
            {
                return null;
            }

            FileInfo resolved = new(target);

            return new ExecutableSnapshot(target, metadata.Identity, resolved.Length, resolved.LastWriteTimeUtc.Ticks);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private sealed record ExecutableSnapshot(string Path, FileHandleIdentity Identity, long Length, long LastWriteUtcTicks);
}
