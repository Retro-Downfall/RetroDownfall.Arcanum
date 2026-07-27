using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// Builds a Seatbelt profile for <c>sandbox-exec</c> (deprecated Apple tool; MVP for Apple Silicon beta).
/// </summary>
/// <remarks>
/// <para>
/// Critical invariant: no profile rule may grant whole-volume file-content read
/// (no <c>(subpath "/")</c>, no <c>(literal "/")</c> for file-read*).
/// </para>
/// <para>
/// Access classes:
/// <list type="bullet">
/// <item>Workspace / Sanctum AllowedPaths — read + write</item>
/// <item>Spell script roots — read + execute (no write unless also a RW root)</item>
/// <item>System runtime — read + execute, no write</item>
/// <item>Per-invocation owner-only TMPDIR — read + write (no broad /tmp)</item>
/// </list>
/// Network is explicitly allowed — filesystem-only MVP; not network isolation.
/// </para>
/// </remarks>
internal static class MacOsSandboxExecProfileBuilder
{

    /// <summary>
    /// Builds a deny-default Seatbelt profile. Throws if any root is unsafe (control chars, whole-volume).
    /// </summary>
    internal static string Build(
        IReadOnlyList<string> readWriteRoots,
        IReadOnlyList<string> readExecuteRoots,
        string invocationTempDir,
        IReadOnlyList<string>? readOnlyRoots = null)
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
        sb.AppendLine("(allow signal)");
        sb.AppendLine("(allow sysctl*)");
        sb.AppendLine("(allow system-socket)");
        sb.AppendLine("(allow system-fsctl)");
        sb.AppendLine("(allow network*)");
        sb.AppendLine("(allow iokit*)");
        sb.AppendLine("(allow user-preference*)");
        sb.AppendLine("(allow file-map-executable)");
        sb.AppendLine("(allow file-read-metadata)");
        sb.AppendLine("(allow file-ioctl)");
        sb.AppendLine("(allow file-read* (vnode-type DIRECTORY))");
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

        if (profile.Contains("(subpath \"/\")", StringComparison.Ordinal)
            || profile.Contains("(literal \"/\")", StringComparison.Ordinal))
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

        if (profile.Contains("(subpath \"/\")", StringComparison.Ordinal)
            || profile.Contains("(literal \"/\")", StringComparison.Ordinal))
        {

            return true;

        }

        return ContainsBroadTempWriteGrant(profile);

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
