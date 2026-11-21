using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal delegate Task<Result> GuildWorkspaceLeaseAdmissionCheck(SqliteConnection connection, SqliteTransaction transaction,
    GuildWorkspaceLeaseBinding binding, CancellationToken cancellationToken);

internal sealed record GuildWorkspaceLeaseToolAdmission(GuildWorkspaceLeaseAdmissionCheck Check,
    Func<GuildWorkspaceLeaseMutationReceipt, bool, Result> Project);

internal sealed record GuildWorkspaceLeaseToolRequest(GuildAuthorityAction Action, GuildMutationIdentity Mutation,
    Guid WorkspaceId, ImmutableArray<GuildPathScope> Scopes, Guid LeaseId, long Epoch, long ExpectedRevision)
{
    internal static Result<GuildWorkspaceLeaseToolRequest> Parse(GuildChildToolAdmission admission, Guid workspaceId)
    {
        JsonElement args = admission.Arguments;

        Result<Guid> key = GuildTaskArguments.Mutation(args, admission.LogicalCallId);

        if (key.IsFailure) return key.Error;

        if (admission.Tool.Name == "acquire_workspace_lease")
        {
            if (!GuildTaskArguments.Closed(args, "scopes", "idempotencyKey")
                || !args.TryGetProperty("scopes", out JsonElement scopes) || scopes.ValueKind != JsonValueKind.Array)
                return GuildTaskArguments.Invalid;

            ImmutableArray<GuildPathScope>.Builder parsed = ImmutableArray.CreateBuilder<GuildPathScope>();

            foreach (JsonElement scope in scopes.EnumerateArray())
            {
                if (!GuildTaskArguments.Closed(scope, "relativePath", "kind")
                    || !GuildTaskArguments.Text(scope, "relativePath", out string? path)
                    || !GuildTaskArguments.Text(scope, "kind", out string? kind)
                    || path is null || kind is not ("file" or "subtree")) return GuildTaskArguments.Invalid;

                parsed.Add(new(path, kind == "file" ? GuildPathScopeKind.File : GuildPathScopeKind.Subtree));
            }

            if (parsed.Count == 0) return GuildTaskArguments.Invalid;

            ImmutableArray<GuildPathScope> paths = parsed.ToImmutable();

            byte[] wire = JsonSerializer.SerializeToUtf8Bytes(new AcquireWorkspaceLeaseRequest(key.Value, workspaceId, paths),
                GuildJsonContext.Default.AcquireWorkspaceLeaseRequest);

            if (wire.Length > GuildStore.MaximumContentBytes) return new Error(ErrorCodes.Guild.PayloadTooLarge,
                "The lease scope metadata exceeds its encoded representation bound.");

            return new GuildWorkspaceLeaseToolRequest(GuildAuthorityAction.LeaseAcquire, Identity(wire), workspaceId, paths, Guid.Empty, 0, 0);
        }

        if (!GuildTaskArguments.Closed(args, "leaseId", "epoch", "expectedRevision", "idempotencyKey")
            || !GuildTaskArguments.Identity(args, "leaseId", out Guid lease)
            || !PositiveInteger(args, "epoch", out long epoch) || !PositiveInteger(args, "expectedRevision", out long revision))
            return GuildTaskArguments.Invalid;

        GuildAuthorityAction action = admission.Tool.Name switch
        {
            "renew_workspace_lease" => GuildAuthorityAction.LeaseRenew,
            "release_workspace_lease" => GuildAuthorityAction.LeaseRelease,
            _ => default,
        };

        if (action is not (GuildAuthorityAction.LeaseRenew or GuildAuthorityAction.LeaseRelease)) return GuildTaskArguments.Invalid;

        // Epoch and revision have the same canonical wire representation for renewal and release.
        byte[] change = JsonSerializer.SerializeToUtf8Bytes(new RenewWorkspaceLeaseRequest(key.Value, revision, epoch),
            GuildJsonContext.Default.RenewWorkspaceLeaseRequest);

        return new GuildWorkspaceLeaseToolRequest(action, Identity(change, lease), workspaceId, [], lease, epoch, revision);

        GuildMutationIdentity Identity(byte[] payload, Guid? target = null)
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            hash.AppendData(System.Text.Encoding.UTF8.GetBytes($"guild-lease-tool:{admission.Tool.Name}:{workspaceId:N}:{target:N}:"));

            hash.AppendData(payload);

            return new(key.Value, Convert.ToHexString(hash.GetHashAndReset()));
        }
    }

    private static bool PositiveInteger(JsonElement args, string name, out long value)
    {
        value = 0;

        return args.TryGetProperty(name, out JsonElement number) && number.ValueKind == JsonValueKind.Number
            && number.TryGetInt64(out value) && value > 0;
    }
}
