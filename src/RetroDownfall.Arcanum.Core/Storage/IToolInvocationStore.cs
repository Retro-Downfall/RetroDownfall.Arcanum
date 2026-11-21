using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>The host's Session slot and the immutable payload of one tool invocation.</summary>
public sealed record ToolInvocationKey(Guid SessionId, Guid AssistantEntryId, int Round, int Call,
    ToolInteractionReceipt Receipt, CovenantDigest ArgumentsDigest)
{
    public static ToolInvocationKey Capture(Guid sessionId, Guid assistantEntryId, ToolInvocationIdentity identity, string arguments)
    {
        ArgumentNullException.ThrowIfNull(identity);

        ArgumentNullException.ThrowIfNull(arguments);

        byte[] bytes = Encoding.UTF8.GetBytes(arguments.Length == 0 ? "{}" : arguments);

        if (bytes.Length <= ArcanumCanonicalJsonV1.MaxInputUtf8Bytes)
        {
            try
            {
                bytes = ArcanumCanonicalJsonV1.Canonicalize(bytes);
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException)
            {
                // Preserve the exact snapshot for tools that own a different argument grammar.
            }
        }

        return new(sessionId, assistantEntryId, identity.ToolRoundOrdinal, identity.CallOrdinal,
            ToolInteractionReceiptDerivation.Derive(identity), new CovenantDigest(SHA256.HashData(bytes)));
    }
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<ToolInvocationState>))]
public enum ToolInvocationState
{
    [JsonStringEnumMemberName("prepared")]
    Prepared = 1,

    [JsonStringEnumMemberName("dispatched")]
    Dispatched,

    [JsonStringEnumMemberName("completed")]
    Completed,
}

/// <summary>Content-free evidence about the exact Session tool invocation.</summary>
public sealed record ToolInvocationEvidence(Guid ReceiptId, Guid AssistantEntryId, int Round, int Call,
    ToolInvocationState State, DateTimeOffset CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ResultEntryId);

public sealed record ToolInvocationDispatch(ToolInvocationEvidence Evidence, Guid? DispatchId)
{
    public bool MayDispatch => DispatchId is not null;
}

internal sealed record ToolInvocationCompletion(ToolInvocationKey Key, ToolInvocationIdentity Identity, Guid DispatchId);

public sealed record ToolInvocationTurnProgress(Guid? OutstandingInvocationId, Guid? LastVerifiedInvocationId);

public interface IToolInvocationStore
{
    Task<Result<ToolInvocationEvidence>> PrepareAsync(ToolInvocationKey key, SessionTurnClaimLease? owner, CancellationToken cancellationToken);

    Task<Result<ToolInvocationDispatch>> AdmitAsync(ToolInvocationKey key, SessionTurnClaimLease? owner, CancellationToken cancellationToken);

    Task<Result> ReleaseUndispatchedAsync(ToolInvocationKey key, Guid dispatchId, SessionTurnClaimLease? owner, CancellationToken cancellationToken);

    Task<Result> ConfirmAsync(ToolInvocationKey key, Guid resultEntryId, CancellationToken cancellationToken);

    Task<Result<ToolInvocationEvidence?>> ReadAsync(Guid sessionId, Guid receiptId, CancellationToken cancellationToken);

    Task<Result<ToolInvocationTurnProgress>> ReadTurnProgressAsync(Guid sessionId, Guid assistantEntryId, CancellationToken cancellationToken);
}
