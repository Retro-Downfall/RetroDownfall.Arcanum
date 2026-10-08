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
///
/// <para>The same fold answers a third question: a file and a directory cannot share a name either, so
/// an entry named <c>a</c> and an entry named <c>A/x</c> cannot both be laid down. Two files that
/// merely sit in directories whose names fold together (<c>A/x</c> and <c>a/y</c>) do not collide,
/// because a destination that folds the directories into one still holds two different files.</para>
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

    /// <summary>
    /// The indexes of every path that cannot coexist with another on one destination: every member of
    /// a group of paths sharing a <see cref="CollisionKey"/>, and both a file whose key is the key of
    /// another path's directory and every path beneath that directory.
    /// </summary>
    /// <remarks>
    /// One pass computes each key once and indexes the first path that owns it, so a second owner is
    /// found on insertion; a second pass looks up each path's own directory prefixes by span, so the
    /// check allocates no prefix strings however deep the tree is. With no collision the result is an
    /// empty set and nothing else outlives the call but the keys.
    /// </remarks>
    public static HashSet<int> FindCollidingIndexes(IReadOnlyList<string> archivePaths)
    {
        ArgumentNullException.ThrowIfNull(archivePaths);

        string[] keys = new string[archivePaths.Count];

        Dictionary<string, int> firstOwner = new(archivePaths.Count, KeyComparer);

        HashSet<int> colliding = [];

        for (int index = 0; index < keys.Length; index++)
        {
            string key = CollisionKey(archivePaths[index]);

            keys[index] = key;

            if (!firstOwner.TryAdd(key, index))
            {
                colliding.Add(index);

                colliding.Add(firstOwner[key]);
            }
        }

        Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> owners =
            firstOwner.GetAlternateLookup<ReadOnlySpan<char>>();

        for (int index = 0; index < keys.Length; index++)
        {
            ReadOnlySpan<char> key = keys[index];

            int end = key.IndexOf('/');

            while (end >= 0)
            {
                if (owners.TryGetValue(key[..end], out int fileOwner))
                {
                    colliding.Add(index);

                    colliding.Add(fileOwner);
                }

                int next = key[(end + 1)..].IndexOf('/');

                end = next < 0
                    ? -1
                    : end + 1 + next;
            }
        }

        return colliding;
    }
}
