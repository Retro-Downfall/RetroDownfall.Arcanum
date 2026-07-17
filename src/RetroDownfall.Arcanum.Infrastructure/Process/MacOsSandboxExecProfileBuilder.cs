using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// Builds a Seatbelt profile for <c>sandbox-exec</c> (deprecated Apple tool; MVP only).
/// </summary>
/// <remarks>
/// Uses deny-default with explicit system + user-data allow rules. Network is explicitly allowed —
/// this MVP is filesystem-only and does not isolate network use by child binaries.
/// </remarks>
internal static class MacOsSandboxExecProfileBuilder
{

    internal static string Build(
        IReadOnlyList<string> readWriteRoots,
        IReadOnlyList<string> readExecuteRoots)
    {

        StringBuilder sb = new();

        sb.AppendLine("(version 1)");

        sb.AppendLine("(deny default)");

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

        sb.AppendLine("(allow file-read-metadata)");

        sb.AppendLine("(allow file-ioctl)");

        // Allow directory traversal / getcwd across the filesystem without granting file-content
        // reads outside the explicit roots below (Seatbelt vnode-type DIRECTORY filter).
        sb.AppendLine("(allow file-read* (vnode-type DIRECTORY))");

        // System runtime: read + execute, no write.
        AppendFileAllow(sb, "file-read*", MergeUnique(SystemReadExecuteRoots, readExecuteRoots, readWriteRoots));

        AppendFileAllow(sb, "file-write*", MergeUnique(readWriteRoots, DevAndTempWriteRoots));

        return sb.ToString();

    }

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
        "/private/tmp",
        "/tmp",
        "/",
    ];

    private static readonly string[] DevAndTempWriteRoots =
    [
        "/dev",
        "/private/tmp",
        "/tmp",
        "/private/var/tmp",
    ];

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

            foreach (string variant in MacOsPathVariants(root))
            {

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

            if (root == "/")
            {

                sb.AppendLine();

                sb.Append("  (literal \"/\")");

                continue;

            }

            sb.AppendLine();

            sb.Append("  (subpath \"").Append(EscapeSeatbeltString(root)).Append("\")");

        }

        sb.AppendLine(")");

    }

    /// <summary>
    /// macOS exposes the same directory as both <c>/var/...</c> and <c>/private/var/...</c>.
    /// <see cref="Path.GetFullPath"/> prefers the short form while Seatbelt matches the kernel path,
    /// so allow-lists must include both variants.
    /// </summary>
    private static IEnumerable<string> MacOsPathVariants(string path)
    {

        yield return path;

        const string privatePrefix = "/private";

        if (path.StartsWith("/var/", StringComparison.Ordinal)
            || string.Equals(path, "/var", StringComparison.Ordinal)
            || path.StartsWith("/tmp", StringComparison.Ordinal)
            || path.StartsWith("/etc", StringComparison.Ordinal))
        {

            yield return privatePrefix + path;

        }
        else if (path.StartsWith(privatePrefix + "/", StringComparison.Ordinal))
        {

            string without = path[privatePrefix.Length..];

            if (without.StartsWith("/var/", StringComparison.Ordinal)
                || without.StartsWith("/tmp", StringComparison.Ordinal)
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

    private static string EscapeSeatbeltString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

}
