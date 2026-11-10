using System.Data;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class SignetAuthority
{
    /// <summary>
    /// Commits one fresh effect admission before its synchronous handoff. The root ordering gate
    /// ends after handoff; installation work and effect admission remain held until the effect settles.
    /// A replay is observation only and cannot invoke the effect again.
    /// </summary>
    internal Task<Result<T>> AdmitQuestEffectAsync<T>(Guid root,
        Func<SqliteConnection, SqliteTransaction, QuestTransaction, CancellationToken, Task<Result<bool>>> admission,
        Func<CancellationToken, Task<Result<T>>> effect, CancellationToken cancellationToken)
    {
        if (admission is null || effect is null)
        {
            return Task.FromResult(Result<T>.Failure(Mismatch));
        }

        return WithInstallationEffectAsync<T>(async token =>
        {
            var acquired = await GuildSignetRootGate.AcquireAsync(gate, root, token).ConfigureAwait(false);

            if (acquired.IsFailure) return acquired.Error;

            using GuildSignetRootGate.RootLease ordering = acquired.Value;

            if (!ordering.Enter()) return GuildSignetStore.Unavailable;

            Result<bool> committed = await ExecuteQuestTransactionAsync(root, admission, token).ConfigureAwait(false);

            if (committed.IsFailure) return committed.Error;

            if (!committed.Value)
            {
                return new Error(ErrorCodes.Guild.ReconciliationRequired,
                    "The recorded effect must be observed; replay cannot authorize another dispatch.");
            }

            token.ThrowIfCancellationRequested();

            Task<Result<T>> pending = effect(token);

            return await ReleaseRootAndObserveAsync(ordering, pending).ConfigureAwait(false);
        }, cancellationToken);
    }

    private static async Task<Result<T>> ReleaseRootAndObserveAsync<T>(GuildSignetRootGate.RootLease ordering,
        Task<Result<T>> pending)
    {
        Result<T> observed;

        try
        {
            ordering.Dispose();
        }
        finally
        {
            // The physical handoff precedes release; even failed ordering cleanup cannot
            // detach it from the installation work and effect lifetime held by the caller.
            observed = await pending.ConfigureAwait(false);
        }

        return observed;
    }

    /// <summary>
    /// Holds installation admission and the exact root gate through one domain write transaction.
    /// The trusted callback only reads and writes that transaction; it must not commit it or perform external effects.
    /// </summary>
    internal Task<Result<T>> InQuestTransactionAsync<T>(Guid root,
        Func<SqliteConnection, SqliteTransaction, QuestTransaction, CancellationToken, Task<Result<T>>> action,
        CancellationToken cancellationToken)
    {
        if (action is null)
        {
            return Task.FromResult(Result<T>.Failure(Mismatch));
        }

        return UnderRootAsync(root, true, token => ExecuteQuestTransactionAsync(root, action, token), cancellationToken);
    }

    private async Task<Result<T>> ExecuteQuestTransactionAsync<T>(Guid root,
        Func<SqliteConnection, SqliteTransaction, QuestTransaction, CancellationToken, Task<Result<T>>> action,
        CancellationToken cancellationToken)
    {
        QuestTransaction? scope = null;

        object completionOwner = new();

        try
        {
            Result<T> committed = await store.ExecuteAsync<T>(true, async (connection, transaction, token) =>
            {
                scope = new QuestTransaction(this, root, connection, transaction, token, completionOwner);

                Result<T> result;

                bool closed;

                try
                {
                    result = await action(connection, transaction, scope, token).ConfigureAwait(false);
                }
                finally
                {
                    closed = await scope.CloseAsync(completionOwner).ConfigureAwait(false);
                }

                return closed ? result : Result<T>.Failure(GuildSignetStore.Unavailable);
            }, cancellationToken).ConfigureAwait(false);

            if (committed.IsSuccess && scope is not null)
            {
                Result registered = scope.RegisterCommitted(completionOwner);

                if (registered.IsFailure)
                {
                    return Result<T>.Failure(registered.Error);
                }
            }

            return committed;
        }
        finally
        {
            scope?.DisposePending(completionOwner);
        }
    }

    /// <summary>One callback's authority participant, fenced to its root and exact live transaction.</summary>
    internal sealed class QuestTransaction
    {
        private readonly SignetAuthority _authority;

        private readonly Guid _root;

        private readonly SqliteConnection _connection;

        private readonly SqliteTransaction _transaction;

        private readonly CancellationToken _lifetime;

        private readonly object _completionOwner;

        private readonly SemaphoreSlim _serial = new(1, 1);

        private readonly List<PendingCredential> _pending = [];

        private int _closed;

        internal QuestTransaction(SignetAuthority authority, Guid root, SqliteConnection connection,
            SqliteTransaction transaction, CancellationToken lifetime, object completionOwner)
        {
            _authority = authority;

            _root = root;

            _connection = connection;

            _transaction = transaction;

            _lifetime = lifetime;

            _completionOwner = completionOwner;
        }

        internal async Task<Result<GuildSignetRecord>> IssueAsync(GuildAdmissionContext admission, GuildSignetBinding binding,
            GuildExecutionHandle? parent, GuildRequestedGrant? request, CancellationToken cancellationToken)
        {
            if (!Usable)
            {
                return GuildSignetStore.Unavailable;
            }

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime, cancellationToken);

            await _serial.WaitAsync(linked.Token).ConfigureAwait(false);

            try
            {
                if (!Usable)
                {
                    return GuildSignetStore.Unavailable;
                }

                if (binding is null || binding.RootQuestId != _root || parent is not null && parent.RootQuestId != _root)
                {
                    return Mismatch;
                }

                Result<GuildSignetAdmissionEvidence> authorized = await _authority.AuthorizeIssuanceAsync(
                    admission, binding, parent, request, linked.Token).ConfigureAwait(false);

                if (authorized.IsFailure)
                {
                    return authorized.Error;
                }

                using Credential credential = Credential.Create();

                Result<GuildSignetRecord> issued = await _authority.IssueWithinAsync(_connection, _transaction,
                    admission, binding, parent, request, authorized.Value, credential, linked.Token).ConfigureAwait(false);

                if (issued.IsSuccess)
                {
                    _pending.Add(new(issued.Value, credential.Take()));
                }

                return issued;
            }
            finally
            {
                _serial.Release();
            }
        }

        internal async Task<Result<GuildGrant>> VerifyAsync(GuildExecutionHandle execution, CancellationToken cancellationToken)
        {
            if (!Usable)
            {
                return GuildSignetStore.Unavailable;
            }

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime, cancellationToken);

            await _serial.WaitAsync(linked.Token).ConfigureAwait(false);

            try
            {
                if (!Usable)
                {
                    return GuildSignetStore.Unavailable;
                }

                if (execution is null || execution.RootQuestId != _root)
                {
                    return Mismatch;
                }

                Result<Verified> live = await _authority.LiveAsync(_connection, _transaction, execution, linked.Token).ConfigureAwait(false);

                return live.IsSuccess ? Result<GuildGrant>.Success(live.Value.Effective) : Result<GuildGrant>.Failure(live.Error);
            }
            finally
            {
                _serial.Release();
            }
        }

        /// <summary>Current target eligibility only; this neither authenticates a caller nor issues an execution handle.</summary>
        internal async Task<Result<GuildGrant>> ValidateCurrentGrantAsync(Guid questId, CancellationToken cancellationToken)
        {
            if (!Usable)
            {
                return GuildSignetStore.Unavailable;
            }

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime, cancellationToken);

            await _serial.WaitAsync(linked.Token).ConfigureAwait(false);

            try
            {
                if (!Usable)
                {
                    return GuildSignetStore.Unavailable;
                }

                Result<GuildSignetRecord> target = await GuildSignetStore.ReadAsync(_connection, _transaction,
                    questId, true, linked.Token).ConfigureAwait(false);

                if (target.IsFailure)
                {
                    return target.Error;
                }

                if (target.Value.Binding.RootQuestId != _root)
                {
                    return Mismatch;
                }

                Result<Verified> live = await _authority.LiveRowAsync(_connection, _transaction, target.Value, linked.Token).ConfigureAwait(false);

                return live.IsSuccess ? Result<GuildGrant>.Success(live.Value.Effective) : Result<GuildGrant>.Failure(live.Error);
            }
            finally
            {
                _serial.Release();
            }
        }

        private bool Usable => Volatile.Read(ref _closed) == 0 && TransactionMatches;

        private bool TransactionMatches => ReferenceEquals(_transaction.Connection, _connection) && _connection.State == ConnectionState.Open;

        internal async Task<bool> CloseAsync(object completionOwner)
        {
            if (!ReferenceEquals(completionOwner, _completionOwner))
            {
                return false;
            }

            Interlocked.Exchange(ref _closed, 1);

            bool unfinished = _serial.CurrentCount == 0;

            // Drain an accidentally unawaited participant before rollback, never underneath it.
            await _serial.WaitAsync(CancellationToken.None).ConfigureAwait(false);

            try
            {
                return !unfinished && TransactionMatches;
            }
            finally
            {
                _serial.Release();
            }
        }

        internal Result RegisterCommitted(object completionOwner)
        {
            if (!ReferenceEquals(completionOwner, _completionOwner) || Volatile.Read(ref _closed) == 0 || _transaction.Connection is not null)
            {
                return GuildSignetStore.Unavailable;
            }

            List<GuildExecutionHandle> registered = [];

            foreach (PendingCredential pending in _pending)
            {
                Result<GuildExecutionHandle> result = _authority.Register(pending.Row, pending.Credential.Take());

                if (result.IsFailure)
                {
                    foreach (GuildExecutionHandle handle in registered)
                    {
                        _authority.Release(handle);
                    }

                    return result.Error;
                }

                registered.Add(result.Value);
            }

            return Result.Success();
        }

        internal void DisposePending(object completionOwner)
        {
            if (!ReferenceEquals(completionOwner, _completionOwner))
            {
                return;
            }

            foreach (PendingCredential pending in _pending)
            {
                pending.Credential.Dispose();
            }

            _pending.Clear();
        }

        private sealed record PendingCredential(GuildSignetRecord Row, Credential Credential);
    }
}
