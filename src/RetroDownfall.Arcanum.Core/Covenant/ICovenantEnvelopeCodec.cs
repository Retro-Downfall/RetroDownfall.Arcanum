using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// The only way an opaque Covenant token is produced or consumed.
/// </summary>
/// <remarks>
/// One codec for all six purposes rather than one per purpose. A second implementation is a second
/// framing, and two framings that drift is how a cursor eventually authenticates as a preflight
/// (§10.12).
///
/// <para>Encoding always uses the current key for the purpose family. Decoding accepts only that same
/// current key: an envelope minted before a reset, restore, reinitialize, or counter rollover fails
/// authentication rather than being honoured under an older generation. There is deliberately no
/// grace window, because every one of those transitions exists precisely to invalidate work in
/// flight.</para>
/// </remarks>
public interface ICovenantEnvelopeCodec
{

    /// <summary>The nonsecret identity of the key material currently in force.</summary>
    CovenantEnvelopeKeySnapshot KeySnapshot { get; }

    /// <summary>
    /// Issues one envelope over <paramref name="payload"/> for <paramref name="purpose"/>.
    /// </summary>
    Result<string> Encode(
        CovenantEnvelopePurpose purpose,
        ReadOnlySpan<byte> payload,
        TimeSpan lifetime);

    /// <summary>
    /// Authenticates and decodes one envelope, refusing anything not issued for
    /// <paramref name="expectedPurpose"/> under the current key.
    /// </summary>
    Result<CovenantEnvelopeBody> Decode(
        CovenantEnvelopePurpose expectedPurpose,
        string? token);

}

/// <summary>
/// Publishes a committed authority transition into the live process.
/// </summary>
/// <remarks>
/// Called by reset, restore, family reinitialize, and counter rollover, each of which presents the
/// still-held exclusive lease that protected its durable transition. The ordering matters: fresh keys,
/// codec, and issuer are constructed off to the side and swapped as one generation *before* the caller
/// reopens admission. Reopening first would leave a window in which new work could be authorized under
/// keys the transition just invalidated (§10.12).
///
/// <para>Any validation, derivation, or publication failure leaves the old contexts unusable and the
/// affected gate closed. A half-published transition is the one outcome this contract must never
/// produce, so failing closed is the only alternative to succeeding.</para>
/// </remarks>
public interface ICovenantAuthorityTransitionPublisher
{

    /// <summary>
    /// Derives fresh purpose keys for a committed transition and publishes them as one generation.
    /// </summary>
    ValueTask<Result> PublishCommittedAsync(
        CovenantCommittedAuthorityTransition transition,
        ICovenantExclusiveOperationLease lease,
        CancellationToken cancellationToken);

}

/// <summary>
/// Produces the keyed correlation label diagnostics use in place of content identity.
/// </summary>
public interface ICovenantDiagnosticTagger
{

    /// <summary>The nonsecret diagnostic key version currently in force.</summary>
    uint KeyVersion { get; }

    /// <summary>
    /// Tags one validated content-identity digest. This is the only production path to a tag value.
    /// </summary>
    CovenantDiagnosticTag Create(CovenantDigest contentIdentityDigest);

}

/// <summary>
/// Lends the current diagnostic key to the one type permitted to use it.
/// </summary>
/// <remarks>
/// This port exists so the tagger can live in Core beside the internal
/// <see cref="CovenantDiagnosticTag"/> constructor while its key material stays in Infrastructure.
/// Widening Core's internals to Infrastructure would have been the shorter path and would also have
/// handed Infrastructure the ability to mint an <c>OperatorAuthorityContext</c> (§10.12).
///
/// <para>Copy-out rather than expose: the caller writes into a stack buffer it clears, so no key array
/// reference escapes the provider that owns its zeroization.</para>
/// </remarks>
public interface ICovenantDiagnosticKeySource
{

    /// <summary>
    /// Copies the 32-byte diagnostic key and its nonsecret version, or reports that none exists.
    /// </summary>
    bool TryCopyDiagnosticKey(Span<byte> destination, out uint keyVersion);

}
