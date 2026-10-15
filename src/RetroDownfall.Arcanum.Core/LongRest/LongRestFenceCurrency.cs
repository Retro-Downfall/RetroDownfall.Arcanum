namespace RetroDownfall.Arcanum.Core.LongRest;

/// <summary>
/// The two questions a fence a generation retained is asked of the live fence of the same scope: can the generation still be resumed,
/// and, once it has completed, does it still describe the installation. Both are pure comparisons, and every site that asks either one
/// asks it here, so a request, a capability and a replay can never disagree about whether a generation is current.
/// </summary>
/// <remarks>
/// <para>The generation's insertion high-water mark is its own frozen boundary, so it is never compared for equality: it only has to
/// lie at or below the live one, which never falls. The canonical scope and the dataset must be the same in every boot. Inside the boot
/// that captured the fence the process terms (admission, runtime authority and capability state) must be unchanged too, because
/// nothing proves that what moved them left the generation's reads intact. In a later boot they are not compared at all: the
/// readmission re-stamps them after it has re-proved the frozen source facts.</para>
///
/// <para>The one difference between the questions is the configuration epoch. It rises on every change to the configuration, including
/// a change that is reverted, and it is what ends work in flight when a setting moves during provider I/O, so a sweep that can still
/// be resumed must carry the live epoch. A completed sweep has no work in flight for it to protect. What says a completed sweep's
/// configuration is the one in force is the configuration value digest, so a setting changed and changed back is the same setting.</para>
/// </remarks>
public static class LongRestFenceCurrency
{
    /// <summary>Whether a generation that has not completed can still be resumed under the live fence.</summary>
    public static bool CanResume(LongRestSweepFence retained, LongRestSweepFence live)
    {
        ArgumentNullException.ThrowIfNull(retained);

        ArgumentNullException.ThrowIfNull(live);

        return SameAnchor(retained, live)
            && (retained.Configuration.BootId != live.Configuration.BootId
                || retained with { InsertionHighWatermark = live.InsertionHighWatermark } == live);
    }

    /// <summary>Whether a completed generation still describes the installation the live fence captured.</summary>
    public static bool Describes(LongRestSweepFence retained, LongRestSweepFence live)
    {
        ArgumentNullException.ThrowIfNull(retained);

        ArgumentNullException.ThrowIfNull(live);

        return SameAnchor(retained, live) && retained.Configuration.ValueDigest == live.Configuration.ValueDigest
            && (retained.Configuration.BootId != live.Configuration.BootId
                || retained with { InsertionHighWatermark = live.InsertionHighWatermark, Configuration = live.Configuration } == live);
    }

    private static bool SameAnchor(LongRestSweepFence retained, LongRestSweepFence live) =>
        retained.CanonicalScope == live.CanonicalScope && retained.DatasetGeneration == live.DatasetGeneration
        && retained.InsertionHighWatermark <= live.InsertionHighWatermark;
}
