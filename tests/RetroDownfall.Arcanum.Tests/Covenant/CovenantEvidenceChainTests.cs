using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

public sealed class CovenantEvidenceChainTests
{
    private static readonly Guid BranchOne = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

    private static readonly Guid BranchTwo = Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f");

    [Fact]
    public void Attempt_chain_seed_and_updates_match_literal_recurrence()
    {
        CovenantAttemptChain chain = CovenantEvidenceChains.SeedAttemptChain();

        Assert.Equal(0UL, chain.Count);
        Assert.Equal("011426617416BDF818E7A0893C9F3883C9D1AB5435846393FB37E61E439BF510", chain.Head.ToString());

        chain = CovenantEvidenceChains.AppendAttempt(chain, D(1));

        Assert.Equal(1UL, chain.Count);
        Assert.Equal("95880907B1CBA63332B8327DFAD8C5153F0B824FE689F6B161DA868248BE3E01", chain.Head.ToString());

        chain = CovenantEvidenceChains.AppendAttempt(chain, D(2));

        Assert.Equal(2UL, chain.Count);
        Assert.Equal("C88B5606F23D9ACA73831247A476B9EAA25B948A232C7555A82D289B4AE8CADC", chain.Head.ToString());
    }

    [Fact]
    public void Branch_chain_seed_pins_absent_and_fork_parent_presence_bytes()
    {
        CovenantBranchChain root = CovenantEvidenceChains.SeedBranchChain(BranchOne, null, null);
        CovenantBranchChain fork = CovenantEvidenceChains.SeedBranchChain(BranchTwo, D(3), D(4));

        Assert.Equal("76FF628A532349AD9A7FF2A63A56A165CDBBD56800A0338AB723181AF4C0E710", root.Head.ToString());
        Assert.Equal("F8BC4AA8B072FA15EC43FE8EDEA75EE9C6E69279C63EEFF2A98C7ED622B7126A", fork.Head.ToString());

        fork = CovenantEvidenceChains.AppendBranch(fork, D(5));

        Assert.Equal(1UL, fork.Ordinal);
        Assert.Equal("00AD30FC9650B23C2D1FA87B6B133E5BA553B12643E8002802E7F8D523D512E9", fork.Head.ToString());

        fork = CovenantEvidenceChains.AppendBranch(fork, D(6));

        Assert.Equal(2UL, fork.Ordinal);
        Assert.Equal("8AB93043E21DBC3E3A4AEE39F056B418FBAF4A41181F940D456C8AE2E86C43DA", fork.Head.ToString());
    }

    [Fact]
    public void Branch_fork_parents_are_both_present_or_both_absent()
    {
        Assert.Throws<ArgumentException>(() => CovenantEvidenceChains.SeedBranchChain(BranchOne, D(1), null));
        Assert.Throws<ArgumentException>(() => CovenantEvidenceChains.SeedBranchChain(BranchOne, null, D(2)));
    }

    [Fact]
    public void Disclosure_chain_seed_and_updates_match_literal_recurrence()
    {
        CovenantDisclosureChain chain = CovenantEvidenceChains.SeedDisclosureChain();

        Assert.Equal("338E809242611A14B2E00821758B33139EC8E59AFD77E2D6A5378818E1ED0D54", chain.Head.ToString());

        chain = CovenantEvidenceChains.AppendDisclosure(chain, D(6));

        Assert.Equal("4EB606B6BBCA7DF58B8E08740FB67CA7D7CB80CCEFF5D68B7B90218FEFD301BB", chain.Head.ToString());

        chain = CovenantEvidenceChains.AppendDisclosure(chain, D(7));

        Assert.Equal(2UL, chain.Count);
        Assert.Equal("EE48B3586B2AF7D65E587C5A2A548955510ACD440AC6D631993BB31AFC035568", chain.Head.ToString());
    }

    [Fact]
    public void Chain_counters_fail_before_unsigned_overflow()
    {
        CovenantAttemptChain attempts = new(ulong.MaxValue, D(1));
        CovenantBranchChain branch = new(BranchOne, ulong.MaxValue, D(2));
        CovenantDisclosureChain disclosures = new(ulong.MaxValue, D(3));

        Assert.Throws<OverflowException>(() => CovenantEvidenceChains.AppendAttempt(attempts, D(4)));
        Assert.Throws<OverflowException>(() => CovenantEvidenceChains.AppendBranch(branch, D(5)));
        Assert.Throws<OverflowException>(() => CovenantEvidenceChains.AppendDisclosure(disclosures, D(6)));
    }

    [Fact]
    public void Default_chain_inputs_fail_closed()
    {
        Assert.Throws<ArgumentException>(() => CovenantEvidenceChains.SeedBranchChain(Guid.Empty, null, null));
        Assert.Throws<ArgumentException>(() => CovenantEvidenceChains.AppendAttempt(new CovenantAttemptChain(0, default), D(1)));
        Assert.Throws<ArgumentException>(() => CovenantEvidenceChains.AppendBranch(new CovenantBranchChain(BranchOne, 0, default), D(1)));
        Assert.Throws<ArgumentException>(() => CovenantEvidenceChains.AppendDisclosure(new CovenantDisclosureChain(0, default), D(1)));
    }

    private static CovenantDigest D(byte value) =>
        new(Enumerable.Repeat(value, CovenantLimits.DigestBytes).ToArray());
}
