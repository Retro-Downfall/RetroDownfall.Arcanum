using System.Text;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// The one place a public Covenant request shape decides whether it is well formed.
/// </summary>
/// <remarks>
/// Shared rather than repeated per request record, because the interesting failures are the ones a
/// second surface forgets. A scope selection that deserialized to its zero value, an oversized token
/// measured in characters instead of bytes, an apply digest that is nearly the right length — each is
/// invisible at a call site and each has exactly one correct answer, so the answer lives once.
///
/// <para>Every check runs before allocation proportional to a caller-supplied length, and every
/// failure is content-free: these errors travel through logs and unauthenticated status surfaces
/// (§10.16).</para>
/// </remarks>
internal static class CovenantWireValidation
{

    /// <summary>The exact character length of a 32-byte digest rendered as uppercase hex.</summary>
    internal const int DigestHexCharacters = CovenantLimits.DigestBytes * 2;

    internal static Error InvalidScope(string detail) =>
        new(ErrorCodes.Covenant.InvalidScope, detail);

    internal static Error InvalidBody(string detail) =>
        new(ErrorCodes.Validation.InvalidBody, detail);

    /// <summary>
    /// Validates a cursor-style scope selection and the Campaign identity that must or must not
    /// accompany it.
    /// </summary>
    internal static Result ValidateScopeSelection(CovenantCursorScopeSelection scope, Guid? campaignId) =>
        scope switch
        {
            CovenantCursorScopeSelection.Global or CovenantCursorScopeSelection.AllScopes =>
                campaignId is null
                    ? Result.Success()
                    : InvalidScope("An installation-wide Covenant selection cannot also name one Campaign."),

            CovenantCursorScopeSelection.Campaign =>
                campaignId is { } id && id != Guid.Empty
                    ? Result.Success()
                    : InvalidScope("A Campaign-scoped Covenant selection requires a nonempty Campaign identity."),

            // Zero is what an omitted JSON property deserializes to. Reading it as "every scope"
            // would make an installation-wide read the accidental default.
            _ => InvalidScope("A Covenant request must state its scope explicitly."),
        };

    /// <summary>
    /// Validates a persisted-scope selection, which has no all-scopes member because the same key can
    /// exist in Global and in every Campaign at once.
    /// </summary>
    internal static Result ValidateOperationScope(CovenantScope scope, Guid? campaignId) =>
        scope switch
        {
            CovenantScope.Global =>
                campaignId is null
                    ? Result.Success()
                    : InvalidScope("A Global Covenant target cannot also name a Campaign."),

            CovenantScope.Campaign =>
                campaignId is { } id && id != Guid.Empty
                    ? Result.Success()
                    : InvalidScope("A Campaign Covenant target requires a nonempty Campaign identity."),

            _ => InvalidScope("A Covenant request must state its scope explicitly."),
        };

    internal static Result ValidateEvaluationCampaign(Guid? evaluationCampaignId) =>
        evaluationCampaignId is not { } id || id != Guid.Empty
            ? Result.Success()
            : InvalidScope("An empty evaluation Campaign is indistinguishable from an absent one.");

    internal static Result ValidateLifecycle(CovenantLifecycle lifecycle) =>
        lifecycle is CovenantLifecycle.Set or CovenantLifecycle.Retired or CovenantLifecycle.Any
            ? Result.Success()
            : InvalidScope("The requested Covenant lifecycle filter is not a member of the contract.");

    internal static Result ValidateLane(CovenantLane? lane) =>
        lane is null or CovenantLane.Confirmed or CovenantLane.Proposed
            ? Result.Success()
            : InvalidScope("The requested Covenant lane is not a member of the contract.");

    /// <summary>
    /// Refuses the one scope-and-lane pairing that cannot exist: Global content is operator-authored,
    /// so there is no Global Proposed head for anything to retire.
    /// </summary>
    internal static Result ValidateRetirableLane(CovenantScope scope, CovenantLane lane) =>
        scope == CovenantScope.Global && lane == CovenantLane.Proposed
            ? InvalidScope("Global Covenant content has no Proposed lane.")
            : Result.Success();

    /// <summary>
    /// Refuses a correction target outside the lane an operator authors.
    /// </summary>
    /// <remarks>
    /// The Proposed lane belongs to the agent. An operator who wants a proposal promotes it with a
    /// write of their own, which makes them its author; correcting it in place would make them the
    /// author of a lane whose whole meaning is that they have not yet agreed to it.
    /// </remarks>
    internal static Result ValidateCorrectableLane(CovenantLane lane) =>
        lane == CovenantLane.Confirmed
            ? Result.Success()
            : InvalidScope("A Covenant correction names the Confirmed lane, because that is the lane an operator authors.");

    /// <summary>Validates a lowercase hexadecimal digest on the wire, before anything tries to read it.</summary>
    internal static Result ValidateDigestText(string? value, string subject)
    {

        if (value is not { Length: 64 })
        {

            return InvalidScope($"The {subject} must be a 64-character hexadecimal digest.");

        }

        foreach (char character in value)
        {

            if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F')))
            {

                return InvalidScope($"The {subject} must be a 64-character hexadecimal digest.");

            }

        }

        return Result.Success();

    }

    internal static Result ValidateCurationKind(CovenantCurationKind kind) =>
        kind is >= CovenantCurationKind.Pin and <= CovenantCurationKind.Unmask
            ? Result.Success()
            : InvalidScope("The requested Covenant curation kind is not a member of the contract.");

    /// <summary>
    /// Refuses the two placements a scope mask cannot have.
    /// </summary>
    /// <remarks>
    /// A Global mask has no broader scope to be falling through from, and the Proposed lane is
    /// review-only beside effective Confirmed content, so masking it would change nothing an operator
    /// could observe. Refused on the request rather than at the table, so a caller learns it before a
    /// read is opened rather than from a constraint inside a commit.
    /// </remarks>
    internal static Result ValidateMaskablePlacement(CovenantCurationKind kind, CovenantScope scope, CovenantLane lane)
    {

        if (kind is not (CovenantCurationKind.Mask or CovenantCurationKind.Unmask))
        {

            return Result.Success();

        }

        if (scope != CovenantScope.Campaign)
        {

            return InvalidScope("A Covenant scope mask names one Campaign, because Global content has no broader scope to fall through from.");

        }

        return lane != CovenantLane.Confirmed
            ? InvalidScope("A Covenant scope mask names the Confirmed lane, because the Proposed lane is review-only beside it.")
            : Result.Success();

    }

    /// <summary>
    /// Validates an opaque token or cursor against the envelope's encoded bound, before anything
    /// tries to decode it.
    /// </summary>
    internal static Result ValidateCursor(string? cursor)
    {

        if (cursor is null)
        {

            return Result.Success();

        }

        return cursor.Length is > 0 and <= CovenantLimits.MaxEnvelopeEncodedBytes
            ? Result.Success()
            : new Error(ErrorCodes.Covenant.InvalidCursor, "This cursor is not valid.");

    }

    /// <summary>
    /// Validates a required opaque token. The failure is deliberately the same content-free code an
    /// authentication failure produces, so a caller cannot learn which half was wrong.
    /// </summary>
    internal static Result ValidateToken(string? token) =>
        !string.IsNullOrWhiteSpace(token) && token.Length <= CovenantLimits.MaxEnvelopeEncodedBytes
            ? Result.Success()
            : new Error(ErrorCodes.Covenant.InvalidCursor, "This token is not valid.");

    internal static Result ValidateKey(string? key) =>
        key is not null && CovenantKey.IsWellFormed(key)
            ? Result.Success()
            : new Error(
                ErrorCodes.Covenant.InvalidKey,
                "A Covenant key must match [a-z0-9][a-z0-9._-]{0,127}.");

    internal static Result ValidateAuthoredContent(string? content)
    {

        if (content is null)
        {

            return new Error(ErrorCodes.Covenant.InvalidContent, "Authored Covenant content is required.");

        }

        // Measured in UTF-8 bytes, which is what the compiler and every storage bound count. A
        // character bound would admit four times the declared cost for astral text.
        return Encoding.UTF8.GetByteCount(content) is > 0 and <= CovenantLimits.MaxAuthoredContentBytes
            ? Result.Success()
            : new Error(
                ErrorCodes.Covenant.InvalidContent,
                $"Authored Covenant content must be between 1 and {CovenantLimits.MaxAuthoredContentBytes} UTF-8 bytes.");

    }

    internal static Result ValidateSearchText(string? query)
    {

        if (query is null || Encoding.UTF8.GetByteCount(query) > CovenantLimits.MaxSearchQueryBytes)
        {

            return new Error(
                ErrorCodes.Validation.InvalidQuery,
                $"A Covenant search is at most {CovenantLimits.MaxSearchQueryBytes} UTF-8 bytes.");

        }

        int terms = 0;

        foreach (Range segment in query.AsSpan().SplitAny(" \t\r\n"))
        {

            if (!query.AsSpan()[segment].IsEmpty)
            {

                terms++;

            }

        }

        return terms is > 0 and <= CovenantLimits.MaxSearchQueryTerms
            ? Result.Success()
            : new Error(
                ErrorCodes.Validation.InvalidQuery,
                $"A Covenant search carries between 1 and {CovenantLimits.MaxSearchQueryTerms} terms.");

    }

    internal static Result RequireIdentity(Guid value, string subject) =>
        value != Guid.Empty
            ? Result.Success()
            : InvalidBody($"A {subject} is required.");

    internal static Result RequireNonNegative(long value, string subject) =>
        value >= 0
            ? Result.Success()
            : InvalidBody($"A {subject} cannot be negative.");

    /// <summary>
    /// Refuses a revision that names no existing head.
    /// </summary>
    /// <remarks>
    /// Zero and one are the same request for an operation that can only follow an existing version.
    /// Admitting zero and clamping it later mints a preflight token whose digest is the digest for
    /// revision one, which makes a token prepared for one request usable for another.
    /// </remarks>
    internal static Result RequirePositive(long value, string subject) =>
        value > 0
            ? Result.Success()
            : InvalidBody($"A {subject} must name an existing version.");

    /// <summary>
    /// Validates a stable apply-request digest on the wire: exactly 64 hexadecimal characters.
    /// </summary>
    /// <remarks>
    /// Length and alphabet are checked rather than decoded, because the value is compared against a
    /// stored digest and never used to size an allocation. A near-miss length is the interesting
    /// failure: it is what a truncated copy-paste produces, and admitting it would turn an exact
    /// replay check into a prefix match.
    /// </remarks>
    internal static Result ValidateApplyRequestDigest(string? digest)
    {

        if (digest is null || digest.Length != DigestHexCharacters)
        {

            return InvalidBody($"An apply-request digest is exactly {DigestHexCharacters} hexadecimal characters.");

        }

        foreach (char character in digest)
        {

            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
            {

                return InvalidBody("An apply-request digest is hexadecimal.");

            }

        }

        return Result.Success();

    }

    /// <summary>
    /// Runs an ordered set of checks and returns the first failure, so a caller learns one reason
    /// rather than a list it has to rank itself.
    /// </summary>
    internal static Result First(params ReadOnlySpan<Result> checks)
    {

        foreach (Result check in checks)
        {

            if (check.IsFailure)
            {

                return check;

            }

        }

        return Result.Success();

    }

}
