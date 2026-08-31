using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

internal enum GrimoireOfflineTransitionKind : byte
{

    CovenantReset = 1,

    HealthyCatalogFactoryErasure = 2,

}

internal enum GrimoireOfflineTransitionAnchorState : byte
{

    Active = 1,

    Closed = 2,

}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GrimoireOfflineTransitionPayloadV1(
    Guid OperationId,
    GrimoireOfflineTransitionKind Kind,
    byte PayloadVersion,
    string PayloadBase64Url);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GrimoireOfflineTransitionEnvelopeV1(
    byte Version,
    CovenantDigest ProfileNamespaceDigest,
    Guid InstallationId,
    ulong SlotEpoch,
    Guid OperationId,
    GrimoireOfflineTransitionKind Kind,
    byte PayloadVersion,
    ulong Revision,
    CovenantDigest PreviousEnvelopeDigest,
    CovenantDigest JournalLocationDigest,
    string NonceBase64Url,
    string CiphertextBase64Url,
    string AuthenticationTagBase64Url);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GrimoireOfflineTransitionAnchorV1(
    byte Version,
    CovenantDigest ProfileNamespaceDigest,
    Guid InstallationId,
    ulong SlotEpoch,
    GrimoireOfflineTransitionAnchorState State,
    Guid? OperationId,
    GrimoireOfflineTransitionKind? Kind,
    byte? PayloadVersion,
    ulong Revision,
    CovenantDigest? EnvelopeDigest,
    CovenantDigest JournalLocationDigest);
