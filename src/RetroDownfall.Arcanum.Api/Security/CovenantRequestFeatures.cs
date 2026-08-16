using Microsoft.AspNetCore.Http;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Security;

/// <summary>
/// Declares the exact Covenant authority one endpoint needs.
/// </summary>
/// <remarks>
/// Endpoint metadata rather than a filter argument, because the pre-binding middleware has to read it
/// before any endpoint filter runs. A requirement discovered after model binding is a requirement
/// discovered after the body was read (§10.18).
/// </remarks>
public sealed record CovenantAuthorityRequirementMetadata(CovenantAuthorityRequirement Requirement);

/// <summary>
/// Marks an endpoint as one whose response may carry injected Covenant content, so the
/// <c>X-Arcanum-Context-Policy</c> header is meaningful on it.
/// </summary>
/// <remarks>
/// Presence is the whole contract. A route without this metadata rejects the header outright rather
/// than ignoring it, because a caller that sent <c>none</c> to a route that quietly discards it
/// believes it suppressed something it did not.
/// </remarks>
public sealed record CovenantContextPolicyRequirementMetadata
{

    private CovenantContextPolicyRequirementMetadata()
    {
    }

    public static CovenantContextPolicyRequirementMetadata Instance { get; } = new();

}

/// <summary>
/// Marks an endpoint whose protected arm is decided by a content-free preflight rather than by the
/// route itself.
/// </summary>
/// <remarks>
/// A Session detail is protected only when that Session is tainted, and whether it is tainted is a
/// fact about data, not about the URL. Declaring the route conditional is how the inventory test can
/// still prove that every content-bearing route made a decision, rather than that every route made
/// the same one.
/// </remarks>
public sealed record CovenantConditionalReadRequirementMetadata
{

    private CovenantConditionalReadRequirementMetadata()
    {
    }

    public static CovenantConditionalReadRequirementMetadata Instance { get; } = new();

}

/// <summary>
/// Parses the one legal wire value of <c>X-Arcanum-Context-Policy</c>.
/// </summary>
/// <remarks>
/// Exactly one lowercase ASCII <c>none</c>, or nothing at all. <c>NONE</c>, <c>None</c>, surrounding
/// whitespace, an empty value, and <c>none,none</c> are all refused. The strictness is deliberate:
/// this header suppresses durable memory injection, so a caller that wrote <c>None</c> intending
/// suppression and silently received <c>Default</c> would send content it believed it had excluded. A
/// 400 is recoverable; a permissive parse is a disclosure.
///
/// <para>Repeated headers are refused rather than merged. HTTP permits repetition, and every merge
/// rule — first wins, last wins, comma-join — is a guess about which of two disagreeing senders meant
/// it.</para>
/// </remarks>
internal static class CovenantContextPolicyParser
{

    private const string NoneValue = "none";

    internal static Result<CovenantContextPolicy> Parse(HttpRequest request)
    {

        ArgumentNullException.ThrowIfNull(request);

        if (!request.Headers.TryGetValue(ArcanumApiHeaders.ContextPolicy, out Microsoft.Extensions.Primitives.StringValues values))
        {

            return Result<CovenantContextPolicy>.Success(CovenantContextPolicy.Default);

        }

        if (values.Count != 1)
        {

            return Invalid();

        }

        string? value = values[0];

        return string.Equals(value, NoneValue, StringComparison.Ordinal)
            ? Result<CovenantContextPolicy>.Success(CovenantContextPolicy.None)
            : Invalid();

    }

    private static Result<CovenantContextPolicy> Invalid() =>
        Result<CovenantContextPolicy>.Failure(
            new Error(
                ErrorCodes.Covenant.InvalidScope,
                $"{ArcanumApiHeaders.ContextPolicy} accepts exactly one value, the lowercase literal 'none'."));

}

/// <summary>
/// The two irrevocable facts the authenticated boundary records about one request.
/// </summary>
/// <remarks>
/// Write-once by construction. Later code reads these and cannot replace them: a downstream filter
/// that could relax the context policy would turn "the operator asked for no durable context" from a
/// decision into a suggestion, and one that could swap the authority context would let a route accept
/// authority issued for a different requirement (§10.12).
/// </remarks>
internal static class CovenantRequestFeatures
{

    private const string ContextPolicyKey = "Arcanum.Covenant.ContextPolicyFeature";

    private const string AuthorityKey = "Arcanum.Covenant.AuthorityFeature";

    private const string ProtectedResponseKey = "Arcanum.Covenant.ProtectedResponse";

    internal static void RecordContextPolicy(HttpContext context, CovenantContextPolicy policy)
    {

        ArgumentNullException.ThrowIfNull(context);

        if (context.Items.ContainsKey(ContextPolicyKey))
        {

            throw new InvalidOperationException(
                "The Covenant context policy for this request has already been decided.");

        }

        context.Items[ContextPolicyKey] = new CovenantContextPolicyFeature(policy);

        // The legacy item key the invocation-context resolver already reads. Written here so the one
        // decision has one writer; two writers is how the prompt path and the cache fingerprint end
        // up disagreeing about the same request.
        if (policy is CovenantContextPolicy.None)
        {

            context.Items[Intelligence.ArcanumInvocationContexts.NoContextRequestFeatureKey] = true;

        }

    }

    internal static CovenantContextPolicy ContextPolicy(HttpContext context)
    {

        ArgumentNullException.ThrowIfNull(context);

        return context.Items.TryGetValue(ContextPolicyKey, out object? value)
            && value is CovenantContextPolicyFeature feature
            ? feature.Policy
            : CovenantContextPolicy.Default;

    }

    internal static void RecordAuthority(HttpContext context, CovenantAuthorityFeature feature)
    {

        ArgumentNullException.ThrowIfNull(context);

        ArgumentNullException.ThrowIfNull(feature);

        if (context.Items.ContainsKey(AuthorityKey))
        {

            throw new InvalidOperationException(
                "Covenant authority for this request has already been issued.");

        }

        context.Items[AuthorityKey] = feature;

    }

    internal static CovenantAuthorityFeature? Authority(HttpContext context)
    {

        ArgumentNullException.ThrowIfNull(context);

        return context.Items.TryGetValue(AuthorityKey, out object? value)
            ? value as CovenantAuthorityFeature
            : null;

    }

    /// <summary>
    /// Marks this response as one that will carry, or refuse, protected content.
    /// </summary>
    /// <remarks>
    /// Set as early as the decision is known and never cleared. The streaming writers consult it
    /// before their first byte, and headers cannot be corrected once a stream has begun.
    /// </remarks>
    internal static void MarkProtectedResponse(HttpContext context)
    {

        ArgumentNullException.ThrowIfNull(context);

        if (context.Items.ContainsKey(ProtectedResponseKey))
        {

            return;

        }

        context.Items[ProtectedResponseKey] = true;

        // Registered on response start rather than left to each result type. A handler that returns
        // a plain Results.Ok, a framework-generated 400, or an exception page would otherwise escape
        // the tuple, and "every protected success or failure" has to include the ones nobody wrote a
        // result type for. OnStarting runs after the handler has set its status and before the first
        // byte, which is the only moment both facts are known and headers are still mutable.
        context.Response.OnStarting(
            static state =>
            {

                CovenantProtectedResponseHeaders.Apply(((HttpContext)state).Response);

                return Task.CompletedTask;

            },
            context);

    }

    internal static bool IsProtectedResponse(HttpContext context)
    {

        ArgumentNullException.ThrowIfNull(context);

        return context.Items.TryGetValue(ProtectedResponseKey, out object? value) && value is true;

    }

}

/// <summary>The decided context policy, recorded once at the authenticated boundary.</summary>
internal sealed record CovenantContextPolicyFeature(CovenantContextPolicy Policy);

/// <summary>
/// The typed authority issued for one request, bound to the requirement its endpoint declared and the
/// authority epoch in force when it was issued.
/// </summary>
/// <remarks>
/// <see cref="AuthorityEpoch"/> is <see cref="long"/> to match the shipped
/// <see cref="OperatorAuthorityContext.AuthorityEpoch"/> it is copied from. It is carried separately so
/// the endpoint filter can recheck staleness without reissuing — a reissue would silently adopt
/// whatever generation is now current, which is exactly the mid-flight authority change the recheck
/// exists to catch.
/// </remarks>
internal sealed record CovenantAuthorityFeature(
    OperatorAuthorityContext Context,
    CovenantAuthorityRequirement Requirement,
    long AuthorityEpoch);
