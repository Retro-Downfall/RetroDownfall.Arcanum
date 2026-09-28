using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Cli.Commands.Tower;

public sealed partial class MemoryCommands
{
    public async Task<int> LexiconShow(string name, Guid? campaignId, CancellationToken cancellationToken)
    {
        LexiconCurationScope scope = ExactLexiconScope(campaignId);

        if (scope.Validate() is { IsFailure: true } invalid)
        {
            return WriteLexiconInputError(invalid.Error.Message);
        }

        Result<LexiconEntryDetail> result = await apiClient
            .ShowLexiconAsync(new(name, scope), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return WriteError(result.Error);
        }

        if (CliInvocationContext.Current.Json)
        {
            dispatcher.WriteJson(result.Value, ArcanumJsonContext.Default.LexiconEntryDetail);
        }
        else
        {
            WriteLexiconDetail(result.Value, dispatcher.WritePayload);
        }

        return (int)CliExitCode.Success;
    }

    public async Task<int> LexiconCorrect(string name, Guid? campaignId, string file, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return WriteLexiconInputError("Lexicon correction requires --file <path|->.");
        }

        if (file == "-" && !CliInvocationContext.Current.Yes)
        {
            return WriteLexiconInputError("Lexicon correction with --file - requires --yes before reading standard input.");
        }

        Result<string> authored = await AuthoredContentReader.ReadAsync(
            file, "Lexicon correction", null, cancellationToken).ConfigureAwait(false);

        if (authored.IsFailure)
        {
            return WriteLexiconInputError(authored.Error.Message);
        }

        LexiconReplacementContent? replacement;

        try
        {
            replacement = JsonSerializer.Deserialize(authored.Value, ArcanumJsonContext.Default.LexiconReplacementContent);
        }
        catch (JsonException)
        {
            return WriteLexiconInputError("Lexicon correction requires a JSON object containing type and a complete facts array.");
        }

        Result<LexiconCanonicalValue> normalized = LexiconValueNormalizer.NormalizeCorrection(
            name, replacement?.Type, replacement?.Facts);

        if (normalized.IsFailure)
        {
            return WriteLexiconInputError(normalized.Error.Message);
        }

        LexiconReplacementContent content = new(normalized.Value.Type, [.. normalized.Value.Facts]);

        return await CurateLexiconAsync(name, campaignId, "correct", content, cancellationToken).ConfigureAwait(false);
    }

    public Task<int> LexiconRetire(string name, Guid? campaignId, CancellationToken cancellationToken) =>
        CurateLexiconAsync(name, campaignId, "retire", null, cancellationToken);

    public Task<int> LexiconReinstate(string name, Guid? campaignId, CancellationToken cancellationToken) =>
        CurateLexiconAsync(name, campaignId, "reinstate", null, cancellationToken);

    public Task<int> LexiconPin(string name, Guid? campaignId, CancellationToken cancellationToken) =>
        CurateLexiconAsync(name, campaignId, "pin", null, cancellationToken);

    public Task<int> LexiconUnpin(string name, Guid? campaignId, CancellationToken cancellationToken) =>
        CurateLexiconAsync(name, campaignId, "unpin", null, cancellationToken);

    private async Task<int> CurateLexiconAsync(string name, Guid? campaignId, string verb,
        LexiconReplacementContent? content, CancellationToken cancellationToken)
    {
        LexiconCurationScope scope = ExactLexiconScope(campaignId);

        if (scope.Validate() is { IsFailure: true } invalid)
        {
            return WriteLexiconInputError(invalid.Error.Message);
        }

        Result<LexiconEntryDetail> shown = await apiClient
            .ShowLexiconAsync(new(name, scope), cancellationToken)
            .ConfigureAwait(false);

        if (shown.IsFailure)
        {
            return WriteError(shown.Error);
        }

        LexiconEntryDetail detail = shown.Value;

        Action<string> writePreflight = CliInvocationContext.Current.Json
            ? dispatcher.WriteDiagnostic
            : dispatcher.WritePayload;

        WriteLexiconDetail(detail, writePreflight);

        writePreflight($"Effect: {LexiconEffect(verb)}");

        if (content is not null)
        {
            writePreflight($"Replacement type: {content.Type}");

            foreach (string fact in content.Facts)
            {
                writePreflight($"  - {fact}");
            }
        }

        if (!CliInvocationContext.Current.Yes
            && !await confirmationPrompt.PromptForConfirmationAsync(
                $"Apply Lexicon {verb} to '{detail.Entry.Name}' in {LexiconScopeText(detail.Scope)}?",
                cancellationToken).ConfigureAwait(false))
        {
            dispatcher.WriteDiagnostic($"Lexicon {verb} cancelled.");

            if (CliInvocationContext.Current.Json)
            {
                dispatcher.WriteJson(detail, ArcanumJsonContext.Default.LexiconEntryDetail);
            }

            return (int)CliExitCode.Success;
        }

        // Forward every piece of the measured target, including lifecycle, Annals, and label evidence.
        // The host owns normalization, scope selection, and snapshot identity; none is reconstructed here.
        Result<LexiconCurationResult> result = await (verb switch
        {
            "correct" => apiClient.CorrectLexiconAsync(new(detail.Target, content!), cancellationToken),
            "retire" => apiClient.RetireLexiconAsync(new(detail.Target), cancellationToken),
            "reinstate" => apiClient.ReinstateLexiconAsync(new(detail.Target), cancellationToken),
            "pin" => apiClient.PinLexiconAsync(new(detail.Target), cancellationToken),
            "unpin" => apiClient.UnpinLexiconAsync(new(detail.Target), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(verb)),
        }).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return WriteError(result.Error);
        }

        if (CliInvocationContext.Current.Json)
        {
            dispatcher.WriteJson(result.Value, ArcanumJsonContext.Default.LexiconCurationResult);
        }
        else
        {
            string outcome = result.Value.Outcome switch
            {
                LexiconCurationOutcomeKind.Applied => verb switch
                {
                    "correct" => "Corrected",
                    "retire" => "Retired",
                    "reinstate" => "Reinstated",
                    "pin" => "Pinned",
                    "unpin" => "Unpinned",
                    _ => throw new ArgumentOutOfRangeException(nameof(verb)),
                },
                LexiconCurationOutcomeKind.Unchanged => "Unchanged",
                LexiconCurationOutcomeKind.AlreadyRetired => "Already retired",
                LexiconCurationOutcomeKind.NotRetired => "Not retired",
                LexiconCurationOutcomeKind.AlreadyPinned => "Already pinned",
                LexiconCurationOutcomeKind.NotPinned => "Not pinned",
                _ => throw new InvalidOperationException("The host returned an unknown Lexicon curation outcome."),
            };

            string unchanged = result.Value.Outcome == LexiconCurationOutcomeKind.Applied ? "" : " Nothing was written.";

            dispatcher.WritePayload($"{outcome}: Lexicon entity '{result.Value.Entry.Entry.Name}' in {LexiconScopeText(result.Value.Entry.Scope)}.{unchanged}");
        }

        return (int)CliExitCode.Success;
    }

    private static void WriteLexiconDetail(LexiconEntryDetail detail, Action<string> write)
    {
        write($"Lexicon entity '{detail.Entry.Name}' ({detail.Entry.Type}).");

        write($"  Scope: {LexiconScopeText(detail.Scope)}");

        write($"  Entry: {detail.Entry.Id:D}; generation: {detail.CurationGeneration}");

        write($"  Retrieval: {detail.Eligibility}; retired: {Stamp(detail.Lifecycle.RetiredAtUtc) ?? "not retired"}; pinned: {Stamp(detail.Lifecycle.PinnedAtUtc) ?? "not pinned"}");

        write($"  Snapshot digest: {detail.SnapshotDigest}");

        write($"  Origin: {detail.CurrentOrigin?.ToString() ?? "no claim"}; updated: {Stamp(detail.Entry.UpdatedAt)}");

        foreach (string fact in detail.Entry.Facts)
        {
            write($"  - {fact}");
        }

        LexiconCurationAnnalHead head = detail.Target.AnnalHead;

        write(head.IsPresent
            ? $"Annals: claim '{head.ClaimId}', version '{head.VersionId}', revision {head.Revision}, {head.Operation}, {head.ContentHashFormat}, digest {head.ContentHash ?? "none"}."
            : "Annals: no claim is recorded for this entry.");

        foreach (AnnalClaimVersion version in detail.AnnalHistory)
        {
            write($"  revision {version.Revision}: {version.Operation}, {version.Origin}, recorded {Stamp(version.RecordedAtUtc)}");
        }

        LexiconCurationSensitivityLabel label = detail.Target.SensitivityLabel;

        write(label.IsPresent
            ? $"Sensitivity: label '{label.LabelId}', artifact revision {label.ArtifactRevision}, digest {label.ArtifactContentDigest}."
            : "Sensitivity: no label is recorded for this entry.");
    }

    private static string LexiconEffect(string verb) => verb switch
    {
        "correct" => "Replace the type and complete facts collection of this exact entry.",
        "retire" => "Remove this entry from retrieval while retaining it for inspection. An eligible Global entry may become visible to its Campaign.",
        "reinstate" => "Make this retained entry eligible for retrieval again.",
        "pin" => "Protect this entry from automatic retention pruning.",
        "unpin" => "Allow automatic retention to consider this entry again.",
        _ => throw new ArgumentOutOfRangeException(nameof(verb)),
    };

    private static LexiconCurationScope ExactLexiconScope(Guid? campaignId) =>
        campaignId is { } id ? new(LexiconScopeKind.Campaign, id) : new(LexiconScopeKind.Global, null);

    private static string LexiconScopeText(LexiconCurationScope scope) =>
        scope.Kind == LexiconScopeKind.Global ? "Global" : $"Campaign {scope.CampaignId:D}";

    private int WriteLexiconInputError(string message)
    {
        dispatcher.WriteDiagnostic(message);

        return (int)CliExitCode.ConfigurationError;
    }
}
