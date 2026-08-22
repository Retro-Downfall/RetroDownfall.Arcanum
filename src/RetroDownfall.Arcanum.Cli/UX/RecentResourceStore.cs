using System.Text;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Coordination;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Cli.UX;

/// <summary>
/// Best-effort local ordering hints. Entries never participate in resolution authority.
/// </summary>
public sealed class RecentResourceStore : IRecentResourceStore
{
    private const int MaxEntries = 50;
    private readonly object _gate = new();
    private readonly string _path;
    private readonly IArcanumClientMutationBoundary _mutationBoundary;

    public RecentResourceStore(IArcanumClientMutationBoundary mutationBoundary)
        : this(
            Path.Combine(ArcanumPaths.GrimoireDirectory, "recent-resources.txt"),
            mutationBoundary)
    {
    }

    internal RecentResourceStore(
        string path,
        IArcanumClientMutationBoundary mutationBoundary)
    {
        _path = path;

        _mutationBoundary = mutationBoundary;
    }

    public IReadOnlyList<string> GetRecentIds(string resourceKind)
    {
        lock (_gate)
        {
            return ReadEntries()
                .Where(entry => string.Equals(entry.Kind, resourceKind, StringComparison.Ordinal))
                .OrderByDescending(entry => entry.SelectedAt)
                .Select(entry => entry.Id)
                .ToArray();
        }
    }

    public async Task RememberAsync(
        string resourceKind,
        string id,
        Func<CancellationToken, Task<Result<bool>>> revalidateAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revalidateAsync);

        if (string.IsNullOrWhiteSpace(resourceKind) || string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        _ = await _mutationBoundary
            .RunAsync(
                async token =>
                {

                    Result<bool> revalidated = await revalidateAsync(token)
                        .ConfigureAwait(false);

                    if (revalidated.IsFailure || !revalidated.Value)
                    {

                        return false;

                    }

                    lock (_gate)
                    {

                        RememberUnderExclusive(resourceKind, id);

                    }

                    return true;

                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void RememberUnderExclusive(string resourceKind, string id)
    {

        try
        {
            List<RecentEntry> entries = ReadEntries()
                .Where(entry => !(string.Equals(entry.Kind, resourceKind, StringComparison.Ordinal)
                    && string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            entries.Add(new RecentEntry(resourceKind, id, DateTimeOffset.UtcNow));
            entries = entries.OrderByDescending(entry => entry.SelectedAt).Take(MaxEntries).ToList();

            string? directory = Path.GetDirectoryName(_path);
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(directory);
            string temp = _path + ".tmp." + Guid.NewGuid().ToString("N");

            try
            {

                using (FileStream stream = new(
                    temp,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                using (StreamWriter writer = new(stream, Encoding.UTF8))
                {

                    SecureFilePermissions.ApplyOwnerOnlyFile(temp);

                    foreach (RecentEntry entry in entries)
                    {

                        writer.WriteLine(Serialize(entry));

                    }

                    writer.Flush();

                    stream.Flush(flushToDisk: true);

                }

                File.Move(temp, _path, overwrite: true);

                SecureFilePermissions.ApplyOwnerOnlyFile(_path);

            }
            finally
            {

                try
                {

                    File.Delete(temp);

                }
                catch (Exception cleanupException) when (
                    cleanupException is IOException or UnauthorizedAccessException)
                {

                }

            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Recency is an optional UX hint. Selection must continue when it cannot persist.
        }
    }

    private List<RecentEntry> ReadEntries()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            List<RecentEntry> entries = [];
            foreach (string line in File.ReadLines(_path).Take(MaxEntries * 2))
            {
                string[] parts = line.Split('\t');
                if (parts.Length != 3
                    || !DateTimeOffset.TryParse(parts[0], out DateTimeOffset selectedAt)
                    || !TryDecode(parts[1], out string kind)
                    || !TryDecode(parts[2], out string id))
                {
                    continue;
                }

                entries.Add(new RecentEntry(kind, id, selectedAt));
            }

            return entries;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private static string Serialize(RecentEntry entry) =>
        $"{entry.SelectedAt:O}\t{Encode(entry.Kind)}\t{Encode(entry.Id)}";

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static bool TryDecode(string value, out string decoded)
    {
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value));
            return true;
        }
        catch (FormatException)
        {
            decoded = string.Empty;
            return false;
        }
    }

    private sealed record RecentEntry(string Kind, string Id, DateTimeOffset SelectedAt);
}
