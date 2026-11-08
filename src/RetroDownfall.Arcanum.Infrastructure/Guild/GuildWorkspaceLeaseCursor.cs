using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Unauthenticated query continuation. The broker independently authorizes every page.</summary>
internal static class GuildWorkspaceLeaseCursor
{
    private const string Prefix = "lease-v1:";

    internal static string Encode(GuildWorkspaceLeaseOwner owner, Guid? questId, GuildWorkspaceLeaseState? state, Guid afterId)
    {
        if (afterId == Guid.Empty)
        {
            throw new ArgumentException("Row id cannot be the empty GUID.");
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(ScopeText(owner, questId, state)));

        string hex = Convert.ToHexString(hash);

        return Prefix + hex + ":" + afterId.ToString("N");
    }

    internal static bool TryDecode(string? cursor, GuildWorkspaceLeaseOwner owner, Guid? questId, GuildWorkspaceLeaseState? state, out Guid? afterId)
    {
        afterId = null;

        if (cursor is null)
        {
            return true;
        }

        if (cursor.Length == 0 || cursor.Length != 106 || !cursor.StartsWith(Prefix, StringComparison.Ordinal) || cursor[73] != ':')
        {
            return false;
        }

        if (!Guid.TryParseExact(cursor.AsSpan(Prefix.Length + 65, 32), "N", out Guid rowId) || rowId == Guid.Empty)
            return false;

        if (!string.Equals(cursor, Encode(owner, questId, state, rowId), StringComparison.Ordinal))
            return false;

        afterId = rowId;

        return true;
    }

    private static string ScopeText(GuildWorkspaceLeaseOwner owner, Guid? questId, GuildWorkspaceLeaseState? state)
    {
        var sb = new StringBuilder("Guild.WorkspaceLease.List.v1");

        Append(sb, ((int)owner.Kind).ToString(CultureInfo.InvariantCulture));

        Append(sb, owner.PrincipalId.ToString("D", CultureInfo.InvariantCulture));

        Append(sb, Quest(owner.QuestId));

        Append(sb, Quest(owner.RootQuestId));

        Append(sb, Quest(owner.SessionId));

        Append(sb, Quest(owner.CampaignId));

        Append(sb, owner.WorkspaceId.ToString("D", CultureInfo.InvariantCulture));

        Append(sb, owner.InstallationGeneration.ToString("D", CultureInfo.InvariantCulture));

        Append(sb, Quest(questId));

        Append(sb, state is null ? "-" : ((int)state).ToString(CultureInfo.InvariantCulture));

        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string value) => sb.Append('|').Append(value);

    private static string Quest(Guid? value) => value is null ? "-" : value.Value.ToString("D", CultureInfo.InvariantCulture);
}
