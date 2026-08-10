using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// One undo step: the security descriptor a directory carried before the broker granted the per-run
/// AppContainer SID access to it.
/// </summary>
internal readonly record struct WindowsAppContainerGrantRecord(
    string Path,
    byte[] Descriptor);

/// <summary>
/// Everything a killed broker left behind for the host to undo.
/// </summary>
internal sealed record WindowsAppContainerRestorePlan(
    string? ProfileName,
    IReadOnlyList<WindowsAppContainerGrantRecord> Grants,
    int UnreadableRecords);

/// <summary>
/// Kill-proof undo log for the Windows AppContainer jail. The broker restores the directory ACLs it
/// granted and deletes the per-run profile in a <c>finally</c>, but the host kills it with
/// TerminateProcess on timeout, cancellation, or a Job Object kill, and managed finally blocks do not
/// run then — the original descriptors would exist only in the dead broker's memory, leaving one
/// orphaned inheritable ACE on the workspace root per killed run. The broker therefore appends each
/// undo step to this owner-only file, which the host created and still owns, <b>before</b> performing
/// the matching mutation, and clears it once it has undone everything itself. Whatever is still in
/// the log once the child is gone is residue the host replays.
/// </summary>
internal static class WindowsAppContainerRestoreJournal
{
    private const char FieldSeparator = ' ';
    private const char RecordTerminator = '\n';
    private const string ProfileTag = "P";
    private const string GrantTag = "A";

    /// <summary>
    /// Records the profile that must be deleted. Called before the profile is created, so a kill in
    /// the creation window cannot strand it.
    /// </summary>
    internal static void RecordProfile(string journalPath, string profileName) =>
        Append(journalPath, ProfileTag + FieldSeparator + Encode(profileName));

    /// <summary>
    /// Records the descriptor <paramref name="path"/> carries right now. Throws when the record
    /// cannot be made durable — the caller must then refuse to mutate the ACL at all.
    /// </summary>
    internal static void RecordGrant(string journalPath, string path, byte[] descriptor) =>
        Append(
            journalPath,
            GrantTag + FieldSeparator + Encode(path) + FieldSeparator + Convert.ToBase64String(descriptor));

    /// <summary>Drops every undo step, after the broker has performed them all itself.</summary>
    internal static void Clear(string journalPath) => File.WriteAllBytes(journalPath, []);

    internal static WindowsAppContainerRestorePlan Read(string journalPath)
    {
        List<WindowsAppContainerGrantRecord> grants = [];
        string? profileName = null;
        int unreadable = 0;

        if (string.IsNullOrWhiteSpace(journalPath) || !File.Exists(journalPath))
        {
            return new WindowsAppContainerRestorePlan(profileName, grants, unreadable);
        }

        string text;
        try
        {
            text = File.ReadAllText(journalPath, Encoding.UTF8);
        }
        catch (Exception)
        {
            return new WindowsAppContainerRestorePlan(profileName, grants, UnreadableRecords: 1);
        }

        // A kill can land mid-append, so a trailing fragment is not a record: the mutation it would
        // describe had not been performed yet. Only terminated records are replayed.
        int lastTerminator = text.LastIndexOf(RecordTerminator);
        if (lastTerminator < 0)
        {
            return new WindowsAppContainerRestorePlan(profileName, grants, unreadable);
        }

        foreach (string line in text[..lastTerminator].Split(RecordTerminator))
        {
            if (line.Length == 0)
            {
                continue;
            }

            string[] fields = line.Split(FieldSeparator);
            if (fields.Length == 2 && fields[0] == ProfileTag && TryDecode(fields[1], out string profile))
            {
                profileName = profile;
                continue;
            }

            if (fields.Length == 3
                && fields[0] == GrantTag
                && TryDecode(fields[1], out string path)
                && TryDecodeBytes(fields[2], out byte[] descriptor))
            {
                grants.Add(new WindowsAppContainerGrantRecord(path, descriptor));
                continue;
            }

            unreadable++;
        }

        return new WindowsAppContainerRestorePlan(profileName, grants, unreadable);
    }

    /// <summary>
    /// Undoes every recorded step, newest first so nested roots unwind in grant order, and clears the
    /// log only when nothing was left behind. Returns <c>false</c> when any step failed or any record
    /// was unreadable, so the caller can report residue rather than claim a clean teardown.
    /// </summary>
    internal static bool Replay(
        string journalPath,
        Func<string, byte[], bool> restoreDescriptor,
        Func<string, bool> deleteProfile)
    {
        ArgumentNullException.ThrowIfNull(restoreDescriptor);
        ArgumentNullException.ThrowIfNull(deleteProfile);

        WindowsAppContainerRestorePlan plan = Read(journalPath);
        bool complete = plan.UnreadableRecords == 0;

        for (int index = plan.Grants.Count - 1; index >= 0; index--)
        {
            WindowsAppContainerGrantRecord grant = plan.Grants[index];
            try
            {
                complete &= restoreDescriptor(grant.Path, grant.Descriptor);
            }
            catch (Exception)
            {
                complete = false;
            }
        }

        if (plan.ProfileName is not null)
        {
            try
            {
                complete &= deleteProfile(plan.ProfileName);
            }
            catch (Exception)
            {
                complete = false;
            }
        }

        if (complete)
        {
            try
            {
                Clear(journalPath);
            }
            catch (Exception)
            {
                complete = false;
            }
        }

        return complete;
    }

    private static void Append(string journalPath, string record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);

        byte[] bytes = Encoding.UTF8.GetBytes(record + RecordTerminator);
        using FileStream stream = new(
            journalPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.None);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static bool TryDecode(string value, out string decoded)
    {
        if (TryDecodeBytes(value, out byte[] bytes))
        {
            decoded = Encoding.UTF8.GetString(bytes);
            return decoded.Length > 0;
        }

        decoded = string.Empty;
        return false;
    }

    private static bool TryDecodeBytes(string value, out byte[] decoded)
    {
        decoded = [];
        if (value.Length == 0)
        {
            return false;
        }

        byte[] buffer = new byte[((value.Length / 4) + 1) * 3];
        if (!Convert.TryFromBase64String(value, buffer, out int written))
        {
            return false;
        }

        decoded = buffer[..written];
        return true;
    }
}
