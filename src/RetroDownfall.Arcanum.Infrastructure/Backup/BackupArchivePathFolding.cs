namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>
/// The one definition of when two archive entry paths cannot both become files on a destination
/// volume, shared by the planner that refuses to write such a set and the codec that refuses to lay
/// one down.
/// </summary>
/// <remarks>
/// The protocol's own identity for an entry is its exact ordinal bytes. This is the second
/// question, the one a destination answers: a case-insensitive volume folds <c>A.md</c> and
/// <c>a.md</c> into one file, and Windows additionally drops trailing dots and spaces from every
/// name, so <c>a.md.</c> and <c>a.md</c> are one file there. Whether the collision materialises is a
/// property of the machine an archive lands on, and the format exists to be carried between
/// machines, so both sides apply the strictest fold on every platform.
/// </remarks>
internal static class BackupArchivePathFolding
{
    /// <summary>
    /// The comparer under which two <see cref="CollisionKey"/> values name the same destination file.
    /// </summary>
    public static StringComparer KeyComparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// The path with the trailing dots and spaces of every segment removed; compare keys with
    /// <see cref="KeyComparer"/>. A segment made only of dots and spaces keeps its spelling, so it
    /// never collapses into an empty segment.
    /// </summary>
    public static string CollisionKey(string archivePath)
    {
        ArgumentNullException.ThrowIfNull(archivePath);

        string[] segments = archivePath.Split('/');

        for (int index = 0; index < segments.Length; index++)
        {
            string trimmed = segments[index].TrimEnd('.', ' ');

            if (trimmed.Length > 0)
            {
                segments[index] = trimmed;
            }
        }

        return string.Join('/', segments);
    }
}
