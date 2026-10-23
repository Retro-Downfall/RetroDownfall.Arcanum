using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// Builds a Seatbelt profile for <c>sandbox-exec</c> (deprecated Apple tool; MVP for Apple Silicon beta).
/// </summary>
/// <remarks>
/// <para>
/// Critical invariant: no profile rule may grant whole-volume file-content read
/// (no <c>(subpath "/")</c>, no <c>(literal "/")</c> for file-read*). The one rule that names the root is
/// <see cref="RootDirectoryEntryRule"/>, emitted only for a profile confined to its roots: the root directory's own
/// listing and metadata, nothing beneath it, which the guard admits by its exact text.
/// </para>
/// <para>
/// Access classes:
/// <list type="bullet">
/// <item>Workspace / Sanctum AllowedPaths — read + write</item>
/// <item>Spell script roots — read + execute (no write unless also a RW root)</item>
/// <item>System runtime — read + execute, no write</item>
/// <item>Per-invocation owner-only TMPDIR — read + write (no broad /tmp); read-only when writes are denied</item>
/// </list>
/// Network is explicitly allowed — filesystem-only MVP; not network isolation — unless the request asks
/// for <see cref="ChildProcessSandboxRequest.DenyNetwork"/>, which removes network and socket access.
/// </para>
/// </remarks>
internal static class MacOsSandboxExecProfileBuilder
{
    /// <summary>
    /// A confined profile's grant on the root directory itself: its listing (the standard top-level names every Mac has)
    /// and its metadata, nothing beneath it. Every process start reads it, and dyld aborts the process without it; the
    /// unconfined profile grants it through its read of every directory.
    /// </summary>
    internal const string RootDirectoryEntryRule = "(allow file-read-data file-read-metadata (literal \"/\"))";

    /// <summary>
    /// A profile that denies file writes grants this one write and no other: data written to <c>/dev/null</c>, which keeps
    /// none of it. The document worker does not need it (both the Native AOT binary and a managed apphost extract with no
    /// device write at all), but a discard sink holds nothing and spares a library that opens it an error path.
    /// </summary>
    internal const string DevNullWriteRule = "(allow file-write-data (literal \"/dev/null\"))";

    /// <summary>
    /// A confined profile ends with this denial. No file operation mediates <c>fsgetpath</c> and <c>fsgetpath_ext</c>:
    /// given a volume id (a <c>statfs</c> of the child's own temp directory supplies it) and any object id they return the
    /// absolute path of any file on the volume, so a sweep of ids would recover every file name the confinement refuses to
    /// list or stat. Only the <c>syscall-unix</c> operation governs them, and a rule naming the two calls leaves every other
    /// system call as it was (<c>(deny default)</c> does not reach <c>syscall-unix</c> until a rule names it). Denying one of the
    /// two is not enough, because the other returns the same path. The rule closes object-id path recovery and nothing else: the
    /// root directory's listing, the ancestors' metadata, the mount-point names <c>getfsstat</c> lists (dyld's own profile allows
    /// that call) and, since <c>process*</c> and <c>sysctl*</c> stay allowed, other processes' paths, argument vectors and
    /// environments remain readable to a confined child, which the design reference records as its residual.
    /// </summary>
    internal const string ObjectIdPathDenialRule = "(deny syscall-unix (syscall-number SYS_fsgetpath SYS_fsgetpath_ext))";

    /// <summary>
    /// Links outside every root that a confined profile's launch chain resolves: <c>/bin/sh</c>, which runs the limit
    /// prelude, reads the link naming the shell it starts.
    /// </summary>
    private static readonly string[] ConfinedLaunchChainLinks =
    [
        "/private/var/select/sh",
    ];

    /// <summary>
    /// Builds a deny-default Seatbelt profile. Throws if any root is unsafe (control chars, whole-volume).
    /// </summary>
    /// <param name="denyNetwork">
    /// When true, the profile grants neither <c>network*</c> nor <c>system-socket</c> (no socket of any
    /// domain can be created) and ends with explicit denials of both. Off for every model-directed tool,
    /// whose jail stays filesystem-only.
    /// </param>
    /// <param name="denyProcessFork">
    /// When true, the profile ends with an explicit denial of <c>process-fork</c>, so the child can create no other
    /// process by fork, vfork or posix_spawn. Exec stays allowed: the launch chain inside the jail (sandbox-exec, the
    /// limit prelude, the target) only ever execs. Off for every model-directed tool, whose children fork their own tools.
    /// </param>
    /// <param name="confineMetadataToRoots">
    /// When true, the profile leaves out the grants that list every directory and read every file's metadata, so nothing
    /// outside the roots can be listed or stat'ed. What it adds back is only what path resolution and process start need:
    /// the metadata of each root's ancestors (each exact directory, never what is beneath it), the root directory's own
    /// entry (<see cref="RootDirectoryEntryRule"/>) and the <c>/bin/sh</c> selector link. The profile also ends with
    /// <see cref="ObjectIdPathDenialRule"/>, since the two calls that turn an object id back into a path would otherwise
    /// name every file on the volume. Off for every model-directed tool, whose children list and stat the workspace's
    /// surroundings as any build does.
    /// </param>
    /// <param name="denyFileWrites">
    /// When true, the profile grants no write of any kind to any root, <paramref name="readWriteRoots"/> and
    /// <paramref name="invocationTempDir"/> included: no file write, no socket creation, no clone and no link. The temp
    /// directory stays readable, and the one write left is <see cref="DevNullWriteRule"/>. Off for every model-directed
    /// tool, whose children write their workspace and temp directory.
    /// </param>
    internal static string Build(
        IReadOnlyList<string> readWriteRoots,
        IReadOnlyList<string> readExecuteRoots,
        string invocationTempDir,
        IReadOnlyList<string>? readOnlyRoots = null,
        bool denyNetwork = false,
        bool denyProcessFork = false,
        bool confineMetadataToRoots = false,
        bool denyFileWrites = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationTempDir);

        readOnlyRoots ??= [];

        RejectWholeVolumeRoots(readWriteRoots, nameof(readWriteRoots));

        RejectWholeVolumeRoots(readExecuteRoots, nameof(readExecuteRoots));

        RejectWholeVolumeRoots(readOnlyRoots, nameof(readOnlyRoots));

        RejectWholeVolumeRoots([invocationTempDir], nameof(invocationTempDir));

        StringBuilder sb = new();

        sb.AppendLine("(version 1)");

        sb.AppendLine("(deny default)");

        // Non-file runtime operations. File content and mutation remain explicitly rooted below.
        sb.AppendLine("(allow process*)");
        // Signals are scoped to the jail: a child may signal itself and the processes it shares this
        // sandbox with (a build tool stopping its worker), never the operator's own processes outside it.
        // The runner terminates the tree from outside the sandbox, which no sandbox rule governs.
        sb.AppendLine("(allow signal (target self))");
        sb.AppendLine("(allow signal (target same-sandbox))");
        sb.AppendLine("(allow sysctl*)");

        if (!denyNetwork)
        {
            sb.AppendLine("(allow system-socket)");
        }

        sb.AppendLine("(allow system-fsctl)");

        if (!denyNetwork)
        {
            sb.AppendLine("(allow network*)");
        }

        sb.AppendLine("(allow iokit*)");
        sb.AppendLine("(allow user-preference*)");
        sb.AppendLine("(allow file-map-executable)");

        if (!confineMetadataToRoots)
        {
            sb.AppendLine("(allow file-read-metadata)");
        }

        sb.AppendLine("(allow file-ioctl)");

        if (!confineMetadataToRoots)
        {
            sb.AppendLine("(allow file-read* (vnode-type DIRECTORY))");
        }

        // The .NET host requires only identity lookup and the Darwin notification center. Keep
        // every other Mach service and generic IPC operation deny-by-default; Roslyn build hosts
        // use Unix-domain sockets rooted beneath the invocation TMPDIR, granted explicitly below.
        sb.AppendLine("(allow mach-lookup");
        sb.AppendLine("  (global-name \"com.apple.system.notification_center\")");
        sb.AppendLine("  (global-name \"com.apple.system.opendirectoryd.libinfo\"))");
        sb.AppendLine("(deny appleevent-send)");
        sb.AppendLine("(deny process-exec");

        foreach (string utility in LaunchBrokerUtilities)
        {
            sb.Append("  (literal \"")
                .Append(EscapeSeatbeltString(utility))
                .AppendLine("\")");
        }

        sb.AppendLine(")");

        AppendFileAllow(
            sb,
            "file-read*",
            MergeUnique(
                SystemReadExecuteRoots,
                readExecuteRoots,
                readOnlyRoots,
                readWriteRoots,
                [invocationTempDir]));

        if (confineMetadataToRoots)
        {
            sb.AppendLine(RootDirectoryEntryRule);

            AppendMetadataAllow(
                sb,
                MergeUnique(
                    AncestorsOf(MergeUnique(
                        SystemReadExecuteRoots,
                        readExecuteRoots,
                        readOnlyRoots,
                        readWriteRoots,
                        [invocationTempDir])),
                    ConfinedLaunchChainLinks));
        }

        if (denyFileWrites)
        {
            // No root, not even the temp directory, is writable: only the device that keeps nothing.
            sb.AppendLine(DevNullWriteRule);
        }
        else
        {
            AppendFileAllow(
                sb,
                "file-write*",
                MergeUnique(
                    readWriteRoots,
                    [invocationTempDir],
                    DevWriteRoots));

            // Roslyn's dotnet-format BuildHost creates a Unix-domain socket beneath TMPDIR. Socket
            // vnode creation remains confined to that canonical per-invocation IPC root.
            sb.Append("(allow file-write-create (vnode-type SOCKET) (subpath \"")
                .Append(EscapeSeatbeltString(invocationTempDir))
                .AppendLine("\"))");

            AppendFileAllow(
                sb,
                "file-clone",
                MergeUnique(
                    readWriteRoots,
                    [invocationTempDir]));

            AppendFileAllow(
                sb,
                "file-link",
                MergeUnique(
                    readWriteRoots,
                    [invocationTempDir]));
        }

        if (denyNetwork)
        {
            // Deny-by-default already refuses both; stating them last means no allow form can follow.
            sb.AppendLine("(deny network*)");

            sb.AppendLine("(deny system-socket)");
        }

        if (denyProcessFork)
        {
            // Stated last, after the broad (allow process*), so no earlier or later allow form can give fork back.
            sb.AppendLine("(deny process-fork)");
        }

        if (confineMetadataToRoots)
        {
            // Stated last, so no allow form can give the call back. The confinement is meant to keep every name outside the
            // roots from the child, and these two calls would hand them over by object id.
            sb.AppendLine(ObjectIdPathDenialRule);
        }

        string profile = sb.ToString();

        AssertNoWholeVolumeFootguns(profile);
        AssertNoLaunchBrokerFootguns(profile);

        return profile;
    }

    /// <summary>
    /// Static footgun check used by production build and unit tests.
    /// </summary>
    internal static void AssertNoWholeVolumeFootguns(string profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (NamesTheWholeVolume(profile))
        {
            throw new InvalidOperationException(
                "macOS Seatbelt profile must not grant whole-volume file access via (subpath \"/\") or (literal \"/\").");
        }

        // Broad system temp grants are forbidden; only per-invocation TMPDIR may be writable.
        if (ContainsBroadTempWriteGrant(profile))
        {
            throw new InvalidOperationException(
                "macOS Seatbelt profile must not grant broad /tmp, /private/tmp, or /var/tmp write access.");
        }
    }

    /// <summary>
    /// Rejects profile changes that could broker execution through LaunchServices or AppleEvents.
    /// </summary>
    internal static void AssertNoLaunchBrokerFootguns(string profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.Contains("(allow mach*)", StringComparison.Ordinal)
            || profile.Contains("(allow ipc*)", StringComparison.Ordinal)
            || !profile.Contains("(deny appleevent-send)", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "macOS Seatbelt profile must keep broad Mach, IPC, and AppleEvents access denied.");
        }

        string? processDeny = ExtractForm(
            profile,
            "(deny process-exec");

        foreach (string utility in LaunchBrokerUtilities)
        {
            if (processDeny is null
                || !processDeny.Contains(
                    $"(literal \"{utility}\")",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"macOS Seatbelt profile must deny launch broker utility '{utility}'.");
            }
        }
    }

    private static string? ExtractForm(
        string profile,
        string prefix)
    {
        int start = profile.IndexOf(
            prefix,
            StringComparison.Ordinal);

        if (start < 0)
        {
            return null;
        }

        int depth = 0;

        for (int index = start;
             index < profile.Length;
             index++)
        {
            if (profile[index] == '(')
            {
                depth++;
            }
            else if (profile[index] == ')')
            {
                depth--;

                if (depth == 0)
                {
                    return profile[start..(index + 1)];
                }
            }
        }

        return null;
    }

    /// <summary>True when profile text looks like it grants RW on whole-volume or broad temp.</summary>
    internal static bool ContainsWholeVolumeOrBroadTempFootgun(string profile)
    {
        if (string.IsNullOrEmpty(profile))
        {
            return true;
        }

        if (NamesTheWholeVolume(profile))
        {
            return true;
        }

        return ContainsBroadTempWriteGrant(profile);
    }

    /// <summary>
    /// Whether any rule names the root as a subpath, or as a literal outside the exact <see cref="RootDirectoryEntryRule"/>.
    /// </summary>
    private static bool NamesTheWholeVolume(string profile)
    {
        string beyondTheRootEntry = profile.Replace(RootDirectoryEntryRule, string.Empty, StringComparison.Ordinal);

        return beyondTheRootEntry.Contains("(subpath \"/\")", StringComparison.Ordinal)
            || beyondTheRootEntry.Contains("(literal \"/\")", StringComparison.Ordinal);
    }

    /// <summary>
    /// Every proper ancestor of each root and of its <c>/private</c> twin, the root directory excluded: the directories path
    /// resolution stats on its way to a root.
    /// </summary>
    private static IEnumerable<string> AncestorsOf(IEnumerable<string> roots)
    {
        foreach (string root in roots)
        {
            foreach (string variant in MacOsPathVariants(root.Trim()))
            {
                for (int slash = variant.LastIndexOf('/'); slash > 0; slash = variant.LastIndexOf('/', slash - 1))
                {
                    yield return variant[..slash];
                }
            }
        }
    }

    /// <summary>Grants the metadata of exactly <paramref name="paths"/>, each as a literal, never what is beneath it.</summary>
    private static void AppendMetadataAllow(StringBuilder sb, IEnumerable<string> paths)
    {
        sb.Append("(allow file-read-metadata");

        foreach (string path in paths)
        {
            sb.AppendLine();

            sb.Append("  (literal \"").Append(EscapeSeatbeltString(path)).Append("\")");
        }

        sb.AppendLine(")");
    }

    private static bool ContainsBroadTempWriteGrant(string profile)
    {
        // Only flag when file-write* block includes these exact system temps (not a longer subpath).
        ReadOnlySpan<string> forbidden = ["(subpath \"/tmp\")", "(subpath \"/private/tmp\")", "(subpath \"/var/tmp\")", "(subpath \"/private/var/tmp\")"];

        int writeIdx = profile.IndexOf("(allow file-write*", StringComparison.Ordinal);

        if (writeIdx < 0)
        {
            return false;
        }

        string writeBlock = profile[writeIdx..];

        // file-write* allow can span multiple lines until the closing ")" of the allow form.
        int depth = 0;

        var span = writeBlock.AsSpan();

        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == '(')
            {
                depth++;
            }
            else if (span[i] == ')')
            {
                depth--;

                if (depth == 0)
                {
                    writeBlock = writeBlock[..(i + 1)];

                    break;
                }
            }
        }

        foreach (string needle in forbidden)
        {
            if (writeBlock.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// System runtime roots: read + execute only. Never include "/" or broad home.
    /// </summary>
    private static readonly string[] SystemReadExecuteRoots =
    [
        "/usr",
        "/System",
        "/bin",
        "/sbin",
        "/private/preboot",
        "/private/var/db",
        "/private/var/run",
        "/private/etc",
        "/etc",
        "/Library",
        "/dev",
        "/AppleInternal",
        // dyld / shared cache live under these; content read of user data is still denied.
        "/System/Volumes/Preboot",
    ];

    /// <summary>Device nodes only — not /tmp.</summary>
    private static readonly string[] DevWriteRoots =
    [
        "/dev",
    ];

    private static readonly string[] LaunchBrokerUtilities =
    [
        "/usr/bin/open",
        "/usr/bin/osascript",
        "/usr/bin/automator",
        "/usr/bin/shortcuts",
        "/bin/launchctl",
    ];

    private static void RejectWholeVolumeRoots(IReadOnlyList<string> roots, string paramName)
    {
        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            foreach (char c in root)
            {
                if (char.IsControl(c))
                {
                    throw new ArgumentException(
                        "Sandbox root paths must not contain control characters or newlines.",
                        paramName);
                }
            }

            string trimmed = root.Trim();

            if (trimmed is "/" or "\\"
                || string.Equals(trimmed, Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Sandbox roots must not grant whole-volume access (\"/\").",
                    paramName);
            }
        }
    }

    private static void AppendFileAllow(StringBuilder sb, string operation, IEnumerable<string> roots)
    {
        List<string> list = [];

        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            string trimmed = root.Trim();

            if (trimmed is "/" or "\\")
            {
                throw new InvalidOperationException(
                    "Refusing to emit whole-volume Seatbelt allow for \"/\".");
            }

            foreach (string variant in MacOsPathVariants(trimmed))
            {
                if (variant is "/" or "\\")
                {
                    continue;
                }

                if (seen.Add(variant))
                {
                    list.Add(variant);
                }
            }
        }

        if (list.Count == 0)
        {
            return;
        }

        sb.Append("(allow ").Append(operation);

        foreach (string root in list)
        {
            sb.AppendLine();

            sb.Append("  (subpath \"").Append(EscapeSeatbeltString(root)).Append("\")");
        }

        sb.AppendLine(")");
    }

    /// <summary>
    /// macOS exposes the same directory as both <c>/var/...</c> and <c>/private/var/...</c>.
    /// Allow-lists must include both variants when applicable.
    /// </summary>
    private static IEnumerable<string> MacOsPathVariants(string path)
    {
        yield return path;

        const string privatePrefix = "/private";

        if (path.StartsWith("/var/", StringComparison.Ordinal)
            || string.Equals(path, "/var", StringComparison.Ordinal)
            || path.StartsWith("/etc", StringComparison.Ordinal))
        {
            yield return privatePrefix + path;
        }
        else if (path.StartsWith(privatePrefix + "/", StringComparison.Ordinal))
        {
            string without = path[privatePrefix.Length..];

            if (without.StartsWith("/var/", StringComparison.Ordinal)
                || without.StartsWith("/etc", StringComparison.Ordinal)
                || string.Equals(without, "/var", StringComparison.Ordinal))
            {
                yield return without;
            }
        }
    }

    private static IEnumerable<string> MergeUnique(params IEnumerable<string>[] groups)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (IEnumerable<string> group in groups)
        {
            foreach (string root in group)
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                if (seen.Add(root))
                {
                    yield return root;
                }
            }
        }
    }

    private static string EscapeSeatbeltString(string value)
    {
        foreach (char c in value)
        {
            if (char.IsControl(c))
            {
                throw new InvalidOperationException(
                    "Seatbelt profile paths must not contain control characters or newlines.");
            }
        }

        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}
