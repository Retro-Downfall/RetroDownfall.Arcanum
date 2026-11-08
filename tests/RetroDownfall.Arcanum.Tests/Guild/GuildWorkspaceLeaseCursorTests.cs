using System.Globalization;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildWorkspaceLeaseCursorTests
{
    private static readonly Guid Anchor = Guid.Parse("abcdef01-2345-6789-abcd-ef0123456789");

    private static GuildWorkspaceLeaseOwner Owner() => new(GuildWorkspaceLeaseOwnerKind.Quest,
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void Equivalent_owner_values_roundtrip_with_the_exact_wire_shape()
    {
        GuildWorkspaceLeaseOwner owner = Owner();

        string cursor = GuildWorkspaceLeaseCursor.Encode(owner, null, GuildWorkspaceLeaseState.Held, Anchor);

        Assert.Equal(106, cursor.Length);

        Assert.Matches("^lease-v1:[0-9A-F]{64}:abcdef0123456789abcdef0123456789$", cursor);

        Assert.True(GuildWorkspaceLeaseCursor.TryDecode(cursor, owner with { }, null, GuildWorkspaceLeaseState.Held, out Guid? after));

        Assert.Equal(Anchor, after);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Every_owner_coordinate_is_bound(int coordinate)
    {
        GuildWorkspaceLeaseOwner owner = Owner();

        GuildWorkspaceLeaseOwner changed = coordinate switch
        {
            0 => owner with { Kind = GuildWorkspaceLeaseOwnerKind.Operator },
            1 => owner with { PrincipalId = Guid.NewGuid() },
            2 => owner with { QuestId = Guid.NewGuid() },
            3 => owner with { RootQuestId = Guid.NewGuid() },
            4 => owner with { SessionId = Guid.NewGuid() },
            5 => owner with { CampaignId = Guid.NewGuid() },
            6 => owner with { WorkspaceId = Guid.NewGuid() },
            _ => owner with { InstallationGeneration = Guid.NewGuid() },
        };

        AssertRejected(GuildWorkspaceLeaseCursor.Encode(owner, null, null, Anchor), changed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Nullable_owner_coordinates_distinguish_absent_from_empty(int coordinate)
    {
        GuildWorkspaceLeaseOwner owner = Owner() with { QuestId = null, RootQuestId = null, SessionId = null, CampaignId = null };

        GuildWorkspaceLeaseOwner changed = coordinate switch
        {
            0 => owner with { QuestId = Guid.Empty },
            1 => owner with { RootQuestId = Guid.Empty },
            2 => owner with { SessionId = Guid.Empty },
            _ => owner with { CampaignId = Guid.Empty },
        };

        AssertRejected(GuildWorkspaceLeaseCursor.Encode(owner, null, null, Anchor), changed);

        AssertRejected(GuildWorkspaceLeaseCursor.Encode(changed, null, null, Anchor), owner);
    }

    [Fact]
    public void Query_filters_are_bound_independently_of_owner_coordinates()
    {
        GuildWorkspaceLeaseOwner owner = Owner();

        Guid quest = Guid.NewGuid();

        string unfiltered = GuildWorkspaceLeaseCursor.Encode(owner, null, null, Anchor);

        AssertRejected(unfiltered, owner, quest);

        AssertRejected(unfiltered, owner, state: GuildWorkspaceLeaseState.Held);

        string filtered = GuildWorkspaceLeaseCursor.Encode(owner, quest, GuildWorkspaceLeaseState.Held, Anchor);

        AssertRejected(filtered, owner, null, GuildWorkspaceLeaseState.Held);

        AssertRejected(filtered, owner, Guid.NewGuid(), GuildWorkspaceLeaseState.Held);

        AssertRejected(filtered, owner, quest);

        AssertRejected(filtered, owner, quest, GuildWorkspaceLeaseState.Released);
    }

    [Fact]
    public void Null_cursor_starts_a_page_without_an_anchor()
    {
        Assert.True(GuildWorkspaceLeaseCursor.TryDecode(null, Owner(), null, null, out Guid? after));

        Assert.Null(after);
    }

    [Fact]
    public void Malformed_versions_lengths_hashes_and_anchors_refuse_without_an_output_anchor()
    {
        GuildWorkspaceLeaseOwner owner = Owner();

        string cursor = GuildWorkspaceLeaseCursor.Encode(owner, null, null, Anchor);

        foreach (string malformed in new[] { "", cursor.Replace("lease-v1:", "lease-v2:"), cursor[..^1], cursor + "x",
            cursor[..9] + new string('G', 64) + cursor[73..], cursor[..9] + cursor[9..73].ToLowerInvariant() + cursor[73..],
            cursor[..9] + (char)(cursor[9] + 256) + cursor[10..], cursor[..74] + new string('z', 32), cursor[..74] + Guid.Empty.ToString("N"), cursor[..74] + Anchor.ToString("N").ToUpperInvariant() })
            AssertRejected(malformed, owner);
    }

    [Fact]
    public void Encoding_an_empty_anchor_throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => GuildWorkspaceLeaseCursor.Encode(Owner(), null, null, Guid.Empty));
    }

    [Fact]
    public void Roundtrip_and_scope_encoding_are_independent_of_current_culture()
    {
        GuildWorkspaceLeaseOwner owner = Owner();

        string cursor = GuildWorkspaceLeaseCursor.Encode(owner, owner.QuestId, GuildWorkspaceLeaseState.Held, Anchor);

        CultureInfo previous = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");

            Assert.Equal(cursor, GuildWorkspaceLeaseCursor.Encode(owner, owner.QuestId, GuildWorkspaceLeaseState.Held, Anchor));

            Assert.True(GuildWorkspaceLeaseCursor.TryDecode(cursor, owner, owner.QuestId, GuildWorkspaceLeaseState.Held, out Guid? after));

            Assert.Equal(Anchor, after);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static void AssertRejected(string cursor, GuildWorkspaceLeaseOwner owner, Guid? quest = null, GuildWorkspaceLeaseState? state = null)
    {
        Assert.False(GuildWorkspaceLeaseCursor.TryDecode(cursor, owner, quest, state, out Guid? after));

        Assert.Null(after);
    }
}
