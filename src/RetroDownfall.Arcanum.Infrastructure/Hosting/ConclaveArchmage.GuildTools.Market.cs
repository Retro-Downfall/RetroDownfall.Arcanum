using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal sealed partial class ConclaveArchmage
{
    private async Task<Result<GuildLocalChildToolResult>> DispatchMarketToolAsync(GuildLocalChildToolCall call, CancellationToken ct)
    {
        if (!SupportsCohorts) return UnavailableGuild();

        GuildChildToolAdmission admission = call.Admission;

        GuildCallerContext caller = admission.Registry.Parent.Driver.Caller!;

        GuildMutationIdentity mutation = new(admission.LogicalCallId, admission.ArgumentsDigest);

        string target = call.Kind switch
        {
            GuildLocalChildToolKind.PostWorkOffer or GuildLocalChildToolKind.AppendSharedNote or GuildLocalChildToolKind.ReadSharedNotes => "cohortId",
            GuildLocalChildToolKind.WithdrawBid => "bidId",
            _ => "offerId",
        };

        if (!GuildTaskArguments.Identity(admission.Arguments, target, out Guid id)) return GuildTaskArguments.Invalid;

        if (call.Kind == GuildLocalChildToolKind.PostWorkOffer)
        {
            var parsed = GuildMarketToolArguments.Post(admission);

            if (parsed.IsFailure) return parsed.Error;

            var accepted = await cohorts!.PostOfferAsync(caller, mutation, id, parsed.Value, ct, admission).ConfigureAwait(false);

            return accepted.IsFailure ? accepted.Error : CohortToolResult(call.ParentQuest, admission);
        }

        if (call.Kind == GuildLocalChildToolKind.SubmitBid)
        {
            var parsed = GuildMarketToolArguments.Submit(admission);

            if (parsed.IsFailure) return parsed.Error;

            var accepted = await cohorts!.SubmitBidAsync(admission.Registry.Parent.Driver.Execution, mutation, id, parsed.Value, ct, admission).ConfigureAwait(false);

            return accepted.IsFailure ? accepted.Error : CohortToolResult(call.ParentQuest, admission);
        }

        if (call.Kind is GuildLocalChildToolKind.WithdrawBid or GuildLocalChildToolKind.WithdrawWorkOffer)
        {
            var parsed = GuildMarketToolArguments.Withdraw(admission);

            if (parsed.IsFailure) return parsed.Error;

            if (call.Kind == GuildLocalChildToolKind.WithdrawBid)
            {
                var accepted = await cohorts!.WithdrawBidAsync(admission.Registry.Parent.Driver.Execution, mutation, id, parsed.Value, ct, admission).ConfigureAwait(false);

                return accepted.IsFailure ? accepted.Error : CohortToolResult(call.ParentQuest, admission);
            }

            var withdrawn = await cohorts!.WithdrawOfferAsync(caller, mutation, id, parsed.Value, ct, admission).ConfigureAwait(false);

            return withdrawn.IsFailure ? withdrawn.Error : CohortToolResult(call.ParentQuest, admission);
        }

        if (call.Kind == GuildLocalChildToolKind.AwardWork)
        {
            var parsed = GuildMarketToolArguments.Award(admission);

            if (parsed.IsFailure) return parsed.Error;

            var awarded = await cohorts!.AwardAsync(caller, mutation, id, parsed.Value, ct, admission).ConfigureAwait(false);

            if (awarded.IsFailure) return awarded.Error;

            var child = await quests!.GetAsync(caller, awarded.Value.QuestId, ct).ConfigureAwait(false);

            if (child.IsFailure) return child.Error;

            guildScheduler!.Schedule(child.Value.Quest);

            return CohortToolResult(child.Value.Quest, admission);
        }

        if (call.Kind == GuildLocalChildToolKind.AppendSharedNote)
        {
            var parsed = GuildMarketToolArguments.Append(admission);

            if (parsed.IsFailure) return parsed.Error;

            var appended = await cohorts!.AppendNoteAsync(caller, mutation, id, parsed.Value, ct, admission).ConfigureAwait(false);

            return appended.IsFailure ? appended.Error : CohortToolResult(call.ParentQuest, admission);
        }

        if (call.Kind == GuildLocalChildToolKind.ReadSharedNotes)
        {
            var parsed = GuildMarketToolArguments.Read(admission);

            if (parsed.IsFailure) return parsed.Error;

            var page = await cohorts!.ReadNotesAsync(caller, id, parsed.Value, ct, admission).ConfigureAwait(false);

            return page.IsFailure ? page.Error : CohortToolResult(call.ParentQuest, admission);
        }

        return GuildTaskArguments.Invalid;
    }
}
