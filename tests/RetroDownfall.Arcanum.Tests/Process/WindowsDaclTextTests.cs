namespace RetroDownfall.Arcanum.Tests.Process;

/// <summary>
/// The text normalisation the Windows AppContainer ACL tests compare through. It is pure string work,
/// so every host pins what it keeps and what it drops: a comparison that dropped more than the one
/// bookkeeping flag would let a restore that lost an entry or its protection pass.
/// </summary>
public sealed class WindowsDaclTextTests
{

    private const string Entry = "(A;OICIID;FA;;;SY)";

    private const string OtherEntry = "(A;OICIID;0x1200a9;;;S-1-15-2-1)";

    [Theory]
    [InlineData("D:AI", "D:")]
    [InlineData("D:PAI", "D:P")]
    [InlineData("D:ARAI", "D:AR")]
    [InlineData("D:PARAI", "D:PAR")]
    public void The_auto_inherited_flag_is_the_only_control_flag_dropped(string flags, string expected)
    {

        Assert.Equal(expected + Entry + OtherEntry, WindowsDaclText.WithoutAutoInheritedFlag(flags + Entry + OtherEntry));

    }

    [Theory]
    [InlineData("D:")]
    [InlineData("D:P")]
    [InlineData("D:AR")]
    [InlineData("D:PAR")]
    public void A_dacl_without_the_flag_is_left_exactly_as_it_was(string flags)
    {

        string sddl = flags + Entry + OtherEntry;

        Assert.Equal(sddl, WindowsDaclText.WithoutAutoInheritedFlag(sddl));

    }

    /// <summary>
    /// The wording the CI Windows lane reported: the same entries, differing only in the flag.
    /// </summary>
    [Fact]
    public void The_restored_dacl_the_lane_reported_compares_equal_to_the_original()
    {

        string original = "D:" + Entry;

        string restored = "D:AI" + Entry;

        Assert.NotEqual(original, restored);

        Assert.Equal(
            WindowsDaclText.WithoutAutoInheritedFlag(original),
            WindowsDaclText.WithoutAutoInheritedFlag(restored));

    }

    [Fact]
    public void A_restore_that_lost_an_entry_still_differs()
    {

        Assert.NotEqual(
            WindowsDaclText.WithoutAutoInheritedFlag("D:AI" + Entry + OtherEntry),
            WindowsDaclText.WithoutAutoInheritedFlag("D:AI" + Entry));

    }

    [Fact]
    public void A_restore_that_lost_protection_still_differs()
    {

        Assert.NotEqual(
            WindowsDaclText.WithoutAutoInheritedFlag("D:PAI" + Entry),
            WindowsDaclText.WithoutAutoInheritedFlag("D:AI" + Entry));

    }

    [Fact]
    public void The_text_of_an_entry_is_never_touched()
    {

        const string entryNamingTheFlag = "(A;ID;FA;;;AI)";

        Assert.Equal("D:" + entryNamingTheFlag, WindowsDaclText.WithoutAutoInheritedFlag("D:AI" + entryNamingTheFlag));

    }

    [Fact]
    public void An_unknown_flag_run_is_kept_verbatim()
    {

        Assert.Equal("D:NO_ACCESS_CONTROL", WindowsDaclText.WithoutAutoInheritedFlag("D:NO_ACCESS_CONTROL"));

    }

    [Theory]
    [InlineData("O:BAG:SYD:AI(A;;FA;;;SY)")]
    [InlineData("")]
    [InlineData("(A;;FA;;;SY)")]
    public void Text_that_is_not_a_dacl_only_descriptor_is_refused(string sddl)
    {

        _ = Assert.Throws<ArgumentException>(() => WindowsDaclText.WithoutAutoInheritedFlag(sddl));

    }

}
