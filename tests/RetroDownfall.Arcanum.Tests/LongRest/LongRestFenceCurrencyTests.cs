using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// The two questions a retained fence is asked of the live one. They are pure comparisons, so the terms each ignores are pinned here.
public sealed class LongRestFenceCurrencyTests
{
    private static readonly CovenantDigest Digest = new(Enumerable.Repeat((byte)3, 32).ToArray());

    private static readonly Guid Boot = Guid.NewGuid();

    private static LongRestSweepFence Fence() => new(CanonicalCampaignContext.GlobalOnly, 7, Guid.NewGuid(), 4, 5, 6, new(Boot, 2, Digest));

    [Fact]
    public void An_identical_fence_can_resume_and_describes_the_installation()
    {
        LongRestSweepFence retained = Fence();

        Assert.True(LongRestFenceCurrency.CanResume(retained, retained));

        Assert.True(LongRestFenceCurrency.Describes(retained, retained));
    }

    [Fact]
    public void The_insertion_boundary_may_only_grow_and_is_never_compared_for_equality()
    {
        LongRestSweepFence retained = Fence();

        Assert.True(LongRestFenceCurrency.CanResume(retained, retained with { InsertionHighWatermark = 9 }));

        Assert.True(LongRestFenceCurrency.Describes(retained, retained with { InsertionHighWatermark = 9 }));

        Assert.False(LongRestFenceCurrency.CanResume(retained, retained with { InsertionHighWatermark = 6 }));

        Assert.False(LongRestFenceCurrency.Describes(retained, retained with { InsertionHighWatermark = 6 }));
    }

    [Theory]
    [InlineData("admission")]
    [InlineData("authority")]
    [InlineData("capability")]
    public void In_the_boot_that_captured_it_a_moved_process_term_ends_resumption_and_currency(string term)
    {
        LongRestSweepFence retained = Fence();

        LongRestSweepFence live = term switch
        {
            "admission" => retained with { AdmissionGeneration = retained.AdmissionGeneration + 1 },
            "authority" => retained with { RuntimeAuthorityGeneration = retained.RuntimeAuthorityGeneration + 1 },
            _ => retained with { CapabilityGeneration = retained.CapabilityGeneration + 2 },
        };

        Assert.False(LongRestFenceCurrency.CanResume(retained, live));

        Assert.False(LongRestFenceCurrency.Describes(retained, live));
    }

    [Fact]
    public void In_a_later_boot_the_process_terms_are_readmitted_not_compared()
    {
        LongRestSweepFence retained = Fence();

        LongRestSweepFence live = retained with
        {
            AdmissionGeneration = 1,
            RuntimeAuthorityGeneration = 1,
            CapabilityGeneration = 1,
            Configuration = new(Guid.NewGuid(), 0, Digest),
        };

        Assert.True(LongRestFenceCurrency.CanResume(retained, live));

        Assert.True(LongRestFenceCurrency.Describes(retained, live));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_dataset_and_the_canonical_scope_must_be_the_same_in_every_boot(bool laterBoot)
    {
        LongRestSweepFence retained = Fence();

        LongRestConfigurationStamp stamp = laterBoot ? new(Guid.NewGuid(), 0, Digest) : retained.Configuration;

        LongRestSweepFence otherDataset = retained with { DatasetGeneration = Guid.NewGuid(), Configuration = stamp };

        LongRestSweepFence otherScope = retained with
        {
            CanonicalScope = CanonicalCampaignContext.Create(SessionCampaignBinding.ForCampaign(Guid.NewGuid()), 1, 1, null, null),
            Configuration = stamp,
        };

        Assert.False(LongRestFenceCurrency.CanResume(retained, otherDataset));

        Assert.False(LongRestFenceCurrency.Describes(retained, otherDataset));

        Assert.False(LongRestFenceCurrency.CanResume(retained, otherScope));

        Assert.False(LongRestFenceCurrency.Describes(retained, otherScope));
    }

    [Fact]
    public void The_configuration_epoch_ends_an_unfinished_sweep_but_not_a_completed_one_in_the_same_boot()
    {
        LongRestSweepFence retained = Fence();

        LongRestSweepFence flipped = retained with { Configuration = retained.Configuration with { Epoch = retained.Configuration.Epoch + 2 } };

        // Work in flight is protected by the epoch; a completed sweep has none, and the value digest is what says the setting is the same.
        Assert.False(LongRestFenceCurrency.CanResume(retained, flipped));

        Assert.True(LongRestFenceCurrency.Describes(retained, flipped));
    }

    [Fact]
    public void A_different_configuration_value_describes_nothing_in_any_boot()
    {
        LongRestSweepFence retained = Fence();

        CovenantDigest other = new(Enumerable.Repeat((byte)4, 32).ToArray());

        Assert.False(LongRestFenceCurrency.Describes(retained, retained with { Configuration = retained.Configuration with { ValueDigest = other } }));

        Assert.False(LongRestFenceCurrency.Describes(retained, retained with { Configuration = new(Guid.NewGuid(), 0, other) }));
    }
}
