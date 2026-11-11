using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class SignetAuthority
{
    private bool IsCurrentOperatorAdmission(GuildAdmissionContext admission) => admission.Caller.Kind == GuildCallerKind.Operator
        && admission.Caller.Execution is null && admission.Caller.InstallationGeneration == policy.InstallationGeneration;

    private Task<Result<GuildSignetAdmissionEvidence>> AuthorizeOperatorChildAsync(GuildAdmissionContext admission,
        GuildSignetBinding binding, CancellationToken ct) => policy.AuthorizeAdmissionAsync(admission, binding, ct);

    internal sealed partial class QuestTransaction
    {
        /// <summary>An authenticated operator manages an award under its actual issuer without acquiring that run's credential.</summary>
        internal async Task<Result<GuildSignetRecord>> IssueOperatorChildAsync(GuildAdmissionContext admission,
            GuildSignetBinding binding, Guid issuerQuestId, GuildRequestedGrant request, CancellationToken cancellationToken)
        {
            if (!Usable) return GuildSignetStore.Unavailable;

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime, cancellationToken);

            await _serial.WaitAsync(linked.Token).ConfigureAwait(false);

            try
            {
                if (!Usable) return GuildSignetStore.Unavailable;

                if (admission is null || binding is null || request is null || issuerQuestId == Guid.Empty
                    || !_authority.IsCurrentOperatorAdmission(admission)
                    || !_authority.ValidBinding(binding) || binding.RootQuestId != _root || binding.QuestId == issuerQuestId
                    || binding.OriginPrincipalId != admission.Caller.PrincipalId) return Mismatch;

                var issuer = await GuildSignetStore.ReadAsync(_connection, _transaction, issuerQuestId, true, linked.Token).ConfigureAwait(false);

                if (issuer.IsFailure) return issuer.Error;

                if (issuer.Value.Binding.RootQuestId != _root || binding.IssuerSignetId != issuer.Value.Binding.SignetId
                    || binding.RunId == issuer.Value.Binding.RunId || binding.OriginPrincipalId != issuer.Value.Binding.OriginPrincipalId)
                    return Mismatch;

                var authorized = await _authority.AuthorizeOperatorChildAsync(admission, binding, linked.Token).ConfigureAwait(false);

                if (authorized.IsFailure) return authorized.Error;

                if (authorized.Value.OperatorIssuerQuestId != issuerQuestId
                    || authorized.Value.Edge is not (GuildSignetIssuanceEdge.LocalCohort or GuildSignetIssuanceEdge.RemoteCohort)) return Mismatch;

                var live = await _authority.LiveRowAsync(_connection, _transaction, issuer.Value, linked.Token).ConfigureAwait(false);

                if (live.IsFailure) return live.Error;

                using Credential credential = Credential.Create();

                var issued = await _authority.AttenuateVerifiedWithinAsync(_connection, _transaction, live.Value, binding,
                    request, authorized.Value, credential, linked.Token).ConfigureAwait(false);

                if (issued.IsSuccess) _pending.Add(new(issued.Value, credential.Take()));

                return issued;
            }
            finally
            {
                _serial.Release();
            }
        }
    }
}
