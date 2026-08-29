namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// One statement in one version step, loaded from exactly one embedded <c>.sql</c> file.
/// </summary>
/// <remarks>
/// Unlike a head object this need not be <c>CREATE ... IF NOT EXISTS</c>. A step's statements commit
/// in one transaction with the journal write that records the step, so a step either fully applies or
/// leaves nothing behind, and nothing re-runs a committed step. <c>ALTER TABLE ... ADD COLUMN</c>,
/// which has no idempotent form, is therefore legal here and is not legal in the head tree.
/// </remarks>
internal sealed record GrimoireSchemaTransitionStatement(
    string ResourcePath,
    int Ordinal,
    string Name,
    string Sql);

/// <summary>
/// One ordered move from an installed version to the next, and the sweep it depends on.
/// </summary>
/// <remarks>
/// <paramref name="FromSourceDefinitionFingerprint"/> is the value the tier's head tree published
/// <i>at</i> <paramref name="FromVersion"/>. It is a pinned literal captured when the step was
/// authored, in the same spirit as <see cref="CovenantAcceleratorSyntheticManifest"/>'s pinned shadow
/// DDL: the tree that produced it no longer exists, so nothing can recompute it, and changing it is a
/// reviewed change rather than an absorbed one. It is what lets an installation at an older version
/// be recognized as the older version this binary knows rather than as an unknown one.
/// </remarks>
internal sealed record GrimoireSchemaVersionStep(
    GrimoireSchemaFamily Family,
    GrimoireSchemaTransactionTier TransactionTier,
    int FromVersion,
    int ToVersion,
    string FromSourceDefinitionFingerprint,
    IReadOnlyList<GrimoireSchemaTransitionStatement> Statements,
    IGrimoireSchemaBackfill? Backfill);

/// <summary>
/// One tier's complete, ordered, closed statement of every version it has had and how to reach the
/// one this binary declares.
/// </summary>
/// <remarks>
/// The constructor is the validating boundary. Everything downstream - the planner, the installer,
/// the backfill runner - treats a constructed chain as trusted, so every authoring mistake has to be
/// refused here rather than discovered against a live database.
/// </remarks>
internal sealed class GrimoireSchemaVersionChain
{

    private readonly Dictionary<int, GrimoireSchemaVersionStep> _byFromVersion;

    internal GrimoireSchemaVersionChain(
        GrimoireSchemaManifest headManifest,
        IReadOnlyList<GrimoireSchemaObject> headObjects,
        IReadOnlyList<GrimoireSchemaVersionStep> steps)
    {

        ArgumentNullException.ThrowIfNull(headManifest);

        ArgumentNullException.ThrowIfNull(headObjects);

        ArgumentNullException.ThrowIfNull(steps);

        HeadManifest = headManifest;

        HeadObjects = headObjects;

        Steps = steps;

        if (steps.Count != headManifest.Version - 1)
        {

            throw new InvalidOperationException(
                $"The {headManifest.TransactionTier} schema chain declares {steps.Count} step(s) for head "
                + $"version {headManifest.Version}; a chain needs exactly one step per version above 1.");

        }

        _byFromVersion = new Dictionary<int, GrimoireSchemaVersionStep>(steps.Count);

        HashSet<string> backfillNames = new(StringComparer.Ordinal);

        int expectedFrom = 1;

        foreach (GrimoireSchemaVersionStep step in steps)
        {

            if (step.TransactionTier != headManifest.TransactionTier || step.Family != headManifest.Family)
            {

                throw new InvalidOperationException(
                    $"A schema step for {step.Family}/{step.TransactionTier} was declared on the "
                    + $"{headManifest.Family}/{headManifest.TransactionTier} chain.");

            }

            if (step.ToVersion != step.FromVersion + 1)
            {

                throw new InvalidOperationException(
                    $"The {headManifest.TransactionTier} schema step {step.FromVersion} to {step.ToVersion} "
                    + "is not consecutive; a step that skipped a version would make the chain's order unverifiable.");

            }

            if (step.FromVersion != expectedFrom)
            {

                throw new InvalidOperationException(
                    $"The {headManifest.TransactionTier} schema chain expected a step leaving version "
                    + $"{expectedFrom} and found one leaving version {step.FromVersion}.");

            }

            if (step.Statements.Count == 0)
            {

                throw new InvalidOperationException(
                    $"The {headManifest.TransactionTier} schema step {step.FromVersion} to {step.ToVersion} "
                    + "declares no statement.");

            }

            HashSet<int> ordinals = new(step.Statements.Count);

            foreach (GrimoireSchemaTransitionStatement statement in step.Statements)
            {

                if (!ordinals.Add(statement.Ordinal))
                {

                    throw new InvalidOperationException(
                        $"The {headManifest.TransactionTier} schema step {step.FromVersion} to "
                        + $"{step.ToVersion} declares ordinal {statement.Ordinal} twice; the ordinal is "
                        + "the install order, so two statements sharing one have no defined order.");

                }

            }

            if (step.FromSourceDefinitionFingerprint.Length != 64)
            {

                throw new InvalidOperationException(
                    $"The {headManifest.TransactionTier} schema step {step.FromVersion} to {step.ToVersion} "
                    + "pins a source-definition fingerprint that is not 64 characters.");

            }

            if (step.Backfill is not null && !backfillNames.Add(step.Backfill.Name))
            {

                throw new InvalidOperationException(
                    $"The {headManifest.TransactionTier} schema chain names backfill "
                    + $"'{step.Backfill.Name}' on more than one step; the journal identifies a pending "
                    + "sweep by name, so two steps sharing one name are indistinguishable after a restart.");

            }

            _byFromVersion[step.FromVersion] = step;

            expectedFrom = step.ToVersion;

        }

        if (steps.Count > 0 && expectedFrom != headManifest.Version)
        {

            throw new InvalidOperationException(
                $"The {headManifest.TransactionTier} schema chain's last step reaches version {expectedFrom}, "
                + $"not head version {headManifest.Version}.");

        }

    }

    internal GrimoireSchemaFamily Family => HeadManifest.Family;

    internal GrimoireSchemaTransactionTier TransactionTier => HeadManifest.TransactionTier;

    internal GrimoireSchemaManifest HeadManifest { get; }

    internal IReadOnlyList<GrimoireSchemaObject> HeadObjects { get; }

    internal IReadOnlyList<GrimoireSchemaVersionStep> Steps { get; }

    internal int HeadVersion => HeadManifest.Version;

    /// <summary>
    /// The source-definition fingerprint this binary expects to find recorded for an installation at
    /// <paramref name="version"/>, or <see langword="null"/> for a version the chain does not cover.
    /// </summary>
    internal string? SourceDefinitionFingerprintFor(int version) =>
        version == HeadVersion
            ? HeadManifest.SourceDefinitionFingerprint
            : _byFromVersion.TryGetValue(version, out GrimoireSchemaVersionStep? step)
                ? step.FromSourceDefinitionFingerprint
                : null;

    internal bool TryGetStep(int fromVersion, out GrimoireSchemaVersionStep step)
    {

        bool found = _byFromVersion.TryGetValue(fromVersion, out GrimoireSchemaVersionStep? resolved);

        step = resolved!;

        return found;

    }

}

/// <summary>
/// Exactly one chain per transaction tier.
/// </summary>
/// <remarks>
/// Injected rather than read statically, which is what makes multi-version behavior reachable from a
/// test through the production entry point: a suite installs one chain, then hands the same installer
/// a longer chain for the same tier. Nothing has to hand-seed a metadata row to describe an older
/// installation, so no test asserts a precondition it wrote itself.
/// </remarks>
internal sealed class GrimoireSchemaVersionChainSet
{

    private readonly Dictionary<GrimoireSchemaTransactionTier, GrimoireSchemaVersionChain> _chains;

    internal GrimoireSchemaVersionChainSet(IReadOnlyList<GrimoireSchemaVersionChain> chains)
    {

        ArgumentNullException.ThrowIfNull(chains);

        _chains = new Dictionary<GrimoireSchemaTransactionTier, GrimoireSchemaVersionChain>(chains.Count);

        foreach (GrimoireSchemaVersionChain chain in chains)
        {

            if (!_chains.TryAdd(chain.TransactionTier, chain))
            {

                throw new InvalidOperationException(
                    $"Two schema chains were declared for the {chain.TransactionTier} transaction tier.");

            }

        }

        foreach (GrimoireSchemaTransactionTier tier in Enum.GetValues<GrimoireSchemaTransactionTier>())
        {

            if (!_chains.ContainsKey(tier))
            {

                throw new InvalidOperationException(
                    $"No schema chain was declared for the {tier} transaction tier.");

            }

        }

    }

    internal GrimoireSchemaVersionChain ForTier(GrimoireSchemaTransactionTier tier) =>
        _chains.TryGetValue(tier, out GrimoireSchemaVersionChain? chain)
            ? chain
            : throw new ArgumentOutOfRangeException(nameof(tier));

}
