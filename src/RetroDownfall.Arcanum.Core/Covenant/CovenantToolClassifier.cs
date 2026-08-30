using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// What one frozen tool call actually is, decided after its identity stopped changing.
/// </summary>
/// <remarks>
/// <see cref="ArgumentsArePrivate"/> is the answer the streaming transport is waiting for. A call
/// whose arguments carry profile content never releases a public projection through SSE, progress,
/// transcript, or diagnostics; the public stream receives the compact redacted staging receipt
/// instead (§10.14).
/// </remarks>
public sealed record ProviderToolCallClassification(
    string ToolName,
    CovenantToolRiskIdentity RiskIdentity,
    bool ArgumentsArePrivate,
    bool IsCovenantMutation,
    CovenantDigest FrozenNameDigest,
    CovenantDigest CanonicalArgumentDigest)
{

    /// <summary>
    /// The identity of this exact call's input, used to resolve tool replay.
    /// </summary>
    /// <remarks>
    /// Deliberately derived from the name and the canonical arguments alone. A capability nonce, an
    /// attempt ordinal, or a request id would all make an honest retry look like a different call and
    /// turn it into an idempotency conflict.
    ///
    /// <para>Both operands are fixed 32-byte digests, so their concatenation is unambiguous without
    /// length prefixes, and it cannot collide with a Covenant canonical preimage because every one of
    /// those begins with a domain tag.</para>
    /// </remarks>
    public CovenantDigest ToolInputDigest { get; } =
        CovenantDigestPair.Combine(FrozenNameDigest, CanonicalArgumentDigest);

}

/// <summary>
/// Classifies a frozen provider tool call and binds its evidence digests.
/// </summary>
/// <remarks>
/// Runs over the complete name and the complete arguments and never over a fragment. Classifying a
/// prefix is what would let <c>propose_</c> be treated as an ordinary tool for exactly as long as it
/// takes to publish its arguments.
/// </remarks>
public static class CovenantToolClassifier
{

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly byte[] EmptyArgumentObject = "{}"u8.ToArray();

    /// <summary>
    /// Classifies a call that is already known to be one of the two Covenant mutation tools.
    /// </summary>
    /// <remarks>
    /// Takes no Ward settings because a frozen call is either a Covenant-sensitive egress or an
    /// ordinary call. The configured forbidden-arts list filters tool advertisement under
    /// <see cref="ToolPolicy.NoForbiddenArts"/> and must not change a frozen call's risk identity.
    /// </remarks>
    public static Result<ProviderToolCallClassification> ClassifyCovenantTool(
        string toolName,
        ReadOnlySpan<byte> rawArgumentsUtf8)
    {

        if (!CovenantToolNames.IsCovenantMutationTool(toolName))
        {

            throw new ArgumentException(
                "This overload classifies the Covenant mutation tools only.",
                nameof(toolName));

        }

        return Classify(toolName, rawArgumentsUtf8);

    }

    /// <summary>
    /// Classifies a call whose identity was already frozen by the transport it arrived on.
    /// </summary>
    /// <remarks>
    /// The in-process MCP server frames its own requests, so a call that reaches a handler has a
    /// complete name and a complete argument body by construction. It still classifies through the
    /// same function, because the evidence digests a Ward receipt binds have to be computed the same
    /// way whichever transport produced the call.
    /// </remarks>
    public static Result<ProviderToolCallClassification> Classify(
        string toolName,
        ReadOnlySpan<byte> rawArgumentsUtf8)
    {

        ArgumentException.ThrowIfNullOrEmpty(toolName);

        byte[] canonicalArguments;

        try
        {
            canonicalArguments = rawArgumentsUtf8.IsEmpty
                ? EmptyArgumentObject
                : ArcanumCanonicalJsonV1.Canonicalize(rawArgumentsUtf8);
        }
        catch (ArgumentException)
        {
            // Evidence has to be computable before anything decides what this call may do. A body
            // that cannot be canonicalized has no stable identity to bind a Ward receipt to.
            return Result<ProviderToolCallClassification>.Failure(new Error(
                ErrorCodes.Hub.ProviderToolCallInvalid,
                "A provider tool call carried arguments that are not valid JSON."));
        }

        bool isCovenantMutation = CovenantToolNames.IsCovenantMutationTool(toolName);

        return Result<ProviderToolCallClassification>.Success(new ProviderToolCallClassification(
            toolName,
            isCovenantMutation
                ? CovenantToolRiskIdentity.CovenantSensitiveEgress
                : CovenantToolRiskIdentity.Ordinary,
            isCovenantMutation,
            isCovenantMutation,
            new CovenantDigest(SHA256.HashData(StrictUtf8.GetBytes(toolName))),
            new CovenantDigest(SHA256.HashData(canonicalArguments))));

    }

}
