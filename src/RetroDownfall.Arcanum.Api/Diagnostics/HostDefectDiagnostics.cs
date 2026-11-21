using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Coordination;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Api.Diagnostics;

internal sealed class HostDefectDiagnostics(IHostDefectProbe probe, GitHubHostDefectTracker tracker)
{
    internal const string Missing = "HostDefect.NotFound";

    internal const string Unconfirmed = "HostDefect.NotConfirmed";

    private const string IssueTitle = "Ollama tool continuation fails the Arcanum wire contract";

    private const string Marker = "<!-- arcanum-host-defect:ollama-tool-continuation-v1 -->";

    private const string Reproduction = "Run the controlled Ollama continuation diagnostic using the affected Arcanum build and response surface.";

    private const string Expected = "The next same-provider request carries assistant reasoning and the ordered tool call and result with the same call identity.";

    private const string Uncertainty = "The controlled fixture measures the host request only. Live-provider and model behavior remain unverified.";

    private readonly SemaphoreSlim _filing = new(1, 1);

    private static string DirectoryPath => Path.Combine(ArcanumPaths.GrimoireDirectory, "host-defects");

    internal async Task<Result<HostDefectRecord>> DiagnoseAsync(HostDefectProbeRequest request, IToolInvocationStore? invocations,
        CancellationToken cancellationToken)
    {
        if (request.SessionId.HasValue != request.InvocationId.HasValue
            || request.SessionId == Guid.Empty || request.InvocationId == Guid.Empty)
            return new Error(ErrorCodes.Validation.InvalidBody, "Provide a valid Session and invocation together.");

        if (request.SessionId is { } session && request.InvocationId is { } invocation)
        {
            if (invocations is null) return CorrelationUnavailable();

            Result<ToolInvocationEvidence?> lookup = await invocations.ReadAsync(session, invocation, cancellationToken).ConfigureAwait(false);

            if (lookup.IsFailure || lookup.Value is null) return CorrelationUnavailable();
        }

        HostDefectProbeObservation observation = await probe.ObserveAsync(request.Streaming, cancellationToken).ConfigureAwait(false);

        string observed = observation.Disposition switch
        {
            HostDefectDisposition.ContractSatisfied => "The captured follow-up request carried the required continuation fields.",
            HostDefectDisposition.ConfirmedHostDefect => "The captured follow-up request omitted or changed the required continuation fields.",
            _ => "The fixture did not produce a qualifying follow-up observation.",
        };

        HostDefectRecord record = new(Guid.NewGuid(), OllamaContinuationDefectProbe.Contract, BuildIdentity(),
            request.Streaming ? "streaming" : "buffered", "ollama", OllamaContinuationDefectProbe.Provider,
            OllamaContinuationDefectProbe.Model, observation.Disposition, observation.ProviderRequests, Reproduction, Expected,
            observed, "Controlled first reply and serialized follow-up through the production adapter and tool-exchange projection.",
            Uncertainty, SessionId: request.SessionId, InvocationId: request.InvocationId);

        try
        {
            return await SaveAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return StorageUnavailable();
        }
    }

    internal async Task<Result<HostDefectRecord>> ReadAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var directory = NoFollowPathTopology.Classify(DirectoryPath);

            if (directory.IsFailure || directory.Value == NoFollowPathTopologyKind.RegularFile) return StorageUnavailable();

            if (directory.Value == NoFollowPathTopologyKind.Absent) return MissingRecord();

            using SecureFileReadResult input = await SecureFileReader.ReadBytesAsync(ReportPath(id), 32_768, cancellationToken,
                requireOwnerControlled: true).ConfigureAwait(false);

            if (input.Status == SecureFileReadStatus.NotFound) return MissingRecord();

            if (input.Status != SecureFileReadStatus.Success) return StorageUnavailable();

            HostDefectRecord? record = JsonSerializer.Deserialize(input.Bytes.Span, ArcanumJsonContext.Default.HostDefectRecord);

            if (record is null || record.Id != id) return StorageUnavailable();

            return record;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return StorageUnavailable();
        }
    }

    internal async Task<Result<HostDefectRecord>> TrackAsync(Guid id, CancellationToken cancellationToken)
    {
        await _filing.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using RetainedExclusiveFileLock? gate = RetainedExclusiveFileLock.Acquire(Path.Combine(DirectoryPath, "filing.lock")).Lock;

            if (gate is null) return StorageUnavailable();

            Result<HostDefectRecord> loaded = await ReadAsync(id, cancellationToken).ConfigureAwait(false);

            if (loaded.IsFailure) return loaded.Error;

            HostDefectRecord record = loaded.Value;

            if (record is not { Contract: OllamaContinuationDefectProbe.Contract,
                Disposition: HostDefectDisposition.ConfirmedHostDefect, ProviderRequests: 2 })
                return new Error(Unconfirmed, "Tracker filing requires a confirmed host defect from the controlled diagnostic.");

            if (record is { TrackerStatus: HostDefectTrackerStatus.Linked, TrackerParentLinked: true, TrackerProjectLinked: true }) return record;

            GitHubHostDefectTracker.Capability? capability = GitHubHostDefectTracker.ReadCapability();

            bool fixedRepository = record.TrackerStatus == HostDefectTrackerStatus.ReconciliationRequired || record.TrackerIssue is not null;

            if (capability is null
                || fixedRepository && record.TrackerRepository is { } previous && previous != capability.Repository)
                return await SetTrackerAsync(record, UnavailableStatus(record), cancellationToken).ConfigureAwait(false);

            return await TrackAuthorizedAsync(record with { TrackerRepository = capability.Repository }, capability, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return StorageUnavailable();
        }
        finally
        {
            _filing.Release();
        }
    }

    private async Task<HostDefectRecord> TrackAuthorizedAsync(HostDefectRecord record,
        GitHubHostDefectTracker.Capability capability, CancellationToken cancellationToken)
    {
        string build = SafeBuild(record.Build);
        string surface = record.Surface == "streaming" ? "streaming" : "buffered";
        string evidence = $"<!-- arcanum-host-reproduction:{Digest(build + "|" + surface)} -->";
        string report = $"{evidence}\n\nBuild `{build}` on the {surface} response surface.\n\n"
            + $"### Reproduction\n\n{Reproduction}\n\n### Expected behavior\n\n{Expected}\n\n"
            + "### Observed behavior\n\nThe serialized follow-up omitted or changed required continuation fields.\n\n"
            + $"### Regression evidence\n\nTwo controlled provider requests through the production Ollama adapter.\n\n{Uncertainty}";

        int? retainedIssue = null;

        if (record.TrackerIssue is { } prior)
        {
            string prefix = $"https://github.com/{capability.Repository}/issues/";

            if (!prior.StartsWith(prefix, StringComparison.Ordinal)
                || !int.TryParse(prior.AsSpan(prefix.Length), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int number) || number <= 0)
                return await SetTrackerAsync(record, UnavailableStatus(record), cancellationToken).ConfigureAwait(false);

            retainedIssue = number;
        }

        var existing = await tracker.FindIssueAsync(capability, Marker, evidence, cancellationToken, retainedIssue).ConfigureAwait(false);

        if (!existing.Complete)
            return await SetTrackerAsync(record, UnavailableStatus(record), cancellationToken).ConfigureAwait(false);

        if (existing.Issue is { } found)
        {
            string link = IssueLink(capability, found);

            if (existing.EvidenceInIssue) return await SetMembershipAsync(record, capability, found, cancellationToken).ConfigureAwait(false);

            var comments = await tracker.FindEvidenceAsync(capability, found, evidence, cancellationToken).ConfigureAwait(false);

            if (!comments.Complete)
                return await SetTrackerAsync(record, UnavailableStatus(record with { TrackerIssue = link }), cancellationToken, link).ConfigureAwait(false);

            if (comments.Found) return await SetMembershipAsync(record, capability, found, cancellationToken).ConfigureAwait(false);

            string pendingComment = PendingPath(capability.Repository, $"comment-{found}-{evidence}");

            bool mayPost = await MarkPossibleSendAsync(record, pendingComment, cancellationToken, link).ConfigureAwait(false);

            if (!mayPost) return record with { TrackerStatus = HostDefectTrackerStatus.ReconciliationRequired, TrackerIssue = link };

            var added = await tracker.AddEvidenceAsync(capability, found, report, cancellationToken).ConfigureAwait(false);

            if (added.Rejected) return await RecordRejectedAsync(record with { TrackerIssue = link }, pendingComment, cancellationToken).ConfigureAwait(false);

            return added.Id > 0
                ? await SetMembershipAsync(record, capability, found, cancellationToken).ConfigureAwait(false)
                : record with { TrackerStatus = HostDefectTrackerStatus.ReconciliationRequired, TrackerIssue = link };
        }

        if (record.TrackerIssue is not null)
            return await SetTrackerAsync(record, UnavailableStatus(record), cancellationToken).ConfigureAwait(false);

        string pendingIssue = PendingPath(capability.Repository, "issue");

        if (!await MarkPossibleSendAsync(record, pendingIssue, cancellationToken).ConfigureAwait(false))
            return record with { TrackerStatus = HostDefectTrackerStatus.ReconciliationRequired };

        var created = await tracker.CreateAsync(capability, IssueTitle, Marker + "\n" + report, cancellationToken).ConfigureAwait(false);

        if (created.Rejected) return await RecordRejectedAsync(record, pendingIssue, cancellationToken).ConfigureAwait(false);

        return created.Id > 0
            ? await SetMembershipAsync(record, capability, (int)created.Id, cancellationToken).ConfigureAwait(false)
            : record with { TrackerStatus = HostDefectTrackerStatus.ReconciliationRequired };
    }

    private async Task<HostDefectRecord> SetMembershipAsync(HostDefectRecord record, GitHubHostDefectTracker.Capability capability,
        int issue, CancellationToken cancellationToken)
    {
        record = await SetTrackerAsync(record, HostDefectTrackerStatus.PostedUnlinked, cancellationToken,
            IssueLink(capability, issue)).ConfigureAwait(false);

        GitHubHostDefectTracker.Membership membership = await tracker.EnsureMembershipAsync(capability, issue, Marker, cancellationToken)
            .ConfigureAwait(false);

        return await SaveAsync(record with
        {
            TrackerStatus = membership is { ParentLinked: true, ProjectLinked: true }
                ? HostDefectTrackerStatus.Linked : HostDefectTrackerStatus.PostedUnlinked,
            TrackerParentLinked = membership.ParentLinked,
            TrackerProjectLinked = membership.ProjectLinked,
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HostDefectRecord> RecordRejectedAsync(HostDefectRecord record, string pending,
        CancellationToken cancellationToken)
    {
        File.Delete(pending);

        return await SetTrackerAsync(record, record.TrackerIssue is null ? HostDefectTrackerStatus.Unavailable
            : HostDefectTrackerStatus.PostedUnlinked, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> MarkPossibleSendAsync(HostDefectRecord record, string pending,
        CancellationToken cancellationToken, string? link = null)
    {
        bool mayPost = !File.Exists(pending);

        if (mayPost) await OwnerOnlyAtomicFile.WriteAsync(pending, "possible-send"u8.ToArray(), cancellationToken).ConfigureAwait(false);

        _ = await SetTrackerAsync(record, HostDefectTrackerStatus.ReconciliationRequired, cancellationToken, link).ConfigureAwait(false);

        return mayPost;
    }

    private static Task<HostDefectRecord> SetTrackerAsync(HostDefectRecord record, HostDefectTrackerStatus status,
        CancellationToken cancellationToken, string? link = null) =>
        SaveAsync(record with { TrackerStatus = status, TrackerIssue = link ?? record.TrackerIssue }, cancellationToken);

    private static async Task<HostDefectRecord> SaveAsync(HostDefectRecord record, CancellationToken cancellationToken)
    {
        var directory = NoFollowPathTopology.Classify(DirectoryPath);

        if (directory.IsFailure || directory.Value == NoFollowPathTopologyKind.RegularFile)
            throw new IOException("The host diagnostic directory is unavailable.");

        SecureFilePermissions.RequireOwnerOnlyDirectory(DirectoryPath);

        await OwnerOnlyAtomicFile.WriteAsync(ReportPath(record.Id),
            JsonSerializer.SerializeToUtf8Bytes(record, ArcanumJsonContext.Default.HostDefectRecord), cancellationToken).ConfigureAwait(false);

        return record;
    }

    private static string ReportPath(Guid id) => Path.Combine(DirectoryPath, $"{id:N}.json");

    private static string PendingPath(string repository, string effect) =>
        Path.Combine(DirectoryPath, "send-" + Digest(repository + "|" + OllamaContinuationDefectProbe.Contract + "|" + effect));

    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string IssueLink(GitHubHostDefectTracker.Capability capability, int number) =>
        $"https://github.com/{capability.Repository}/issues/{number}";

    private static HostDefectTrackerStatus UnavailableStatus(HostDefectRecord record) => record.TrackerStatus switch
    {
        HostDefectTrackerStatus.ReconciliationRequired => HostDefectTrackerStatus.ReconciliationRequired,
        _ => record.TrackerIssue is null ? HostDefectTrackerStatus.Unavailable : HostDefectTrackerStatus.PostedUnlinked,
    };

    private static string BuildIdentity()
    {
        Assembly assembly = typeof(HostDefectDiagnostics).Assembly;

        return SafeBuild(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString());
    }

    private static string SafeBuild(string? build) => build is { Length: > 0 and <= 128 }
        && build.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '+' or '-' or '_')
            ? build : "unavailable";

    private static Error CorrelationUnavailable() => new(Missing, "The specified Session invocation is unavailable.");

    private static Error MissingRecord() => new(Missing, "The retained host diagnostic is unavailable.");

    private static Error StorageUnavailable() => new("HostDefect.StorageUnavailable", "The local host diagnostic record is unavailable.");
}
