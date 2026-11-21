using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Intelligence.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HostDefectProbeRequest(bool Streaming = false, Guid? SessionId = null, Guid? InvocationId = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HostDefectTrackRequest;

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<HostDefectDisposition>))]
public enum HostDefectDisposition
{
    [JsonStringEnumMemberName("contractSatisfied")]
    ContractSatisfied = 1,

    [JsonStringEnumMemberName("confirmedHostDefect")]
    ConfirmedHostDefect,

    [JsonStringEnumMemberName("inconclusive")]
    Inconclusive,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<HostDefectTrackerStatus>))]
public enum HostDefectTrackerStatus
{
    [JsonStringEnumMemberName("notRequested")]
    NotRequested = 1,

    [JsonStringEnumMemberName("unavailable")]
    Unavailable,

    [JsonStringEnumMemberName("reconciliationRequired")]
    ReconciliationRequired,

    [JsonStringEnumMemberName("linked")]
    Linked,

    [JsonStringEnumMemberName("postedUnlinked")]
    PostedUnlinked,
}

/// <summary>A closed local observation of an Arcanum-owned contract, with optional Session correlation.</summary>
public sealed record HostDefectRecord(
    Guid Id,
    string Contract,
    string Build,
    string Surface,
    string Adapter,
    string Provider,
    string Model,
    HostDefectDisposition Disposition,
    int ProviderRequests,
    string Reproduction,
    string Expected,
    string Observed,
    string RegressionEvidence,
    string Uncertainty,
    HostDefectTrackerStatus TrackerStatus = HostDefectTrackerStatus.NotRequested,
    string? TrackerIssue = null,
    string? TrackerRepository = null,
    Guid? SessionId = null,
    Guid? InvocationId = null,
    bool TrackerParentLinked = false,
    bool TrackerProjectLinked = false);
