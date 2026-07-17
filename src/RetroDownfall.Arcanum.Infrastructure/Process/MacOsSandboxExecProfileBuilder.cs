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
        string invocationTempDir)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(invocationTempDir);

        RejectWholeVolumeRoots(readWriteRoots, nameof(readWriteRoots));

        RejectWholeVolumeRoots(readExecuteRoots, nameof(readExecuteRoots));

        RejectWholeVolumeRoots([invocationTempDir], nameof(invocationTempDir));

        StringBuilder sb = new();

        sb.AppendLine("(version 1)");

        sb.AppendLine("(deny default)");

        // Minimal non-file startup allows — never "fix" getcwd/dyld by adding "/".
        sb.AppendLine("(allow process*)");

        sb.AppendLine("(allow signal)");

        sb.AppendLine("(allow sysctl*)");

        sb.AppendLine("(allow mach*)");

        sb.AppendLine("(allow system-socket)");

        sb.AppendLine("(allow network*)");

        sb.AppendLine("(allow iokit*)");

        sb.AppendLine("(allow ipc*)");

        sb.AppendLine("(allow user-preference*)");

        sb.AppendLine("(allow file-map-executable)");

        // Metadata-only for path identity.
        sb.AppendLine("(allow file-read-metadata)");

        sb.AppendLine("(allow file-ioctl)");

        // Directory walk / getcwd across the filesystem without granting regular-file content
        // reads outside the explicit roots below (Seatbelt vnode-type DIRECTORY filter).
        // Required for dyld/path resolution; proven necessary — do not replace with "/".
        sb.AppendLine("(allow file-read* (vnode-type DIRECTORY))");

        // System runtime + spell/script/interpreter roots: read + execute, no write.
        AppendFileAllow(
            sb,
            "file-read*",
            MergeUnique(SystemReadExecuteRoots, readExecuteRoots, readWriteRoots, [invocationTempDir]));

        // Writable: workspace / AllowedPaths / invocation TMPDIR only — never broad /tmp.
        AppendFileAllow(
            sb,
            "file-write*",
            MergeUnique(readWriteRoots, [invocationTempDir], DevWriteRoots));

        string profile = sb.ToString();

        AssertNoWholeVolumeFootguns(profile);

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
