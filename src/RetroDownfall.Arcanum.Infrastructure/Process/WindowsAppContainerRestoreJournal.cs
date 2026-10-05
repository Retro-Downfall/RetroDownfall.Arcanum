using System.Globalization;
using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// One undo step: a directory the broker granted the per-run AppContainer SID access to, and that SID.
/// Undoing it removes this SID's explicit ACEs from the directory's current DACL — never a snapshot of
/// the whole DACL, which would also erase or resurrect other runs' grants on a shared root.
/// </summary>
internal readonly record struct WindowsAppContainerGrantRecord(
    string Path,
    string Sid);

/// <summary>
/// Everything a killed broker left behind for the host to undo.
/// </summary>
internal sealed record WindowsAppContainerRestorePlan(
    string? ProfileName,
    IReadOnlyList<WindowsAppContainerGrantRecord> Grants,
    int UnreadableRecords);

/// <summary>
/// Kill-proof undo log for the Windows AppContainer jail. The broker removes the ACEs it granted and
/// deletes the per-run profile in a <c>finally</c>, but the host kills it with TerminateProcess on
/// timeout, cancellation, or a Job Object kill, and managed finally blocks do not run then — the grant
/// would be remembered only by the dead broker, leaving one orphaned inheritable ACE on the workspace
/// root per killed run. The broker therefore appends each undo step (the root and the per-run SID) to
/// this owner-only file, which the host created and still owns, <b>before</b> performing the matching
/// mutation, and clears it once it has undone everything itself. Whatever is still in the log once the
/// child is gone is residue the host replays.
/// </summary>
internal static class WindowsAppContainerRestoreJournal
{
    private const char FieldSeparator = ' ';
    private const char RecordTerminator = '\n';
    private const string ProfileTag = "P";

    /// <summary>
    /// Grant records carry the per-run SID. The retired <c>A</c> tag carried a whole security
    /// descriptor; such a record is reported as unreadable residue rather than reapplied.
    /// </summary>
    private const string GrantTag = "G";

    /// <summary>
    /// Every per-run profile SID starts here: SECURITY_APP_PACKAGE_AUTHORITY (15) and
    /// SECURITY_APP_PACKAGE_BASE_RID (2).
    /// </summary>
    private const string AppContainerSidPrefix = "S-1-15-2-";

    /// <summary>
    /// SECURITY_APP_PACKAGE_RID_COUNT is 8: the base RID plus the seven sub-authorities
    /// <c>DeriveAppContainerSidFromAppContainerName</c> hashes from the profile name. The built-in
    /// package SIDs (ALL APPLICATION PACKAGES <c>S-1-15-2-1</c>, ALL RESTRICTED APPLICATION PACKAGES
    /// <c>S-1-15-2-2</c>) carry only one.
    /// </summary>
    private const int AppContainerSidSubAuthorityCount = 7;

    /// <summary>The prefix plus seven ten-digit sub-authorities and their six separators.</summary>
    private const int MaxAppContainerSidLength = 85;

    /// <summary>
    /// Records the profile that must be deleted. Called before the profile is created, so a kill in
    /// the creation window cannot strand it.
    /// </summary>
    internal static void RecordProfile(string journalPath, string profileName) =>
        Append(journalPath, ProfileTag + FieldSeparator + Encode(profileName));

    /// <summary>
    /// Records that <paramref name="sid"/> is about to be granted access to <paramref name="path"/>.
    /// Throws when the record cannot be made durable — the caller must then refuse to mutate the ACL
    /// at all.
    /// </summary>
    internal static void RecordGrant(string journalPath, string path, string sid)
    {
        if (!IsAppContainerSidString(sid))
        {
            throw new ArgumentException("The SID is not a per-run AppContainer SID in string form.", nameof(sid));
        }

        Append(
            journalPath,
            GrantTag + FieldSeparator + Encode(path) + FieldSeparator + sid);
    }

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
                && IsAppContainerSidString(fields[2]))
            {
                grants.Add(new WindowsAppContainerGrantRecord(path, fields[2]));
                continue;
            }

            unreadable++;
        }

        return new WindowsAppContainerRestorePlan(profileName, grants, unreadable);
    }

    /// <summary>
    /// Undoes every recorded step, newest first, by removing the recorded SID's ACEs from each root's
    /// current DACL, and clears the log only when nothing was left behind. Removing a SID that was
    /// never granted (a kill between the record and the mutation) is a no-op, so every record is safe
    /// to replay, and another run's grant on the same root is never touched. Returns <c>false</c> when
    /// any step failed or any record was unreadable, so the caller can report residue rather than
    /// claim a clean teardown.
    /// </summary>
    internal static bool Replay(
        string journalPath,
        Func<string, string, bool> removeGrant,
        Func<string, bool> deleteProfile)
    {
        ArgumentNullException.ThrowIfNull(removeGrant);
        ArgumentNullException.ThrowIfNull(deleteProfile);

        WindowsAppContainerRestorePlan plan = Read(journalPath);
        bool complete = plan.UnreadableRecords == 0;

        for (int index = plan.Grants.Count - 1; index >= 0; index--)
        {
            WindowsAppContainerGrantRecord grant = plan.Grants[index];
            try
            {
                complete &= removeGrant(grant.Path, grant.Sid);
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

    /// <summary>
    /// The canonical string form of a per-run AppContainer SID — <c>S-1-15-2-</c> and exactly seven
    /// 32-bit sub-authorities, as <c>SecurityIdentifier.Value</c> prints it: what the broker records and
    /// what the restore parses back. Replay purges every explicit ACE the recorded SID holds on the
    /// recorded root, so anything broader — a user, group or well-known SID, ALL APPLICATION PACKAGES, a
    /// capability SID — is not a record this broker wrote, and acting on it would strip access no run
    /// ever granted.
    /// </summary>
    internal static bool IsAppContainerSidString(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > MaxAppContainerSidLength
            || !value.StartsWith(AppContainerSidPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string[] subAuthorities = value[AppContainerSidPrefix.Length..].Split('-');

        return subAuthorities.Length == AppContainerSidSubAuthorityCount
            && subAuthorities.All(static subAuthority =>
                subAuthority.Length > 0
                && (subAuthority.Length == 1 || subAuthority[0] != '0')
                && subAuthority.All(char.IsAsciiDigit)
                && uint.TryParse(subAuthority, NumberStyles.None, CultureInfo.InvariantCulture, out _));
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
